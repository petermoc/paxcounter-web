using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

[ApiController]
[Route("api/ttn")]
public class TtnWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly WebhookLogStore _logStore;

    public TtnWebhookController(AppDbContext db, IConfiguration config, WebhookLogStore logStore)
    {
        _db = db;
        _config = config;
        _logStore = logStore;
    }

    [HttpPost("uplink")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Uplink([FromBody] JsonElement body, CancellationToken ct)
    {
        try
        {
            if (!IsAuthorized())
            {
                _logStore.Add(new WebhookLogEntry
                {
                    Status = "Unauthorized",
                    Message = "Missing or invalid X-Api-Key"
                });
                return Unauthorized();
            }

            var hasData = TryGetProperty(body, "data", out var data);
            var payloadRoot = hasData ? data : body;

            if (!TryGetProperty(payloadRoot, "end_device_ids", out var endDeviceIds) ||
                !TryGetString(endDeviceIds, "dev_eui", out var devEui) ||
                !TryGetString(endDeviceIds, "device_id", out var deviceId))
            {
                _logStore.Add(new WebhookLogEntry
                {
                    Status = "BadRequest",
                    Message = $"Missing device identifiers (keys: {GetPropertyNames(payloadRoot)})"
                });
                return BadRequest("Missing device identifiers");
            }

            var timestamp = GetTimestamp(payloadRoot);

            var device = _db.Devices.FirstOrDefault(d => d.DeviceUid == devEui);
            if (device == null)
            {
                device = new Device
                {
                    DeviceUid = devEui,
                    DeviceId = deviceId,
                    Name = deviceId
                };

                if (TryGetGatewayLocation(data, out var lat, out var lng))
                {
                    device.Latitude = lat;
                    device.Longitude = lng;
                }

                _db.Devices.Add(device);
                await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
            }

            if (!TryGetProperty(payloadRoot, "uplink_message", out var uplink))
            {
                return Ok();
            }

            var decoded = default(JsonElement);
            var hasDecoded = TryGetProperty(uplink, "decoded_payload", out decoded);

            var wifi = 0;
            var ble = 0;
            var decodedHasCounts = false;
            if (hasDecoded)
            {
                decodedHasCounts = TryGetInt(decoded, "wifi", out wifi) | TryGetInt(decoded, "ble", out ble);
                if (!decodedHasCounts)
                {
                    decodedHasCounts = TryGetInt(decoded, "wifi_count", out wifi) | TryGetInt(decoded, "ble_count", out ble);
                }
            }

            var rssiLimit = hasDecoded ? GetInt(decoded, "rssilimit") : 0;
            var batteryMv = hasDecoded ? GetFirstIntNullable(decoded, "voltage", "battery_mv", "vbat", "battery", "batt", "bat") : null;
            var batteryPct = GetDoubleNullable(uplink, "last_battery_percentage", "value");
            if (batteryPct == null && hasDecoded)
            {
                batteryPct = GetFirstDoubleNullable(decoded, "battery_percent", "batt_percent", "bat_percent");
            }

            var latitude = hasDecoded ? GetDoubleNullable(decoded, "latitude") : null;
            var longitude = hasDecoded ? GetDoubleNullable(decoded, "longitude") : null;
            var satellites = hasDecoded ? GetIntNullable(decoded, "sats") : null;
            var hdop = hasDecoded ? GetDoubleNullable(decoded, "hdop") : null;
            var altitude = hasDecoded ? GetIntNullable(decoded, "altitude") : null;
            var hasPackedFallback = false;

            var decodeSource = hasDecoded && decodedHasCounts ? "decoded_payload" : "none";

            var hasFrmFallback = TryDecodePackedCountsFromFrmPayload(uplink, out var wifiFromPayload, out var bleFromPayload);
            if (((!hasDecoded || !decodedHasCounts) ||
                 (decodedHasCounts && wifi == 0 && ble == 0 && hasFrmFallback && (wifiFromPayload != 0 || bleFromPayload != 0))) &&
                hasFrmFallback)
            {
                wifi = wifiFromPayload;
                ble = bleFromPayload;
                hasPackedFallback = true;
                decodeSource = "frm_payload";
            }

            if ((!hasDecoded || !decodedHasCounts) &&
                !hasPackedFallback &&
                TryDecodePackedCountsFromDecodedBytes(decoded, out var wifiFromBytes, out var bleFromBytes))
            {
                wifi = wifiFromBytes;
                ble = bleFromBytes;
                hasPackedFallback = true;
                decodeSource = "decoded_payload.bytes";
            }

            if (batteryMv == null || batteryPct == null)
            {
                var lastWithBattery = _db.PaxSamples
                    .Where(p => p.DeviceId == device.Id && (p.BatteryVoltageMv != null || p.BatteryPercent != null))
                    .OrderByDescending(p => p.Timestamp)
                    .FirstOrDefault();

                if (lastWithBattery != null)
                {
                    batteryMv ??= lastWithBattery.BatteryVoltageMv;
                    batteryPct ??= lastWithBattery.BatteryPercent;
                }
            }

            if (hasDecoded || hasPackedFallback || batteryPct != null || batteryMv != null)
            {
                var sample = new PaxSample
                {
                    DeviceId = device.Id,
                    Timestamp = timestamp,
                    WifiCount = wifi,
                    BleCount = ble,
                    RssiLimit = rssiLimit,
                    BatteryVoltageMv = batteryMv,
                    BatteryPercent = batteryPct,
                    Latitude = latitude,
                    Longitude = longitude,
                    Satellites = satellites,
                    Hdop = hdop,
                    Altitude = altitude,
                    SourceChannel = "LoRa/TTN"
                };

                _db.PaxSamples.Add(sample);
                await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
            }

            _logStore.Add(new WebhookLogEntry
            {
                Status = "OK",
                EventName = GetString(body, "name"),
                DeviceId = deviceId,
                DevEui = devEui,
                Message = (hasDecoded || hasPackedFallback)
                    ? $"src={decodeSource} wifi={wifi} ble={ble} lat={latitude?.ToString() ?? "-"} lon={longitude?.ToString() ?? "-"}"
                    : "no decoded_payload"
            });

            return Ok();
        }
        catch (OperationCanceledException)
        {
            _logStore.Add(new WebhookLogEntry
            {
                Status = "Canceled",
                EventName = GetString(body, "name"),
                Message = "Webhook request canceled; returning 200 to avoid TTN retry storm."
            });
            return Ok();
        }
        catch (Exception ex) when (SqliteWriteHelper.IsDatabaseLocked(ex))
        {
            _logStore.Add(new WebhookLogEntry
            {
                Status = "Locked",
                EventName = GetString(body, "name"),
                Message = "SQLite database was locked during TTN uplink save; returning 200 to avoid retry storm."
            });
            return Ok();
        }
    }

    private static string GetString(JsonElement obj, string prop)
    {
        return TryGetProperty(obj, prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";
    }

    private static string GetPropertyNames(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return "(not an object)";
        }

        return string.Join(", ", obj.EnumerateObject().Select(p => p.Name));
    }

    private bool IsAuthorized()
    {
        var expected = _config["TtnWebhook:ApiKey"];
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        if (!Request.Headers.TryGetValue("X-Api-Key", out var actual))
        {
            return false;
        }

        return string.Equals(actual.ToString(), expected, StringComparison.Ordinal);
    }

    private static DateTime GetTimestamp(JsonElement data)
    {
        if (TryGetString(data, "received_at", out var receivedAt) &&
            DateTimeOffset.TryParse(receivedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
        {
            return dto.UtcDateTime;
        }

        return DateTime.UtcNow;
    }

    private static bool TryGetGatewayLocation(JsonElement data, out double lat, out double lng)
    {
        lat = 0;
        lng = 0;

        if (!TryGetProperty(data, "uplink_message", out var uplink))
        {
            return false;
        }

        if (!TryGetProperty(uplink, "rx_metadata", out var rxMeta) || rxMeta.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in rxMeta.EnumerateArray())
        {
            if (TryGetProperty(item, "location", out var loc) &&
                TryGetDouble(loc, "latitude", out lat) &&
                TryGetDouble(loc, "longitude", out lng))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement obj, string prop, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (obj.TryGetProperty(prop, out value))
        {
            return true;
        }

        foreach (var candidate in obj.EnumerateObject())
        {
            if (string.Equals(candidate.Name, prop, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate.Value;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetString(JsonElement obj, string prop, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(obj, prop, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt(JsonElement obj, string prop, out int value)
    {
        value = 0;
        if (!TryGetProperty(obj, prop, out var element))
        {
            return false;
        }

        return TryConvertInt(element, out value);
    }

    private static bool TryGetIntNullable(JsonElement obj, string prop, out int? value)
    {
        value = null;
        if (!TryGetProperty(obj, prop, out var element))
        {
            return false;
        }

        if (TryConvertInt(element, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static int GetInt(JsonElement obj, string prop)
    {
        return TryGetInt(obj, prop, out var value) ? value : 0;
    }

    private static int? GetIntNullable(JsonElement obj, string prop)
    {
        return TryGetIntNullable(obj, prop, out var value) ? value : null;
    }

    private static bool TryGetDouble(JsonElement obj, string prop, out double value)
    {
        value = 0;
        if (!TryGetProperty(obj, prop, out var element))
        {
            return false;
        }

        return TryConvertDouble(element, out value);
    }

    private static double? GetDoubleNullable(JsonElement obj, params string[] path)
    {
        if (!TryFollowPath(obj, path, out var element))
        {
            return null;
        }

        return TryConvertDouble(element, out var value) ? value : null;
    }

    private static int? GetFirstIntNullable(JsonElement obj, params string[] props)
    {
        foreach (var prop in props)
        {
            var value = GetIntNullable(obj, prop);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static double? GetFirstDoubleNullable(JsonElement obj, params string[] props)
    {
        foreach (var prop in props)
        {
            var value = GetDoubleNullable(obj, prop);
            if (value != null)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryFollowPath(JsonElement obj, string[] path, out JsonElement value)
    {
        value = obj;
        foreach (var segment in path)
        {
            if (!TryGetProperty(value, segment, out value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryConvertInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value) || (element.TryGetDouble(out var dbl) && TryCastToInt(dbl, out value)),
            JsonValueKind.String => int.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value) ||
                                    (double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl) && TryCastToInt(dbl, out value)),
            _ => false
        };
    }

    private static bool TryConvertDouble(JsonElement element, out double value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool TryCastToInt(double input, out int value)
    {
        value = 0;
        if (input < int.MinValue || input > int.MaxValue)
        {
            return false;
        }

        value = Convert.ToInt32(Math.Round(input, MidpointRounding.AwayFromZero));
        return true;
    }

    private static bool TryDecodePackedCountsFromFrmPayload(JsonElement uplink, out int wifi, out int ble)
    {
        wifi = 0;
        ble = 0;

        if (!TryGetString(uplink, "frm_payload", out var frmPayload) || string.IsNullOrWhiteSpace(frmPayload))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(frmPayload);
            return TryDecodePackedCounts(bytes, out wifi, out ble);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodePackedCountsFromDecodedBytes(JsonElement decoded, out int wifi, out int ble)
    {
        wifi = 0;
        ble = 0;

        if (decoded.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetProperty(decoded, "bytes", out var bytesElement) && bytesElement.ValueKind == JsonValueKind.Array)
        {
            var bytes = new List<byte>();
            foreach (var item in bytesElement.EnumerateArray())
            {
                if (!TryConvertInt(item, out var intValue) || intValue < byte.MinValue || intValue > byte.MaxValue)
                {
                    return false;
                }

                bytes.Add((byte)intValue);
            }

            return TryDecodePackedCounts(bytes.ToArray(), out wifi, out ble);
        }

        if (TryGetString(decoded, "bytes", out var bytesString) && !string.IsNullOrWhiteSpace(bytesString))
        {
            try
            {
                return TryDecodePackedCounts(Convert.FromBase64String(bytesString), out wifi, out ble);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryDecodePackedCounts(byte[] bytes, out int wifi, out int ble)
    {
        wifi = 0;
        ble = 0;
        if (bytes.Length < 4)
        {
            return false;
        }

        wifi = (bytes[0] << 8) | bytes[1];
        ble = (bytes[2] << 8) | bytes[3];
        return true;
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

[ApiController]
[Route("api/pax")]
public class PaxWifiController : ControllerBase
{
    private const string WifiSourcePrefix = "WiFi/HTTP";

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly WebhookLogStore _logStore;
    private readonly DeviceCommandService _deviceCommandService;
    private readonly FootfallRelayService _footfallRelayService;
    private readonly ILogger<PaxWifiController> _logger;

    public PaxWifiController(AppDbContext db, IConfiguration config, WebhookLogStore logStore, DeviceCommandService deviceCommandService, FootfallRelayService footfallRelayService, ILogger<PaxWifiController> logger)
    {
        _db = db;
        _config = config;
        _logStore = logStore;
        _deviceCommandService = deviceCommandService;
        _footfallRelayService = footfallRelayService;
        _logger = logger;
    }

    [HttpPost("uplink")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Uplink([FromBody] PaxWifiUplinkRequest body, CancellationToken ct)
    {
        try
        {
            if (!IsAuthorized())
            {
                _logStore.Add(new WebhookLogEntry
                {
                    Status = "Unauthorized",
                    DeviceId = body.DeviceId,
                    DevEui = body.DeviceUid,
                    Message = "Missing or invalid X-Api-Key for WiFi fallback uplink."
                });
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(body.DeviceId))
            {
                _logStore.Add(new WebhookLogEntry
                {
                    Status = "BadRequest",
                    Message = "WiFi fallback uplink is missing deviceId."
                });
                return BadRequest("deviceId is required");
            }

            var deviceUid = string.IsNullOrWhiteSpace(body.DeviceUid)
                ? body.DeviceId
                : body.DeviceUid;
            var canonicalDeviceId = NormalizeKnownWifiDeviceId(body.DeviceId, deviceUid);

            _logger.LogInformation(
                "WiFi uplink received: deviceId={DeviceId} canonicalId={CanonicalDeviceId} uid={DeviceUid} ssid={Ssid} wifi={Wifi} ble={Ble} hasGps={HasGps} attempt={Attempt} consecutiveFailures={Failures} lastSuccessAge={LastSuccessAge}",
                body.DeviceId,
                canonicalDeviceId,
                deviceUid,
                string.IsNullOrWhiteSpace(body.WifiSsid) ? "-" : body.WifiSsid,
                body.Wifi,
                body.Ble,
                body.Latitude.HasValue && body.Longitude.HasValue,
                body.TransportAttempt,
                body.ConsecutiveTransportFailures,
                body.LastHttpSuccessAgeSeconds);

            if (_footfallRelayService.IsConfigured && _footfallRelayService.ShouldRelay(canonicalDeviceId, deviceUid))
            {
                var relayTimestamp = DateTime.UtcNow;
                var relayBaseChannel = BuildWifiSourceChannel(body.WifiSsid);
                // H2 follows the preferred phone: A52 first, then S23.
                var phoneGps = string.Equals(canonicalDeviceId, "heltec2-new", StringComparison.OrdinalIgnoreCase)
                    ? await TryGetJoinedGpsAsync(relayBaseChannel, relayTimestamp, ct)
                    : null;
                await _footfallRelayService.RelayAsync(new FootfallRelayRequest
                {
                    DeviceId = canonicalDeviceId,
                    DeviceUid = deviceUid,
                    TimestampUtc = relayTimestamp,
                    BleCount = body.Ble,
                    WifiCount = body.Wifi,
                    Latitude = body.Latitude ?? phoneGps?.Latitude,
                    Longitude = body.Longitude ?? phoneGps?.Longitude,
                    SourceChannel = BuildWifiSourceChannel(body.WifiSsid, phoneGps?.SourceTopic)
                }, ct);

                _logStore.Add(new WebhookLogEntry
                {
                    Status = "Relayed",
                    DeviceId = body.DeviceId,
                    DevEui = deviceUid,
                    Message = $"Footfall relay wifi={body.Wifi} ble={body.Ble} hotspot={body.WifiSsid ?? "-"}"
                });

                _logger.LogInformation(
                    "WiFi uplink relayed to footfall: deviceId={DeviceId} uid={DeviceUid} ssid={Ssid}",
                    canonicalDeviceId,
                    deviceUid,
                    string.IsNullOrWhiteSpace(body.WifiSsid) ? "-" : body.WifiSsid);

                return Ok(new { ok = true, relayed = true });
            }

            var device = _db.Devices.FirstOrDefault(d => d.DeviceUid == deviceUid) ??
                         _db.Devices.FirstOrDefault(d => d.DeviceId == canonicalDeviceId);

            if (device == null)
            {
                device = new Device
                {
                    DeviceUid = deviceUid,
                    DeviceId = canonicalDeviceId,
                    Name = canonicalDeviceId,
                    Latitude = body.Latitude ?? 0,
                    Longitude = body.Longitude ?? 0
                };

                _db.Devices.Add(device);
                await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
            }
            else
            {
                device.DeviceUid = deviceUid;
                device.DeviceId = canonicalDeviceId;
                if (body.Latitude.HasValue)
                {
                    device.Latitude = body.Latitude.Value;
                }
                if (body.Longitude.HasValue)
                {
                    device.Longitude = body.Longitude.Value;
                }
            }

            var timestamp = DateTime.UtcNow;
            var recentSample = _db.PaxSamples
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= timestamp.AddSeconds(-12))
                .OrderByDescending(p => p.Timestamp)
                .FirstOrDefault();

            if (recentSample != null &&
                recentSample.WifiCount == body.Wifi &&
                recentSample.BleCount == body.Ble &&
                IsWifiSourceChannel(recentSample.SourceChannel))
            {
                _logStore.Add(new WebhookLogEntry
                {
                    Status = "Deduplicated",
                    DeviceId = body.DeviceId,
                    DevEui = deviceUid,
                    Message = $"Skipped WiFi fallback duplicate because {recentSample.SourceChannel} sample arrived {Math.Round((timestamp - recentSample.Timestamp).TotalSeconds, 1)}s earlier."
                });

                return Ok(new { ok = true, deduplicated = true });
            }

            var baseSourceChannel = BuildWifiSourceChannel(body.WifiSsid);
            var joinedGps = (!body.Latitude.HasValue || !body.Longitude.HasValue)
                ? await TryGetJoinedGpsAsync(baseSourceChannel, timestamp, ct)
                : null;

            var sourceChannel = BuildWifiSourceChannel(body.WifiSsid, joinedGps?.SourceTopic, body);
            var sampleLatitude = body.Latitude ?? joinedGps?.Latitude;
            var sampleLongitude = body.Longitude ?? joinedGps?.Longitude;
            var sampleSourceChannel = joinedGps == null
                ? sourceChannel
                : $"{sourceChannel} + GPS join ({DescribeGpsSource(joinedGps.SourceTopic)})";

            if (sampleLatitude.HasValue)
            {
                device.Latitude = sampleLatitude.Value;
            }
            if (sampleLongitude.HasValue)
            {
                device.Longitude = sampleLongitude.Value;
            }

            var sample = new PaxSample
            {
                DeviceId = device.Id,
                Timestamp = timestamp,
                WifiCount = body.Wifi,
                BleCount = body.Ble,
                RssiLimit = 0,
                BatteryVoltageMv = body.BatteryMv,
                BatteryPercent = body.BatteryPercent,
                Latitude = sampleLatitude,
                Longitude = sampleLongitude,
                Satellites = body.Satellites,
                Hdop = body.Hdop,
                Altitude = body.Altitude,
                SourceChannel = sampleSourceChannel
            };

            _db.PaxSamples.Add(sample);
            await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);

            _logger.LogInformation(
                "WiFi uplink stored: deviceId={DeviceId} uid={DeviceUid} channel={Channel} ssid={Ssid} lat={Lat} lon={Lon}",
                body.DeviceId,
                deviceUid,
                sample.SourceChannel,
                string.IsNullOrWhiteSpace(body.WifiSsid) ? "-" : body.WifiSsid,
                sample.Latitude,
                sample.Longitude);

            var pendingCommand = await _deviceCommandService.TryConsumePendingCommandAsync(device, ct);
            var ackId = $"{canonicalDeviceId}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            _logStore.Add(new WebhookLogEntry
            {
                Status = "OK",
                DeviceId = canonicalDeviceId,
                DevEui = deviceUid,
                Message = $"wifi={body.Wifi} ble={body.Ble} hotspot={body.WifiSsid ?? "-"} attempt={body.TransportAttempt?.ToString() ?? "-"} fail={body.ConsecutiveTransportFailures?.ToString() ?? "-"} ackAge={body.LastHttpSuccessAgeSeconds?.ToString() ?? "-"}s batt={body.BatteryMv?.ToString() ?? "-"}" +
                    (pendingCommand == null ? string.Empty : $" command={FormatPendingCommand(pendingCommand)}")
            });

            return Ok(new
            {
                ok = true,
                ackId,
                serverTimeUtc = DateTime.UtcNow,
                commandBase64 = pendingCommand?.PayloadBase64,
                commandHex = pendingCommand?.PayloadHex,
                commandName = pendingCommand?.CommandName,
                requestedSeconds = pendingCommand?.RequestedSeconds
            });
        }
        catch (OperationCanceledException)
        {
            _logStore.Add(new WebhookLogEntry
            {
                Status = "Canceled",
                DeviceId = body.DeviceId,
                DevEui = body.DeviceUid,
                Message = "WiFi fallback request canceled; returning 200 to avoid retry storm."
            });
            return Ok(new { ok = true, canceled = true });
        }
        catch (Exception ex) when (SqliteWriteHelper.IsDatabaseLocked(ex))
        {
            _logStore.Add(new WebhookLogEntry
            {
                Status = "Locked",
                DeviceId = body.DeviceId,
                DevEui = body.DeviceUid,
                Message = "SQLite database was locked during WiFi fallback save; returning 200 to avoid retry storm."
            });
            return Ok(new { ok = true, locked = true });
        }
    }

    private sealed record JoinedGpsSample(DateTime Timestamp, double Latitude, double Longitude, string? SourceTopic);

    private async Task<JoinedGpsSample?> TryGetJoinedGpsAsync(string? sourceChannel, DateTime timestamp, CancellationToken ct)
    {
        var maxAge = TimeSpan.FromMinutes(30);
        var freshnessWindow = TimeSpan.FromSeconds(20);
        var earliestTimestamp = timestamp - maxAge;
        var preferredTopics = ResolvePreferredGpsTopics(sourceChannel);

        var candidates = await _db.GpsSamples
            .AsNoTracking()
            .Where(g => g.Timestamp >= earliestTimestamp && g.Timestamp <= timestamp)
            .OrderByDescending(g => g.Timestamp)
            .Select(g => new JoinedGpsSample(g.Timestamp, g.Latitude, g.Longitude, g.SourceTopic))
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return null;
        }

        var freshestTimestamp = candidates.Max(g => g.Timestamp);
        var freshCandidates = candidates
            .Where(g => freshestTimestamp - g.Timestamp <= freshnessWindow)
            .ToList();

        return freshCandidates
            .OrderBy(g => PreferredTopicRank(g.SourceTopic, preferredTopics))
            .ThenByDescending(g => g.Timestamp)
            .FirstOrDefault()
            ?? candidates
                .OrderBy(g => PreferredTopicRank(g.SourceTopic, preferredTopics))
                .ThenByDescending(g => g.Timestamp)
                .FirstOrDefault();
    }

    private static string[] ResolvePreferredGpsTopics(string? sourceChannel)
    {
        if (!string.IsNullOrWhiteSpace(sourceChannel) && sourceChannel.Contains("LoRa/TTN", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "lora/direct" };
        }

        if (!string.IsNullOrWhiteSpace(sourceChannel) && sourceChannel.Contains("via A52", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "phone/direct/a52", "phone/direct/s23", "phone/gps/a52", "ha:device_tracker.sm_a528b_star_telefon", "phone/gps/s23", "ha:device_tracker.sm_s911b", "phone/gps" };
        }

        return new[] { "phone/direct/a52", "phone/direct/s23", "phone/gps/a52", "ha:device_tracker.sm_a528b_star_telefon", "phone/gps/s23", "ha:device_tracker.sm_s911b", "phone/gps" };
    }

    private static int PreferredTopicRank(string? sourceTopic, string[] preferredTopics)
    {
        if (string.IsNullOrWhiteSpace(sourceTopic))
        {
            return preferredTopics.Length + 10;
        }

        for (var i = 0; i < preferredTopics.Length; i++)
        {
            if (string.Equals(sourceTopic, preferredTopics[i], StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return preferredTopics.Length + 5;
    }

    private static string DescribeGpsSource(string? sourceTopic)
    {
        return sourceTopic switch
        {
            "phone/direct/a52" => "A52 Direct GPS",
            "phone/direct/s23" => "S23 Direct GPS",
            "phone/gps/a52" => "A52",
            "phone/gps/s23" => "S23",
            "ha:device_tracker.sm_a528b_star_telefon" => "A52 HA",
            "ha:device_tracker.sm_s911b" => "S23 HA",
            "phone/gps" => "Preferred phone",
            _ => "MQTT"
        };
    }

    private static string BuildWifiSourceChannel(string? wifiSsid, string? gpsSourceTopic = null, PaxWifiUplinkRequest? request = null)
    {
        var routeLabel = ResolveWifiRouteLabel(wifiSsid, gpsSourceTopic);
        var baseChannel = string.IsNullOrWhiteSpace(routeLabel)
            ? WifiSourcePrefix
            : $"{WifiSourcePrefix} via {routeLabel}";
        var diagnostics = BuildWifiTransportDiagnostics(wifiSsid, request);
        return string.IsNullOrWhiteSpace(diagnostics)
            ? baseChannel
            : $"{baseChannel} [{diagnostics}]";
    }

    private static string BuildWifiTransportDiagnostics(string? wifiSsid, PaxWifiUplinkRequest? request)
    {
        if (request == null)
        {
            return string.IsNullOrWhiteSpace(wifiSsid) ? string.Empty : $"ssid={wifiSsid.Trim()}";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(wifiSsid))
        {
            parts.Add($"ssid={wifiSsid.Trim()}");
        }
        if (request.TransportAttempt.HasValue)
        {
            parts.Add($"try={request.TransportAttempt.Value}");
        }
        if (request.ConsecutiveTransportFailures.HasValue)
        {
            parts.Add($"fail={request.ConsecutiveTransportFailures.Value}");
        }
        if (request.LastHttpSuccessAgeSeconds.HasValue)
        {
            parts.Add($"ackAge={request.LastHttpSuccessAgeSeconds.Value}s");
        }
        if (!string.IsNullOrWhiteSpace(request.LastAckId))
        {
            parts.Add($"ack={request.LastAckId}");
        }

        return string.Join(" ", parts);
    }

    private static string? ResolveWifiRouteLabel(string? wifiSsid, string? gpsSourceTopic)
    {
        if (IsA52GpsSource(gpsSourceTopic))
        {
            return "A52";
        }

        if (IsS23GpsSource(gpsSourceTopic))
        {
            return "S23";
        }

        if (string.IsNullOrWhiteSpace(wifiSsid))
        {
            return null;
        }

        var ssid = wifiSsid.Trim();
        if (ssid.Contains("a52", StringComparison.OrdinalIgnoreCase) ||
            ssid.Contains("528", StringComparison.OrdinalIgnoreCase))
        {
            return "A52";
        }

        if (ssid.Contains("s23", StringComparison.OrdinalIgnoreCase) ||
            ssid.Contains("911", StringComparison.OrdinalIgnoreCase))
        {
            return "S23";
        }

        return ssid;
    }

    private static bool IsA52GpsSource(string? sourceTopic)
    {
        return string.Equals(sourceTopic, "phone/direct/a52", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceTopic, "phone/gps/a52", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceTopic, "ha:device_tracker.sm_a528b_star_telefon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsS23GpsSource(string? sourceTopic)
    {
        return string.Equals(sourceTopic, "phone/direct/s23", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceTopic, "phone/gps/s23", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(sourceTopic, "ha:device_tracker.sm_s911b", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWifiSourceChannel(string? sourceChannel)
    {
        return !string.IsNullOrWhiteSpace(sourceChannel) &&
               sourceChannel.StartsWith(WifiSourcePrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeKnownWifiDeviceId(string deviceId, string deviceUid)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && deviceId.Trim().StartsWith("paxcounter-", StringComparison.OrdinalIgnoreCase))
        {
            return deviceId.Trim();
        }

        var normalizedUid = (deviceUid ?? string.Empty).Trim().ToUpperInvariant();
        return normalizedUid switch
        {
            "A0:DD:6C:73:09:F4" => "paxcounter-T3",
            "F0:24:F9:AD:5C:10" => "paxcounter-Tbeam",
            "44:1B:F6:F9:40:7C" => "paxcounter-TBeamS3",
            _ => deviceId
        };
    }

    private static string FormatPendingCommand(PendingDeviceCommand command)
    {
        return command.CommandName switch
        {
            "set_rssi" when command.RequestedSeconds == 0 => "set_rssi off",
            "set_rssi" => $"set_rssi {command.RequestedSeconds}dBm",
            "set_ble_scan_interval" => $"set_ble_scan_interval {command.RequestedSeconds}ms",
            _ => $"{command.CommandName} {command.RequestedSeconds}s"
        };
    }

    private bool IsAuthorized()
    {
        var expected = _config["PaxWifiFallback:ApiKey"];
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
}

public sealed class PaxWifiUplinkRequest
{
    public string DeviceId { get; set; } = "";
    public string? DeviceUid { get; set; }
    public string? WifiSsid { get; set; }
    public int? TransportAttempt { get; set; }
    public int? TransportSequence { get; set; }
    public int? ConsecutiveTransportFailures { get; set; }
    public int? LastHttpSuccessAgeSeconds { get; set; }
    public string? LastAckId { get; set; }
    public int Wifi { get; set; }
    public int Ble { get; set; }
    public int Pax { get; set; }
    public int? BatteryMv { get; set; }
    public double? BatteryPercent { get; set; }
    public long? Uptime { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Satellites { get; set; }
    public double? Hdop { get; set; }
    public int? Altitude { get; set; }
}






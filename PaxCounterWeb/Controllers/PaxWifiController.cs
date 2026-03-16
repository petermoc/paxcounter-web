using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

[ApiController]
[Route("api/pax")]
public class PaxWifiController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly WebhookLogStore _logStore;
    private readonly DeviceCommandService _deviceCommandService;

    public PaxWifiController(AppDbContext db, IConfiguration config, WebhookLogStore logStore, DeviceCommandService deviceCommandService)
    {
        _db = db;
        _config = config;
        _logStore = logStore;
        _deviceCommandService = deviceCommandService;
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

            var device = _db.Devices.FirstOrDefault(d => d.DeviceUid == deviceUid) ??
                         _db.Devices.FirstOrDefault(d => d.DeviceId == body.DeviceId);

            if (device == null)
            {
                device = new Device
                {
                    DeviceUid = deviceUid,
                    DeviceId = body.DeviceId,
                    Name = body.DeviceId,
                    Latitude = body.Latitude ?? 0,
                    Longitude = body.Longitude ?? 0
                };

                _db.Devices.Add(device);
                await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
            }
            else
            {
                device.DeviceUid = deviceUid;
                device.DeviceId = body.DeviceId;
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
                !string.Equals(recentSample.SourceChannel, "WiFi/HTTP", StringComparison.OrdinalIgnoreCase))
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

            var sample = new PaxSample
            {
                DeviceId = device.Id,
                Timestamp = timestamp,
                WifiCount = body.Wifi,
                BleCount = body.Ble,
                RssiLimit = 0,
                BatteryVoltageMv = body.BatteryMv,
                BatteryPercent = body.BatteryPercent,
                Latitude = body.Latitude,
                Longitude = body.Longitude,
                Satellites = body.Satellites,
                Hdop = body.Hdop,
                Altitude = body.Altitude,
                SourceChannel = "WiFi/HTTP"
            };

            _db.PaxSamples.Add(sample);
            await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);

            var pendingCommand = await _deviceCommandService.TryConsumePendingCommandAsync(device, ct);

            _logStore.Add(new WebhookLogEntry
            {
                Status = "OK",
                DeviceId = body.DeviceId,
                DevEui = deviceUid,
                Message = $"wifi={body.Wifi} ble={body.Ble} hotspot={body.WifiSsid ?? "-"} batt={body.BatteryMv?.ToString() ?? "-"}" +
                    (pendingCommand == null ? string.Empty : $" command={pendingCommand.CommandName} {pendingCommand.RequestedSeconds}s")
            });

            return Ok(new
            {
                ok = true,
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

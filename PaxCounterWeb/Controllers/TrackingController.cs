using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

[Route("tracking")]
public class TrackingController : Controller
{
    private readonly TrackStore _trackStore;
    private readonly AppDbContext _db;
    private readonly GpsDisplaySettingsService _gpsDisplaySettingsService;
    private readonly HomeAssistantGpsService _homeAssistantGpsService;

    public TrackingController(TrackStore trackStore, AppDbContext db, GpsDisplaySettingsService gpsDisplaySettingsService, HomeAssistantGpsService homeAssistantGpsService)
    {
        _trackStore = trackStore;
        _db = db;
        _gpsDisplaySettingsService = gpsDisplaySettingsService;
        _homeAssistantGpsService = homeAssistantGpsService;
    }

    [HttpGet("")]
    public IActionResult Index([FromQuery] string[]? deviceIds = null)
    {
        var knownDevices = _db.Devices
            .Where(d => !d.IsHidden)
            .Select(d => d.DeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList()
            .Concat(_trackStore.GetDevices())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToList();

        var vm = new ViewModels.TrackingViewModel
        {
            Devices = knownDevices,
            SelectedDeviceIds = deviceIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
            GpsDisplayMode = _gpsDisplaySettingsService.GetMode().ToString(),
            ForceGpsConfigured = _homeAssistantGpsService.IsConfigured
        };
        return View(vm);
    }

    [HttpPost("api/force-gps")]
    public async Task<IActionResult> ForceGps(CancellationToken ct)
    {
        var result = await _homeAssistantGpsService.ForceRefreshAsync(ct);
        return Json(new
        {
            success = result.Success,
            message = result.Message,
            requestedAtUtc = DateTime.UtcNow
        });
    }

    [HttpGet("api/track")]
    public IActionResult Track([FromQuery] int minutes = 120, [FromQuery] string[]? deviceIds = null)
    {
        minutes = Math.Clamp(minutes, 5, 43200);
        var sinceUtc = DateTime.UtcNow.AddMinutes(-minutes);
        var selectedDeviceIds = deviceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        var counterJoinWindow = TimeSpan.FromHours(6);

        var gpsRows = _db.GpsSamples
            .Where(g => g.Timestamp >= sinceUtc)
            .OrderBy(g => g.Timestamp)
            .ToList();

        if (gpsRows.Count == 0)
        {
            var lastKnownGps = _db.GpsSamples
                .OrderByDescending(g => g.Timestamp)
                .Take(1)
                .ToList();

            if (lastKnownGps.Count == 0)
            {
                var paxGpsFallback = _db.PaxSamples
                    .Where(p => p.Latitude.HasValue && p.Longitude.HasValue)
                    .OrderByDescending(p => p.Timestamp)
                    .Select(p => new FallbackGpsPoint
                    {
                        Timestamp = p.Timestamp,
                        Latitude = p.Latitude!.Value,
                        Longitude = p.Longitude!.Value,
                        Accuracy = null
                    })
                    .Take(1)
                    .ToList();

                if (paxGpsFallback.Count == 0)
                {
                    return Json(new List<TrackPoint>());
                }

                gpsRows = paxGpsFallback
                    .Select(p => new GpsSample
                    {
                        Timestamp = p.Timestamp,
                        Latitude = p.Latitude,
                        Longitude = p.Longitude,
                        Accuracy = p.Accuracy
                    })
                    .ToList();
            }
            else
            {
                gpsRows = lastKnownGps;
            }
        }

        if (gpsRows.Count > 5000)
        {
            var step = (int)Math.Ceiling(gpsRows.Count / 5000d);
            gpsRows = gpsRows.Where((_, i) => i % step == 0).ToList();
        }

        var effectiveDeviceIds = selectedDeviceIds.Count == 0
            ? _db.Devices.Where(d => !d.IsHidden).Select(d => d.DeviceId).ToList()
            : selectedDeviceIds;

        var seriesRows = _db.PaxSamples
            .Include(p => p.Device)
            .Where(p => p.Timestamp >= sinceUtc.Subtract(counterJoinWindow))
            .ToList()
            .Where(p => effectiveDeviceIds.Contains(p.Device.DeviceId, StringComparer.OrdinalIgnoreCase))
            .Select(p => new DeviceCounterPoint
            {
                DeviceId = p.Device.DeviceId,
                Timestamp = DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc),
                BleCount = p.BleCount,
                WifiCount = p.WifiCount
            })
            .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Timestamp).ToList(), StringComparer.OrdinalIgnoreCase);

        var allPoints = new List<TrackPoint>();
        foreach (var dev in effectiveDeviceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            seriesRows.TryGetValue(dev, out var deviceSeries);
            deviceSeries ??= new List<DeviceCounterPoint>();
            var s = 0;

            foreach (var g in gpsRows)
            {
                var ts = DateTime.SpecifyKind(g.Timestamp, DateTimeKind.Utc);

                while (s + 1 < deviceSeries.Count && deviceSeries[s + 1].Timestamp <= ts)
                {
                    s++;
                }

                var ble = 0;
                var wifi = 0;
                if (deviceSeries.Count > 0)
                {
                    var current = deviceSeries[s];
                    if (current.Timestamp <= ts && ts - current.Timestamp <= counterJoinWindow)
                    {
                        ble = current.BleCount;
                        wifi = current.WifiCount;
                    }
                }

                allPoints.Add(new TrackPoint
                {
                    TimestampUtc = ts,
                    Lat = g.Latitude,
                    Lon = g.Longitude,
                    Accuracy = g.Accuracy ?? 0,
                    Ble = ble,
                    Wifi = wifi,
                    Pax = ble + wifi,
                    DeviceId = dev
                });
            }
        }

        return Json(allPoints.OrderBy(p => p.TimestampUtc).ToList());
    }

    private sealed class DeviceCounterPoint
    {
        public string DeviceId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public int BleCount { get; set; }
        public int WifiCount { get; set; }
    }

    private sealed class FallbackGpsPoint
    {
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int? Accuracy { get; set; }
    }
}

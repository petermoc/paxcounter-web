using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;

namespace PaxCounterWeb.Controllers;

[Route("tracking")]
public class TrackingController : Controller
{
    private readonly TrackStore _trackStore;
    private readonly AppDbContext _db;
    private readonly GpsDisplaySettingsService _gpsDisplaySettingsService;
    private readonly PhoneGpsService _phoneGpsService;
    private readonly IConfiguration _configuration;
    private readonly DeviceCommandService _deviceCommandService;
    private readonly TrackerHomeModeSettingsService _trackerHomeModeSettingsService;
    private readonly UiFeatureSettingsService _uiFeatureSettingsService;
    private readonly TrackerModeOverrideService _trackerModeOverrideService;
    private readonly ILogger<TrackingController> _logger;

    public TrackingController(
        TrackStore trackStore,
        AppDbContext db,
        GpsDisplaySettingsService gpsDisplaySettingsService,
        PhoneGpsService phoneGpsService,
        IConfiguration configuration,
        DeviceCommandService deviceCommandService,
        TrackerHomeModeSettingsService trackerHomeModeSettingsService,
        UiFeatureSettingsService uiFeatureSettingsService,
        TrackerModeOverrideService trackerModeOverrideService,
        ILogger<TrackingController> logger)
    {
        _trackStore = trackStore;
        _db = db;
        _gpsDisplaySettingsService = gpsDisplaySettingsService;
        _phoneGpsService = phoneGpsService;
        _configuration = configuration;
        _deviceCommandService = deviceCommandService;
        _trackerHomeModeSettingsService = trackerHomeModeSettingsService;
        _uiFeatureSettingsService = uiFeatureSettingsService;
        _trackerModeOverrideService = trackerModeOverrideService;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index([FromQuery] string[]? deviceIds = null, [FromQuery] int minutes = 120, [FromQuery] DateTimeOffset? fromUtc = null, [FromQuery] DateTimeOffset? toUtc = null)
    {
        return View("Index", await BuildViewModelAsync(
            pageTitle: "Tracking Map",
            requestedDeviceIds: deviceIds,
            defaultDeviceIds: Array.Empty<string>(),
            restrictToDefaultDevices: false,
            allDevicesLabel: "All devices",
            emptyStateMessage: "No devices available yet.",
            requestedMinutes: minutes,
            fromUtc: fromUtc,
            toUtc: toUtc));
    }

    [HttpGet("trackers")]
    public async Task<IActionResult> Trackers([FromQuery] string[]? deviceIds = null, [FromQuery] int minutes = 120, [FromQuery] DateTimeOffset? fromUtc = null, [FromQuery] DateTimeOffset? toUtc = null)
    {
        var configuredTrackerIds = GetConfiguredTrackerDeviceIds();
        return View("Index", await BuildViewModelAsync(
            pageTitle: "Tracker Devices",
            requestedDeviceIds: deviceIds,
            defaultDeviceIds: configuredTrackerIds,
            restrictToDefaultDevices: true,
            allDevicesLabel: "All trackers",
            emptyStateMessage: configuredTrackerIds.Count == 0
                ? "Configure Tracking:TrackerDeviceIds in appsettings.json to show your tracker devices here."
                : "Waiting for tracker uplinks.",
            requestedMinutes: minutes,
            fromUtc: fromUtc,
            toUtc: toUtc));
    }

    [HttpGet("crowd")]
    public async Task<IActionResult> Crowd([FromQuery] string[]? deviceIds = null, [FromQuery] int minutes = 120)
    {
        return View("Crowd", await BuildCrowdEstimateViewModelAsync(deviceIds, minutes, HttpContext.RequestAborted));
    }

    [HttpPost("api/force-gps")]
    public async Task<IActionResult> ForceGps(CancellationToken ct)
    {
        _trackerModeOverrideService.Clear();
        _trackerHomeModeSettingsService.SetCurrentMode("away");
        var result = await _phoneGpsService.ForceRefreshAsync(ct);
        return Json(new { success = result.Success, message = result.Message, requestedAtUtc = DateTime.UtcNow });
    }

    [HttpPost("api/set-home-interval")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SetHomeInterval([FromBody] TrackingIntervalRequest? request, CancellationToken ct)
    {
        var targetInterval = ResolveConfiguredAutoModeIntervalSeconds(isHome: true);

        var configuredTrackerIds = GetConfiguredTrackerDeviceIds();
        var requestedIds = request?.DeviceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (requestedIds.Count == 0)
        {
            requestedIds = configuredTrackerIds;
        }
        else
        {
            requestedIds = requestedIds
                .Where(id => configuredTrackerIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var devices = await _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .Where(d => requestedIds.Contains(d.DeviceId))
            .OrderBy(d => d.DeviceId)
            .ToListAsync(ct);

        if (devices.Count == 0)
        {
            return Json(new { success = false, message = $"No tracker devices were found for the {targetInterval}s interval update." });
        }

        var queued = 0;
        var messages = new List<string>();
        foreach (var device in devices)
        {
            var result = await _deviceCommandService.QueueSendCycleAsync(device, targetInterval, ct);
            if (result.Success)
            {
                queued++;
            }
            else
            {
                messages.Add($"{device.DeviceId}: {result.Message}");
            }
        }

        if (queued == 0)
        {
            return Json(new { success = false, message = messages.FirstOrDefault() ?? "Home interval could not be queued." });
        }

        _trackerModeOverrideService.SetMode("home");
        _trackerHomeModeSettingsService.SetCurrentMode("home");

        var phoneMessages = new List<string>();
        if (_phoneGpsService.IsConfigured)
        {
            var intervalSync = await _phoneGpsService.ApplyIntervalAsync(targetInterval, ct, highAccuracyEnabled: false);
            if (!intervalSync.Success)
            {
                phoneMessages.Add($"Phone interval sync: {intervalSync.Message}");
            }

            var highAccuracyOff = await _phoneGpsService.DisableHighAccuracyAsync(ct);
            if (!highAccuracyOff.Success)
            {
                phoneMessages.Add($"High accuracy off: {highAccuracyOff.Message}");
            }
        }

        var summary = $"Queued {targetInterval}s for {queued} device(s) and forced HOME mode.";
        if (messages.Count > 0)
        {
            summary += " " + string.Join(" ", messages.Take(2));
        }
        if (phoneMessages.Count > 0)
        {
            summary += " " + string.Join(" ", phoneMessages.Take(2));
        }

        return Json(new { success = true, message = summary, seconds = targetInterval, queued });
    }

    [HttpGet("api/track")]
    public async Task<IActionResult> Track([FromQuery] int minutes = 120, [FromQuery] string[]? deviceIds = null, [FromQuery] bool directOnly = false, [FromQuery] bool fullGpsDetail = false, [FromQuery] DateTimeOffset? fromUtc = null, [FromQuery] DateTimeOffset? toUtc = null)
    {
        var (sinceUtc, untilUtc, effectiveMinutes) = ResolveTimeWindow(minutes, fromUtc, toUtc);
        var selectedDeviceIds = deviceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeTrackingDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
        var counterJoinWindow = TimeSpan.FromSeconds(Math.Clamp(_configuration.GetValue<int?>("Tracking:CounterJoinWindowSeconds") ?? 25, 5, 300));

        var gpsRows = new List<GpsSample>();
        if (!directOnly)
        {
            gpsRows = _db.GpsSamples
                .Where(g => g.Timestamp >= sinceUtc && g.Timestamp <= untilUtc)
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
                            Accuracy = null,
                            SourceTopic = p.SourceChannel
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
                            Accuracy = p.Accuracy,
                            SourceTopic = p.SourceTopic
                        })
                        .ToList();
                }
                else
                {
                    gpsRows = lastKnownGps;
                }
            }
        }

        if (!directOnly)
        {
            var requestedWindowMinutes = Math.Max(1, (int)Math.Ceiling((untilUtc - sinceUtc).TotalMinutes));
            var maxGpsRows = fullGpsDetail
                ? 12000
                : requestedWindowMinutes >= 4320
                    ? 1200
                    : requestedWindowMinutes >= 1440
                        ? 2200
                        : 5000;

            if (gpsRows.Count > maxGpsRows)
            {
                var step = (int)Math.Ceiling(gpsRows.Count / (double)maxGpsRows);
                gpsRows = gpsRows.Where((_, i) => i % step == 0).ToList();
            }
        }

        if (!directOnly)
        {
            // All route history is stored locally, including historical imported samples.
            gpsRows = FilterUnconfirmedGpsJumps(gpsRows).FilteredRows;
            gpsRows = DownsampleGpsSamplesForJoin(gpsRows, effectiveMinutes, fullGpsDetail);
        }

        var effectiveDeviceIds = selectedDeviceIds.Count == 0
            ? _db.Devices.Where(d => !d.IsHidden).Select(d => NormalizeTrackingDeviceId(d.DeviceId)).Distinct().ToList()
            : selectedDeviceIds;
        var configuredTrackerIds = GetConfiguredTrackerDeviceIds();

        if (directOnly)
        {
            var trackerPoints = _db.TrackerPositions
                .Include(p => p.Device)
                .Where(p => p.TimestampUtc >= sinceUtc && p.TimestampUtc <= untilUtc)
                .OrderBy(p => p.TimestampUtc)
                .ToList()
                .Where(p => effectiveDeviceIds.Contains(NormalizeTrackingDeviceId(p.Device.DeviceId), StringComparer.OrdinalIgnoreCase))
                .Select(p => new TrackPoint
                {
                    TimestampUtc = DateTime.SpecifyKind(p.TimestampUtc, DateTimeKind.Utc),
                    Lat = p.Latitude,
                    Lon = p.Longitude,
                    Accuracy = 0,
                    Ble = 0,
                    Wifi = 0,
                    Pax = 0,
                    DeviceId = NormalizeTrackingDeviceId(p.Device.DeviceId),
                    SourceTopic = "tracker/direct"
                })
                .ToList();

            return Json(DeduplicateTrackPoints(trackerPoints));
        }

        var seriesRows = _db.PaxSamples
            .Include(p => p.Device)
            .Where(p => p.Timestamp >= sinceUtc.Subtract(counterJoinWindow) && p.Timestamp <= untilUtc)
            .ToList()
            .Where(p => effectiveDeviceIds.Contains(NormalizeTrackingDeviceId(p.Device.DeviceId), StringComparer.OrdinalIgnoreCase))
                .Select(p => new DeviceCounterPoint
                {
                    DeviceId = NormalizeTrackingDeviceId(p.Device.DeviceId),
                    Timestamp = DateTime.SpecifyKind(p.Timestamp, DateTimeKind.Utc),
                    BleCount = p.BleCount,
                    WifiCount = p.WifiCount,
                    Latitude = p.Latitude,
                    Longitude = p.Longitude,
                    SourceChannel = p.SourceChannel
                })
                .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Timestamp).ToList(), StringComparer.OrdinalIgnoreCase);

        var allPoints = new List<TrackPoint>();
        foreach (var dev in effectiveDeviceIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            seriesRows.TryGetValue(dev, out var deviceSeries);
            deviceSeries ??= new List<DeviceCounterPoint>();

            var directPoints = deviceSeries
                .Where(p => p.Timestamp >= sinceUtc && p.Timestamp <= untilUtc && p.Latitude.HasValue && p.Longitude.HasValue)
                .Where(p => !directOnly || string.Equals(p.SourceChannel, "LoRa/TTN", StringComparison.OrdinalIgnoreCase))
                .Select(p => new TrackPoint
                {
                    TimestampUtc = p.Timestamp,
                    Lat = p.Latitude!.Value,
                    Lon = p.Longitude!.Value,
                    Accuracy = 0,
                    Ble = p.BleCount,
                    Wifi = p.WifiCount,
                    Pax = p.BleCount + p.WifiCount,
                    DeviceId = dev,
                    SourceTopic = p.SourceChannel
                })
                .OrderBy(p => p.TimestampUtc)
                .ToList();

            var isConfiguredTracker = configuredTrackerIds.Contains(dev, StringComparer.OrdinalIgnoreCase);
            if (!isConfiguredTracker)
            {
                if (directPoints.Count > 0)
                {
                    allPoints.AddRange(directPoints);
                }

                continue;
            }

            if (gpsRows.Count == 0)
            {
                if (directPoints.Count > 0)
                {
                    allPoints.AddRange(directPoints);
                }

                continue;
            }

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
                DeviceCounterPoint? current = null;
                if (deviceSeries.Count > 0)
                {
                    current = deviceSeries[s];
                    if (current.Timestamp <= ts && ts - current.Timestamp <= counterJoinWindow)
                    {
                        ble = current.BleCount;
                        wifi = current.WifiCount;
                    }
                }

                if (current != null && !IsGpsSourceAcceptedForCounter(g.SourceTopic, current.SourceChannel))
                {
                    continue;
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
                    DeviceId = dev,
                    SourceTopic = g.SourceTopic
                });
            }
        }

        return Json(DownsampleTrackPoints(DeduplicateTrackPoints(allPoints.OrderBy(p => p.TimestampUtc).ToList()), effectiveMinutes, fullGpsDetail));
    }


    private static ViewModels.TrackingHaTrackerCardViewModel ToTrackerCard(string label, PhoneTrackerStatus status, DateTimeOffset nowUtc, bool isPrimary)
    {
        var age = status.LastUpdatedUtc.HasValue ? nowUtc - status.LastUpdatedUtc.Value : (TimeSpan?)null;
        var isStale = status.IsStale(TimeSpan.FromMinutes(2), nowUtc);
        return new ViewModels.TrackingHaTrackerCardViewModel
        {
            Label = label,
            IsPrimary = isPrimary,
            IsConfigured = status.IsConfigured,
            EntityId = status.EntityId,
            State = string.IsNullOrWhiteSpace(status.State) ? "-" : status.State,
            LastUpdatedLocal = status.LastUpdatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-",
            LastUpdatedUtc = status.LastUpdatedUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-",
            Age = age.HasValue ? $"{Math.Round(age.Value.TotalSeconds)} s" : "-",
            Latitude = status.Latitude,
            Longitude = status.Longitude,
            Accuracy = status.Accuracy,
            IsStale = isStale,
            Message = status.Message
        };
    }

    private static bool IsGpsSourceAcceptedForCounter(string? sourceTopic, string? sourceChannel)
    {
        if (string.IsNullOrWhiteSpace(sourceChannel))
        {
            return true;
        }

        if (sourceChannel.Contains("LoRa/TTN", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (sourceChannel.Contains("via A52", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(sourceTopic, "phone/gps/a52", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceTopic, "phone/direct/a52", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceTopic, "ha:device_tracker.sm_a528b_star_telefon", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceTopic, "phone/gps", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(sourceTopic, "phone/gps/s23", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceTopic, "phone/direct/s23", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceTopic, "ha:device_tracker.sm_s911b", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceTopic, "phone/gps", StringComparison.OrdinalIgnoreCase);
    }

    private static List<GpsSample> DownsampleGpsSamplesForJoin(List<GpsSample> gpsRows, int effectiveMinutes, bool fullGpsDetail)
    {
        var maxGpsRows = fullGpsDetail
            ? 12000
            : effectiveMinutes >= 10080
                ? 900
                : effectiveMinutes >= 4320
                    ? 1200
                    : effectiveMinutes >= 1440
                        ? 1800
                        : 4000;

        if (gpsRows.Count <= maxGpsRows)
        {
            return gpsRows;
        }

        var ordered = gpsRows
            .OrderBy(g => g.Timestamp)
            .ToList();
        var sampled = new List<GpsSample>(maxGpsRows + 2)
        {
            ordered[0]
        };

        var step = (ordered.Count - 1d) / Math.Max(1, maxGpsRows - 1);
        for (var i = 1; i < maxGpsRows - 1; i++)
        {
            var index = (int)Math.Round(i * step);
            index = Math.Clamp(index, 1, ordered.Count - 2);
            sampled.Add(ordered[index]);
        }

        sampled.Add(ordered[^1]);
        return sampled
            .GroupBy(g => $"{g.Timestamp:O}|{g.Latitude:F6}|{g.Longitude:F6}|{g.SourceTopic}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Timestamp)
            .ToList();
    }

    private static List<TrackPoint> DownsampleTrackPoints(List<TrackPoint> points, int effectiveMinutes, bool fullGpsDetail)
    {
        // Full detail is an explicit on-demand view. Keep a finite cap so a
        // multi-day route remains usable in a mobile browser.
        var maxPoints = fullGpsDetail ? 10000 : effectiveMinutes <= 120 ? 3000 : 1400;
        if (points.Count <= maxPoints)
        {
            return points;
        }

        var ordered = points
            .OrderBy(p => p.TimestampUtc)
            .ToList();
        var grouped = ordered
            .GroupBy(p => p.DeviceId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deviceCount = Math.Max(1, grouped.Count);
        var perDeviceBudget = Math.Max(2, maxPoints / deviceCount);
        var result = new List<TrackPoint>(Math.Min(points.Count, maxPoints + deviceCount * 2));

        foreach (var group in grouped)
        {
            var devicePoints = group.OrderBy(p => p.TimestampUtc).ToList();
            if (devicePoints.Count <= perDeviceBudget)
            {
                result.AddRange(devicePoints);
                continue;
            }

            var sampled = new List<TrackPoint>(perDeviceBudget + 2)
            {
                devicePoints[0]
            };

            var step = (devicePoints.Count - 1d) / Math.Max(1, perDeviceBudget - 1);
            for (var i = 1; i < perDeviceBudget - 1; i++)
            {
                var index = (int)Math.Round(i * step);
                index = Math.Clamp(index, 1, devicePoints.Count - 2);
                sampled.Add(devicePoints[index]);
            }

            sampled.Add(devicePoints[^1]);
            result.AddRange(sampled
                .GroupBy(p => $"{p.TimestampUtc:O}|{p.Lat:F6}|{p.Lon:F6}|{p.DeviceId}|{p.Ble}|{p.Wifi}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.TimestampUtc));
        }

        if (result.Count <= maxPoints)
        {
            return result
                .OrderBy(p => p.TimestampUtc)
                .ToList();
        }

        var trimmed = new List<TrackPoint>(maxPoints + 2)
        {
            result[0]
        };

        var trimmedStep = (result.Count - 1d) / Math.Max(1, maxPoints - 1);
        for (var i = 1; i < maxPoints - 1; i++)
        {
            var index = (int)Math.Round(i * trimmedStep);
            index = Math.Clamp(index, 1, result.Count - 2);
            trimmed.Add(result[index]);
        }

        trimmed.Add(result[^1]);
        return trimmed
            .GroupBy(p => $"{p.TimestampUtc:O}|{p.Lat:F6}|{p.Lon:F6}|{p.DeviceId}|{p.Ble}|{p.Wifi}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.TimestampUtc)
            .ToList();
    }

    private static List<TrackPoint> DeduplicateTrackPoints(List<TrackPoint> points)
    {
        const double coordinateTolerance = 0.000003; // about 0.33 m
        var collapseWindow = TimeSpan.FromSeconds(4);
        var result = new List<TrackPoint>(points.Count);

        foreach (var point in points)
        {
            var previous = result.LastOrDefault();
            if (previous != null &&
                string.Equals(previous.DeviceId, point.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                previous.Ble == point.Ble &&
                previous.Wifi == point.Wifi &&
                Math.Abs(previous.Lat - point.Lat) <= coordinateTolerance &&
                Math.Abs(previous.Lon - point.Lon) <= coordinateTolerance &&
                point.TimestampUtc - previous.TimestampUtc <= collapseWindow)
            {
                // Collapse only very tight bursts; keep slower stationary updates visible on the map.
                previous.TimestampUtc = point.TimestampUtc;
                previous.Accuracy = point.Accuracy;
                continue;
            }

            result.Add(point);
        }

        return result;
    }

    private sealed class DeviceCounterPoint
    {
        public string DeviceId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public int BleCount { get; set; }
        public int WifiCount { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public string? SourceChannel { get; set; }
    }

    private sealed class FallbackGpsPoint
    {
        public DateTime Timestamp { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int? Accuracy { get; set; }
        public string? SourceTopic { get; set; }
    }

    private async Task<ViewModels.TrackingViewModel> BuildViewModelAsync(
        string pageTitle,
        string[]? requestedDeviceIds,
        IReadOnlyCollection<string> defaultDeviceIds,
        bool restrictToDefaultDevices,
        string allDevicesLabel,
        string emptyStateMessage,
        int requestedMinutes,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var dbDeviceOptions = _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .Select(d => new DeviceOptionViewModel
            {
                DeviceId = d.DeviceId,
                DisplayName = string.IsNullOrWhiteSpace(d.Name) ? d.DeviceId : d.Name
            })
            .ToList();

        var allKnownIds = dbDeviceOptions
            .Select(d => d.DeviceId)
            .Concat(_trackStore.GetDevices())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var deviceLookup = allKnownIds
            .Select(id => NormalizeTrackingDeviceId(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                id => id,
                id => new DeviceOptionViewModel
                {
                    DeviceId = id,
                    DisplayName = ResolveTrackingDisplayName(id)
                },
                StringComparer.OrdinalIgnoreCase);

        var knownDevices = allKnownIds
            .Select(NormalizeTrackingDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => deviceLookup[id].DisplayName)
            .ToList();

        var effectiveDefaultIds = defaultDeviceIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeTrackingDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => deviceLookup.TryGetValue(id, out var option) ? option.DisplayName : id)
            .ToList();

        var visibleDevices = restrictToDefaultDevices
            ? effectiveDefaultIds
                .Concat(knownDevices)
                .Where(id => effectiveDefaultIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id)
                .ToList()
            : knownDevices;

        var requested = requestedDeviceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeTrackingDeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (restrictToDefaultDevices)
        {
            requested = requested
                .Where(id => effectiveDefaultIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var (effectiveFromUtc, effectiveToUtc, effectiveMinutes) = ResolveTimeWindow(requestedMinutes, fromUtc, toUtc);
        var localFrom = effectiveFromUtc.ToLocalTime();
        var localTo = effectiveToUtc.ToLocalTime();
        var rangeHours = (int)Math.Round((effectiveToUtc - effectiveFromUtc).TotalHours);
        if (rangeHours <= 0)
        {
            rangeHours = 6;
        }

        var autoModeEnabled = _configuration.GetValue("DirectGps:AutoModeEnabled", false);
        var selectedGpsPhone = _uiFeatureSettingsService.GetSelectedGpsPhone();
        var primaryTrackerEntity = ResolveAutoModeTrackerEntityId();
        var fallbackTrackerEntity = string.Equals(primaryTrackerEntity, "S23", StringComparison.OrdinalIgnoreCase)
            ? "A52"
            : "S23";
        var trackerStatus = await _phoneGpsService.GetTrackerStatusAsync(primaryTrackerEntity, CancellationToken.None);
        var secondaryTrackerStatus = await _phoneGpsService.GetTrackerStatusAsync(fallbackTrackerEntity, CancellationToken.None);
        var trackerNowUtc = DateTimeOffset.UtcNow;
        var trackerAge = trackerStatus.LastUpdatedUtc.HasValue ? trackerNowUtc - trackerStatus.LastUpdatedUtc.Value : (TimeSpan?)null;
        var trackerIsStale = trackerStatus.IsStale(TimeSpan.FromMinutes(2), trackerNowUtc);
        var trackerCards = new List<ViewModels.TrackingHaTrackerCardViewModel>
        {
            ToTrackerCard("Primary", trackerStatus, trackerNowUtc, true),
            ToTrackerCard("Fallback", secondaryTrackerStatus, trackerNowUtc, false)
        };
        var transportCards = await BuildTransportCardsAsync(visibleDevices, trackerNowUtc, CancellationToken.None);
        var activeTrackerCard = trackerCards
            .OrderBy(card => card.IsStale ? 1 : 0)
            .ThenBy(card => card.IsPrimary ? 0 : 1)
            .FirstOrDefault();
        var gpsRowsInWindow = await _db.GpsSamples
            .AsNoTracking()
            .Where(g => g.Timestamp >= effectiveFromUtc && g.Timestamp <= effectiveToUtc)
            .OrderBy(g => g.Timestamp)
            .ToListAsync(CancellationToken.None);
        var gpsSampleCountInWindow = gpsRowsInWindow.Count;
        var gpsJumpDiagnostics = BuildGpsWindowDiagnostics(gpsRowsInWindow);
        var gpsRowsForDiagnostics = gpsJumpDiagnostics.FilteredRows.Count > 0
            ? gpsJumpDiagnostics.FilteredRows
            : gpsRowsInWindow;
        var gpsFilteredSampleCountInWindow = gpsRowsForDiagnostics.Count;
        var gpsUniqueCoordinateCountInWindow = gpsRowsForDiagnostics
            .Select(g => $"{Math.Round(g.Latitude, 6):F6}|{Math.Round(g.Longitude, 6):F6}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var gpsMovementChangeCountInWindow = CountGpsMovementChanges(gpsRowsForDiagnostics);
        var gpsWindowPrimaryTopic = gpsRowsForDiagnostics
            .GroupBy(g => string.IsNullOrWhiteSpace(g.SourceTopic) ? "-" : g.SourceTopic!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "-";
        var homeStates = ResolveConfiguredAutoModeHomeStates();
        var trackerReportsHome = homeStates.Any(state => string.Equals(state, trackerStatus.State, StringComparison.OrdinalIgnoreCase));
        var effectiveMode = _trackerModeOverrideService.GetMode() ?? (trackerReportsHome ? "home" : "away");
        var configuredHomeInterval = ResolveConfiguredAutoModeIntervalSeconds(isHome: true);
        var configuredAwayInterval = ResolveConfiguredAutoModeIntervalSeconds(isHome: false);

        return new ViewModels.TrackingViewModel
        {
            PageTitle = pageTitle,
            Devices = visibleDevices
                .Select(id => deviceLookup.TryGetValue(id, out var option)
                    ? option
                    : new DeviceOptionViewModel { DeviceId = id, DisplayName = id })
                .ToList(),
            SelectedDeviceIds = requested,
            DefaultDeviceIds = effectiveDefaultIds,
            RestrictToDefaultDevices = restrictToDefaultDevices,
            AllDevicesLabel = allDevicesLabel,
            EmptyStateMessage = emptyStateMessage,
            GpsDisplayMode = _gpsDisplaySettingsService.GetMode().ToString(),
            ForceGpsConfigured = _phoneGpsService.IsConfigured,
            HaTrackerConfigured = trackerStatus.IsConfigured,
            HaTrackerEntityId = trackerStatus.EntityId,
            HaTrackerState = string.IsNullOrWhiteSpace(trackerStatus.State) ? "-" : trackerStatus.State,
            HaTrackerLastUpdatedLocal = trackerStatus.LastUpdatedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "-",
            HaTrackerLastUpdatedUtc = trackerStatus.LastUpdatedUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "-",
            HaTrackerAge = trackerAge.HasValue ? $"{Math.Round(trackerAge.Value.TotalSeconds)} s" : "-",
            HaTrackerLatitude = trackerStatus.Latitude,
            HaTrackerLongitude = trackerStatus.Longitude,
            HaTrackerAccuracy = trackerStatus.Accuracy,
            HaTrackerIsStale = trackerIsStale,
            HaTrackers = trackerCards,
            GpsAutoModeEnabled = autoModeEnabled,
            TrackerCurrentMode = effectiveMode,
            TrackerModeIsManualOverride = _trackerModeOverrideService.GetMode() != null,
            HomeIntervalSeconds = configuredHomeInterval,
            AwayIntervalSeconds = configuredAwayInterval,
            AutoModeTrackerEntityId = primaryTrackerEntity,
            AutoModeHomeStatesSummary = homeStates.Count == 0 ? "-" : string.Join(", ", homeStates),
            ManualHomeIntervalSeconds = configuredHomeInterval,
            InitialMinutes = effectiveMinutes,
            InitialFromLocal = localFrom.ToString("yyyy-MM-ddTHH:mm"),
            InitialToLocal = localTo.ToString("yyyy-MM-ddTHH:mm"),
            InitialRangeHours = rangeHours,
            InitialUseCustomRange = fromUtc.HasValue || toUtc.HasValue,
            SelectedGpsPhone = _uiFeatureSettingsService.GetSelectedGpsPhone(),
            ActiveAutoModeTrackerEntityId = activeTrackerCard?.EntityId ?? "-",
            ActiveAutoModeTrackerState = activeTrackerCard?.State ?? "-",
            ActiveAutoModeTrackerAge = activeTrackerCard?.Age ?? "-",
            GpsSampleCountInWindow = gpsSampleCountInWindow,
            GpsFilteredSampleCountInWindow = gpsFilteredSampleCountInWindow,
            GpsUniqueCoordinateCountInWindow = gpsUniqueCoordinateCountInWindow,
            GpsMovementChangeCountInWindow = gpsMovementChangeCountInWindow,
            GpsHiddenJumpCountInWindow = gpsJumpDiagnostics.HiddenJumps.Count,
            GpsWindowPrimaryTopic = gpsWindowPrimaryTopic,
            GpsLastRawPointSummary = gpsJumpDiagnostics.LastRawPointSummary,
            GpsLastAcceptedPointSummary = gpsJumpDiagnostics.LastAcceptedPointSummary,
            GpsLastHiddenJumpSummary = gpsJumpDiagnostics.LastHiddenJumpSummary,
            TransportCards = transportCards
        };
    }

    private static GpsWindowDiagnostics BuildGpsWindowDiagnostics(IEnumerable<GpsSample> gpsRows)
    {
        var orderedRows = gpsRows
            .OrderBy(g => g.Timestamp)
            .ToList();
        var filter = FilterUnconfirmedGpsJumps(orderedRows);

        return new GpsWindowDiagnostics
        {
            FilteredRows = filter.FilteredRows,
            HiddenJumps = filter.HiddenJumps,
            LastRawPointSummary = DescribeGpsPoint(orderedRows.LastOrDefault()),
            LastAcceptedPointSummary = DescribeGpsPoint(filter.FilteredRows.LastOrDefault()),
            LastHiddenJumpSummary = filter.HiddenJumps.Count == 0
                ? "-"
                : DescribeHiddenJump(filter.HiddenJumps[^1])
        };
    }

    private static GpsJumpFilterResult FilterUnconfirmedGpsJumps(IEnumerable<GpsSample> gpsRows)
    {
        const double jumpDistanceMeters = 3000d;
        const double confirmationRadiusMeters = 300d;
        const double confirmationWindowMinutes = 15d;

        var orderedRows = gpsRows
            .OrderBy(g => g.Timestamp)
            .ToList();
        if (orderedRows.Count <= 1)
        {
            return new GpsJumpFilterResult(orderedRows, new List<GpsJumpCandidateInfo>());
        }

        var filteredRows = new List<GpsSample> { orderedRows[0] };
        var hiddenJumps = new List<GpsJumpCandidateInfo>();

        for (var i = 1; i < orderedRows.Count; i++)
        {
            var current = orderedRows[i];
            var previousAccepted = filteredRows[^1];
            var distanceMeters = HaversineMeters(previousAccepted.Latitude, previousAccepted.Longitude, current.Latitude, current.Longitude);
            var currentIsHomeAssistant = current.SourceTopic?.StartsWith("ha:", StringComparison.OrdinalIgnoreCase) ?? false;
            if (distanceMeters < jumpDistanceMeters || currentIsHomeAssistant)
            {
                filteredRows.Add(current);
                continue;
            }

            var confirmation = orderedRows
                .Skip(i + 1)
                .FirstOrDefault(next =>
                    string.Equals(next.SourceTopic ?? string.Empty, current.SourceTopic ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
                    (next.Timestamp - current.Timestamp).TotalMinutes <= confirmationWindowMinutes &&
                    HaversineMeters(current.Latitude, current.Longitude, next.Latitude, next.Longitude) <= confirmationRadiusMeters);

            if (confirmation != null)
            {
                filteredRows.Add(current);
                continue;
            }

            hiddenJumps.Add(new GpsJumpCandidateInfo
            {
                Timestamp = current.Timestamp,
                SourceTopic = string.IsNullOrWhiteSpace(current.SourceTopic) ? "-" : current.SourceTopic!,
                Latitude = current.Latitude,
                Longitude = current.Longitude,
                Accuracy = current.Accuracy,
                PreviousLatitude = previousAccepted.Latitude,
                PreviousLongitude = previousAccepted.Longitude,
                DistanceMeters = distanceMeters
            });
        }

        return new GpsJumpFilterResult(filteredRows, hiddenJumps);
    }

    private static string DescribeGpsPoint(GpsSample? sample)
    {
        if (sample == null)
        {
            return "-";
        }

        var topic = string.IsNullOrWhiteSpace(sample.SourceTopic) ? "-" : sample.SourceTopic!;
        var accuracy = sample.Accuracy?.ToString() ?? "-";
        return $"{sample.Timestamp:yyyy-MM-dd HH:mm:ss UTC} | {topic} | {sample.Latitude:F6}, {sample.Longitude:F6} | acc {accuracy}m";
    }

    private static string DescribeHiddenJump(GpsJumpCandidateInfo jump)
    {
        var accuracy = jump.Accuracy?.ToString() ?? "-";
        return $"{jump.Timestamp:yyyy-MM-dd HH:mm:ss UTC} | {jump.SourceTopic} | {jump.Latitude:F6}, {jump.Longitude:F6} | hidden {Math.Round(jump.DistanceMeters)}m from previous | acc {accuracy}m";
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMeters = 6371000d;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private static int CountGpsMovementChanges(IEnumerable<GpsSample> gpsRows)
    {
        const double coordinateTolerance = 0.00001;
        double? previousLat = null;
        double? previousLon = null;
        var changes = 0;

        foreach (var row in gpsRows.OrderBy(g => g.Timestamp))
        {
            if (!previousLat.HasValue || !previousLon.HasValue)
            {
                previousLat = row.Latitude;
                previousLon = row.Longitude;
                continue;
            }

            if (Math.Abs(previousLat.Value - row.Latitude) > coordinateTolerance ||
                Math.Abs(previousLon.Value - row.Longitude) > coordinateTolerance)
            {
                changes++;
                previousLat = row.Latitude;
                previousLon = row.Longitude;
            }
        }

        return changes;
    }

    private sealed class GpsWindowDiagnostics
    {
        public List<GpsSample> FilteredRows { get; init; } = new();
        public List<GpsJumpCandidateInfo> HiddenJumps { get; init; } = new();
        public string LastRawPointSummary { get; init; } = "-";
        public string LastAcceptedPointSummary { get; init; } = "-";
        public string LastHiddenJumpSummary { get; init; } = "-";
    }

    private sealed class GpsJumpCandidateInfo
    {
        public DateTime Timestamp { get; init; }
        public string SourceTopic { get; init; } = "-";
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public int? Accuracy { get; init; }
        public double PreviousLatitude { get; init; }
        public double PreviousLongitude { get; init; }
        public double DistanceMeters { get; init; }
    }

    private sealed record GpsJumpFilterResult(List<GpsSample> FilteredRows, List<GpsJumpCandidateInfo> HiddenJumps);

    private async Task<List<TrackingTransportCardViewModel>> BuildTransportCardsAsync(
        IReadOnlyList<string> deviceIds,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        if (deviceIds.Count == 0)
        {
            return new List<TrackingTransportCardViewModel>();
        }

        var rows = await _db.PaxSamples
            .AsNoTracking()
            .Where(p => p.Device.DeviceId != null && deviceIds.Contains(p.Device.DeviceId))
            .OrderByDescending(p => p.Timestamp)
            .Select(p => new
            {
                p.Device.DeviceId,
                p.Timestamp,
                p.SourceChannel,
                p.BleCount,
                p.WifiCount
            })
            .ToListAsync(ct);

        return rows
            .GroupBy(row => row.DeviceId!)
            .Select(group =>
            {
                var latest = group.First();
                var selected = group.FirstOrDefault(row =>
                    !string.IsNullOrWhiteSpace(row.SourceChannel) &&
                    row.SourceChannel.Contains("WiFi/HTTP", StringComparison.OrdinalIgnoreCase) &&
                    latest.Timestamp - row.Timestamp <= TimeSpan.FromSeconds(30)) ?? latest;

                var age = nowUtc - new DateTimeOffset(DateTime.SpecifyKind(selected.Timestamp, DateTimeKind.Utc));
                var ackId = ExtractTransportToken(selected.SourceChannel, "ack=");
                var ackAge = ExtractTransportToken(selected.SourceChannel, "ackAge=");
                var ssid = ExtractTransportToken(selected.SourceChannel, "ssid=");
                var failures = ExtractTransportToken(selected.SourceChannel, "fail=");

                return new TrackingTransportCardViewModel
                {
                    DeviceId = selected.DeviceId ?? "-",
                    DisplayName = ResolveTrackingDisplayName(selected.DeviceId ?? "-"),
                    LastSeenAge = $"{Math.Round(age.TotalSeconds)} s",
                    IsStale = age > TimeSpan.FromSeconds(30),
                    PathLabel = ResolveTransportPathLabel(selected.SourceChannel),
                    SourceChannel = string.IsNullOrWhiteSpace(selected.SourceChannel) ? "-" : selected.SourceChannel,
                    WifiSsid = string.IsNullOrWhiteSpace(ssid) ? "-" : ssid,
                    AckId = string.IsNullOrWhiteSpace(ackId) ? "-" : ackId,
                    AckAge = string.IsNullOrWhiteSpace(ackAge) ? "-" : ackAge,
                    ConsecutiveFailures = string.IsNullOrWhiteSpace(failures) ? "-" : failures,
                    Ble = selected.BleCount,
                    Wifi = selected.WifiCount
                };
            })
            .OrderBy(card => card.DisplayName)
            .ToList();
    }

    private static string ResolveTransportPathLabel(string? sourceChannel)
    {
        if (string.IsNullOrWhiteSpace(sourceChannel))
        {
            return "-";
        }

        if (sourceChannel.Contains("LoRa/TTN", StringComparison.OrdinalIgnoreCase))
        {
            return "LoRa";
        }

        if (sourceChannel.Contains("WiFi/HTTP via A52", StringComparison.OrdinalIgnoreCase))
        {
            return "WiFi via A52";
        }

        if (sourceChannel.Contains("WiFi/HTTP via S23", StringComparison.OrdinalIgnoreCase))
        {
            return "WiFi via S23";
        }

        if (sourceChannel.Contains("WiFi/HTTP", StringComparison.OrdinalIgnoreCase))
        {
            return "WiFi";
        }

        return sourceChannel;
    }

    private static string? ExtractTransportToken(string? sourceChannel, string tokenPrefix)
    {
        if (string.IsNullOrWhiteSpace(sourceChannel))
        {
            return null;
        }

        var start = sourceChannel.IndexOf(tokenPrefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += tokenPrefix.Length;
        var end = sourceChannel.IndexOfAny(new[] { ' ', ']' }, start);
        if (end < 0)
        {
            end = sourceChannel.Length;
        }

        return sourceChannel[start..end].Trim();
    }

    private static string NormalizeTrackingDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        return deviceId.Trim().ToLowerInvariant() switch
        {
            "heltec-tracker1" => "heltec1-new",
            "paxcounter-heltec1" => "heltec1-new",
            "paxcounter_aa8130d5" => "heltec1-new",
            "heltec1-new" => "heltec1-new",
            "heltec-tracker2" => "heltec2-new",
            "paxcounter-heltec2" => "heltec2-new",
            "paxcounter_b2faeaf2" => "heltec2-new",
            "heltec2-new" => "heltec2-new",
            "paxcounter-tbeam" => "paxcounter-Tbeam",
            "t3-a3-a" => "paxcounter-T3S3A",
            "paxcounter-t3s3a" => "paxcounter-T3S3A",
            "t3-s3-b" => "paxcounter-T3S3B",
            "paxcounter-t3s3b" => "paxcounter-T3S3B",
            _ => deviceId.Trim()
        };
    }

    private static string ResolveTrackingDisplayName(string deviceId)
    {
        return NormalizeTrackingDeviceId(deviceId).ToLowerInvariant() switch
        {
            "heltec1-new" => "Heltec1",
            "heltec2-new" => "Heltec2",
            "paxcounter-t3" => "T3",
            "paxcounter-tbeam" => "T-B",
            "paxcounter-t3s3a" => "S3 A",
            "paxcounter-t3s3b" => "S3 B",
            _ => deviceId
        };
    }

    private static (DateTime sinceUtc, DateTime untilUtc, int effectiveMinutes) ResolveTimeWindow(
        int requestedMinutes,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc)
    {
        var nowUtc = DateTime.UtcNow;
        var clampedMinutes = Math.Clamp(requestedMinutes, 5, 43200);

        if (fromUtc.HasValue || toUtc.HasValue)
        {
            var start = fromUtc?.UtcDateTime;
            var end = toUtc?.UtcDateTime;

            if (!start.HasValue && end.HasValue)
            {
                start = end.Value.AddHours(-6);
            }

            if (!end.HasValue && start.HasValue)
            {
                end = start.Value.AddHours(6);
            }

            start ??= nowUtc.AddMinutes(-clampedMinutes);
            end ??= nowUtc;

            if (end <= start)
            {
                end = start.Value.AddMinutes(5);
            }

            var maxWindow = TimeSpan.FromDays(30);
            if (end.Value - start.Value > maxWindow)
            {
                end = start.Value.Add(maxWindow);
            }

            var minutes = Math.Clamp((int)Math.Ceiling((end.Value - start.Value).TotalMinutes), 5, 43200);
            return (start.Value, end.Value, minutes);
        }

        return (nowUtc.AddMinutes(-clampedMinutes), nowUtc, clampedMinutes);
    }

    private async Task<ViewModels.CrowdEstimateViewModel> BuildCrowdEstimateViewModelAsync(
        string[]? requestedDeviceIds,
        int requestedMinutes,
        CancellationToken ct)
    {
        var configuredTrackerIds = GetConfiguredTrackerDeviceIds()
            .Select(NormalizeTrackingDeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allVisibleDevices = (await _db.Devices
            .AsNoTracking()
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .Select(d => d.DeviceId!)
            .ToListAsync(ct))
            .Select(NormalizeTrackingDeviceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var visibleDeviceIds = (configuredTrackerIds.Count > 0
                ? allVisibleDevices.Where(id => configuredTrackerIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                : allVisibleDevices)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => ResolveTrackingDisplayName(id))
            .ThenBy(id => id)
            .ToList();

        var selectedDeviceIds = requestedDeviceIds?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeTrackingDeviceId)
            .Where(id => visibleDeviceIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();

        if (selectedDeviceIds.Count == 0)
        {
            selectedDeviceIds = visibleDeviceIds.ToList();
        }

        return new ViewModels.CrowdEstimateViewModel
        {
            PageTitle = "Crowd Estimate",
            Devices = visibleDeviceIds
                .Select(id => new DeviceOptionViewModel
                {
                    DeviceId = id,
                    DisplayName = ResolveTrackingDisplayName(id)
                })
                .ToList(),
            SelectedDeviceIds = selectedDeviceIds,
            AllDevicesLabel = "All trackers",
            EmptyStateMessage = visibleDeviceIds.Count == 0
                ? "Configure Tracking:TrackerDeviceIds in appsettings.json or create visible tracker devices first."
                : "Choose one or more trackers to estimate crowd coverage.",
            InitialMinutes = Math.Clamp(requestedMinutes, 30, 43200),
            InitialCellMeters = 20,
            InitialActiveMinutes = 15
        };
    }

    private List<string> GetConfiguredTrackerDeviceIds()
    {
        return _configuration.GetSection("Tracking:TrackerDeviceIds")
            .Get<string[]>()?
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToList()
            ?? new List<string>();
    }

    private string ResolveAutoModeTrackerEntityId()
        => _configuration["DirectGps:AutoModeDeviceTrackerEntityId"] ?? "A52";

    private List<string> ResolveConfiguredAutoModeHomeStates()
    {
        return (_configuration["DirectGps:AutoModeHomeStates"] ?? "home,Paxcounter Zone")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private int ResolveConfiguredAutoModeIntervalSeconds(bool isHome)
    {
        var key = isHome
            ? "DirectGps:AutoModeHomeIntervalSeconds"
            : "DirectGps:AutoModeAwayIntervalSeconds";
        var fallback = isHome ? 30 : 5;
        return Math.Clamp(_configuration.GetValue(key, fallback), 5, 3600);
    }

    public sealed class TrackingIntervalRequest
    {
        public List<string> DeviceIds { get; set; } = new();
    }
}











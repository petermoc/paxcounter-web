using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;
using System.Diagnostics;

namespace PaxCounterWeb.Controllers
{
    public class HomeController : Controller
    {
        private readonly AppDbContext _db;
        private readonly GpsDisplaySettingsService _gpsDisplaySettingsService;
        private readonly DeviceCommandService _deviceCommandService;
        private readonly UiFeatureSettingsService _uiFeatureSettingsService;
        private readonly TrackerHomeModeSettingsService _trackerHomeModeSettingsService;

        public HomeController(AppDbContext db, GpsDisplaySettingsService gpsDisplaySettingsService, DeviceCommandService deviceCommandService, UiFeatureSettingsService uiFeatureSettingsService, TrackerHomeModeSettingsService trackerHomeModeSettingsService)
        {
            _db = db;
            _gpsDisplaySettingsService = gpsDisplaySettingsService;
            _deviceCommandService = deviceCommandService;
            _uiFeatureSettingsService = uiFeatureSettingsService;
            _trackerHomeModeSettingsService = trackerHomeModeSettingsService;
        }

        public async Task<IActionResult> Index(int historyMinutes = 180, string? historyDeviceId = null, string? historyChannelFilter = null, int page = 0, int pageSize = 10, bool pendingOnly = false, bool showHidden = false)
        {
            historyMinutes = Math.Clamp(historyMinutes, 15, 43200);
            page = Math.Max(0, page);
            var allowedPageSizes = new[] { 10, 100, 200 };
            if (!allowedPageSizes.Contains(pageSize))
            {
                pageSize = 10;
            }
            var sinceUtc = DateTime.UtcNow.AddMinutes(-historyMinutes);

            var devicesQuery = _db.Devices.AsQueryable();
            if (!showHidden)
            {
                devicesQuery = devicesQuery.Where(d => !d.IsHidden);
            }

            var devices = await devicesQuery
                .Include(d => d.PaxSamples)
                .ToListAsync();

            var liveSamplesQuery = _db.PaxSamples
                .Include(p => p.Device)
                .Where(p => p.Timestamp >= sinceUtc)
                .Where(p => p.RssiLimit != int.MinValue);

            if (!showHidden)
            {
                liveSamplesQuery = liveSamplesQuery.Where(p => !p.Device.IsHidden);
            }

            if (!string.IsNullOrWhiteSpace(historyDeviceId))
            {
                liveSamplesQuery = liveSamplesQuery.Where(p => p.Device.DeviceId == historyDeviceId);
            }

            if (!string.IsNullOrWhiteSpace(historyChannelFilter))
            {
                switch (historyChannelFilter)
                {
                    case "wifi":
                        liveSamplesQuery = liveSamplesQuery.Where(p => p.SourceChannel != null && p.SourceChannel.Contains("WiFi"));
                        break;
                    case "lora":
                        liveSamplesQuery = liveSamplesQuery.Where(p => p.SourceChannel != null && p.SourceChannel.Contains("LoRa"));
                        break;
                    case "gps":
                        liveSamplesQuery = liveSamplesQuery.Where(p => p.SourceChannel != null && p.SourceChannel.Contains("GPS"));
                        break;
                }
            }

            var historyTotalInWindow = await liveSamplesQuery.CountAsync();
            var totalPages = historyTotalInWindow == 0 ? 1 : (int)Math.Ceiling(historyTotalInWindow / (double)pageSize);
            if (page >= totalPages)
            {
                page = totalPages - 1;
            }

            var liveSamples = await liveSamplesQuery
                .OrderByDescending(p => p.Timestamp)
                .Skip(page * pageSize)
                .Take(pageSize)
                .Select(p => new LiveSampleViewModel
                {
                    Timestamp = p.Timestamp,
                    DeviceId = p.Device.DeviceId,
                    DeviceName = p.Device.Name,
                    WifiCount = p.WifiCount,
                    BleCount = p.BleCount,
                    RssiLimit = p.RssiLimit,
                    BatteryVoltageMv = p.BatteryVoltageMv,
                    BatteryPercent = p.BatteryPercent,
                    Latitude = p.Latitude,
                    Longitude = p.Longitude,
                    Satellites = p.Satellites,
                    Hdop = p.Hdop,
                    Altitude = p.Altitude,
                    SourceChannel = p.SourceChannel,
                    WifiSsid = ExtractWifiSsid(p.SourceChannel)
                })
                .ToListAsync();

            await AttachGpsFromGpsSamplesAsync(liveSamples);

            var latestCommands = await _db.PendingDeviceCommands
                .Include(x => x.Device)
                .Where(x => devices.Select(d => d.Id).Contains(x.DeviceEntityId))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync();

            var commandStatuses = BuildCommandStatuses(latestCommands);
            var recentDeviceCommands = BuildCommandHistory(latestCommands, 30);
            var deviceBleSummaries = BuildDeviceBleSummaries(devices);

            if (pendingOnly)
            {
                devices = devices
                    .Where(d => commandStatuses.TryGetValue(d.Id, out var status) && status.CanCancel)
                    .ToList();
            }

            var showWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts();

            var vm = new HomeViewModel
            {
                Devices = devices,
                LiveSamples = liveSamples,
                HistoryMinutes = historyMinutes,
                HistoryDeviceId = historyDeviceId,
                HistoryChannelFilter = historyChannelFilter,
                HistoryFromUtc = sinceUtc,
                HistoryTotalInWindow = historyTotalInWindow,
                Page = page,
                PageSize = pageSize,
                TotalPages = totalPages,
                GpsDisplayMode = _gpsDisplaySettingsService.GetMode(),
                PendingOnly = pendingOnly,
                ShowHidden = showHidden,
                EnableRlimOtaControls = _uiFeatureSettingsService.GetRlimControlsEnabled(),
                SyncPhoneGpsIntervalFromApp = _uiFeatureSettingsService.GetGpsIntervalSyncEnabled(),
                ShowWifiCounts = showWifiCounts,
                EnableBleScanOtaControls = _uiFeatureSettingsService.GetBleScanControlsEnabled(),
                DeviceCommandStatuses = commandStatuses,
                RecentDeviceCommands = recentDeviceCommands,
                DeviceBleSummaries = deviceBleSummaries,
                HistoryAvailableDevices = devices
                    .Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
                    .Select(d => new DeviceOptionViewModel
                    {
                        DeviceId = NormalizeHeltecAlias(d.DeviceId),
                        DisplayName = ResolveCanonicalDisplayName(d.DeviceId, d.Name)
                    })
                    .DistinctBy(d => d.DeviceId)
                    .OrderBy(d => d.DisplayName)
                    .ToList()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ToggleDeviceHidden(int deviceEntityId, bool hide, bool showHidden = false)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceEntityId);
            if (device == null)
            {
                TempData["CommandError"] = "Device not found.";
                return RedirectToAction(nameof(Index), new { showHidden });
            }

            device.IsHidden = hide;
            await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, HttpContext.RequestAborted);
            TempData["CommandStatus"] = hide
                ? $"Hidden {device.DeviceId}."
                : $"Unhid {device.DeviceId}.";

            return RedirectToAction(nameof(Index), new { showHidden = showHidden || hide });
        }

        [HttpGet("/api/commands/status")]
        public async Task<IActionResult> CommandStatuses()
        {
            var devices = await _db.Devices
                .Where(d => !d.IsHidden)
                .Select(d => new { d.Id, d.DeviceId, d.Name })
                .ToListAsync();

            var latestCommands = await _db.PendingDeviceCommands
                .Include(x => x.Device)
                .Where(x => devices.Select(d => d.Id).Contains(x.DeviceEntityId))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToListAsync();

            var statuses = BuildCommandStatuses(latestCommands)
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        text = kvp.Value.Text,
                        badgeClass = kvp.Value.BadgeClass,
                        canCancel = kvp.Value.CanCancel,
                        requestedDisplay = kvp.Value.RequestedDisplay,
                        deliveryMode = kvp.Value.DeliveryMode,
                        updatedAtUtc = kvp.Value.UpdatedAtUtc
                    });

            var history = BuildCommandHistory(latestCommands, 20)
                .Select(x => new
                {
                    id = x.Id,
                    deviceEntityId = x.DeviceEntityId,
                    deviceId = x.DeviceId,
                    deviceName = x.DeviceName,
                    text = x.Text,
                    badgeClass = x.BadgeClass,
                    requestedDisplay = x.RequestedDisplay,
                    requestedTransport = x.RequestedTransport,
                    createdAtUtc = x.CreatedAtUtc,
                    updatedAtUtc = x.UpdatedAtUtc
                })
                .ToList();

            return Json(new
            {
                generatedAtUtc = DateTime.UtcNow,
                statuses,
                history
            });
        }


        private static Dictionary<int, DeviceBleSummaryViewModel> BuildDeviceBleSummaries(IEnumerable<Device> devices)
        {
            var recentWindowUtc = DateTime.UtcNow.AddMinutes(-5);

            return devices.ToDictionary(
                d => d.Id,
                d =>
                {
                    var samples = d.PaxSamples
                        .Where(x => x.RssiLimit != int.MinValue)
                        .OrderByDescending(x => x.Timestamp)
                        .ToList();

                    var latest = samples.FirstOrDefault();
                    var recent = samples.Where(x => x.Timestamp >= recentWindowUtc).ToList();
                    var lastNonZero = samples.FirstOrDefault(x => x.BleCount > 0);

                    return new DeviceBleSummaryViewModel
                    {
                        LatestBle = latest?.BleCount ?? 0,
                        RecentMaxBle5m = recent.Count == 0 ? 0 : recent.Max(x => x.BleCount),
                        RecentAvgBle5m = recent.Count == 0 ? 0 : recent.Average(x => x.BleCount),
                        LastNonZeroBle = lastNonZero?.BleCount,
                        LastNonZeroAtUtc = lastNonZero?.Timestamp
                    };
                });
        }

        private static Dictionary<int, DeviceCommandStatusViewModel> BuildCommandStatuses(IEnumerable<PendingDeviceCommand> commands)
        {
            return commands
                .GroupBy(x => x.DeviceEntityId)
                .ToDictionary(
                    g => g.Key,
                    g => BuildStatus(g.First()));
        }

        private static List<DeviceCommandHistoryItemViewModel> BuildCommandHistory(IEnumerable<PendingDeviceCommand> commands, int maxItems)
        {
            return commands
                .OrderByDescending(x => x.ConsumedAtUtc ?? x.LoRaAttemptedAtUtc ?? x.CreatedAtUtc)
                .Take(maxItems)
                .Select(x =>
                {
                    var status = BuildStatus(x);
                    return new DeviceCommandHistoryItemViewModel
                    {
                        Id = x.Id,
                        DeviceEntityId = x.DeviceEntityId,
                        DeviceId = x.Device?.DeviceId ?? string.Empty,
                        DeviceName = string.IsNullOrWhiteSpace(x.Device?.Name) ? (x.Device?.DeviceId ?? string.Empty) : x.Device.Name,
                        Text = status.Text,
                        BadgeClass = status.BadgeClass,
                        RequestedDisplay = status.RequestedDisplay,
                        RequestedTransport = x.RequestedTransport,
                        CreatedAtUtc = x.CreatedAtUtc,
                        UpdatedAtUtc = status.UpdatedAtUtc
                    };
                })
                .ToList();
        }

        private static DeviceCommandStatusViewModel BuildStatus(PendingDeviceCommand command)
        {
            var requestedDisplay = FormatRequestedDisplay(command);

            if (command.ConsumedAtUtc != null)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"WiFi delivered {requestedDisplay}",
                    BadgeClass = "bg-info text-dark",
                    CanCancel = false,
                    RequestedDisplay = requestedDisplay,
                    DeliveryMode = "WiFi",
                    UpdatedAtUtc = command.ConsumedAtUtc
                };
            }

            if (command.LoRaAttemptedAtUtc == null)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"Queued {requestedDisplay}",
                    BadgeClass = "bg-secondary",
                    CanCancel = true,
                    RequestedDisplay = requestedDisplay,
                    DeliveryMode = "Queued",
                    UpdatedAtUtc = command.CreatedAtUtc
                };
            }

            if (command.LoRaAccepted)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"LoRa accepted {requestedDisplay}",
                    BadgeClass = "bg-success",
                    CanCancel = true,
                    RequestedDisplay = requestedDisplay,
                    DeliveryMode = "LoRa",
                    UpdatedAtUtc = command.LoRaAttemptedAtUtc
                };
            }

            return new DeviceCommandStatusViewModel
            {
                Text = $"Waiting on WiFi ({requestedDisplay})",
                BadgeClass = "bg-warning text-dark",
                CanCancel = true,
                RequestedDisplay = requestedDisplay,
                DeliveryMode = "WiFi pending",
                UpdatedAtUtc = command.LoRaAttemptedAtUtc
            };
        }

        private static string FormatRequestedDisplay(PendingDeviceCommand command)
        {
            return command.CommandName switch
            {
                "set_rssi" when command.RequestedSeconds == 0 => "RLIM off",
                "set_rssi" => $"RLIM {command.RequestedSeconds} dBm",
                "set_ble_scan_interval" => $"BLE scan {command.RequestedSeconds} ms",
                _ => $"{command.RequestedSeconds}s"
            };
        }
        private static string? ExtractWifiSsid(string? sourceChannel)
        {
            const string marker = " via ";
            if (string.IsNullOrWhiteSpace(sourceChannel))
            {
                return null;
            }

            var idx = sourceChannel.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            var ssid = sourceChannel[(idx + marker.Length)..].Trim();
            return string.IsNullOrWhiteSpace(ssid) ? null : ssid;
        }

        private async Task AttachGpsFromGpsSamplesAsync(List<LiveSampleViewModel> liveSamples)
        {
            var withoutGps = liveSamples
                .Where(x => !x.Latitude.HasValue || !x.Longitude.HasValue)
                .OrderBy(x => x.Timestamp)
                .ToList();

            if (withoutGps.Count == 0)
            {
                return;
            }

            var oldest = withoutGps[0].Timestamp.AddHours(-2);
            var gpsRows = await _db.GpsSamples
                .Where(g => g.Timestamp >= oldest)
                .OrderBy(g => g.Timestamp)
                .Select(g => new { g.Timestamp, g.Latitude, g.Longitude, g.SourceTopic })
                .ToListAsync();

            if (gpsRows.Count == 0)
            {
                return;
            }

            var maxAge = TimeSpan.FromMinutes(30);

            foreach (var row in withoutGps)
            {
                var preferredTopics = ResolvePreferredGpsTopics(row.SourceChannel);
                var candidate = gpsRows
                    .Where(g => g.Timestamp <= row.Timestamp && row.Timestamp - g.Timestamp <= maxAge)
                    .OrderBy(g => PreferredTopicRank(g.SourceTopic, preferredTopics))
                    .ThenByDescending(g => g.Timestamp)
                    .FirstOrDefault();

                if (candidate == null)
                {
                    continue;
                }

                row.Latitude = candidate.Latitude;
                row.Longitude = candidate.Longitude;
                row.SourceChannel = string.IsNullOrWhiteSpace(row.SourceChannel)
                    ? $"GPS join ({DescribeGpsSource(candidate.SourceTopic)})"
                    : $"{row.SourceChannel} + GPS join ({DescribeGpsSource(candidate.SourceTopic)})";
            }
        }

        private static string[] ResolvePreferredGpsTopics(string? sourceChannel)
        {
            if (!string.IsNullOrWhiteSpace(sourceChannel) && sourceChannel.Contains("LoRa/TTN", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "lora/direct" };
            }

            if (!string.IsNullOrWhiteSpace(sourceChannel) && sourceChannel.Contains("via A52", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "phone/direct/a52", "phone/direct/s23", "phone/gps/a52", "ha:device_tracker.sm_a528b_star_telefon", "phone/gps", "phone/gps/s23", "ha:device_tracker.sm_s911b" };
            }

            return new[] { "phone/direct/s23", "phone/direct/a52", "phone/gps/s23", "ha:device_tracker.sm_s911b", "phone/gps", "phone/gps/a52", "ha:device_tracker.sm_a528b_star_telefon" };
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
                _ => "MQTT"
            };
        }

        [HttpGet("/live/{deviceId}")]
        public async Task<IActionResult> Live(string deviceId)
        {
            var canonicalDeviceId = NormalizeHeltecAlias(deviceId);
            var aliasIds = GetHeltecAliases(canonicalDeviceId);
            var device = await _db.Devices
                .Where(d => aliasIds.Contains(d.DeviceId))
                .OrderByDescending(d => d.DeviceId == canonicalDeviceId)
                .FirstOrDefaultAsync();

            if (device == null)
            {
                return NotFound($"Device '{deviceId}' not found");
            }

            var aliasDeviceEntityIds = await _db.Devices
                .Where(d => aliasIds.Contains(d.DeviceId))
                .Select(d => d.Id)
                .ToListAsync();

            var nowUtc = DateTime.UtcNow;
            var dayStartUtc = nowUtc.Date;
            var lastSeenUtc = await _db.PaxSamples
                .Where(p => aliasDeviceEntityIds.Contains(p.DeviceId))
                .OrderByDescending(p => p.Timestamp)
                .Select(p => (DateTime?)p.Timestamp)
                .FirstOrDefaultAsync();

            var latest = await _db.PaxSamples
                .Where(p => aliasDeviceEntityIds.Contains(p.DeviceId))
                .OrderByDescending(p => p.Timestamp)
                .Select(p => new { p.BleCount, p.WifiCount })
                .FirstOrDefaultAsync();

            var avgBle15m = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= nowUtc.AddMinutes(-15))
                .Select(p => (double?)p.BleCount)
                .AverageAsync() ?? 0d;

            var peakBleToday = await _db.PaxSamples
                .Where(p => aliasDeviceEntityIds.Contains(p.DeviceId) && p.Timestamp >= dayStartUtc)
                .Select(p => (int?)p.BleCount)
                .MaxAsync() ?? 0;

            var vm = new LiveDevicePageViewModel
            {
                DeviceId = canonicalDeviceId,
                DeviceName = ResolveCanonicalDisplayName(device.DeviceId, device.Name),
                LatestBle = latest?.BleCount ?? 0,
                LatestWifi = latest?.WifiCount ?? 0,
                ShowWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts(),
                AvgBle15m = avgBle15m,
                PeakBleToday = peakBleToday,
                LastSeenUtc = lastSeenUtc
            };

            return View(vm);
        }

        [HttpGet("/api/metrics/ble")]
        public async Task<IActionResult> BleMetrics([FromQuery] string deviceId, [FromQuery] int minutes = 180, [FromQuery] int bucketMinutes = 5)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return BadRequest("deviceId is required");
            }

            minutes = Math.Clamp(minutes, 15, 43200);
            bucketMinutes = Math.Clamp(bucketMinutes, 1, 60);

            var canonicalDeviceId = NormalizeHeltecAlias(deviceId);
            var aliasIds = GetHeltecAliases(canonicalDeviceId);
            var aliasDeviceEntityIds = await _db.Devices
                .Where(d => aliasIds.Contains(d.DeviceId))
                .Select(d => d.Id)
                .ToListAsync();

            if (aliasDeviceEntityIds.Count == 0)
            {
                return NotFound($"Device '{deviceId}' not found");
            }

            var nowUtc = DateTime.UtcNow;
            var sinceUtc = nowUtc.AddMinutes(-minutes);
            var dayStartUtc = nowUtc.Date;

            var rawSamples = await _db.PaxSamples
                .Where(p => aliasDeviceEntityIds.Contains(p.DeviceId) && p.Timestamp >= sinceUtc)
                .OrderBy(p => p.Timestamp)
                .Select(p => new { p.Timestamp, p.BleCount, p.WifiCount })
                .ToListAsync();

            var latest = rawSamples.LastOrDefault();
            var avgBle15m = rawSamples
                .Where(p => p.Timestamp >= nowUtc.AddMinutes(-15))
                .Select(p => (double)p.BleCount)
                .DefaultIfEmpty(0d)
                .Average();

            var peakBleToday = await _db.PaxSamples
                .Where(p => aliasDeviceEntityIds.Contains(p.DeviceId) && p.Timestamp >= dayStartUtc)
                .Select(p => (int?)p.BleCount)
                .MaxAsync() ?? 0;

            var bucketTicks = TimeSpan.FromMinutes(bucketMinutes).Ticks;
            var points = rawSamples
                .GroupBy(p => new DateTime(((long)(p.Timestamp.Ticks / bucketTicks)) * bucketTicks, DateTimeKind.Utc))
                .OrderBy(g => g.Key)
                .Select(g => new BleSeriesPointViewModel
                {
                    BucketUtc = g.Key,
                    BleAvg = Math.Round(g.Average(x => x.BleCount), 2),
                    BleMax = g.Max(x => x.BleCount),
                    Samples = g.Count()
                })
                .ToList();

            var response = new BleMetricsResponseViewModel
            {
                DeviceId = canonicalDeviceId,
                Minutes = minutes,
                BucketMinutes = bucketMinutes,
                LatestBle = latest?.BleCount ?? 0,
                LatestWifi = latest?.WifiCount ?? 0,
                ShowWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts(),
                AvgBle15m = Math.Round(avgBle15m, 2),
                PeakBleToday = peakBleToday,
                LastSeenUtc = latest?.Timestamp,
                Points = points
            };

            return Json(response);
        }

        [HttpGet("/compare")]
        public async Task<IActionResult> Compare()
        {
            var deviceIds = await _db.Devices
                .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
                .Select(d => new DeviceOptionViewModel
                {
                    DeviceId = NormalizeHeltecAlias(d.DeviceId),
                    DisplayName = ResolveCanonicalDisplayName(d.DeviceId, d.Name)
                })
                .ToListAsync();

            var vm = new ComparePageViewModel
            {
                DeviceIds = deviceIds
                    .DistinctBy(d => d.DeviceId)
                    .OrderBy(d => d.DisplayName)
                    .ToList()
            };

            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetReportingInterval(int deviceEntityId, int seconds)
        {
            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceEntityId && !d.IsHidden);
            if (device == null)
            {
                TempData["CommandError"] = "Device not found.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _deviceCommandService.QueueSendCycleAsync(device, seconds, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            UpdateTrackerModeFromInterval(seconds);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetReportingIntervalForAll(int seconds)
        {
            var result = await _deviceCommandService.QueueSendCycleForAllAsync(seconds, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            UpdateTrackerModeFromInterval(seconds);
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRssiLimit(int deviceEntityId, int rssiLimit)
        {
            if (!_uiFeatureSettingsService.GetRlimControlsEnabled())
            {
                TempData["CommandError"] = "RLIM OTA controls are disabled in Settings.";
                return RedirectToAction(nameof(Index));
            }

            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceEntityId && !d.IsHidden);
            if (device == null)
            {
                TempData["CommandError"] = "Device not found.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _deviceCommandService.QueueRssiLimitAsync(device, rssiLimit, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetRssiLimitForAll(int rssiLimit)
        {
            if (!_uiFeatureSettingsService.GetRlimControlsEnabled())
            {
                TempData["CommandError"] = "RLIM OTA controls are disabled in Settings.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _deviceCommandService.QueueRssiLimitForAllAsync(rssiLimit, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBleScanInterval(int deviceEntityId, int intervalMilliseconds)
        {
            if (!_uiFeatureSettingsService.GetBleScanControlsEnabled())
            {
                TempData["CommandError"] = "BLE scan OTA controls are disabled in Settings.";
                return RedirectToAction(nameof(Index));
            }

            var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == deviceEntityId && !d.IsHidden);
            if (device == null)
            {
                TempData["CommandError"] = "Device not found.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _deviceCommandService.QueueBleScanIntervalAsync(device, intervalMilliseconds, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBleScanIntervalForAll(int intervalMilliseconds)
        {
            if (!_uiFeatureSettingsService.GetBleScanControlsEnabled())
            {
                TempData["CommandError"] = "BLE scan OTA controls are disabled in Settings.";
                return RedirectToAction(nameof(Index));
            }

            var result = await _deviceCommandService.QueueBleScanIntervalForAllAsync(intervalMilliseconds, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelPendingCommand(int deviceEntityId)
        {
            var result = await _deviceCommandService.CancelPendingAsync(deviceEntityId, HttpContext.RequestAborted);
            if (result.Success)
            {
                TempData["CommandStatus"] = result.Message;
            }
            else
            {
                TempData["CommandError"] = result.Message;
            }

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SimulateUplink()
        {
            var device = _db.Devices.FirstOrDefault(d => d.DeviceUid == "1147DC7E94C69E36");
            if (device == null)
            {
                device = new Device
                {
                    DeviceUid = "1147DC7E94C69E36",
                    DeviceId = "paxcounter-ljubljana-02",
                    Name = "paxcounter-ljubljana-02"
                };
                _db.Devices.Add(device);
                await _db.SaveChangesAsync();
            }

            var rng = new Random();
            var sample = new PaxSample
            {
                DeviceId = device.Id,
                Timestamp = DateTime.UtcNow,
                WifiCount = rng.Next(0, 5),
                BleCount = rng.Next(0, 25),
                RssiLimit = 0
            };

            _db.PaxSamples.Add(sample);
            await _db.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClearSamples()
        {
            _db.RssiSamples.RemoveRange(_db.RssiSamples);
            _db.PaxSamples.RemoveRange(_db.PaxSamples);
            await _db.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        private static string NormalizeHeltecAlias(string deviceId)
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
                _ => deviceId.Trim()
            };
        }

        private static List<string> GetHeltecAliases(string canonicalDeviceId)
        {
            return canonicalDeviceId.ToLowerInvariant() switch
            {
                "heltec1-new" => new List<string>
                {
                    "heltec1-new",
                    "paxcounter_aa8130d5",
                    "paxcounter-heltec1",
                    "heltec-tracker1"
                },
                "heltec2-new" => new List<string>
                {
                    "heltec2-new",
                    "paxcounter_b2faeaf2",
                    "paxcounter-heltec2",
                    "heltec-tracker2"
                },
                _ => new List<string> { canonicalDeviceId }
            };
        }

        private static string ResolveCanonicalDisplayName(string deviceId, string? fallbackName)
        {
            return NormalizeHeltecAlias(deviceId).ToLowerInvariant() switch
            {
                "heltec1-new" => "Heltec1",
                "heltec2-new" => "Heltec2",
                _ => string.IsNullOrWhiteSpace(fallbackName) ? deviceId : fallbackName
            };
        }

        private void UpdateTrackerModeFromInterval(int seconds)
        {
            var settings = _trackerHomeModeSettingsService.GetSettings();
            var normalizedSeconds = Math.Clamp(seconds, 10, 3600);
            var nextMode = normalizedSeconds >= Math.Clamp(settings.HomeIntervalSeconds, 30, 3600)
                ? "home"
                : "away";
            _trackerHomeModeSettingsService.SetCurrentMode(nextMode);
        }
    }
}






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

        public HomeController(AppDbContext db, GpsDisplaySettingsService gpsDisplaySettingsService, DeviceCommandService deviceCommandService)
        {
            _db = db;
            _gpsDisplaySettingsService = gpsDisplaySettingsService;
            _deviceCommandService = deviceCommandService;
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
                    SourceChannel = p.SourceChannel
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
                DeviceCommandStatuses = commandStatuses,
                RecentDeviceCommands = recentDeviceCommands,
                DeviceBleSummaries = deviceBleSummaries,
                HistoryAvailableDeviceIds = devices
                    .Select(d => d.DeviceId)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList()!
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
                        requestedSeconds = kvp.Value.RequestedSeconds,
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
                    requestedSeconds = x.RequestedSeconds,
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
                        RequestedSeconds = x.RequestedSeconds,
                        RequestedTransport = x.RequestedTransport,
                        CreatedAtUtc = x.CreatedAtUtc,
                        UpdatedAtUtc = status.UpdatedAtUtc
                    };
                })
                .ToList();
        }

        private static DeviceCommandStatusViewModel BuildStatus(PendingDeviceCommand command)
        {
            if (command.ConsumedAtUtc != null)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"WiFi delivered {command.RequestedSeconds}s",
                    BadgeClass = "bg-info text-dark",
                    CanCancel = false,
                    RequestedSeconds = command.RequestedSeconds,
                    DeliveryMode = "WiFi",
                    UpdatedAtUtc = command.ConsumedAtUtc
                };
            }

            if (command.LoRaAttemptedAtUtc == null)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"Queued {command.RequestedSeconds}s",
                    BadgeClass = "bg-secondary",
                    CanCancel = true,
                    RequestedSeconds = command.RequestedSeconds,
                    DeliveryMode = "Queued",
                    UpdatedAtUtc = command.CreatedAtUtc
                };
            }

            if (command.LoRaAccepted)
            {
                return new DeviceCommandStatusViewModel
                {
                    Text = $"LoRa accepted {command.RequestedSeconds}s",
                    BadgeClass = "bg-success",
                    CanCancel = true,
                    RequestedSeconds = command.RequestedSeconds,
                    DeliveryMode = "LoRa",
                    UpdatedAtUtc = command.LoRaAttemptedAtUtc
                };
            }

            return new DeviceCommandStatusViewModel
            {
                Text = $"Waiting on WiFi ({command.RequestedSeconds}s)",
                BadgeClass = "bg-warning text-dark",
                CanCancel = true,
                RequestedSeconds = command.RequestedSeconds,
                DeliveryMode = "WiFi pending",
                UpdatedAtUtc = command.LoRaAttemptedAtUtc
            };
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
                .Select(g => new { g.Timestamp, g.Latitude, g.Longitude })
                .ToListAsync();

            if (gpsRows.Count == 0)
            {
                return;
            }

            var idx = 0;
            var maxAge = TimeSpan.FromMinutes(30);

            foreach (var row in withoutGps)
            {
                while (idx + 1 < gpsRows.Count && gpsRows[idx + 1].Timestamp <= row.Timestamp)
                {
                    idx++;
                }

                var candidate = gpsRows[idx];
                if (candidate.Timestamp > row.Timestamp)
                {
                    continue;
                }

                if (row.Timestamp - candidate.Timestamp > maxAge)
                {
                    continue;
                }

                row.Latitude = candidate.Latitude;
                row.Longitude = candidate.Longitude;
                row.SourceChannel = string.IsNullOrWhiteSpace(row.SourceChannel)
                    ? "GPS join"
                    : $"{row.SourceChannel} + GPS join";
            }
        }

        [HttpGet("/live/{deviceId}")]
        public async Task<IActionResult> Live(string deviceId)
        {
            var device = await _db.Devices
                .FirstOrDefaultAsync(d => d.DeviceId == deviceId);

            if (device == null)
            {
                return NotFound($"Device '{deviceId}' not found");
            }

            var nowUtc = DateTime.UtcNow;
            var dayStartUtc = nowUtc.Date;
            var lastSeenUtc = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id)
                .OrderByDescending(p => p.Timestamp)
                .Select(p => (DateTime?)p.Timestamp)
                .FirstOrDefaultAsync();

            var latest = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id)
                .OrderByDescending(p => p.Timestamp)
                .Select(p => new { p.BleCount, p.WifiCount })
                .FirstOrDefaultAsync();

            var avgBle15m = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= nowUtc.AddMinutes(-15))
                .Select(p => (double?)p.BleCount)
                .AverageAsync() ?? 0d;

            var peakBleToday = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= dayStartUtc)
                .Select(p => (int?)p.BleCount)
                .MaxAsync() ?? 0;

            var vm = new LiveDevicePageViewModel
            {
                DeviceId = device.DeviceId,
                DeviceName = string.IsNullOrWhiteSpace(device.Name) ? device.DeviceId : device.Name,
                LatestBle = latest?.BleCount ?? 0,
                LatestWifi = latest?.WifiCount ?? 0,
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

            var device = await _db.Devices
                .FirstOrDefaultAsync(d => d.DeviceId == deviceId);

            if (device == null)
            {
                return NotFound($"Device '{deviceId}' not found");
            }

            var nowUtc = DateTime.UtcNow;
            var sinceUtc = nowUtc.AddMinutes(-minutes);
            var dayStartUtc = nowUtc.Date;

            var rawSamples = await _db.PaxSamples
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= sinceUtc)
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
                .Where(p => p.DeviceId == device.Id && p.Timestamp >= dayStartUtc)
                .Select(p => (int?)p.BleCount)
                .MaxAsync() ?? 0;

            var bucketTicks = TimeSpan.FromMinutes(bucketMinutes).Ticks;
            var points = rawSamples
                .GroupBy(p => new DateTime((p.Timestamp.Ticks / bucketTicks) * bucketTicks, DateTimeKind.Utc))
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
                DeviceId = device.DeviceId,
                Minutes = minutes,
                BucketMinutes = bucketMinutes,
                LatestBle = latest?.BleCount ?? 0,
                LatestWifi = latest?.WifiCount ?? 0,
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
                .Where(d => !d.IsHidden)
                .Select(d => d.DeviceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .OrderBy(id => id)
                .ToListAsync();

            var vm = new ComparePageViewModel
            {
                DeviceIds = deviceIds
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
    }
}




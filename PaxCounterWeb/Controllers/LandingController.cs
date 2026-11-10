using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;

namespace PaxCounterWeb.Controllers;

[Route("landing")]
public sealed class LandingController : Controller
{
    private readonly AppDbContext _db;

    public LandingController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("")]
    public IActionResult Index()
    {
        return View();
    }

    [HttpGet("api/live")]
    public async Task<IActionResult> Live(CancellationToken cancellationToken)
    {
        var latestPerDevice = _db.PaxSamples
            .Where(p => p.RssiLimit != int.MinValue && !p.Device.IsHidden)
            .GroupBy(p => p.DeviceId)
            .Select(g => new
            {
                DeviceId = g.Key,
                Timestamp = g.Max(p => p.Timestamp)
            });

        var snapshots = await (
            from sample in _db.PaxSamples
            join latest in latestPerDevice
                on new { sample.DeviceId, sample.Timestamp } equals new { latest.DeviceId, latest.Timestamp }
            join device in _db.Devices on sample.DeviceId equals device.Id
            orderby sample.BleCount descending, sample.Timestamp descending
            select new
            {
                DeviceId = device.DeviceId,
                DeviceName = string.IsNullOrWhiteSpace(device.Name) ? device.DeviceId : device.Name,
                Ble = sample.BleCount,
                TimestampUtc = sample.Timestamp,
                Source = sample.SourceChannel
            })
            .ToListAsync(cancellationToken);

        var nowUtc = DateTime.UtcNow;
        var trendSinceUtc = nowUtc.AddHours(-2);
        var trendSamples = await _db.PaxSamples
            .Where(p => p.RssiLimit != int.MinValue && !p.Device.IsHidden && p.Timestamp >= trendSinceUtc)
            .Select(p => new { p.Timestamp, p.BleCount })
            .ToListAsync(cancellationToken);

        // A compact five-minute maximum series shows whether the live peak is rising or falling.
        var trend = trendSamples
            .GroupBy(p => new DateTime(
                p.Timestamp.Year,
                p.Timestamp.Month,
                p.Timestamp.Day,
                p.Timestamp.Hour,
                (p.Timestamp.Minute / 5) * 5,
                0,
                DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new { timestampUtc = g.Key, ble = g.Max(x => x.BleCount) })
            .ToList();

        // The live card must only compare counters that have actually checked in
        // recently. Otherwise an old TTN peak can look like a current reading.
        var freshnessCutoffUtc = nowUtc.AddMinutes(-5);
        var freshSnapshots = snapshots
            .Where(x => x.TimestampUtc >= freshnessCutoffUtc)
            .GroupBy(x => x.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.TimestampUtc).First())
            .OrderByDescending(x => x.Ble)
            .ToList();

        var newest = freshSnapshots.OrderByDescending(x => x.TimestampUtc).FirstOrDefault();
        var maximum = freshSnapshots.FirstOrDefault();

        return Json(new
        {
            generatedAtUtc = nowUtc,
            maxBle = maximum?.Ble ?? 0,
            maxDevice = maximum?.DeviceName ?? "Waiting for a counter",
            maxDeviceId = maximum?.DeviceId ?? string.Empty,
            maxTimestampUtc = maximum?.TimestampUtc,
            isStale = maximum == null,
            latestTimestampUtc = newest?.TimestampUtc,
            activeDevices = freshSnapshots.Count,
            trend,
            devices = freshSnapshots.Select(x => new
            {
                id = x.DeviceId,
                name = x.DeviceName,
                ble = x.Ble,
                timestampUtc = x.TimestampUtc,
                source = x.Source
            })
        });
    }
}

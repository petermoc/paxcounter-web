using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;

namespace PaxCounterWeb.Controllers;

[Route("footfall")]
public class FootfallController : Controller
{
    private readonly AppDbContext _db;
    private readonly UiFeatureSettingsService _uiFeatureSettingsService;

    public FootfallController(AppDbContext db, UiFeatureSettingsService uiFeatureSettingsService)
    {
        _db = db;
        _uiFeatureSettingsService = uiFeatureSettingsService;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var layout = await GetOrCreateDefaultLayoutAsync(ct);
        return View("Index", await BuildLayoutViewModelAsync(layout, "Footfall Monitor", ct));
    }

    [HttpGet("layout")]
    public async Task<IActionResult> Layout(CancellationToken ct)
    {
        var layout = await GetOrCreateDefaultLayoutAsync(ct);
        return View("Layout", await BuildLayoutViewModelAsync(layout, "Footfall Layout", ct));
    }

    [HttpGet("api/layout")]
    public async Task<IActionResult> GetLayout(CancellationToken ct)
    {
        var layout = await _db.FootfallLayouts
            .Include(l => l.Nodes.OrderBy(n => n.Label))
            .FirstOrDefaultAsync(ct);

        if (layout == null)
        {
            layout = await GetOrCreateDefaultLayoutAsync(ct);
        }

        return Json(new
        {
            layout.Id,
            layout.Name,
            layout.Width,
            layout.Height,
            layout.BackgroundImageUrl,
            nodes = layout.Nodes
                .OrderBy(n => n.Label)
                .Select(n => new
                {
                    n.Id,
                    n.DeviceId,
                    n.Label,
                    n.Zone,
                    n.X,
                    n.Y,
                    n.AccentColor,
                    n.RlimHint,
                    n.IsActive
                })
        });
    }

    [HttpPost("api/layout")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> SaveLayout([FromBody] FootfallLayoutSaveRequest request, CancellationToken ct)
    {
        var layout = await GetOrCreateDefaultLayoutAsync(ct);
        layout.Name = string.IsNullOrWhiteSpace(request.Name) ? "Default Layout" : request.Name.Trim();
        layout.Width = Math.Clamp(request.Width <= 0 ? layout.Width : request.Width, 600, 5000);
        layout.Height = Math.Clamp(request.Height <= 0 ? layout.Height : request.Height, 400, 3000);
        layout.BackgroundImageUrl = string.IsNullOrWhiteSpace(request.BackgroundImageUrl) ? null : request.BackgroundImageUrl.Trim();
        layout.UpdatedAtUtc = DateTime.UtcNow;

        var existingNodes = await _db.FootfallNodes
            .Where(n => n.LayoutId == layout.Id)
            .ToListAsync(ct);

        _db.FootfallNodes.RemoveRange(existingNodes);

        foreach (var node in request.Nodes.Where(n => !string.IsNullOrWhiteSpace(n.DeviceId)))
        {
            _db.FootfallNodes.Add(new FootfallNode
            {
                LayoutId = layout.Id,
                DeviceId = node.DeviceId.Trim(),
                Label = string.IsNullOrWhiteSpace(node.Label) ? node.DeviceId.Trim() : node.Label.Trim(),
                Zone = node.Zone?.Trim() ?? string.Empty,
                X = Math.Clamp(node.X, -180, 180),
                Y = Math.Clamp(node.Y, -90, 90),
                AccentColor = string.IsNullOrWhiteSpace(node.AccentColor) ? "#2563eb" : node.AccentColor.Trim(),
                RlimHint = node.RlimHint,
                IsActive = node.IsActive,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
        return Json(new { success = true, message = $"Saved {request.Nodes.Count} footfall node(s)." });
    }

    [HttpGet("api/live")]
    public async Task<IActionResult> Live([FromQuery] int minutes = 30, CancellationToken ct = default)
    {
        minutes = Math.Clamp(minutes, 5, 1440);
        var sinceUtc = DateTime.UtcNow.AddMinutes(-minutes);
        var layout = await _db.FootfallLayouts
            .Include(l => l.Nodes)
            .FirstOrDefaultAsync(ct);

        if (layout == null)
        {
            layout = await GetOrCreateDefaultLayoutAsync(ct);
        }

        var deviceIds = layout.Nodes
            .Where(n => !string.IsNullOrWhiteSpace(n.DeviceId) && n.IsActive)
            .Select(n => n.DeviceId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var latestSamples = await _db.PaxSamples
            .Include(p => p.Device)
            .Where(p => p.Timestamp >= sinceUtc)
            .Where(p => p.Device != null && deviceIds.Contains(p.Device.DeviceId))
            .OrderByDescending(p => p.Timestamp)
            .ToListAsync(ct);

        var latestByDevice = latestSamples
            .Where(p => p.Device != null)
            .GroupBy(p => p.Device!.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.First(),
                StringComparer.OrdinalIgnoreCase);

        var nodes = layout.Nodes
            .OrderBy(n => n.Label)
            .Select(n =>
            {
                latestByDevice.TryGetValue(n.DeviceId, out var sample);
                var ble = sample?.BleCount ?? 0;
                var showWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts();
                var wifi = showWifiCounts ? sample?.WifiCount ?? 0 : 0;
                return new
                {
                    n.Id,
                    n.DeviceId,
                    n.Label,
                    n.Zone,
                    n.X,
                    n.Y,
                    n.AccentColor,
                    n.RlimHint,
                    n.IsActive,
                    ble,
                    wifi,
                    pax = ble + wifi,
                    timestampUtc = sample?.Timestamp,
                    source = sample?.SourceChannel ?? "-"
                };
            });

        return Json(new
        {
            layout = new
            {
                layout.Name,
                layout.Width,
                layout.Height,
                layout.BackgroundImageUrl
            },
            showWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts(),
            nodes
        });
    }

    private async Task<FootfallLayoutViewModel> BuildLayoutViewModelAsync(FootfallLayout layout, string title, CancellationToken ct)
    {
        var knownDevices = await _db.Devices
            .Where(d => !d.IsHidden && !string.IsNullOrWhiteSpace(d.DeviceId))
            .OrderBy(d => string.IsNullOrWhiteSpace(d.Name) ? d.DeviceId : d.Name)
            .Select(d => new DeviceOptionViewModel
            {
                DeviceId = d.DeviceId,
                DisplayName = string.IsNullOrWhiteSpace(d.Name) ? d.DeviceId : d.Name
            })
            .ToListAsync(ct);

        return new FootfallLayoutViewModel
        {
            Title = title,
            LayoutName = layout.Name,
            Width = layout.Width,
            Height = layout.Height,
            BackgroundImageUrl = layout.BackgroundImageUrl,
            ShowWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts(),
            KnownDevices = knownDevices
        };
    }

    private async Task<FootfallLayout> GetOrCreateDefaultLayoutAsync(CancellationToken ct)
    {
        var layout = await _db.FootfallLayouts
            .Include(l => l.Nodes)
            .FirstOrDefaultAsync(ct);

        if (layout != null)
        {
            return layout;
        }

        layout = new FootfallLayout
        {
            Name = "Block Test Layout",
            Width = 1200,
            Height = 800,
            BackgroundImageUrl = null,
            UpdatedAtUtc = DateTime.UtcNow
        };

        _db.FootfallLayouts.Add(layout);
        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
        return layout;
    }

    public sealed class FootfallLayoutSaveRequest
    {
        public string Name { get; set; } = "Default Layout";
        public int Width { get; set; } = 1600;
        public int Height { get; set; } = 900;
        public string? BackgroundImageUrl { get; set; }
        public List<FootfallNodeDto> Nodes { get; set; } = new();
    }

    public sealed class FootfallNodeDto
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Zone { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public string AccentColor { get; set; } = "#2563eb";
        public int RlimHint { get; set; }
        public bool IsActive { get; set; } = true;
    }
}

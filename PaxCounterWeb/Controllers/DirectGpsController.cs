using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

[ApiController]
[Route("api/gps")]
public sealed class DirectGpsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly DirectGpsIngestService _ingestService;
    private readonly AppDbContext _db;
    private readonly PhoneGpsControlStore _controls;
    private readonly TrackerModeOverrideService _modeOverride;

    public DirectGpsController(IConfiguration configuration, DirectGpsIngestService ingestService, AppDbContext db,
        PhoneGpsControlStore controls, TrackerModeOverrideService modeOverride)
    {
        _configuration = configuration;
        _ingestService = ingestService;
        _db = db;
        _controls = controls;
        _modeOverride = modeOverride;
    }

    [HttpGet("control/{phoneId}")]
    public async Task<IActionResult> Control(string phoneId, [FromQuery] string? wifiSsid, CancellationToken ct)
    {
        if (!HasValidApiKey(phoneId)) return Unauthorized();
        phoneId = phoneId.Trim().ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        var heartbeat = await _db.Set<PhoneGpsHeartbeat>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.PhoneId == phoneId, ct);
        var primary = await _db.Set<PhoneGpsHeartbeat>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.PhoneId == "A52", ct);
        // A primary must be communicating and have a usable fix. Replaying an old
        // offline queue must not indefinitely suppress the backup phone.
        var primaryOnline = primary != null && now.UtcDateTime - primary.LastReceivedUtc <= TimeSpan.FromSeconds(90)
            && now.UtcDateTime - primary.SampleTimestampUtc <= TimeSpan.FromMinutes(10);
        var desired = _controls.Get(phoneId);
        var boost = desired.BoostUntilUtc > now;
        var freshPosition = heartbeat != null && now.UtcDateTime - heartbeat.SampleTimestampUtc <= TimeSpan.FromMinutes(10);
        var atHome = _configuration.GetValue("DirectGps:AutoModeEnabled", false)
            && _controls.IsHome(wifiSsid, freshPosition ? heartbeat!.Latitude : null, freshPosition ? heartbeat!.Longitude : null);
        var mode = _modeOverride.GetMode() ?? (atHome ? "home" : "away");
        var home = mode == "home";
        var interval = boost ? 5 : desired.IntervalSeconds ?? Math.Clamp(_configuration.GetValue(
            home ? "DirectGps:AutoModeHomeIntervalSeconds" : "DirectGps:AutoModeAwayIntervalSeconds", home ? 30 : 5), 5, 3600);
        var highAccuracy = boost || (desired.IntervalSeconds.HasValue ? desired.HighAccuracy : !home);
        return Ok(new { intervalSeconds = interval, highAccuracy,
            mode = boost ? "boost" : mode, refreshId = desired.RefreshId, boostUntilUtc = desired.BoostUntilUtc,
            backupMode = phoneId == "S23", backupFor = "A52", primaryOnline,
            trackingActive = phoneId != "S23" || !primaryOnline || boost, pollSeconds = 30 });
    }

    [HttpPost("ingest")]
    [IgnoreAntiforgeryToken]
    public async Task<ActionResult<DirectGpsIngestResponse>> Ingest([FromBody] DirectGpsIngestRequest request, CancellationToken ct)
    {
        if (!HasValidApiKey(request.PhoneId))
            return Unauthorized();

        try
        {
            return Ok(await _ingestService.IngestAsync(request, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpGet("health/{phoneId}")]
    public async Task<IActionResult> Health(string phoneId, CancellationToken ct)
    {
        if (!HasValidApiKey(phoneId))
            return Unauthorized();

        var normalizedPhoneId = phoneId.Trim().ToUpperInvariant();
        var heartbeat = await _db.Set<PhoneGpsHeartbeat>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PhoneId == normalizedPhoneId, ct);
        if (heartbeat is null)
            return NotFound(new { phoneId = normalizedPhoneId, status = "No direct GPS sample received yet." });

        return Ok(new
        {
            phoneId = heartbeat.PhoneId,
            lastReceivedUtc = heartbeat.LastReceivedUtc,
            sampleTimestampUtc = heartbeat.SampleTimestampUtc,
            ageSeconds = Math.Max(0, (DateTime.UtcNow - heartbeat.SampleTimestampUtc).TotalSeconds),
            heartbeat.Latitude,
            heartbeat.Longitude,
            heartbeat.Accuracy,
            heartbeat.BatteryPercent,
            heartbeat.NetworkType,
            heartbeat.WifiSsid,
            heartbeat.PendingSamples,
            heartbeat.AppVersion
        });
    }

    [HttpGet("backup-status/{primaryPhoneId}")]
    public async Task<IActionResult> BackupStatus(string primaryPhoneId, CancellationToken ct)
    {
        // A backup phone only needs a liveness signal, never the primary phone's location.
        if (!HasAnyValidApiKey())
            return Unauthorized();

        var normalizedPhoneId = primaryPhoneId.Trim().ToUpperInvariant();
        var heartbeat = await _db.Set<PhoneGpsHeartbeat>()
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.PhoneId == normalizedPhoneId, ct);
        var ageSeconds = heartbeat is null
            ? double.PositiveInfinity
            : Math.Max(0, (DateTime.UtcNow - heartbeat.LastReceivedUtc).TotalSeconds);

        return Ok(new
        {
            primaryPhoneId = normalizedPhoneId,
            primaryOnline = ageSeconds <= 90 && heartbeat != null
                && DateTime.UtcNow - heartbeat.SampleTimestampUtc <= TimeSpan.FromMinutes(10),
            ageSeconds = double.IsFinite(ageSeconds) ? ageSeconds : (double?)null
        });
    }

    private bool HasValidApiKey(string? phoneId)
    {
        if (string.IsNullOrWhiteSpace(phoneId))
            return false;

        var configured = _configuration[$"DirectGps:ApiKeys:{phoneId.Trim().ToUpperInvariant()}"];
        var supplied = Request.Headers["X-Gps-Api-Key"].ToString();
        return !string.IsNullOrWhiteSpace(configured) &&
               !string.IsNullOrWhiteSpace(supplied) &&
               string.Equals(configured, supplied, StringComparison.Ordinal);
    }

    private bool HasAnyValidApiKey()
    {
        var supplied = Request.Headers["X-Gps-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(supplied))
            return false;

        return _configuration.GetSection("DirectGps:ApiKeys").GetChildren()
            .Select(item => item.Value)
            .Any(configured => !string.IsNullOrWhiteSpace(configured) &&
                               string.Equals(configured, supplied, StringComparison.Ordinal));
    }
}

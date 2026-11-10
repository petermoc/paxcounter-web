using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class PhoneGpsService(AppDbContext db, IConfiguration configuration,
    UiFeatureSettingsService settings, PhoneGpsControlStore controls)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration[$"DirectGps:ApiKeys:{settings.GetSelectedGpsPhone()}"]);

    public Task<(bool Success, string Message)> ForceRefreshAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) return Task.FromResult((false, "Direct GPS phone is not paired."));
        controls.Boost(settings.GetSelectedGpsPhone());
        return Task.FromResult((true, "GPS boost queued; the phone will apply it on its next HTTPS check (within 30 seconds when online)."));
    }

    public Task<(bool Success, string Message)> ApplyIntervalAsync(int seconds, CancellationToken ct, bool highAccuracyEnabled = true)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConfigured) return Task.FromResult((false, "Direct GPS phone is not paired."));
        controls.SetInterval(settings.GetSelectedGpsPhone(), seconds, highAccuracyEnabled);
        return Task.FromResult((true, "Direct GPS interval queued for the phone's next HTTPS check."));
    }

    public Task<(bool Success, string Message)> DisableHighAccuracyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        controls.StopBoost(settings.GetSelectedGpsPhone());
        return Task.FromResult((true, "Power-saving GPS queued for the phone."));
    }

    public Task<PhoneTrackerStatus> GetTrackerStatusAsync(CancellationToken ct)
        => GetTrackerStatusAsync(configuration["DirectGps:AutoModeDeviceTrackerEntityId"] ?? "A52", ct);

    public async Task<PhoneTrackerStatus> GetTrackerStatusAsync(string phoneId, CancellationToken ct)
    {
        phoneId = NormalizePhone(phoneId);
        var paired = !string.IsNullOrWhiteSpace(configuration[$"DirectGps:ApiKeys:{phoneId}"]);
        var heartbeat = await db.Set<PhoneGpsHeartbeat>().AsNoTracking().SingleOrDefaultAsync(p => p.PhoneId == phoneId, ct);
        if (heartbeat is null) return new(paired, phoneId, "offline", null, null, null, null, "No Direct GPS sample received.");
        var online = DateTime.UtcNow - heartbeat.LastReceivedUtc <= TimeSpan.FromSeconds(90);
        var home = controls.IsHome(heartbeat.WifiSsid, heartbeat.Latitude, heartbeat.Longitude);
        return new(paired, phoneId, online ? home ? "home" : "away" : "offline",
            new DateTimeOffset(DateTime.SpecifyKind(heartbeat.SampleTimestampUtc, DateTimeKind.Utc)),
            heartbeat.Latitude, heartbeat.Longitude, heartbeat.Accuracy,
            $"Direct GPS {heartbeat.AppVersion}; last contact {heartbeat.LastReceivedUtc:u}")
            { LastContactUtc = new DateTimeOffset(DateTime.SpecifyKind(heartbeat.LastReceivedUtc, DateTimeKind.Utc)) };
    }

    // Optional legacy MQTT input keeps its source identity; no HA lookup is performed.
    public Task<string> ResolveGpsSourceTopicAsync(GpsMessage gps, string defaultTopic, CancellationToken ct)
        => Task.FromResult(defaultTopic);

    public static string NormalizePhone(string value) => value.ToUpperInvariant() switch
    {
        "DEVICE_TRACKER.SM_A528B_STAR_TELEFON" => "A52",
        "DEVICE_TRACKER.SM_S911B" => "S23",
        _ => value.ToUpperInvariant()
    };
}

public sealed record PhoneTrackerStatus(bool IsConfigured, string EntityId, string State,
    DateTimeOffset? LastUpdatedUtc, double? Latitude, double? Longitude, int? Accuracy, string Message)
{
    public DateTimeOffset? LastContactUtc { get; init; }
    public bool IsStale(TimeSpan threshold, DateTimeOffset nowUtc)
        => !LastContactUtc.HasValue || nowUtc - LastContactUtc.Value > threshold;
}

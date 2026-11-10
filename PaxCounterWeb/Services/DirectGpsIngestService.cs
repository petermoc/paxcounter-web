using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public sealed class DirectGpsIngestService
{
    private readonly AppDbContext _db;
    private readonly ILogger<DirectGpsIngestService> _logger;

    public DirectGpsIngestService(AppDbContext db, ILogger<DirectGpsIngestService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DirectGpsIngestResponse> IngestAsync(DirectGpsIngestRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneId)) throw new ArgumentException("phoneId is required.");
        var phoneId = request.PhoneId.Trim().ToUpperInvariant();
        var timestampUtc = request.TimestampUtc.Kind == DateTimeKind.Utc
            ? request.TimestampUtc
            : request.TimestampUtc.ToUniversalTime();
        var nowUtc = DateTime.UtcNow;

        Validate(request, timestampUtc, nowUtc);

        var sourceTopic = $"phone/direct/{phoneId.ToLowerInvariant()}";
        var existing = await _db.GpsSamples
            .AsNoTracking()
            .AnyAsync(sample => sample.SourceTopic == sourceTopic && sample.Timestamp == timestampUtc, ct);

        if (!existing)
        {
            _db.GpsSamples.Add(new GpsSample
            {
                Timestamp = timestampUtc,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                Accuracy = request.Accuracy,
                SourceTopic = sourceTopic
            });
        }

        var heartbeat = await _db.Set<PhoneGpsHeartbeat>()
            .SingleOrDefaultAsync(item => item.PhoneId == phoneId, ct);
        if (heartbeat is null)
        {
            heartbeat = new PhoneGpsHeartbeat { PhoneId = phoneId };
            _db.Add(heartbeat);
        }

        heartbeat.LastReceivedUtc = nowUtc;
        // Delayed uploads remain in history but cannot move the current position backwards.
        if (timestampUtc >= heartbeat.SampleTimestampUtc)
        {
            heartbeat.SampleTimestampUtc = timestampUtc;
            heartbeat.Latitude = request.Latitude;
            heartbeat.Longitude = request.Longitude;
            heartbeat.Accuracy = request.Accuracy;
        }
        heartbeat.BatteryPercent = request.BatteryPercent;
        heartbeat.NetworkType = Trim(request.NetworkType, 40);
        heartbeat.WifiSsid = Trim(request.WifiSsid, 80);
        heartbeat.PendingSamples = Math.Max(0, request.PendingSamples);
        heartbeat.AppVersion = Trim(request.AppVersion, 40);

        await SqliteWriteHelper.SaveChangesWithRetryAsync(_db, ct);
        _logger.LogInformation("Direct GPS accepted from {PhoneId}: lat={Latitude}, lon={Longitude}, accuracy={Accuracy}, pending={Pending}", phoneId, request.Latitude, request.Longitude, request.Accuracy, heartbeat.PendingSamples);
        return new DirectGpsIngestResponse(true, sourceTopic, nowUtc);
    }

    private static void Validate(DirectGpsIngestRequest request, DateTime timestampUtc, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(request.PhoneId) || request.PhoneId.Length > 32)
            throw new ArgumentException("phoneId is required and must be at most 32 characters.");
        if (!double.IsFinite(request.Latitude) || !double.IsFinite(request.Longitude)
            || request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            throw new ArgumentException("latitude or longitude is outside its valid range.");
        if (request.Accuracy is < 0 or > 5000)
            throw new ArgumentException("accuracy must be between 0 and 5000 meters.");
        if (timestampUtc < nowUtc.AddDays(-7) || timestampUtc > nowUtc.AddMinutes(10))
            throw new ArgumentException("timestampUtc is outside the accepted range.");
    }

    private static string? Trim(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximumLength)];
}

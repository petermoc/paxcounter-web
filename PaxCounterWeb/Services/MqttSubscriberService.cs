using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public class MqttSubscriberService : BackgroundService
{
    private readonly ILogger<MqttSubscriberService> _logger;
    private readonly IConfiguration _config;
    private readonly TrackStore _trackStore;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly object _pendingGpsJumpLock = new();
    private readonly Dictionary<string, PendingGpsJumpCandidate> _pendingGpsJumpCandidates = new(StringComparer.OrdinalIgnoreCase);
    private IMqttClient? _client;

    public MqttSubscriberService(
        ILogger<MqttSubscriberService> logger,
        IConfiguration config,
        TrackStore trackStore,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _trackStore = trackStore;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqttSection = _config.GetSection("Mqtt");
        var host = mqttSection.GetValue<string>("Host") ?? "127.0.0.1";
        var port = mqttSection.GetValue<int>("Port", 1883);
        var username = mqttSection.GetValue<string>("Username") ?? "";
        var password = mqttSection.GetValue<string>("Password") ?? "";
        var gpsTopic = mqttSection.GetValue<string>("GpsTopic") ?? "phone/gps";
        var paxTopic = mqttSection.GetValue<string>("PaxTopic") ?? "paxcounter/18d01e31/state";
        var clientId = mqttSection.GetValue<string>("ClientId") ?? $"PaxCounterWeb-{Guid.NewGuid()}";

        _logger.LogInformation("MQTT config: host={Host} port={Port} user={User} gpsTopic={GpsTopic} paxTopic={PaxTopic} clientId={ClientId}",
            host, port, string.IsNullOrWhiteSpace(username) ? "<empty>" : username, gpsTopic, paxTopic, clientId);

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                var payload = e.ApplicationMessage.Payload == null
                    ? string.Empty
                    : Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (string.Equals(e.ApplicationMessage.Topic, gpsTopic, StringComparison.OrdinalIgnoreCase))
                {
                    var gps = JsonSerializer.Deserialize<GpsMessage>(payload);
                    if (gps != null)
                    {
                        _trackStore.AddGps(gps);
                        await PersistGpsSampleAsync(gps, e.ApplicationMessage.Topic);
                    }
                }
                else if (string.Equals(e.ApplicationMessage.Topic, paxTopic, StringComparison.OrdinalIgnoreCase))
                {
                    var pax = JsonSerializer.Deserialize<PaxMessage>(payload);
                    if (pax != null)
                    {
                        _trackStore.UpdatePax(ExtractDeviceId(e.ApplicationMessage.Topic), pax);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT message parse failed");
            }
        };

        var options = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(host, port)
            .WithCredentials(username, password)
            .WithCleanSession()
            .Build();

        _client.DisconnectedAsync += async e =>
        {
            _logger.LogWarning("MQTT disconnected. Reconnecting in 5s...");
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            try
            {
                if (!stoppingToken.IsCancellationRequested)
                {
                    await _client.ConnectAsync(options, stoppingToken);
                    await _client.SubscribeAsync(gpsTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);
                    await _client.SubscribeAsync(paxTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MQTT reconnect failed");
            }
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(options, stoppingToken);
                    await _client.SubscribeAsync(gpsTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);
                    await _client.SubscribeAsync(paxTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);
                    _logger.LogInformation("MQTT subscriber started. Topics: {Gps} and {Pax}", gpsTopic, paxTopic);
                }

                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial MQTT connect failed. Retrying in 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(1000, stoppingToken);
        }
    }

    private static string ExtractDeviceId(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return "unknown";
        }

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return parts[1];
        }

        return topic;
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

    private async Task PersistGpsSampleAsync(GpsMessage gps, string topic)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var gpsService = scope.ServiceProvider.GetService<PhoneGpsService>();

        var resolvedTopic = topic;
        if (gpsService != null)
        {
            try
            {
                resolvedTopic = await gpsService.ResolveGpsSourceTopicAsync(gps, topic, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to resolve GPS phone source; keeping topic {Topic}.", topic);
            }
        }

        var timestampUtc = DateTime.UtcNow;
        var currentSample = new GpsSample
        {
            Timestamp = timestampUtc,
            Latitude = gps.lat,
            Longitude = gps.lon,
            Accuracy = gps.accuracy,
            SourceTopic = resolvedTopic
        };

        var previousSample = await db.GpsSamples
            .AsNoTracking()
            .Where(g => g.SourceTopic == resolvedTopic)
            .OrderByDescending(g => g.Timestamp)
            .FirstOrDefaultAsync();

        if (previousSample != null)
        {
            var secondsSincePrevious = (timestampUtc - previousSample.Timestamp).TotalSeconds;
            if (secondsSincePrevious > 0)
            {
                var distanceMeters = HaversineMeters(previousSample.Latitude, previousSample.Longitude, gps.lat, gps.lon);
                var speedKmh = (distanceMeters / secondsSincePrevious) * 3.6d;
                if (speedKmh > 250d)
                {
                    _logger.LogWarning(
                        "Rejected implausible GPS jump for {SourceTopic}: prev=({PrevLat},{PrevLon}) new=({Lat},{Lon}) dt={Seconds}s speed={SpeedKmh}km/h acc={Accuracy}",
                        resolvedTopic,
                        previousSample.Latitude,
                        previousSample.Longitude,
                        gps.lat,
                        gps.lon,
                        Math.Round(secondsSincePrevious, 1),
                        Math.Round(speedKmh, 1),
                        gps.accuracy);
                    return;
                }
            }

            if (TryHandlePendingGpsJump(db, resolvedTopic, previousSample, currentSample))
            {
                await SaveGpsChangesAsync(db);
                return;
            }
        }
        else
        {
            ClearPendingGpsJump(resolvedTopic);
        }

        db.GpsSamples.Add(currentSample);
        await SaveGpsChangesAsync(db);
    }

    private bool TryHandlePendingGpsJump(AppDbContext db, string sourceTopic, GpsSample previousSample, GpsSample currentSample)
    {
        if (!ShouldRequireJumpConfirmation(sourceTopic, previousSample, currentSample, out var distanceMeters))
        {
            ClearPendingGpsJump(sourceTopic);
            return false;
        }

        lock (_pendingGpsJumpLock)
        {
            if (_pendingGpsJumpCandidates.TryGetValue(sourceTopic, out var pendingCandidate))
            {
                if ((currentSample.Timestamp - pendingCandidate.TimestampUtc).TotalMinutes <= 15 &&
                    HaversineMeters(pendingCandidate.Latitude, pendingCandidate.Longitude, currentSample.Latitude, currentSample.Longitude) <= 300d)
                {
                    _pendingGpsJumpCandidates.Remove(sourceTopic);
                    db.GpsSamples.Add(new GpsSample
                    {
                        Timestamp = pendingCandidate.TimestampUtc,
                        Latitude = pendingCandidate.Latitude,
                        Longitude = pendingCandidate.Longitude,
                        Accuracy = pendingCandidate.Accuracy,
                        SourceTopic = sourceTopic
                    });
                    db.GpsSamples.Add(currentSample);
                    _logger.LogInformation(
                        "Confirmed pending GPS jump for {SourceTopic}: ({Lat},{Lon}) then ({CurrentLat},{CurrentLon})",
                        sourceTopic,
                        pendingCandidate.Latitude,
                        pendingCandidate.Longitude,
                        currentSample.Latitude,
                        currentSample.Longitude);
                    return true;
                }
            }

            _pendingGpsJumpCandidates[sourceTopic] = new PendingGpsJumpCandidate
            {
                TimestampUtc = currentSample.Timestamp,
                Latitude = currentSample.Latitude,
                Longitude = currentSample.Longitude,
                Accuracy = currentSample.Accuracy,
                PreviousTimestampUtc = previousSample.Timestamp,
                PreviousLatitude = previousSample.Latitude,
                PreviousLongitude = previousSample.Longitude,
                DistanceMeters = distanceMeters
            };
        }

        _logger.LogWarning(
            "Holding unconfirmed GPS jump for {SourceTopic}: prev=({PrevLat},{PrevLon}) candidate=({Lat},{Lon}) distance={DistanceMeters}m acc={Accuracy}",
            sourceTopic,
            previousSample.Latitude,
            previousSample.Longitude,
            currentSample.Latitude,
            currentSample.Longitude,
            Math.Round(distanceMeters),
            currentSample.Accuracy);
        return true;
    }

    private static bool ShouldRequireJumpConfirmation(string sourceTopic, GpsSample previousSample, GpsSample currentSample, out double distanceMeters)
    {
        distanceMeters = HaversineMeters(previousSample.Latitude, previousSample.Longitude, currentSample.Latitude, currentSample.Longitude);
        if (!sourceTopic.StartsWith("phone/gps", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ageSeconds = (currentSample.Timestamp - previousSample.Timestamp).TotalSeconds;
        return ageSeconds >= 120d && distanceMeters >= 3000d;
    }

    private void ClearPendingGpsJump(string sourceTopic)
    {
        lock (_pendingGpsJumpLock)
        {
            _pendingGpsJumpCandidates.Remove(sourceTopic);
        }
    }

    private async Task SaveGpsChangesAsync(AppDbContext db)
    {
        try
        {
            await SqliteWriteHelper.SaveChangesWithRetryAsync(db, CancellationToken.None);
        }
        catch (Exception ex) when (SqliteWriteHelper.IsDatabaseLocked(ex))
        {
            _logger.LogWarning("SQLite locked while persisting GPS sample; skipping this point.");
        }
    }

    private sealed class PendingGpsJumpCandidate
    {
        public DateTime TimestampUtc { get; init; }
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public int? Accuracy { get; init; }
        public DateTime PreviousTimestampUtc { get; init; }
        public double PreviousLatitude { get; init; }
        public double PreviousLongitude { get; init; }
        public double DistanceMeters { get; init; }
    }
}


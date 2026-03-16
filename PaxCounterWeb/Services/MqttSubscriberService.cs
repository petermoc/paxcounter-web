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

        await _client.ConnectAsync(options, stoppingToken);
        await _client.SubscribeAsync(gpsTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);
        await _client.SubscribeAsync(paxTopic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce, stoppingToken);

        _logger.LogInformation("MQTT subscriber started. Topics: {Gps} and {Pax}", gpsTopic, paxTopic);

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

    private async Task PersistGpsSampleAsync(GpsMessage gps, string topic)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.GpsSamples.Add(new GpsSample
        {
            Timestamp = DateTime.UtcNow,
            Latitude = gps.lat,
            Longitude = gps.lon,
            Accuracy = gps.accuracy,
            SourceTopic = topic
        });

        try
        {
            await SqliteWriteHelper.SaveChangesWithRetryAsync(db, CancellationToken.None);
        }
        catch (Exception ex) when (SqliteWriteHelper.IsDatabaseLocked(ex))
        {
            _logger.LogWarning("SQLite locked while persisting GPS sample; skipping this point.");
        }
    }
}

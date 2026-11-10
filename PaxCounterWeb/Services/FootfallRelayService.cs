using System.Net.Http.Json;

namespace PaxCounterWeb.Services;

public sealed class FootfallRelayService(HttpClient httpClient, IConfiguration configuration)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["FootfallRelay:BaseUrl"]);

    public bool ShouldRelay(string deviceId, string? deviceUid)
    {
        return Normalize(deviceId) switch
        {
            "heltec1new" or "heltec2new" or "paxcounteraa8130d5" or "paxcounterb2faeaf2" or "paxcounterheltec1" or "paxcounterheltec2" or "heltectracker1" or "heltectracker2" or "paxcountert3" or "paxcounterc3mini1" or "paxcountertbeam" or "tbf4" or "paxcountertbeams3" => true,
            _ => Normalize(deviceUid) is "f09e9e75d170feff" or "f09e9e7555c0feff" or "f09e9e75d170" or "f09e9e7555c0"
        };
    }

    public async Task RelayAsync(FootfallRelayRequest request, CancellationToken ct)
    {
        var baseUrl = configuration["FootfallRelay:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/ingest/pax")
        {
            Content = JsonContent.Create(request)
        };

        var apiKey = configuration["FootfallRelay:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
        }

        using var response = await httpClient.SendAsync(message, ct);
        response.EnsureSuccessStatusCode();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}

public sealed class FootfallRelayRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceUid { get; set; }
    public DateTime TimestampUtc { get; set; }
    public int BleCount { get; set; }
    public int WifiCount { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string SourceChannel { get; set; } = string.Empty;
}



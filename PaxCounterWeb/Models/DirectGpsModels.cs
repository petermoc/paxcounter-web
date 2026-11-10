namespace PaxCounterWeb.Models;

public sealed class DirectGpsIngestRequest
{
    public string PhoneId { get; init; } = string.Empty;
    public DateTime TimestampUtc { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int? Accuracy { get; init; }
    public double? SpeedMetersPerSecond { get; init; }
    public double? BearingDegrees { get; init; }
    public int? BatteryPercent { get; init; }
    public string? NetworkType { get; init; }
    public string? WifiSsid { get; init; }
    public int PendingSamples { get; init; }
    public string? AppVersion { get; init; }
}

public sealed record DirectGpsIngestResponse(bool Accepted, string SourceTopic, DateTime ReceivedUtc);

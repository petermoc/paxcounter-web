namespace PaxCounterWeb.Models;

public class PhoneGpsHeartbeat
{
    public int Id { get; set; }
    public string PhoneId { get; set; } = string.Empty;
    public DateTime LastReceivedUtc { get; set; }
    public DateTime SampleTimestampUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? Accuracy { get; set; }
    public int? BatteryPercent { get; set; }
    public string? NetworkType { get; set; }
    public string? WifiSsid { get; set; }
    public int PendingSamples { get; set; }
    public string? AppVersion { get; set; }
}

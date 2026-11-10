namespace PaxCounterWeb.Models;

public class TrackerPosition
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public Device Device { get; set; } = null!;
    public DateTime TimestampUtc { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? Altitude { get; set; }
    public int? Satellites { get; set; }
    public double? Hdop { get; set; }
    public int? BatteryVoltageMv { get; set; }
    public double? BatteryPercent { get; set; }
    public string SourceChannel { get; set; } = "LoRa/TTN";
}

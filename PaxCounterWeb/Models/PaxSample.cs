using PaxCounterWeb.Models.PaxCounterWeb.Models;

namespace PaxCounterWeb.Models;

public class PaxSample
{
    public int Id { get; set; }

    public int DeviceId { get; set; }          // ⬅ INT
    public Device Device { get; set; } = null!;

    public DateTime Timestamp { get; set; }

    public int WifiCount { get; set; }
    public int BleCount { get; set; }

    public int RssiLimit { get; set; }
    public int? BatteryVoltageMv { get; set; }
    public double? BatteryPercent { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Satellites { get; set; }
    public double? Hdop { get; set; }
    public int? Altitude { get; set; }
    public string? SourceChannel { get; set; }

    // Navigation property
    public ICollection<RssiSample> RssiSamples { get; set; } = new List<RssiSample>();
}

public class PaxSampleEntity
{
    public int Id { get; set; }

    public int DeviceIdRef { get; set; }
    public Device Device { get; set; } = null!;

    public int WifiCount { get; set; }
    public int BleCount { get; set; }
    public int RssiLimit { get; set; }
    public int? BatteryVoltageMv { get; set; }
    public double? BatteryPercent { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Satellites { get; set; }
    public double? Hdop { get; set; }
    public int? Altitude { get; set; }
    public string? SourceChannel { get; set; }
    public DateTime Timestamp { get; set; }
}

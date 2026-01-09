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
    public DateTime Timestamp { get; set; }
}

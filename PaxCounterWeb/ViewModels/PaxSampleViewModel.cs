namespace PaxCounterWeb.ViewModels;

public class PaxSampleViewModel
{
    public DateTime Timestamp { get; set; }
    public int WifiCount { get; set; }
    public int BleCount { get; set; }
    public int RssiLimit { get; set; }
}

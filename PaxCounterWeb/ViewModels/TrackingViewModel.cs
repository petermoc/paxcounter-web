namespace PaxCounterWeb.ViewModels;

public class TrackingViewModel
{
    public List<string> Devices { get; set; } = new();
    public List<string> SelectedDeviceIds { get; set; } = new();
    public string GpsDisplayMode { get; set; } = "Decimal";
    public bool ForceGpsConfigured { get; set; }
}

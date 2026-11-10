namespace PaxCounterWeb.ViewModels;

public class FootfallLayoutViewModel
{
    public string Title { get; set; } = "Footfall Layout";
    public string LayoutName { get; set; } = "Default Layout";
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public string? BackgroundImageUrl { get; set; }
    public bool ShowWifiCounts { get; set; } = true;
    public List<DeviceOptionViewModel> KnownDevices { get; set; } = new();
}

namespace PaxCounterWeb.ViewModels;

public class CrowdEstimateViewModel
{
    public string PageTitle { get; set; } = "Crowd Estimate";
    public List<DeviceOptionViewModel> Devices { get; set; } = new();
    public List<string> SelectedDeviceIds { get; set; } = new();
    public string AllDevicesLabel { get; set; } = "All trackers";
    public string EmptyStateMessage { get; set; } = "No tracker devices available yet.";
    public int InitialMinutes { get; set; } = 120;
    public int InitialCellMeters { get; set; } = 20;
    public int InitialActiveMinutes { get; set; } = 15;
}

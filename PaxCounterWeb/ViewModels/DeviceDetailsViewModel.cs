namespace PaxCounterWeb.ViewModels;

public class DeviceDetailsViewModel
{
    public int Id { get; set; }
    public string Name { get; set; }

    public List<PaxSampleViewModel> Samples { get; set; } = new();
}

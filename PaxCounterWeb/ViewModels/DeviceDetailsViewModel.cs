namespace PaxCounterWeb.ViewModels;

public class DeviceDetailsViewModel
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string Name { get; set; } = "";   // <- odpravi non-null warning

    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public int TotalSamples { get; set; }

    public List<PaxSampleViewModel> SamplesAsc { get; set; } = new();
    public List<PaxSampleViewModel> SamplesDesc { get; set; } = new();

}

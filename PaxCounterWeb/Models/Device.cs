namespace PaxCounterWeb.Models;

public class Device
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = "";  // LOGIČNI ID (MQTT / paxcounter)
    public string DeviceUid { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public ICollection<PaxSample> PaxSamples { get; set; } = new List<PaxSample>();
}

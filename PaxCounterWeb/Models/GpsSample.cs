namespace PaxCounterWeb.Models;

public class GpsSample
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int? Accuracy { get; set; }
    public string? SourceTopic { get; set; }
}

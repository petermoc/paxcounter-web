namespace PaxCounterWeb.Models;

public class FootfallNode
{
    public int Id { get; set; }
    public int LayoutId { get; set; }
    public FootfallLayout? Layout { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Zone { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public string AccentColor { get; set; } = "#2563eb";
    public int RlimHint { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

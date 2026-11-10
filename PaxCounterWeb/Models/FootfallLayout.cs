namespace PaxCounterWeb.Models;

public class FootfallLayout
{
    public int Id { get; set; }
    public string Name { get; set; } = "Default Layout";
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 900;
    public string? BackgroundImageUrl { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public ICollection<FootfallNode> Nodes { get; set; } = new List<FootfallNode>();
}

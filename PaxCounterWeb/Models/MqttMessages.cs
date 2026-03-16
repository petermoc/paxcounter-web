namespace PaxCounterWeb.Models;

public class GpsMessage
{
    public double lat { get; set; }
    public double lon { get; set; }
    public int accuracy { get; set; }
}

public class PaxMessage
{
    public int ble { get; set; }
    public int wifi { get; set; }
    public int pax { get; set; }
    public long uptime { get; set; }
}

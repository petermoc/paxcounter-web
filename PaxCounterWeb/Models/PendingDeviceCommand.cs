using System.ComponentModel.DataAnnotations.Schema;

namespace PaxCounterWeb.Models;

public class PendingDeviceCommand
{
    public int Id { get; set; }

    [ForeignKey(nameof(Device))]
    public int DeviceEntityId { get; set; }
    public Device Device { get; set; } = null!;
    public string CommandName { get; set; } = "";
    public string PayloadBase64 { get; set; } = "";
    public string PayloadHex { get; set; } = "";
    public int RequestedSeconds { get; set; }
    public string RequestedTransport { get; set; } = "LoRa+WiFi";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LoRaAttemptedAtUtc { get; set; }
    public bool LoRaAccepted { get; set; }
    public string? LoRaStatus { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public int WifiDispatchCount { get; set; }
}

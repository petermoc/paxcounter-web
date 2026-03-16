using PaxCounterWeb.Models;

namespace PaxCounterWeb.ViewModels;

public class HomeViewModel
{
    public List<Device> Devices { get; set; } = new();
    public List<LiveSampleViewModel> LiveSamples { get; set; } = new();
    public int HistoryMinutes { get; set; } = 180;
    public string? HistoryDeviceId { get; set; }
    public string? HistoryChannelFilter { get; set; }
    public DateTime HistoryFromUtc { get; set; }
    public int HistoryTotalInWindow { get; set; }
    public List<string> HistoryAvailableDeviceIds { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
    public int TotalPages { get; set; }
    public bool HasPreviousPage => Page > 0;
    public bool HasNextPage => Page + 1 < TotalPages;
    public GpsDisplayMode GpsDisplayMode { get; set; } = GpsDisplayMode.Decimal;
    public bool PendingOnly { get; set; }
    public bool ShowHidden { get; set; }
    public Dictionary<int, DeviceCommandStatusViewModel> DeviceCommandStatuses { get; set; } = new();
    public List<DeviceCommandHistoryItemViewModel> RecentDeviceCommands { get; set; } = new();
    public Dictionary<int, DeviceBleSummaryViewModel> DeviceBleSummaries { get; set; } = new();
}

public class DeviceBleSummaryViewModel
{
    public int LatestBle { get; set; }
    public int RecentMaxBle5m { get; set; }
    public double RecentAvgBle5m { get; set; }
    public int? LastNonZeroBle { get; set; }
    public DateTime? LastNonZeroAtUtc { get; set; }
}

public class DeviceCommandStatusViewModel
{
    public string Text { get; set; } = "";
    public string BadgeClass { get; set; } = "bg-secondary";
    public bool CanCancel { get; set; }
    public int? RequestedSeconds { get; set; }
    public string? DeliveryMode { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class DeviceCommandHistoryItemViewModel
{
    public int Id { get; set; }
    public int DeviceEntityId { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Text { get; set; } = "";
    public string BadgeClass { get; set; } = "bg-secondary";
    public int RequestedSeconds { get; set; }
    public string RequestedTransport { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

public class LiveSampleViewModel
{
    public DateTime Timestamp { get; set; }
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int WifiCount { get; set; }
    public int BleCount { get; set; }
    public int RssiLimit { get; set; }
    public int? BatteryVoltageMv { get; set; }
    public double? BatteryPercent { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int? Satellites { get; set; }
    public double? Hdop { get; set; }
    public int? Altitude { get; set; }
    public string? SourceChannel { get; set; }
}

public class LiveDevicePageViewModel
{
    public string DeviceId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int LatestBle { get; set; }
    public int LatestWifi { get; set; }
    public double AvgBle15m { get; set; }
    public int PeakBleToday { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}

public class BleSeriesPointViewModel
{
    public DateTime BucketUtc { get; set; }
    public double BleAvg { get; set; }
    public int BleMax { get; set; }
    public int Samples { get; set; }
}

public class BleMetricsResponseViewModel
{
    public string DeviceId { get; set; } = "";
    public int Minutes { get; set; }
    public int BucketMinutes { get; set; }
    public int LatestBle { get; set; }
    public int LatestWifi { get; set; }
    public double AvgBle15m { get; set; }
    public int PeakBleToday { get; set; }
    public DateTime? LastSeenUtc { get; set; }
    public List<BleSeriesPointViewModel> Points { get; set; } = new();
}

public class ComparePageViewModel
{
    public List<string> DeviceIds { get; set; } = new();
}


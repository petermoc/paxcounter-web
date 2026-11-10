using PaxCounterWeb.Models;

namespace PaxCounterWeb.ViewModels;

public class DisplaySettingsViewModel
{
    public GpsDisplayMode GpsMode { get; set; } = GpsDisplayMode.Decimal;
    public string SelectedGpsPhone { get; set; } = "A52";
    public bool EnableRlimOtaControls { get; set; }
    public bool SyncPhoneGpsIntervalFromApp { get; set; } = true;
    public bool ShowWifiCounts { get; set; } = true;
    public bool EnableBleScanOtaControls { get; set; } = true;
    public bool AutoModeEnabled { get; set; }
    public string HomeWifiSsidsText { get; set; } = string.Empty;
    public string AutoModeTrackerEntityId { get; set; } = string.Empty;
    public string AutoModeHomeStatesText { get; set; } = string.Empty;
    public int AutoModeHomeIntervalSeconds { get; set; } = 30;
    public int AutoModeAwayIntervalSeconds { get; set; } = 5;
}

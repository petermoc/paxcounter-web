using PaxCounterWeb.Models;

namespace PaxCounterWeb.ViewModels;

public class DisplaySettingsViewModel
{
    public GpsDisplayMode GpsMode { get; set; } = GpsDisplayMode.Decimal;
}


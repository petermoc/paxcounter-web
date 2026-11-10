using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;

namespace PaxCounterWeb.Controllers;

public class SettingsController : Controller
{
    private readonly IConfiguration _configuration;
    private readonly GpsDisplaySettingsService _gpsDisplaySettingsService;
    private readonly UiFeatureSettingsService _uiFeatureSettingsService;
    private readonly HomeWifiSettingsService _homeWifi;

    public SettingsController(
        IConfiguration configuration,
        GpsDisplaySettingsService gpsDisplaySettingsService,
        UiFeatureSettingsService uiFeatureSettingsService,
        HomeWifiSettingsService homeWifi)
    {
        _configuration = configuration;
        _gpsDisplaySettingsService = gpsDisplaySettingsService;
        _uiFeatureSettingsService = uiFeatureSettingsService;
        _homeWifi = homeWifi;
    }

    [HttpGet]
    public IActionResult Index()
    {
        var homeStates = (_configuration["DirectGps:AutoModeHomeStates"] ?? "home,Paxcounter Zone")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return View(new DisplaySettingsViewModel
        {
            GpsMode = _gpsDisplaySettingsService.GetMode(),
            SelectedGpsPhone = _uiFeatureSettingsService.GetSelectedGpsPhone(),
            EnableRlimOtaControls = _uiFeatureSettingsService.GetRlimControlsEnabled(),
            SyncPhoneGpsIntervalFromApp = _uiFeatureSettingsService.GetGpsIntervalSyncEnabled(),
            ShowWifiCounts = _uiFeatureSettingsService.GetShowWifiCounts(),
            EnableBleScanOtaControls = _uiFeatureSettingsService.GetBleScanControlsEnabled(),
            AutoModeEnabled = _configuration.GetValue("DirectGps:AutoModeEnabled", false),
            HomeWifiSsidsText = TempData["HomeWifiInput"] as string ?? string.Join("\n", _homeWifi.GetSsids()),
            AutoModeTrackerEntityId = _configuration["DirectGps:AutoModeDeviceTrackerEntityId"] ?? "A52",
            AutoModeHomeStatesText = homeStates.Length == 0 ? "-" : string.Join(", ", homeStates),
            AutoModeHomeIntervalSeconds = Math.Clamp(GetConfiguredInt("DirectGps:AutoModeHomeIntervalSeconds", 30), 5, 3600),
            AutoModeAwayIntervalSeconds = Math.Clamp(GetConfiguredInt("DirectGps:AutoModeAwayIntervalSeconds", 5), 5, 3600)
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Index(DisplaySettingsViewModel vm)
    {
        _gpsDisplaySettingsService.SetMode(vm.GpsMode);
        _uiFeatureSettingsService.SetSelectedGpsPhone(vm.SelectedGpsPhone);
        _uiFeatureSettingsService.SetRlimControlsEnabled(vm.EnableRlimOtaControls);
        _uiFeatureSettingsService.SetGpsIntervalSyncEnabled(vm.SyncPhoneGpsIntervalFromApp);
        _uiFeatureSettingsService.SetShowWifiCounts(vm.ShowWifiCounts);
        _uiFeatureSettingsService.SetBleScanControlsEnabled(vm.EnableBleScanOtaControls);
        TempData["SettingsSaved"] = "Display settings saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("/api/settings/tracker-home-mode")]
    public IActionResult TrackerHomeMode()
    {
        return Json(new
        {
            enabled = _configuration.GetValue("DirectGps:AutoModeEnabled", false),
            homeWifiSsids = _homeWifi.GetSsids(),
            trackerEntityId = _configuration["DirectGps:AutoModeDeviceTrackerEntityId"] ?? "A52",
            homeStates = (_configuration["DirectGps:AutoModeHomeStates"] ?? "home,Paxcounter Zone")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            homeIntervalSeconds = Math.Clamp(GetConfiguredInt("DirectGps:AutoModeHomeIntervalSeconds", 30), 5, 3600),
            awayIntervalSeconds = Math.Clamp(GetConfiguredInt("DirectGps:AutoModeAwayIntervalSeconds", 5), 5, 3600)
        });
    }

    private int GetConfiguredInt(string key, int fallback)
        => int.TryParse(_configuration[key], out var parsed) ? parsed : fallback;

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult HomeWifi(string? homeWifiSsidsText, bool homeWifiEnabled)
    {
        try
        {
            _homeWifi.Save(homeWifiSsidsText, homeWifiEnabled);
            TempData["SettingsSaved"] = "Home Wi-Fi networks saved. Phones use the new settings on their next check.";
        }
        catch (ArgumentException exception)
        {
            TempData["HomeWifiError"] = exception.Message;
            // Keep invalid input visible so it can be corrected.
            TempData["HomeWifiInput"] = (homeWifiSsidsText ?? "")[..Math.Min(homeWifiSsidsText?.Length ?? 0, 8192)];
        }
        return RedirectToAction(nameof(Index));
    }
}

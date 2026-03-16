using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;

namespace PaxCounterWeb.Controllers;

public class SettingsController : Controller
{
    private readonly GpsDisplaySettingsService _gpsDisplaySettingsService;

    public SettingsController(GpsDisplaySettingsService gpsDisplaySettingsService)
    {
        _gpsDisplaySettingsService = gpsDisplaySettingsService;
    }

    [HttpGet]
    public IActionResult Index()
    {
        return View(new DisplaySettingsViewModel
        {
            GpsMode = _gpsDisplaySettingsService.GetMode()
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Index(DisplaySettingsViewModel vm)
    {
        _gpsDisplaySettingsService.SetMode(vm.GpsMode);
        TempData["SettingsSaved"] = "Display settings saved.";
        return RedirectToAction(nameof(Index));
    }
}


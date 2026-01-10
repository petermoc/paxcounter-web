using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

public class DebugController : Controller
{
    private readonly PaxSimulatorService _simulator;

    public DebugController(PaxSimulatorService simulator)
    {
        _simulator = simulator;
    }

    [HttpPost]
    public async Task<IActionResult> GenerateSample(int deviceId)
    {
        await _simulator.GenerateSampleAsync(deviceId);
        return RedirectToAction("Details", "Devices", new { id = deviceId });
    }
}

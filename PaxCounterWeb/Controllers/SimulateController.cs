using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

public class SimulateController : Controller
{
    private readonly PaxSimulatorService _simulator;

    public SimulateController(PaxSimulatorService sim)
    {
        _simulator = sim;
    }

    [HttpPost]
    public async Task<IActionResult> GenerateSample(int deviceId)
    {
        await _simulator.GenerateSampleAsync(deviceId);
        return RedirectToAction("Details", new { id = deviceId });
    }
}

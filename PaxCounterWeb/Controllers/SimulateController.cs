using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

public class SimulateController : Controller
{
    private readonly PaxSimulatorService _sim;

    public SimulateController(PaxSimulatorService sim)
    {
        _sim = sim;
    }

    public async Task<IActionResult> Generate()
    {
        await _sim.GenerateSampleAsync();
        return RedirectToAction("Index", "Devices");
    }
}

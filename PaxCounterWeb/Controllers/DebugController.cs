using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

public class DebugController : Controller
{
    private readonly PaxSimulatorService _sim;

    public DebugController(PaxSimulatorService sim)
    {
        _sim = sim;
    }

    public async Task<IActionResult> Generate()
    {
        await _sim.GenerateSampleAsync();
        return Content("Sample generated");
    }
}

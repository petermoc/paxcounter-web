using Microsoft.AspNetCore.Mvc;
using PaxCounterWeb.Services;

namespace PaxCounterWeb.Controllers;

public class WebhookLogsController : Controller
{
    private readonly WebhookLogStore _logStore;

    public WebhookLogsController(WebhookLogStore logStore)
    {
        _logStore = logStore;
    }

    public IActionResult Index()
    {
        var logs = _logStore.GetLatest();
        return View(logs);
    }
}

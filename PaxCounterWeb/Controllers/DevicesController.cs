using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;
using PaxCounterWeb.Services;
using PaxCounterWeb.ViewModels;

namespace PaxCounterWeb.Controllers;

public class DevicesController : Controller
{
    private readonly AppDbContext _db;
    private readonly PaxSimulatorService _simulator;

    public DevicesController(AppDbContext db, PaxSimulatorService simulator)
    {
        _db = db;
        _simulator = simulator;
    }

    public async Task<IActionResult> Index()
    {
        var devices = await _db.Devices
            .Include(d => d.PaxSamples)
            .ToListAsync();

        return View(devices);
    }


    [HttpPost]
    public async Task<IActionResult> GenerateSample()
    {
        await _simulator.GenerateSampleAsync();
        return RedirectToAction("Index");
    }

    //public async Task<IActionResult> Details(int id)
    //{
    //    var device = await _db.Devices
    //        .Include(d => d.PaxSamples.OrderByDescending(p => p.Timestamp).Take(50))
    //        .FirstOrDefaultAsync(d => d.Id == id);

    //    if (device == null) return NotFound();
    //    return View(device);
    //}


    public async Task<IActionResult> Details(int id)
    {
        var device = await _db.Devices
            .Include(d => d.PaxSamples)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (device == null)
            return NotFound();

        var vm = new DeviceDetailsViewModel
        {
            Id = device.Id,
            Name = device.Name,
            Samples = device.PaxSamples
                .OrderByDescending(p => p.Timestamp)
                .Take(50)
                .Select(p => new PaxSampleViewModel
                {
                    Timestamp = p.Timestamp,
                    WifiCount = p.WifiCount,
                    BleCount = p.BleCount,
                    RssiLimit = p.RssiLimit
                })
                .ToList()
        };

        return View(vm);
    }


    [HttpGet]
    public async Task<IActionResult> HeatmapData(int id)
    {
        var device = await _db.Devices
            .Include(d => d.PaxSamples)
            .FirstOrDefaultAsync(d => d.Id == id);

        if (device == null)
            return NotFound();

        var data = device.PaxSamples
            .OrderByDescending(p => p.Timestamp)
            .Take(100)
            .Select(p => new
            {
                lat = device.Latitude,
                lon = device.Longitude,
                intensity = p.WifiCount + p.BleCount,
                rssi = p.RssiLimit,
                timestamp = p.Timestamp
            });

        return Json(data);
    }


}


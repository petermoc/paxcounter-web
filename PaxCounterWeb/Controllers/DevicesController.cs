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
    public async Task<IActionResult> GenerateSampleForDevice(int deviceId)
    {
        await _simulator.GenerateSampleAsync(deviceId);
        return RedirectToAction("Details", new { id = deviceId });
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

        var samples = device.PaxSamples
        .Select(p => new PaxSampleViewModel
        {
            Timestamp = p.Timestamp,
            WifiCount = p.WifiCount,
            BleCount = p.BleCount,
            RssiLimit = p.RssiLimit
        })
        .ToList();

        var vm = new DeviceDetailsViewModel
        {
            DeviceId = device.Id,
            Name = device.Name,
            Latitude = device.Latitude,
            Longitude = device.Longitude,

            SamplesAsc = samples
                .OrderBy(s => s.Timestamp)
                .ToList(),

            SamplesDesc = samples
                .OrderByDescending(s => s.Timestamp)
                .Take(10)
                .ToList(),

            TotalSamples = samples.Count
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

    [HttpPost]
    public async Task<IActionResult> GenerateSampleAjax(int deviceId)
    {
        await _simulator.GenerateSampleAsync(deviceId);

        var latest = await _db.PaxSamples
            .Where(p => p.DeviceId == deviceId)
            .OrderByDescending(p => p.Timestamp)
            .FirstAsync();

        /*return Json(new
        {
            time = latest.Timestamp.ToLocalTime().ToString("HH:mm:ss"),
            wifi = latest.WifiCount,
            ble = latest.BleCount,
            rssi = latest.RssiLimit
        });*/
        return Ok(new { success = true });
    }






}


using Microsoft.EntityFrameworkCore;
using PaxCounterWeb.Data.PaxCounterWeb.Data;
using PaxCounterWeb.Models;


namespace PaxCounterWeb.Services;

public class PaxSimulatorService
{
    private readonly AppDbContext _db;
    private readonly Random _rnd = new();

    public PaxSimulatorService(AppDbContext db)
    {
        _db = db;
    }

    //int[] rssiLevels = { -65, -75, -85, -95 };

    public async Task GenerateSampleAsync(int deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device == null) return;

        var rssiLevels = new[] { -65, -75, -85, -95 };
        var rssi = rssiLevels[_rnd.Next(rssiLevels.Length)];

        var sample = new PaxSample
        {
            DeviceId = deviceId,
            Timestamp = DateTime.UtcNow,
            WifiCount = _rnd.Next(0, 5),
            BleCount = _rnd.Next(5, 20),
            RssiLimit = rssi
        };

        _db.PaxSamples.Add(sample);
        await _db.SaveChangesAsync();
    }


}

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

    public async Task GenerateSampleAsync()
    {
        var device = _db.Devices.First();

        var sample = new PaxSample
        {
            DeviceId = device.Id,
            Timestamp = DateTime.UtcNow,
            WifiCount = _rnd.Next(0, 5),
            BleCount = _rnd.Next(5, 20),
            RssiLimit = -65
        };

        _db.PaxSamples.Add(sample);
        await _db.SaveChangesAsync();
    }
}

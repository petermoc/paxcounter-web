using System.Text.Json;

namespace PaxCounterWeb.Services;

// Durable desired state: phones poll it over their existing authenticated HTTPS link.
public sealed class PhoneGpsControlStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IConfiguration _configuration;
    private Dictionary<string, PhoneGpsControl> _phones;

    public PhoneGpsControlStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _configuration = configuration;
        _path = Path.Combine(environment.ContentRootPath, "Data", "phone-gps-controls.json");
        _phones = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, PhoneGpsControl>>(File.ReadAllText(_path))
                ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public PhoneGpsControl Get(string phoneId)
    {
        lock (_gate)
        {
            return _phones.GetValueOrDefault(phoneId.ToUpperInvariant()) ?? new();
        }
    }

    public void SetInterval(string phoneId, int seconds, bool highAccuracy)
        => Update(phoneId, current => current with { IntervalSeconds = Math.Clamp(seconds, 5, 3600),
            HighAccuracy = highAccuracy, BoostUntilUtc = null });

    public void Boost(string phoneId)
        => Update(phoneId, current => current with { IntervalSeconds = null,
            BoostUntilUtc = DateTimeOffset.UtcNow.AddSeconds(120), RefreshId = Guid.NewGuid().ToString("N") });

    public void StopBoost(string phoneId)
        => Update(phoneId, current => current with { BoostUntilUtc = null, HighAccuracy = false });

    public bool IsHome(string? ssid, double? latitude, double? longitude)
    {
        var homeSsids = HomeWifiSettingsService.ReadSsids(_configuration);
        if (!string.IsNullOrWhiteSpace(ssid) && homeSsids.Contains(ssid.Trim('"'), StringComparer.Ordinal))
            return true;
        var homeLatitude = _configuration.GetValue<double?>("DirectGps:HomeLatitude");
        var homeLongitude = _configuration.GetValue<double?>("DirectGps:HomeLongitude");
        if (latitude is null || longitude is null || homeLatitude is null || homeLongitude is null) return false;
        var dLat = (latitude.Value - homeLatitude.Value) * Math.PI / 180;
        var dLon = (longitude.Value - homeLongitude.Value) * Math.PI / 180;
        var a = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(latitude.Value * Math.PI / 180)
            * Math.Cos(homeLatitude.Value * Math.PI / 180) * Math.Pow(Math.Sin(dLon / 2), 2);
        var distance = 6371000 * 2 * Math.Asin(Math.Sqrt(Math.Clamp(a, 0, 1)));
        return distance <= _configuration.GetValue("DirectGps:HomeRadiusMeters", 150);
    }

    private void Update(string phoneId, Func<PhoneGpsControl, PhoneGpsControl> update)
    {
        lock (_gate)
        {
            var updated = new Dictionary<string, PhoneGpsControl>(_phones, StringComparer.OrdinalIgnoreCase);
            var key = phoneId.ToUpperInvariant();
            updated[key] = update(updated.GetValueOrDefault(key) ?? new());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(updated));
            File.Move(_path + ".tmp", _path, true);
            _phones = updated;
        }
    }
}

public sealed record PhoneGpsControl
{
    public int? IntervalSeconds { get; init; }
    public bool HighAccuracy { get; init; } = true;
    public DateTimeOffset? BoostUntilUtc { get; init; }
    public string RefreshId { get; init; } = "";
}

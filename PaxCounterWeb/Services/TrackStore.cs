using System.Collections.Concurrent;
using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public class TrackPoint
{
    public DateTime TimestampUtc { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int Accuracy { get; set; }
    public int Ble { get; set; }
    public int Wifi { get; set; }
    public int Pax { get; set; }
    public string DeviceId { get; set; } = "";
}

public class TrackStore
{
    private readonly object _lock = new();
    private readonly List<TrackPoint> _track = new();
    private readonly Dictionary<string, PaxMessage> _lastPaxByDevice = new();
    private readonly HashSet<string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private string _lastPaxDeviceId = "";

    public void UpdatePax(string deviceId, PaxMessage pax)
    {
        lock (_lock)
        {
            _lastPaxByDevice[deviceId] = pax;
            _knownDevices.Add(deviceId);
            _lastPaxDeviceId = deviceId;
        }
    }

    public void SetKnownDevices(IEnumerable<string> deviceIds)
    {
        lock (_lock)
        {
            foreach (var id in deviceIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _knownDevices.Add(id);
                }
            }
        }
    }

    public List<TrackPoint> AddGps(GpsMessage gps)
    {
        lock (_lock)
        {
            var added = new List<TrackPoint>();
            var targetDevices = _knownDevices
                .Concat(_lastPaxByDevice.Keys)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (targetDevices.Count == 0)
            {
                targetDevices.Add(string.IsNullOrWhiteSpace(_lastPaxDeviceId) ? "unknown" : _lastPaxDeviceId);
            }

            var now = DateTime.UtcNow;
            foreach (var deviceId in targetDevices)
            {
                _lastPaxByDevice.TryGetValue(deviceId, out var pax);
                pax ??= new PaxMessage();

                var point = new TrackPoint
                {
                    TimestampUtc = now,
                    Lat = gps.lat,
                    Lon = gps.lon,
                    Accuracy = gps.accuracy,
                    Ble = pax.ble,
                    Wifi = pax.wifi,
                    Pax = pax.pax,
                    DeviceId = deviceId
                };

                _track.Add(point);
                added.Add(point);
            }

            if (_track.Count > 5000)
            {
                _track.RemoveRange(0, _track.Count - 5000);
            }

            return added;
        }
    }

    public List<TrackPoint> GetTrack(DateTime? sinceUtc = null, string? deviceId = null)
    {
        lock (_lock)
        {
            IEnumerable<TrackPoint> query = _track;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                query = query.Where(t => string.Equals(t.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            }
            if (sinceUtc != null)
            {
                query = query.Where(t => t.TimestampUtc >= sinceUtc.Value);
            }
            return query.ToList();
        }
    }

    public List<string> GetDevices()
    {
        lock (_lock)
        {
            return _track.Select(t => t.DeviceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id)
                .ToList();
        }
    }

    public TrackPoint? GetLastPointForDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        lock (_lock)
        {
            return _track
                .Where(t => string.Equals(t.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.TimestampUtc)
                .FirstOrDefault();
        }
    }
}

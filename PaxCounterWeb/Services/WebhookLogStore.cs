using System.Collections.Concurrent;

namespace PaxCounterWeb.Services;

public class WebhookLogStore
{
    private const int MaxEntries = 200;
    private readonly ConcurrentQueue<WebhookLogEntry> _entries = new();

    public IReadOnlyList<WebhookLogEntry> GetLatest()
    {
        return _entries.Reverse().ToList();
    }

    public void Add(WebhookLogEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }
    }
}

public class WebhookLogEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string EventName { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DevEui { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

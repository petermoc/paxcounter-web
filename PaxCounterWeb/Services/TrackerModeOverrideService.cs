namespace PaxCounterWeb.Services;

public sealed class TrackerModeOverrideService
{
    private readonly object _gate = new();
    private string? _mode;

    public string? GetMode()
    {
        lock (_gate)
        {
            return _mode;
        }
    }

    public void SetMode(string? mode)
    {
        lock (_gate)
        {
            _mode = Normalize(mode);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _mode = null;
        }
    }

    private static string? Normalize(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "home" => "home",
        "away" => "away",
        _ => null
    };
}

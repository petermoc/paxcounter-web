using System.Text;
using System.Text.Json;

namespace PaxCounterWeb.Services;

public sealed class HomeWifiSettingsService(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly object _gate = new();

    public static string[] ReadSsids(IConfiguration config)
    {
        var names = config.GetSection("DirectGps:HomeWifiSsids").Get<string[]>() ?? [];
        // Configuration merges arrays by index; a count prevents deleted entries
        // in earlier providers from reappearing when this list is shortened.
        var count = config.GetValue<int?>("DirectGps:HomeWifiSsidCount");
        return count.HasValue ? names.Take(Math.Max(0, count.Value)).ToArray() : names;
    }
    public string[] GetSsids() => ReadSsids(configuration);
    public bool Enabled => configuration.GetValue("DirectGps:AutoModeEnabled", false);

    public static string[] ParseSsids(string? text)
    {
        if (text?.Length > 8192) throw new ArgumentException("The network list is too long.");
        var names = (text ?? "").Split('\n').Select(line => line.Trim())
            .Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length > 64) throw new ArgumentException("Enter at most 64 home networks.");
        if (names.Any(name => Encoding.UTF8.GetByteCount(name) > 32 || name.Any(char.IsControl)))
            throw new ArgumentException("Each Wi-Fi name must contain at most 32 UTF-8 bytes and no control characters.");
        return names;
    }

    public void Save(string? text, bool enabled)
    {
        var names = ParseSsids(text);
        if (enabled && names.Length == 0)
            throw new ArgumentException("Add at least one home Wi-Fi network, or turn automatic home mode off.");
        lock (_gate)
        {
            var path = Path.Combine(environment.ContentRootPath, "appsettings.homewifi.local.json");
            var json = JsonSerializer.Serialize(new { DirectGps = new { HomeWifiSsids = names, HomeWifiSsidCount = names.Length, AutoModeEnabled = enabled } },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
            // Make the saved list effective immediately, including removal of names.
            ((IConfigurationRoot)configuration).Reload();
        }
    }
}

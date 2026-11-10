using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace PaxCounterWeb.Services;

public sealed class TrackerHomeModeSettingsService
{
    private const string EnabledCookie = "tracker_home_mode_enabled";
    private const string SsidsCookie = "tracker_home_mode_ssids";
    private const string SsidCookie = "tracker_home_mode_ssid";
    private const string BssidCookie = "tracker_home_mode_bssid";
    private const string HomeIntervalCookie = "tracker_home_mode_home_interval";
    private const string AwayIntervalCookie = "tracker_home_mode_away_interval";
    private const string CurrentModeCookie = "tracker_home_mode_current_mode";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public TrackerHomeModeSettingsService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public TrackerHomeModeSettings GetSettings()
    {
        var ctx = _httpContextAccessor.HttpContext;

        return new TrackerHomeModeSettings
        {
            Enabled = GetBoolCookie(ctx, EnabledCookie, false),
            HomeWifiSsids = GetSsidList(ctx),
            HomeWifiSsid = GetPrimarySsid(ctx),
            HomeWifiBssid = GetStringCookie(ctx, BssidCookie),
            HomeIntervalSeconds = GetIntCookie(ctx, HomeIntervalCookie, 300, 30, 3600),
            AwayIntervalSeconds = GetIntCookie(ctx, AwayIntervalCookie, 10, 10, 3600),
            CurrentMode = GetModeCookie(ctx)
        };
    }

    public void SetSettings(TrackerHomeModeSettings settings)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null)
        {
            return;
        }

        SetCookie(ctx, EnabledCookie, settings.Enabled.ToString());
        var normalizedSsids = NormalizeSsids(settings.HomeWifiSsids, settings.HomeWifiSsid);
        SetCookie(ctx, SsidsCookie, JsonSerializer.Serialize(normalizedSsids));
        SetCookie(ctx, SsidCookie, normalizedSsids.FirstOrDefault() ?? string.Empty);
        SetCookie(ctx, BssidCookie, settings.HomeWifiBssid?.Trim() ?? string.Empty);
        SetCookie(ctx, HomeIntervalCookie, Math.Clamp(settings.HomeIntervalSeconds, 30, 3600).ToString());
        SetCookie(ctx, AwayIntervalCookie, Math.Clamp(settings.AwayIntervalSeconds, 10, 3600).ToString());
        SetCurrentMode(settings.CurrentMode);
    }

    public void SetCurrentMode(string? mode)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null)
        {
            return;
        }

        SetCookie(ctx, CurrentModeCookie, NormalizeMode(mode));
    }

    public static string NormalizeMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "home" => "home",
            _ => "away"
        };
    }

    private static bool GetBoolCookie(HttpContext? ctx, string name, bool defaultValue)
    {
        var raw = ctx?.Request.Cookies[name];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private static int GetIntCookie(HttpContext? ctx, string name, int defaultValue, int min, int max)
    {
        var raw = ctx?.Request.Cookies[name];
        if (!int.TryParse(raw, out var parsed))
        {
            return defaultValue;
        }

        return Math.Clamp(parsed, min, max);
    }

    private static string GetStringCookie(HttpContext? ctx, string name)
    {
        return ctx?.Request.Cookies[name] ?? string.Empty;
    }

    private static string GetModeCookie(HttpContext? ctx)
    {
        return NormalizeMode(GetStringCookie(ctx, CurrentModeCookie));
    }

    private static List<string> GetSsidList(HttpContext? ctx)
    {
        var raw = GetStringCookie(ctx, SsidsCookie);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(raw);
                var normalized = NormalizeSsids(parsed);
                if (normalized.Count > 0)
                {
                    return normalized;
                }
            }
            catch
            {
                // Fall back to the legacy single-SSID cookie below.
            }
        }

        return NormalizeSsids(new[] { GetStringCookie(ctx, SsidCookie) });
    }

    private static string GetPrimarySsid(HttpContext? ctx)
    {
        return GetSsidList(ctx).FirstOrDefault() ?? string.Empty;
    }

    private static List<string> NormalizeSsids(IEnumerable<string>? ssids, string? fallback = null)
    {
        var values = (ssids ?? Enumerable.Empty<string>())
            .Append(fallback ?? string.Empty)
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Cast<string>()
            .ToList();

        return values;
    }

    private static void SetCookie(HttpContext ctx, string name, string value)
    {
        ctx.Response.Cookies.Append(
            name,
            value,
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(2),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = false
            });
    }
}

public sealed class TrackerHomeModeSettings
{
    public bool Enabled { get; set; }
    public List<string> HomeWifiSsids { get; set; } = new();
    public string HomeWifiSsid { get; set; } = string.Empty;
    public string HomeWifiBssid { get; set; } = string.Empty;
    public int HomeIntervalSeconds { get; set; } = 300;
    public int AwayIntervalSeconds { get; set; } = 10;
    public string CurrentMode { get; set; } = "away";
}

using Microsoft.AspNetCore.Http;

namespace PaxCounterWeb.Services;

public class UiFeatureSettingsService
{
    private const string RlimCookie = "ui_enable_rlim_ota";
    private const string GpsSyncCookie = "ui_sync_phone_gps_interval";
    private const string GpsPhoneCookie = "ui_selected_gps_phone";
    private const string ShowWifiCountsCookie = "ui_show_wifi_counts";
    private const string BleScanOtaCookie = "ui_enable_ble_scan_ota";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public UiFeatureSettingsService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public bool GetRlimControlsEnabled() => GetBoolCookie(RlimCookie, false);
    public bool GetGpsIntervalSyncEnabled() => GetBoolCookie(GpsSyncCookie, true);
    public string GetSelectedGpsPhone() => GetStringCookie(GpsPhoneCookie, "A52");
    public bool GetShowWifiCounts() => GetBoolCookie(ShowWifiCountsCookie, true);
    public bool GetBleScanControlsEnabled() => GetBoolCookie(BleScanOtaCookie, true);

    public void SetRlimControlsEnabled(bool enabled) => SetBoolCookie(RlimCookie, enabled);
    public void SetGpsIntervalSyncEnabled(bool enabled) => SetBoolCookie(GpsSyncCookie, enabled);
    public void SetSelectedGpsPhone(string phone) => SetStringCookie(GpsPhoneCookie, NormalizePhone(phone));
    public void SetShowWifiCounts(bool enabled) => SetBoolCookie(ShowWifiCountsCookie, enabled);
    public void SetBleScanControlsEnabled(bool enabled) => SetBoolCookie(BleScanOtaCookie, enabled);

    private bool GetBoolCookie(string name, bool defaultValue)
    {
        var ctx = _httpContextAccessor.HttpContext;
        var raw = ctx?.Request.Cookies[name];
        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
    }

    private string GetStringCookie(string name, string defaultValue)
    {
        var ctx = _httpContextAccessor.HttpContext;
        var raw = ctx?.Request.Cookies[name];
        return string.IsNullOrWhiteSpace(raw) ? defaultValue : NormalizePhone(raw);
    }

    private void SetBoolCookie(string name, bool value)
        => SetStringCookie(name, value.ToString());

    private void SetStringCookie(string name, string value)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null)
        {
            return;
        }

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

    private static string NormalizePhone(string? phone)
    {
        return string.Equals(phone, "S23", StringComparison.OrdinalIgnoreCase) ? "S23" : "A52";
    }
}


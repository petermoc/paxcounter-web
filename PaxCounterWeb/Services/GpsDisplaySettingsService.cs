using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public class GpsDisplaySettingsService
{
    private const string CookieName = "gps_display_mode";
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GpsDisplaySettingsService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public GpsDisplayMode GetMode()
    {
        var ctx = _httpContextAccessor.HttpContext;
        var raw = ctx?.Request.Cookies[CookieName];
        return ParseMode(raw);
    }

    public void SetMode(GpsDisplayMode mode)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null)
        {
            return;
        }

        ctx.Response.Cookies.Append(
            CookieName,
            mode.ToString(),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(2),
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                HttpOnly = false
            });
    }

    public static GpsDisplayMode ParseMode(string? raw)
    {
        if (Enum.TryParse<GpsDisplayMode>(raw, true, out var mode))
        {
            return mode;
        }
        return GpsDisplayMode.Decimal;
    }
}


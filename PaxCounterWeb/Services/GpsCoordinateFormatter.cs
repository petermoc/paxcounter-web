using PaxCounterWeb.Models;

namespace PaxCounterWeb.Services;

public static class GpsCoordinateFormatter
{
    public static string FormatLatitude(double? lat, GpsDisplayMode mode)
    {
        return FormatCoordinate(lat, true, mode);
    }

    public static string FormatLongitude(double? lon, GpsDisplayMode mode)
    {
        return FormatCoordinate(lon, false, mode);
    }

    private static string FormatCoordinate(double? value, bool isLatitude, GpsDisplayMode mode)
    {
        if (!value.HasValue)
        {
            return "-";
        }

        var decimalText = value.Value.ToString("0.000000");
        var dmsText = ToDms(value.Value, isLatitude);

        return mode switch
        {
            GpsDisplayMode.Dms => dmsText,
            GpsDisplayMode.Both => $"{decimalText} ({dmsText})",
            _ => decimalText
        };
    }

    private static string ToDms(double value, bool isLatitude)
    {
        var abs = Math.Abs(value);
        var deg = (int)Math.Floor(abs);
        var minFloat = (abs - deg) * 60.0;
        var min = (int)Math.Floor(minFloat);
        var sec = (minFloat - min) * 60.0;

        var hemi = isLatitude
            ? (value >= 0 ? "N" : "S")
            : (value >= 0 ? "E" : "W");

        return $"{deg}°{min:00}'{sec:00.0}\"{hemi}";
    }
}


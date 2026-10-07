using System;
using System.Globalization;
using System.Linq;

namespace Sidera.Desktop.Sessions;

/// <summary>Coordinates as people write them: hours and degrees, as a decimal number or as hours minutes seconds ("05:35:17", "-05 23 28"), and shown the way an atlas shows them.</summary>
public static class SkyFormat
{
    /// <summary>"05h 35m 17s".</summary>
    public static string Ra(double hours)
    {
        var total = (int)Math.Round(((hours % 24) + 24) % 24 * 3600) % 86400;
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}h {total / 60 % 60:00}m {total % 60:00}s");
    }

    /// <summary>"−05° 23′ 28″" (a real minus sign).</summary>
    public static string Dec(double degrees)
    {
        var sign = degrees < 0 ? "−" : "+";
        var total = (int)Math.Round(Math.Abs(degrees) * 3600);
        return string.Create(CultureInfo.InvariantCulture, $"{sign}{total / 3600:00}° {total / 60 % 60:00}′ {total % 60:00}″");
    }

    /// <summary>A number of hours: "5.588", "05:35:17", "5 35 17.5".</summary>
    public static bool TryParseHours(string? text, out double hours) => TryParse(text, out hours);

    /// <summary>A number of degrees: "-5.39", "-05:23:28", "-5 23 28".</summary>
    public static bool TryParseDegrees(string? text, out double degrees) => TryParse(text, out degrees);

    private static bool TryParse(string? text, out double value)
    {
        value = 0;
        var trimmed = text?.Trim().Replace('−', '-');
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return double.IsFinite(value);
        }

        var negative = trimmed.StartsWith('-');
        var parts = trimmed.TrimStart('-', '+').Split([':', ' ', 'h', 'm', 's', '°', '′', '″', '\'', '"'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var numbers = new double[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] < 0 || !double.IsFinite(numbers[i]))
            {
                return false;
            }
        }

        if (numbers[1] >= 60 || numbers[2] >= 60)
        {
            return false;
        }

        value = (numbers[0] + (numbers[1] / 60) + (numbers[2] / 3600)) * (negative ? -1 : 1);
        return true;
    }
}

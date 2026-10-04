using System.Globalization;

namespace Sidera.Core.Location;

/// <summary>
/// How latitude, longitude and elevation are written and read: "47.7192° N", "122.4194° W", "410 m". The values stay signed
/// degrees everywhere else; this is the one place where the signs turn into letters and back, so that no view model does it its own way.
/// </summary>
public static class GeoCoordinateFormat
{
    public static string FormatLatitude(double latitudeDegrees) => Format(latitudeDegrees, 'N', 'S');

    public static string FormatLongitude(double longitudeDegrees) => Format(longitudeDegrees, 'E', 'W');

    public static string FormatElevation(double elevationMeters) =>
        string.Create(CultureInfo.InvariantCulture, $"{elevationMeters:0} m");

    private static string Format(double degrees, char positive, char negative)
    {
        var letter = degrees < 0 ? negative : positive;
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(degrees):0.0000}° {letter}");
    }

    /// <summary>The signed decimal degrees of "47.7192° N", "33.8688 S", "47.7192" or "-33.8688"; the range is checked.</summary>
    public static bool TryParseLatitude(string? text, out double degrees, out string? problem) =>
        TryParse(text, 'N', 'S', 90, "latitude", out degrees, out problem);

    /// <summary>The signed decimal degrees of "7.8231° E", "122.4194 W", "7.8231" or "-122.4194": east is positive, west negative.</summary>
    public static bool TryParseLongitude(string? text, out double degrees, out string? problem) =>
        TryParse(text, 'E', 'W', 180, "longitude", out degrees, out problem);

    /// <summary>Meters, with or without the unit ("410", "410 m").</summary>
    public static bool TryParseElevation(string? text, out double meters, out string? problem)
    {
        meters = 0;
        problem = null;
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.EndsWith('m') || trimmed.EndsWith('M'))
        {
            trimmed = trimmed[..^1].Trim();
        }

        if (trimmed.Length == 0 || !TryNumber(trimmed, out meters))
        {
            problem = "The elevation must be a number of meters.";
            return false;
        }

        problem = ObservingSite.Problem(0, 0, meters);
        return problem is null;
    }

    private static bool TryParse(string? text, char positive, char negative, double limit, string what, out double degrees, out string? problem)
    {
        degrees = 0;
        problem = null;
        var trimmed = (text ?? string.Empty).Replace("°", " ", StringComparison.Ordinal).Trim();
        if (trimmed.Length == 0)
        {
            problem = $"The {what} is empty.";
            return false;
        }

        var sign = 0;
        var last = char.ToUpperInvariant(trimmed[^1]);
        var first = char.ToUpperInvariant(trimmed[0]);
        if (last == positive || last == negative)
        {
            sign = last == positive ? 1 : -1;
            trimmed = trimmed[..^1].Trim();
        }
        else if (first == positive || first == negative)
        {
            sign = first == positive ? 1 : -1;
            trimmed = trimmed[1..].Trim();
        }

        if (!TryNumber(trimmed, out var value))
        {
            problem = $"The {what} must be a number of degrees, for example {(positive == 'N' ? "47.7192° N" : "7.8231° E")}.";
            return false;
        }

        if (sign != 0 && (trimmed.StartsWith('-') || trimmed.StartsWith('+')))
        {
            problem = $"The {what} has a sign and a letter; use one of them.";
            return false;
        }

        if (sign != 0)
        {
            value *= sign;
        }

        if (!double.IsFinite(value) || Math.Abs(value) > limit)
        {
            problem = $"The {what} must be from -{limit:0}° to +{limit:0}°.";
            return false;
        }

        degrees = value;
        return true;
    }

    // A decimal point or a decimal comma; never a thousands separator.
    private static bool TryNumber(string text, out double value)
    {
        var normalized = text.Contains('.') ? text : text.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }
}

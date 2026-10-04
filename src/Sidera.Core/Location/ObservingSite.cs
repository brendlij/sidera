using System.Globalization;

namespace Sidera.Core.Location;

/// <summary>
/// Where the observing takes place, backend-neutral. Latitude is WGS84 geodetic latitude in degrees, from -90 to +90, north
/// positive. Longitude is WGS84 geodetic longitude in degrees, from -180 to +180, east positive and west negative. Elevation is
/// in meters above mean sea level. The signs are the whole convention: "N", "S", "E" and "W" are presentation (see
/// <see cref="GeoCoordinateFormat"/>). Nothing is clamped or wrapped: a value that is out of range is refused.
/// </summary>
public sealed record ObservingSite
{
    /// <summary>The lowest elevation Sidera accepts: below the Dead Sea shore (about -430 m), with a margin for mines and driver noise.</summary>
    public const double MinimumElevationMeters = -500;

    /// <summary>The highest elevation Sidera accepts: above the highest observatories on earth (about 5,600 m).</summary>
    public const double MaximumElevationMeters = 9000;

    /// <exception cref="ArgumentOutOfRangeException">A value is out of range or not a finite number.</exception>
    public ObservingSite(double latitudeDegrees, double longitudeDegrees, double elevationMeters, string? name = null)
    {
        if (Problem(latitudeDegrees, longitudeDegrees, elevationMeters) is { } problem)
        {
            throw new ArgumentOutOfRangeException(null, problem);
        }

        LatitudeDegrees = latitudeDegrees;
        LongitudeDegrees = longitudeDegrees;
        ElevationMeters = elevationMeters;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>A name the user gave the place; <c>null</c> when there is none.</summary>
    public string? Name { get; }

    public double LatitudeDegrees { get; }

    public double LongitudeDegrees { get; }

    public double ElevationMeters { get; }

    /// <summary>The sentence that says what is wrong with the values, or <c>null</c> when they are a place.</summary>
    public static string? Problem(double latitudeDegrees, double longitudeDegrees, double elevationMeters)
    {
        if (!double.IsFinite(latitudeDegrees) || latitudeDegrees < -90 || latitudeDegrees > 90)
        {
            return "The latitude must be from -90° to +90°.";
        }

        if (!double.IsFinite(longitudeDegrees) || longitudeDegrees < -180 || longitudeDegrees > 180)
        {
            return "The longitude must be from -180° to +180°.";
        }

        if (!double.IsFinite(elevationMeters) || elevationMeters < MinimumElevationMeters || elevationMeters > MaximumElevationMeters)
        {
            return string.Create(
                CultureInfo.InvariantCulture, $"The elevation must be from {MinimumElevationMeters:0} m to {MaximumElevationMeters:0} m.");
        }

        return null;
    }

    public static bool TryCreate(double latitudeDegrees, double longitudeDegrees, double elevationMeters, string? name, out ObservingSite? site, out string? problem)
    {
        problem = Problem(latitudeDegrees, longitudeDegrees, elevationMeters);
        site = problem is null ? new ObservingSite(latitudeDegrees, longitudeDegrees, elevationMeters, name) : null;
        return problem is null;
    }

    /// <summary>
    /// Wraps a longitude that is outside of -180..+180 into that range (190° becomes -170°). Only for input that is known to count
    /// around the circle, such as a sum of angles; a longitude that a user or a driver reported is validated, never wrapped.
    /// </summary>
    public static double WrapLongitude(double longitudeDegrees)
    {
        if (!double.IsFinite(longitudeDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(longitudeDegrees), "The longitude must be a finite number.");
        }

        var wrapped = (longitudeDegrees + 180) % 360;
        if (wrapped < 0)
        {
            wrapped += 360;
        }

        return wrapped - 180;
    }

    /// <summary>The same place with another name.</summary>
    public ObservingSite WithName(string? name) => new(LatitudeDegrees, LongitudeDegrees, ElevationMeters, name);
}

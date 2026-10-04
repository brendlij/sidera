using Sidera.Core.Astronomy;

namespace Sidera.Core.Location;

/// <summary>How much two places that should be the same differ.</summary>
public enum SiteMismatchSeverity
{
    /// <summary>The same place for every purpose of Sidera: GPS noise and rounding.</summary>
    None,

    /// <summary>A different place nearby (some kilometers): a stale setting, a move within the site.</summary>
    Minor,

    /// <summary>Far away, or hundreds of meters higher or lower: pointing and rise and set times would be off.</summary>
    Major,
}

/// <summary>
/// The comparison of the observing site of Sidera with the one of a mount. The policy lives here and not in a view: the places are the same
/// when they are less than <see cref="HorizontalToleranceMeters"/> apart and differ in elevation by less than
/// <see cref="ElevationToleranceMeters"/>. That is wider than a consumer GPS error (about 5 to 10 m) and than the rounding of a mount that stores
/// arcseconds (about 30 m), and narrower than anything that matters for pointing (100 m is 3 arcseconds of latitude).
/// </summary>
/// <param name="HorizontalDistanceMeters">The great-circle distance on a sphere (haversine), in meters.</param>
/// <param name="ElevationDifferenceMeters">The absolute difference of the elevations, in meters.</param>
public sealed record SiteComparison(double HorizontalDistanceMeters, double ElevationDifferenceMeters, bool IsEquivalent, SiteMismatchSeverity Severity)
{
    public const double HorizontalToleranceMeters = 100;

    public const double ElevationToleranceMeters = 100;

    /// <summary>From this horizontal distance, or this difference in elevation, a mismatch is major.</summary>
    public const double MajorHorizontalMeters = 10_000;

    public const double MajorElevationMeters = 500;

    public static SiteComparison Compare(ObservingSite a, ObservingSite b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var horizontal = DistanceMeters(a.LatitudeDegrees, a.LongitudeDegrees, b.LatitudeDegrees, b.LongitudeDegrees);
        var elevation = Math.Abs(a.ElevationMeters - b.ElevationMeters);
        var equivalent = horizontal < HorizontalToleranceMeters && elevation < ElevationToleranceMeters;
        var severity = equivalent ? SiteMismatchSeverity.None
            : horizontal >= MajorHorizontalMeters || elevation >= MajorElevationMeters ? SiteMismatchSeverity.Major
            : SiteMismatchSeverity.Minor;
        return new SiteComparison(horizontal, elevation, equivalent, severity);
    }

    /// <summary>The haversine distance on a sphere of the mean radius of the earth: good to a fraction of a percent, which is all a comparison needs.</summary>
    public static double DistanceMeters(double latitude1Degrees, double longitude1Degrees, double latitude2Degrees, double longitude2Degrees)
    {
        static double Rad(double degrees) => degrees * Math.PI / 180;
        var phi1 = Rad(latitude1Degrees);
        var phi2 = Rad(latitude2Degrees);
        var dPhi = phi2 - phi1;
        var dLambda = Rad(longitude2Degrees - longitude1Degrees);
        var h = Math.Pow(Math.Sin(dPhi / 2), 2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Pow(Math.Sin(dLambda / 2), 2);
        return 2 * AstronomyConstants.EarthMeanRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }
}

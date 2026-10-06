using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Core.Astronomy;

/// <summary>
/// The altitude of the Sun and of a target above the horizon, from the site and the time. Geometric altitudes (no atmospheric refraction): twilight is defined that way (the Sun's centre at
/// -6°, -12° and -18°), and a threshold on a target is a planning limit, not an observation. The sidereal time is the one the meridian flip uses; nothing here reads a mount.
/// </summary>
public static class SkyAltitude
{
    private const double Radians = Math.PI / 180;

    /// <summary>The altitude in degrees of a point with the given right ascension (hours) and declination (degrees), at a place and a time.</summary>
    public static double TargetDegrees(double rightAscensionHours, double declinationDegrees, DateTime utc, ObservingSite site)
    {
        var hourAngle = MeridianFlipTiming.HourAngleHours(rightAscensionHours, utc, site.LongitudeDegrees) * 15 * Radians;
        return AltitudeDegrees(site.LatitudeDegrees * Radians, declinationDegrees * Radians, hourAngle);
    }

    /// <summary>
    /// The altitude of the centre of the Sun in degrees. The position is the low-precision solar formula of the Astronomical Almanac (accurate to about 0.01° from 1950 to 2050), which is far
    /// below what a twilight threshold needs: 0.01° of altitude is a few seconds of time around dusk.
    /// </summary>
    public static double SunDegrees(DateTime utc, ObservingSite site)
    {
        var (rightAscensionHours, declinationDegrees) = SunPosition(utc);
        return TargetDegrees(rightAscensionHours, declinationDegrees, utc, site);
    }

    /// <summary>The Sun's right ascension in hours and declination in degrees (of date, apparent to about 0.01°).</summary>
    public static (double RightAscensionHours, double DeclinationDegrees) SunPosition(DateTime utc)
    {
        var n = utc.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        var meanLongitude = Normalize360(280.460 + 0.9856474 * n);
        var meanAnomaly = Normalize360(357.528 + 0.9856003 * n) * Radians;
        var eclipticLongitude = (meanLongitude + 1.915 * Math.Sin(meanAnomaly) + 0.020 * Math.Sin(2 * meanAnomaly)) * Radians;
        var obliquity = (23.439 - 0.0000004 * n) * Radians;
        var rightAscension = Math.Atan2(Math.Cos(obliquity) * Math.Sin(eclipticLongitude), Math.Cos(eclipticLongitude));
        var declination = Math.Asin(Math.Sin(obliquity) * Math.Sin(eclipticLongitude));
        return (Normalize360(rightAscension / Radians) / 15, declination / Radians);
    }

    private static double AltitudeDegrees(double latitude, double declination, double hourAngle)
    {
        var sine = Math.Sin(latitude) * Math.Sin(declination) + Math.Cos(latitude) * Math.Cos(declination) * Math.Cos(hourAngle);
        return Math.Asin(Math.Clamp(sine, -1, 1)) / Radians;
    }

    private static double Normalize360(double degrees)
    {
        var d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }
}

/// <summary>How dark it has to be: the altitude of the Sun's centre that ends the day's light for the purpose.</summary>
public enum Twilight
{
    /// <summary>The Sun at -6°: bright planets and stars show.</summary>
    Civil,

    /// <summary>The Sun at -12°: the horizon is no longer visible at sea.</summary>
    Nautical,

    /// <summary>The Sun at -18°: the sky is as dark as it gets; the usual start of deep-sky imaging.</summary>
    Astronomical
}

public static class TwilightExtensions
{
    /// <summary>The altitude of the Sun that ends this twilight: -6°, -12° or -18°.</summary>
    public static double ThresholdDegrees(this Twilight twilight) => twilight switch
    {
        Twilight.Civil => -6,
        Twilight.Nautical => -12,
        Twilight.Astronomical => -18,
        _ => throw new ArgumentOutOfRangeException(nameof(twilight)),
    };

    public static string Title(this Twilight twilight) => twilight switch
    {
        Twilight.Civil => "civil",
        Twilight.Nautical => "nautical",
        _ => "astronomical",
    };
}

/// <summary>Whether the Sun crossed the altitude, did not, and if not why: it was above it the whole time, or below it.</summary>
public enum SunCrossingState
{
    /// <summary>The Sun crosses the altitude at <see cref="SunCrossing.Utc"/>.</summary>
    Found,

    /// <summary>The Sun stays above the altitude for the whole search (polar day, or a summer night that never gets dark enough).</summary>
    AlwaysAbove,

    /// <summary>The Sun stays below the altitude for the whole search (polar night, or a winter day that never gets bright enough).</summary>
    AlwaysBelow
}

/// <summary>The next time the Sun crosses an altitude, or the reason there is none. A time is never made up.</summary>
public readonly record struct SunCrossing(SunCrossingState State, DateTime? Utc)
{
    public bool IsFound => State == SunCrossingState.Found;
}

/// <summary>Finds when the Sun crosses an altitude: dusk is the Sun going down through it, dawn is the Sun coming up through it.</summary>
public static class SunCrossings
{
    /// <summary>The hours that are searched when nothing else is asked: a day and a night, and some.</summary>
    public const double DefaultSearchHours = 48;

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Precision = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The first time after <paramref name="fromUtc"/> that the Sun's centre goes <paramref name="rising"/> (up) or down through <paramref name="thresholdDegrees"/>. The search steps through
    /// two-minute intervals (the Sun moves at most a quarter of a degree in that time) and refines a crossing to a second. A dip of less than a minute that grazes the altitude can be missed;
    /// that is a night that is not dark in any useful sense.
    /// </summary>
    public static SunCrossing Next(ObservingSite site, DateTime fromUtc, double thresholdDegrees, bool rising, double searchHours = DefaultSearchHours)
    {
        var start = fromUtc.ToUniversalTime();
        var end = start + TimeSpan.FromHours(searchHours);
        var previousTime = start;
        var previous = SkyAltitude.SunDegrees(start, site) - thresholdDegrees;
        var sawAbove = previous >= 0;
        var sawBelow = previous < 0;
        for (var t = start + Step; ; t += Step)
        {
            if (t > end)
            {
                t = end;
            }

            var current = SkyAltitude.SunDegrees(t, site) - thresholdDegrees;
            sawAbove |= current >= 0;
            sawBelow |= current < 0;
            var crossed = rising ? previous < 0 && current >= 0 : previous >= 0 && current < 0;
            if (crossed)
            {
                return new SunCrossing(SunCrossingState.Found, Refine(site, previousTime, t, thresholdDegrees, rising));
            }

            if (t >= end)
            {
                break;
            }

            previous = current;
            previousTime = t;
        }

        return new SunCrossing(sawBelow ? SunCrossingState.AlwaysBelow : SunCrossingState.AlwaysAbove, null);
    }

    /// <summary>The next dusk of <paramref name="twilight"/>: the Sun going down through its altitude.</summary>
    public static SunCrossing NextDusk(ObservingSite site, DateTime fromUtc, Twilight twilight, double searchHours = DefaultSearchHours) =>
        Next(site, fromUtc, twilight.ThresholdDegrees(), rising: false, searchHours);

    /// <summary>The next dawn of <paramref name="twilight"/>: the Sun coming up through its altitude.</summary>
    public static SunCrossing NextDawn(ObservingSite site, DateTime fromUtc, Twilight twilight, double searchHours = DefaultSearchHours) =>
        Next(site, fromUtc, twilight.ThresholdDegrees(), rising: true, searchHours);

    private static DateTime Refine(ObservingSite site, DateTime before, DateTime after, double threshold, bool rising)
    {
        // The crossing is between the two: bisect until it is known to a second.
        while (after - before > Precision)
        {
            var middle = before + (after - before) / 2;
            var above = SkyAltitude.SunDegrees(middle, site) - threshold >= 0;
            if (above == rising)
            {
                after = middle;
            }
            else
            {
                before = middle;
            }
        }

        return after;
    }
}

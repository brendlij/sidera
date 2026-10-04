namespace Astra.Core.Mounts;

/// <summary>
/// A light check before a slew that a person asked for by hand: is the target far from where the mount points now? It knows nothing
/// of any backend and decides nothing about horizons or limits, it only says "this is a big move, ask first".
/// </summary>
public static class SlewSafety
{
    /// <summary>A move of more than this many degrees on the sky has to be confirmed.</summary>
    public const double LargeSlewDegrees = 2.0;

    /// <summary>The angle between two points on the sky, in degrees (great circle).</summary>
    public static double SeparationDegrees(CelestialCoordinates from, CelestialCoordinates to)
    {
        var ra1 = from.RightAscensionHours * 15 * Math.PI / 180;
        var ra2 = to.RightAscensionHours * 15 * Math.PI / 180;
        var dec1 = from.DeclinationDegrees * Math.PI / 180;
        var dec2 = to.DeclinationDegrees * Math.PI / 180;

        // The haversine form is exact for small angles too, which is where the small adjustments are.
        var a = Math.Pow(Math.Sin((dec2 - dec1) / 2), 2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Pow(Math.Sin((ra2 - ra1) / 2), 2);
        return 2 * Math.Asin(Math.Min(1, Math.Sqrt(a))) * 180 / Math.PI;
    }

    public static bool IsLargeSlew(CelestialCoordinates from, CelestialCoordinates to, double thresholdDegrees = LargeSlewDegrees) =>
        SeparationDegrees(from, to) > thresholdDegrees;
}

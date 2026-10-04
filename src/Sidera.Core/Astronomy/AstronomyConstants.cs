namespace Sidera.Core.Astronomy;

/// <summary>Named constants of the astronomical and geodetic calculations, so that no formula carries a magic number.</summary>
public static class AstronomyConstants
{
    /// <summary>Arcseconds in one radian: 180 / pi * 3600.</summary>
    public const double ArcsecondsPerRadian = 206264.806247;

    /// <summary>
    /// The pixel scale in arcseconds per pixel of a pixel of 1 micrometer behind 1 millimeter of focal length: the arcseconds
    /// per radian divided by the 1000 micrometers in a millimeter. Multiply by the pixel size in micrometers, divide by the focal length in millimeters.
    /// </summary>
    public const double ArcsecondsPerPixelPerMicrometerPerMillimeter = ArcsecondsPerRadian / 1000;

    /// <summary>The mean radius of the earth in meters (IUGG), for the spherical distance between two places.</summary>
    public const double EarthMeanRadiusMeters = 6_371_008.8;
}

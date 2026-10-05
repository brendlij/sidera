using Sidera.Core.Mounts;

namespace Sidera.Core.Astrometry;

/// <summary>
/// The spherical geometry of pointing: angular separation and the correction of a pointing error. Right ascension is in hours and
/// declination in degrees, as in <see cref="CelestialCoordinates"/>; every other angle here is in degrees, and the conversions between hours,
/// degrees, arcminutes and arcseconds are named constants and functions, never numbers in a formula. Nothing is computed as a flat
/// difference of right ascensions: a degree of right ascension is a different length at every declination, and 23.9 h is next to 0.1 h.
/// </summary>
public static class SkyMath
{
    public const double DegreesPerHour = 15.0;
    public const double ArcsecondsPerDegree = 3600.0;
    public const double ArcminutesPerDegree = 60.0;

    /// <summary>
    /// A rotation as Sidera states it everywhere (the plate solver's result, a framing's desired rotation): degrees in (-180, 180], where 180 and -180 are the same angle
    /// and are written as 180.
    /// </summary>
    public static double NormalizeRotationDegrees(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }
        else if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }

        return wrapped;
    }

    /// <summary>
    /// The signed angle that turns the rotation <paramref name="fromDegrees"/> into <paramref name="toDegrees"/> by the short way, in (-180, 180]: 179° to -179° is +2°, not -358°.
    /// </summary>
    public static double RotationDifferenceDegrees(double fromDegrees, double toDegrees) => NormalizeRotationDegrees(toDegrees - fromDegrees);

    public static double HoursToDegrees(double hours) => hours * DegreesPerHour;

    public static double DegreesToHours(double degrees) => degrees / DegreesPerHour;

    public static double ArcsecondsToDegrees(double arcseconds) => arcseconds / ArcsecondsPerDegree;

    public static double DegreesToArcseconds(double degrees) => degrees * ArcsecondsPerDegree;

    /// <summary>Right ascension in degrees (any value, also negative or beyond 360) as hours in [0, 24).</summary>
    public static double WrapRightAscensionHours(double rightAscensionDegrees)
    {
        var hours = DegreesToHours(rightAscensionDegrees) % 24.0;
        if (hours < 0)
        {
            hours += 24.0;
        }

        // A value like 23.9999999999999 + rounding must not become 24: the type refuses it.
        return hours >= 24.0 ? 0.0 : hours;
    }

    /// <summary>The coordinates of a position given in degrees of right ascension (wrapped) and declination.</summary>
    public static CelestialCoordinates FromDegrees(double rightAscensionDegrees, double declinationDegrees) =>
        new(WrapRightAscensionHours(rightAscensionDegrees), declinationDegrees);

    /// <summary>The angle between two positions on the sky, in degrees, from 0 to 180: exact for tiny and for large separations, at any declination.</summary>
    public static double AngularSeparationDegrees(CelestialCoordinates a, CelestialCoordinates b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (ax, ay, az) = Unit(a);
        var (bx, by, bz) = Unit(b);

        // atan2 of the length of the cross product and the dot product: accurate where acos of the dot product is not.
        var cx = ay * bz - az * by;
        var cy = az * bx - ax * bz;
        var cz = ax * by - ay * bx;
        var cross = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        var dot = ax * bx + ay * by + az * bz;
        return Math.Atan2(cross, dot) * 180.0 / Math.PI;
    }

    /// <summary>
    /// Where <paramref name="point"/> lies in the tangent plane at <paramref name="center"/>: <c>Xi</c> towards the east and <c>Eta</c> towards
    /// the north, in degrees (the angle whose tangent is the plane coordinate). Exact in the middle, a small error towards the edge of a field of a few degrees.
    /// </summary>
    public static (double Xi, double Eta) TangentOffsetDegrees(CelestialCoordinates center, CelestialCoordinates point)
    {
        ArgumentNullException.ThrowIfNull(center);
        ArgumentNullException.ThrowIfNull(point);
        var c = Unit(center);
        var p = Unit(point);
        var (east, north) = Axes(center);
        var along = Dot(p, c);
        if (along <= 1e-9)
        {
            throw new ArgumentException("The point is not in the hemisphere around the center: it has no tangent-plane position.", nameof(point));
        }

        return (Math.Atan(Dot(p, east) / along) * 180.0 / Math.PI, Math.Atan(Dot(p, north) / along) * 180.0 / Math.PI);
    }

    /// <summary>The position at a tangent-plane offset (degrees towards the east and the north) from <paramref name="center"/>; the inverse of <see cref="TangentOffsetDegrees"/>.</summary>
    public static CelestialCoordinates FromTangentOffset(CelestialCoordinates center, double xiDegrees, double etaDegrees)
    {
        ArgumentNullException.ThrowIfNull(center);
        var c = Unit(center);
        var (east, north) = Axes(center);
        var xi = Math.Tan(xiDegrees * Math.PI / 180.0);
        var eta = Math.Tan(etaDegrees * Math.PI / 180.0);
        var x = c.X + xi * east.X + eta * north.X;
        var y = c.Y + xi * east.Y + eta * north.Y;
        var z = c.Z + xi * east.Z + eta * north.Z;
        var length = Math.Sqrt(x * x + y * y + z * z);
        x /= length;
        y /= length;
        z /= length;
        var declination = Math.Asin(Math.Clamp(z, -1.0, 1.0)) * 180.0 / Math.PI;
        var rightAscension = Math.Atan2(y, x) * 180.0 / Math.PI;
        return FromDegrees(rightAscension, declination);
    }

    /// <summary>
    /// The position to command so that the mount ends up at <paramref name="target"/>, when it was commanded to <paramref name="commanded"/> and
    /// a plate solve found it at <paramref name="solved"/>: the target moved by the opposite of the pointing offset (the offset of the solved position
    /// from the commanded one, in the tangent plane). It assumes the offset is the same for the small step of a correction, which is what a
    /// pointing error of an uncalibrated mount is. Nothing is synchronized in the mount.
    /// </summary>
    public static CelestialCoordinates CorrectedTarget(CelestialCoordinates commanded, CelestialCoordinates solved, CelestialCoordinates target)
    {
        var (xi, eta) = TangentOffsetDegrees(commanded, solved);
        return FromTangentOffset(target, -xi, -eta);
    }

    private static (double X, double Y, double Z) Unit(CelestialCoordinates c)
    {
        var ra = HoursToDegrees(c.RightAscensionHours) * Math.PI / 180.0;
        var dec = c.DeclinationDegrees * Math.PI / 180.0;
        return (Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec));
    }

    private static (double X, double Y, double Z) EastVector(double raRadians) => (-Math.Sin(raRadians), Math.Cos(raRadians), 0);

    private static ((double X, double Y, double Z) East, (double X, double Y, double Z) North) Axes(CelestialCoordinates c)
    {
        var ra = HoursToDegrees(c.RightAscensionHours) * Math.PI / 180.0;
        var dec = c.DeclinationDegrees * Math.PI / 180.0;
        var north = (-Math.Sin(dec) * Math.Cos(ra), -Math.Sin(dec) * Math.Sin(ra), Math.Cos(dec));
        return (EastVector(ra), north);
    }

    private static double Dot((double X, double Y, double Z) a, (double X, double Y, double Z) b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

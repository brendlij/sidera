using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Core.Framing;

/// <summary>
/// Where a rig should point and how the frame should be turned: a plan, independent of any hardware. It keeps only what is chosen (a name, the center, the
/// desired rotation, the rig, where the object came from); the field of view is never kept, because it follows from the rig as it is configured now
/// (see <see cref="FramingGeometry"/>). The rotation is the one of the plate solver: <see cref="PlateSolveResult.RotationDegrees"/> and
/// <see cref="DesiredRotationDegrees"/> are the same quantity, in (-180, 180], and can be subtracted to see how far a frame is from the plan.
/// </summary>
public sealed record FramingTarget
{
    public FramingTarget(
        string name, CelestialCoordinates center, double desiredRotationDegrees = 0, RigId? rigId = null, string? catalogId = null, string? surveyId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(center);
        if (!double.IsFinite(desiredRotationDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(desiredRotationDegrees), "The rotation must be a finite number of degrees.");
        }

        Name = name.Trim();
        Center = center;
        DesiredRotationDegrees = SkyMath.NormalizeRotationDegrees(desiredRotationDegrees);
        RigId = rigId;
        CatalogId = string.IsNullOrWhiteSpace(catalogId) ? null : catalogId.Trim();
        SurveyId = string.IsNullOrWhiteSpace(surveyId) ? null : surveyId.Trim();
    }

    public string Name { get; }

    public CelestialCoordinates Center { get; }

    /// <summary>The angle from the top of the frame to celestial north, counterclockwise as the frame is seen, in (-180, 180].</summary>
    public double DesiredRotationDegrees { get; }

    /// <summary>The rig the plan is for; <c>null</c> while none is chosen.</summary>
    public RigId? RigId { get; }

    /// <summary>The identifier of the object in the catalog it was found in ("M31", "NGC 7000"); <c>null</c> for a place that was just chosen.</summary>
    public string? CatalogId { get; }

    /// <summary>The survey the target was framed on; informational, it does not make the plan depend on that survey.</summary>
    public string? SurveyId { get; }

    public FramingTarget WithCenter(CelestialCoordinates center) => new(Name, center, DesiredRotationDegrees, RigId, CatalogId, SurveyId);

    public FramingTarget WithRotation(double rotationDegrees) => new(Name, Center, rotationDegrees, RigId, CatalogId, SurveyId);

    public FramingTarget WithRig(RigId? rigId) => new(Name, Center, DesiredRotationDegrees, rigId, CatalogId, SurveyId);

    /// <summary>The rotation of a solved frame minus the desired one, in (-180, 180]: positive when the frame is turned further counterclockwise than planned.</summary>
    public double RotationDifferenceDegrees(double solvedRotationDegrees) =>
        SkyMath.NormalizeRotationDegrees(solvedRotationDegrees - DesiredRotationDegrees);
}

/// <summary>The size of the field that a rig sees, from its optical train: nothing here is stored, it is read from <see cref="OpticalTrainGeometry"/> each time.</summary>
public sealed record RigField(double WidthDegrees, double HeightDegrees)
{
    /// <summary>The field of a rig as it is now; <c>null</c> while the focal length or the sensor is not known.</summary>
    public static RigField? From(OpticalTrainGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return geometry is { FieldOfViewXDegrees: { } w, FieldOfViewYDegrees: { } h } && w > 0 && h > 0 ? new RigField(w, h) : null;
    }
}

/// <summary>
/// The geometry of a frame on the sky, in the tangent plane at its center (the projection of a plate solver and of FITS WCS), so that the field is a rectangle that
/// is rotated and not a box of right ascensions and declinations: it is right at any declination and across 0 h. Plane coordinates are degrees, <c>Xi</c> towards
/// the east and <c>Eta</c> towards the north (<see cref="SkyMath.TangentOffsetDegrees"/>).
/// <para>
/// Orientation: at rotation 0 the top of the frame is north and the right of the frame is west (east is to the left, as on the sky). At a rotation <c>r</c>
/// north is <c>r</c> degrees counterclockwise of the top. So the top of the frame points to (-sin r, cos r) and its right to (-cos r, -sin r) in (Xi, Eta).
/// </para>
/// </summary>
public static class FramingGeometry
{
    /// <summary>The offset in the tangent plane of a point of the frame: <paramref name="right"/> degrees to the right of the center and <paramref name="up"/> degrees up.</summary>
    public static (double Xi, double Eta) PlaneOffset(double right, double up, double rotationDegrees)
    {
        var r = rotationDegrees * Math.PI / 180.0;
        var (sin, cos) = (Math.Sin(r), Math.Cos(r));
        return (right * -cos + up * -sin, right * -sin + up * cos);
    }

    /// <summary>The position on the sky of a point of the frame (degrees right and up from its center).</summary>
    public static CelestialCoordinates PointOnSky(CelestialCoordinates center, RigField field, double rightFraction, double upFraction, double rotationDegrees)
    {
        var (xi, eta) = PlaneOffset(rightFraction * field.WidthDegrees, upFraction * field.HeightDegrees, rotationDegrees);
        return SkyMath.FromTangentOffset(center, xi, eta);
    }

    /// <summary>The four corners of the frame on the sky: top left, top right, bottom right, bottom left.</summary>
    public static IReadOnlyList<CelestialCoordinates> Corners(CelestialCoordinates center, RigField field, double rotationDegrees) =>
    [
        PointOnSky(center, field, -0.5, 0.5, rotationDegrees),
        PointOnSky(center, field, 0.5, 0.5, rotationDegrees),
        PointOnSky(center, field, 0.5, -0.5, rotationDegrees),
        PointOnSky(center, field, -0.5, -0.5, rotationDegrees),
    ];
}

/// <summary>
/// A view of the sky as a picture: the tangent plane at <see cref="Center"/> at a scale of degrees per pixel, north up and east to the left (the way a chart of the
/// sky is drawn), <see cref="WidthPixels"/> by <see cref="HeightPixels"/>. Converts between positions on the sky and pixels of the picture; used to draw the field on a
/// survey image and to read a drag back as a position.
/// </summary>
public sealed record SkyViewport(CelestialCoordinates Center, double DegreesPerPixel, int WidthPixels, int HeightPixels)
{
    public double FieldWidthDegrees => DegreesPerPixel * WidthPixels;

    public double FieldHeightDegrees => DegreesPerPixel * HeightPixels;

    /// <summary>The pixel of a position, or <c>null</c> for a position that is not in the hemisphere around the center (it has no place in this plane).</summary>
    public (double X, double Y)? ToPixel(CelestialCoordinates position)
    {
        ArgumentNullException.ThrowIfNull(position);
        try
        {
            var (xi, eta) = SkyMath.TangentOffsetDegrees(Center, position);
            return (WidthPixels / 2.0 - xi / DegreesPerPixel, HeightPixels / 2.0 - eta / DegreesPerPixel);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The position under a pixel; the inverse of <see cref="ToPixel"/>.</summary>
    public CelestialCoordinates ToSky(double x, double y) =>
        SkyMath.FromTangentOffset(Center, (WidthPixels / 2.0 - x) * DegreesPerPixel, (HeightPixels / 2.0 - y) * DegreesPerPixel);

    /// <summary>The outline of a frame in pixels: the corners of <see cref="FramingGeometry.Corners"/>; empty when one is not in the plane.</summary>
    public IReadOnlyList<(double X, double Y)> Outline(CelestialCoordinates frameCenter, RigField field, double rotationDegrees)
    {
        var outline = new List<(double X, double Y)>(4);
        foreach (var corner in FramingGeometry.Corners(frameCenter, field, rotationDegrees))
        {
            if (ToPixel(corner) is not { } pixel)
            {
                return [];
            }

            outline.Add(pixel);
        }

        return outline;
    }

    /// <summary>The same view moved so that <paramref name="x"/>,<paramref name="y"/> stays under the pointer after a drag of <paramref name="dx"/>,<paramref name="dy"/> pixels.</summary>
    public SkyViewport Panned(double dx, double dy) => this with { Center = ToSky(WidthPixels / 2.0 - dx, HeightPixels / 2.0 - dy) };

    public SkyViewport WithScale(double degreesPerPixel) => this with { DegreesPerPixel = degreesPerPixel };
}

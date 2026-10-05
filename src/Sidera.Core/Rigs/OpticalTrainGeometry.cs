using Sidera.Core.Astronomy;
using Sidera.Core.Cameras;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;

namespace Sidera.Core.Rigs;

/// <summary>Which value of the sensor geometry two sources disagree about.</summary>
public enum GeometryField
{
    WidthPixels,
    HeightPixels,
    PixelSizeXMicrons,
    PixelSizeYMicrons,
}

/// <summary>A value that the camera reports and the Sidera camera database says differently. Neither is replaced: the device's is used, and the difference is shown.</summary>
public sealed record GeometryConflict(GeometryField Field, double DeviceValue, double DatabaseValue)
{
    public bool IsPixelSize => Field is GeometryField.PixelSizeXMicrons or GeometryField.PixelSizeYMicrons;
}

/// <summary>
/// What is known about the sensor of a camera apart from what a user configured: what the camera reports and, for a field it does not report, what the Sidera camera database says
/// about the camera. A value that nobody says is <c>null</c>. Each value has its own source, so the pixel size can come from the database while the pixel counts come from the device.
/// </summary>
public sealed record SensorGeometry(int? WidthPixels, int? HeightPixels, double? PixelSizeXMicrons, double? PixelSizeYMicrons)
{
    /// <summary>Two values are the same when they differ by less than this share of the larger one (rounding in a driver or a data sheet).</summary>
    public const double EquivalenceTolerance = 0.005;

    public GeometrySource WidthSource { get; init; } = GeometrySource.DeviceReported;
    public GeometrySource HeightSource { get; init; } = GeometrySource.DeviceReported;
    public GeometrySource PixelSizeXSource { get; init; } = GeometrySource.DeviceReported;
    public GeometrySource PixelSizeYSource { get; init; } = GeometrySource.DeviceReported;

    /// <summary>The sensor as the driver names it, when it does.</summary>
    public string? SensorName { get; init; }

    /// <summary>The camera database entry that the camera was found as; <c>null</c> when it is not a known camera (or the names fit more than one).</summary>
    public CameraDatabaseEntry? DatabaseEntry { get; init; }

    /// <summary>Where the device and the database say different things; empty without a database entry or without a difference.</summary>
    public IReadOnlyList<GeometryConflict> Conflicts { get; init; } = [];

    /// <summary>The sensor of a camera as its capabilities report it; <c>null</c> without capabilities. Only what the device says.</summary>
    public static SensorGeometry? From(CameraCapabilities? capabilities) => capabilities is null
        ? null
        : new SensorGeometry(
            capabilities.SensorWidth > 0 ? capabilities.SensorWidth : null,
            capabilities.SensorHeight > 0 ? capabilities.SensorHeight : null,
            capabilities.PixelSizeXMicrons is > 0 ? capabilities.PixelSizeXMicrons : null,
            capabilities.PixelSizeYMicrons is > 0 ? capabilities.PixelSizeYMicrons : null)
        {
            SensorName = string.IsNullOrWhiteSpace(capabilities.SensorName) ? null : capabilities.SensorName,
        };

    /// <summary>
    /// The geometry of a camera device: what it reports, completed field by field from the camera database when the camera is a known one (found by its display name and the name and
    /// description its driver reports). A camera that is not connected has no reported values, so a known one still gets its geometry from the database. <c>null</c> when nothing is known.
    /// </summary>
    public static SensorGeometry? For(IDevice? camera, CameraDatabase? database = null)
    {
        if (camera is null)
        {
            return null;
        }

        var capabilities = (camera as ICameraControl)?.Capabilities.Value;
        var entry = (database ?? CameraDatabase.Default).Find([capabilities?.Driver.Description, capabilities?.Driver.Name, camera.Name]);
        return Combine(From(capabilities), entry);
    }

    /// <summary>
    /// The reported geometry with the database entry's values for every field that the device does not report. A reported value always stays; where it differs from the database's
    /// by more than <see cref="EquivalenceTolerance"/> that is listed as a conflict.
    /// </summary>
    public static SensorGeometry? Combine(SensorGeometry? device, CameraDatabaseEntry? entry)
    {
        if (entry is null)
        {
            return device;
        }

        var conflicts = new List<GeometryConflict>();
        var width = Fill(device?.WidthPixels, entry.WidthPixels, GeometryField.WidthPixels, conflicts);
        var height = Fill(device?.HeightPixels, entry.HeightPixels, GeometryField.HeightPixels, conflicts);
        var px = Fill(device?.PixelSizeXMicrons, entry.PixelSizeXMicrometers, GeometryField.PixelSizeXMicrons, conflicts);
        var py = Fill(device?.PixelSizeYMicrons, entry.PixelSizeYMicrometers, GeometryField.PixelSizeYMicrons, conflicts);
        return new SensorGeometry((int)width.Value, (int)height.Value, px.Value, py.Value)
        {
            WidthSource = width.Source,
            HeightSource = height.Source,
            PixelSizeXSource = px.Source,
            PixelSizeYSource = py.Source,
            SensorName = device?.SensorName ?? entry.SensorName,
            DatabaseEntry = entry,
            Conflicts = conflicts,
        };
    }

    private static (double Value, GeometrySource Source) Fill(double? reported, double database, GeometryField field, List<GeometryConflict> conflicts)
    {
        if (reported is not { } value)
        {
            return (database, GeometrySource.Database);
        }

        if (Math.Abs(value - database) > EquivalenceTolerance * Math.Max(value, database))
        {
            conflicts.Add(new GeometryConflict(field, value, database));
        }

        return (value, GeometrySource.DeviceReported);
    }
}

/// <summary>Where a value of the geometry comes from, from the strongest source to the weakest.</summary>
public enum GeometrySource
{
    /// <summary>Nobody has said.</summary>
    Unknown,

    /// <summary>The user configured it for the rig.</summary>
    Configured,

    /// <summary>The camera reported it.</summary>
    DeviceReported,

    /// <summary>It is from the Sidera camera database, for a camera that is a known one.</summary>
    Database,
}

/// <summary>
/// What the optical train of a rig implies: the pixel scale of each axis, the physical size of the sensor and the field of view. All of it is
/// derived, in one place, from the focal length and the sensor, so that the plate solver, the framing and the rig page never carry a formula
/// of their own. A value whose inputs are missing is <c>null</c>: unknown, never zero.
/// <list type="bullet">
/// <item>pixel scale (arcsec per pixel) = 206.264806247 * pixel size (µm) / focal length (mm)</item>
/// <item>sensor size (mm) = pixel size (µm) * pixels / 1000</item>
/// <item>field of view = 2 * atan(sensor size / (2 * focal length)), on each axis and on the diagonal</item>
/// </list>
/// </summary>
public sealed record OpticalTrainGeometry
{
    public double? FocalLengthMm { get; init; }
    public double? PixelSizeXMicrons { get; init; }
    public double? PixelSizeYMicrons { get; init; }
    public int? SensorWidthPixels { get; init; }
    public int? SensorHeightPixels { get; init; }

    /// <summary>Where the pixel size comes from; when X and Y differ in source, the stronger one is named (configured, then device, then database).</summary>
    public GeometrySource PixelSizeSource { get; init; }

    public GeometrySource SensorPixelsSource { get; init; }

    public GeometrySource PixelSizeXSource { get; init; }
    public GeometrySource PixelSizeYSource { get; init; }
    public GeometrySource SensorWidthSource { get; init; }
    public GeometrySource SensorHeightSource { get; init; }

    /// <summary>The sensor as the driver or the database names it.</summary>
    public string? SensorName { get; init; }

    /// <summary>The camera database entry the camera was found as, whether or not a value of it was used.</summary>
    public CameraDatabaseEntry? DatabaseEntry { get; init; }

    /// <summary>Where the device and the camera database disagree. Both are shown; the device's value is the one that is used.</summary>
    public IReadOnlyList<GeometryConflict> Conflicts { get; init; } = [];

    public double? PixelScaleXArcsecPerPixel { get; init; }
    public double? PixelScaleYArcsecPerPixel { get; init; }
    public double? SensorWidthMm { get; init; }
    public double? SensorHeightMm { get; init; }
    public double? FieldOfViewXDegrees { get; init; }
    public double? FieldOfViewYDegrees { get; init; }
    public double? DiagonalFieldOfViewDegrees { get; init; }

    /// <summary>The geometry of the given inputs; what cannot be computed stays unknown.</summary>
    public static OpticalTrainGeometry Compute(
        double? focalLengthMm, double? pixelSizeXMicrons, double? pixelSizeYMicrons, int? sensorWidthPixels, int? sensorHeightPixels)
    {
        var focal = Positive(focalLengthMm);
        var px = Positive(pixelSizeXMicrons);
        var py = Positive(pixelSizeYMicrons);
        var width = sensorWidthPixels is > 0 ? sensorWidthPixels : null;
        var height = sensorHeightPixels is > 0 ? sensorHeightPixels : null;

        double? widthMm = px is { } x && width is { } w ? x * w / 1000 : null;
        double? heightMm = py is { } y && height is { } h ? y * h / 1000 : null;
        return new OpticalTrainGeometry
        {
            FocalLengthMm = focal,
            PixelSizeXMicrons = px,
            PixelSizeYMicrons = py,
            SensorWidthPixels = width,
            SensorHeightPixels = height,
            PixelScaleXArcsecPerPixel = focal is { } f1 && px is { } p1 ? PixelScaleArcsecPerPixel(p1, f1) : null,
            PixelScaleYArcsecPerPixel = focal is { } f2 && py is { } p2 ? PixelScaleArcsecPerPixel(p2, f2) : null,
            SensorWidthMm = widthMm,
            SensorHeightMm = heightMm,
            FieldOfViewXDegrees = focal is { } f3 && widthMm is { } wm ? FieldOfViewDegrees(wm, f3) : null,
            FieldOfViewYDegrees = focal is { } f4 && heightMm is { } hm ? FieldOfViewDegrees(hm, f4) : null,
            DiagonalFieldOfViewDegrees = focal is { } f5 && widthMm is { } wd && heightMm is { } hd
                ? FieldOfViewDegrees(Math.Sqrt(wd * wd + hd * hd), f5)
                : null,
        };
    }

    /// <summary>
    /// The geometry of a rig, one input at a time: the configured value (an explicit override by the user), else what the camera reports, else what the camera database says about a
    /// known camera, else unknown. The strongest source wins for each input, so there is one answer for each and the geometry says which source it took. Nothing of the database is
    /// stored: it is read again each time.
    /// </summary>
    public static OpticalTrainGeometry Resolve(OpticalTrain? configured, SensorGeometry? reported)
    {
        var (px, pxSource) = Pick(configured?.PixelSizeXMicrons, reported?.PixelSizeXMicrons, reported?.PixelSizeXSource);
        var (py, pySource) = Pick(configured?.PixelSizeYMicrons, reported?.PixelSizeYMicrons, reported?.PixelSizeYSource);
        var (width, widthSource) = Pick(configured?.SensorWidthPixels, reported?.WidthPixels, reported?.WidthSource);
        var (height, heightSource) = Pick(configured?.SensorHeightPixels, reported?.HeightPixels, reported?.HeightSource);

        return Compute(configured?.FocalLengthMm, px, py, width, height) with
        {
            PixelSizeSource = Combine(pxSource, pySource),
            SensorPixelsSource = Combine(widthSource, heightSource),
            PixelSizeXSource = pxSource,
            PixelSizeYSource = pySource,
            SensorWidthSource = widthSource,
            SensorHeightSource = heightSource,
            SensorName = reported?.SensorName,
            DatabaseEntry = reported?.DatabaseEntry,
            Conflicts = reported?.Conflicts ?? [],
        };
    }

    /// <summary>The geometry of a rig as a hint for a plate solver, around a center when one is known.</summary>
    public PlateSolveHints ToPlateSolveHints(CelestialCoordinates? approximateCenter = null) =>
        new(approximateCenter, PixelScaleXArcsecPerPixel, PixelScaleYArcsecPerPixel, FieldOfViewXDegrees, FieldOfViewYDegrees, FocalLengthMm);

    /// <summary>Arcseconds per pixel for a pixel of the given size behind the given focal length.</summary>
    public static double PixelScaleArcsecPerPixel(double pixelSizeMicrons, double focalLengthMm) =>
        AstronomyConstants.ArcsecondsPerPixelPerMicrometerPerMillimeter * pixelSizeMicrons / focalLengthMm;

    /// <summary>The angle in degrees that a sensor edge of the given length covers behind the given focal length.</summary>
    public static double FieldOfViewDegrees(double sensorSizeMm, double focalLengthMm) =>
        2 * Math.Atan(sensorSizeMm / (2 * focalLengthMm)) * 180 / Math.PI;

    private static double? Positive(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : null;

    private static (T? Value, GeometrySource Source) Pick<T>(T? configured, T? reported, GeometrySource? reportedSource) where T : struct =>
        configured is not null ? (configured, GeometrySource.Configured)
        : reported is not null ? (reported, reportedSource ?? GeometrySource.DeviceReported)
        : (null, GeometrySource.Unknown);

    private static GeometrySource Combine(GeometrySource a, GeometrySource b) =>
        a == GeometrySource.Configured || b == GeometrySource.Configured ? GeometrySource.Configured
        : a == GeometrySource.DeviceReported || b == GeometrySource.DeviceReported ? GeometrySource.DeviceReported
        : a == GeometrySource.Database || b == GeometrySource.Database ? GeometrySource.Database
        : GeometrySource.Unknown;
}

/// <summary>
/// What Sidera knows that helps a plate solver: where the telescope points about, the scale of the image and the size of the field. Each value
/// is optional; the solver decides what it does with what is missing. Backend-neutral: no solver is named here.
/// </summary>
public sealed record PlateSolveHints(
    CelestialCoordinates? ApproximateCenter,
    double? PixelScaleXArcsecPerPixel,
    double? PixelScaleYArcsecPerPixel,
    double? FieldOfViewXDegrees,
    double? FieldOfViewYDegrees,
    double? FocalLengthMm);

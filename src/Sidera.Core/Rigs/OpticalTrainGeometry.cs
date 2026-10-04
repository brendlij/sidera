using Sidera.Core.Astronomy;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;

namespace Sidera.Core.Rigs;

/// <summary>What a camera says about its sensor. A value it does not say is <c>null</c>.</summary>
public sealed record SensorGeometry(int? WidthPixels, int? HeightPixels, double? PixelSizeXMicrons, double? PixelSizeYMicrons)
{
    /// <summary>The sensor of a camera as its capabilities report it; <c>null</c> without capabilities.</summary>
    public static SensorGeometry? From(CameraCapabilities? capabilities) => capabilities is null
        ? null
        : new SensorGeometry(
            capabilities.SensorWidth > 0 ? capabilities.SensorWidth : null,
            capabilities.SensorHeight > 0 ? capabilities.SensorHeight : null,
            capabilities.PixelSizeXMicrons is > 0 ? capabilities.PixelSizeXMicrons : null,
            capabilities.PixelSizeYMicrons is > 0 ? capabilities.PixelSizeYMicrons : null);
}

/// <summary>Where a value of the geometry comes from.</summary>
public enum GeometrySource
{
    /// <summary>Nobody has said.</summary>
    Unknown,

    /// <summary>The user configured it for the rig.</summary>
    Configured,

    /// <summary>The camera reported it.</summary>
    DeviceReported,
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

    /// <summary>Where the pixel size comes from; when X and Y differ in source, the configured one is named.</summary>
    public GeometrySource PixelSizeSource { get; init; }

    public GeometrySource SensorPixelsSource { get; init; }

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
    /// The geometry of a rig: the configured values, and for each one the configuration leaves out, what the camera reports. A configured
    /// value wins, so there is one answer for each input and the geometry says which source it took.
    /// </summary>
    public static OpticalTrainGeometry Resolve(OpticalTrain? configured, SensorGeometry? reported)
    {
        var (px, pxSource) = Pick(configured?.PixelSizeXMicrons, reported?.PixelSizeXMicrons);
        var (py, pySource) = Pick(configured?.PixelSizeYMicrons, reported?.PixelSizeYMicrons);
        var (width, widthSource) = Pick(configured?.SensorWidthPixels, reported?.WidthPixels);
        var (height, heightSource) = Pick(configured?.SensorHeightPixels, reported?.HeightPixels);

        return Compute(configured?.FocalLengthMm, px, py, width, height) with
        {
            PixelSizeSource = Combine(pxSource, pySource),
            SensorPixelsSource = Combine(widthSource, heightSource),
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

    private static (T? Value, GeometrySource Source) Pick<T>(T? configured, T? reported) where T : struct =>
        configured is not null ? (configured, GeometrySource.Configured)
        : reported is not null ? (reported, GeometrySource.DeviceReported)
        : (null, GeometrySource.Unknown);

    private static GeometrySource Combine(GeometrySource a, GeometrySource b) =>
        a == GeometrySource.Configured || b == GeometrySource.Configured ? GeometrySource.Configured
        : a == GeometrySource.DeviceReported || b == GeometrySource.DeviceReported ? GeometrySource.DeviceReported
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

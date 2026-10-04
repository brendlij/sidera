using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Core.Astrometry;

public sealed record PlateSolveDefaults
{
    public double SearchRadiusDegrees { get; init; } = 10;
    public DownsamplePolicy Downsample { get; init; } = DownsamplePolicy.Auto;
    public bool BlindAllowed { get; init; } = true;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
}

public sealed record PlateSolveOverrides
{
    public PlateSolveHints? Hints { get; init; }
    public double? SearchRadiusDegrees { get; init; }
    public DownsamplePolicy? Downsample { get; init; }
    public bool? BlindAllowed { get; init; }
    public TimeSpan? Timeout { get; init; }
}

/// <summary>One resolution path for manual solves, sequences and centering; geometry belongs to OpticalTrainGeometry.</summary>
public static class PlateSolveHintResolver
{
    public static PlateSolveRequest Resolve(CameraFrame frame, Rig rig, SensorGeometry? sensor,
        CelestialCoordinates? mountCenter, PlateSolveDefaults defaults, PlateSolveOverrides? overrides = null)
    {
        var native = OpticalTrainGeometry.Resolve(rig.Optics, sensor);
        var binX = frame.Acquisition?.BinX ?? 1;
        var binY = frame.Acquisition?.BinY ?? 1;
        var geometry = OpticalTrainGeometry.Compute(native.FocalLengthMm,
            native.PixelSizeXMicrons * binX, native.PixelSizeYMicrons * binY, frame.Width, frame.Height);
        var hints = geometry.ToPlateSolveHints(mountCenter);
        var chosen = overrides?.Hints;
        return new PlateSolveRequest(new PlateSolveImage(frame,
            PixelSizeXMicrons: geometry.PixelSizeXMicrons, PixelSizeYMicrons: geometry.PixelSizeYMicrons))
        {
            ApproximateCenter = chosen?.ApproximateCenter ?? hints.ApproximateCenter,
            PixelScaleXArcsecPerPixel = chosen?.PixelScaleXArcsecPerPixel ?? hints.PixelScaleXArcsecPerPixel,
            PixelScaleYArcsecPerPixel = chosen?.PixelScaleYArcsecPerPixel ?? hints.PixelScaleYArcsecPerPixel,
            FieldOfViewXDegrees = chosen?.FieldOfViewXDegrees ?? hints.FieldOfViewXDegrees,
            FieldOfViewYDegrees = chosen?.FieldOfViewYDegrees ?? hints.FieldOfViewYDegrees,
            FocalLengthMm = chosen?.FocalLengthMm ?? hints.FocalLengthMm,
            SearchRadiusDegrees = overrides?.SearchRadiusDegrees ?? defaults.SearchRadiusDegrees,
            Downsample = overrides?.Downsample ?? defaults.Downsample,
            BlindAllowed = overrides?.BlindAllowed ?? defaults.BlindAllowed,
            Timeout = overrides?.Timeout ?? defaults.Timeout,
        };
    }
}

public sealed record PlateSolveDiagnostics(double? ExpectedScale, double? SolvedScale,
    double? ScaleDifferencePercent, double? ConfiguredFocalLengthMm, double? EstimatedFocalLengthMm, double? FocalDifferencePercent)
{
    public static PlateSolveDiagnostics From(PlateSolveRequest request, PlateSolveResult result)
    {
        var expected = request.PixelScaleXArcsecPerPixel;
        var solved = result.PixelScaleXArcsecPerPixel;
        var estimate = request.Image.PixelSizeXMicrons is > 0 && solved is > 0
            ? Sidera.Core.Astronomy.AstronomyConstants.ArcsecondsPerPixelPerMicrometerPerMillimeter * request.Image.PixelSizeXMicrons / solved : null;
        return new(expected, solved, Difference(expected, solved), request.FocalLengthMm, estimate,
            Difference(request.FocalLengthMm, estimate));
    }

    private static double? Difference(double? configured, double? actual) =>
        configured is > 0 && actual is > 0 ? (actual / configured - 1) * 100 : null;
}

using System;
using Sidera.Core.Astrometry;

namespace Sidera.Desktop.Settings;

public sealed record PlateSolvingSettings
{
    public string Backend { get; init; } = "ASTAP";
    public string? ExecutablePath { get; init; }
    public string? DatabasePath { get; init; }
    public double TimeoutSeconds { get; init; } = 90;
    public double SearchRadiusDegrees { get; init; } = 10;
    public int DownsampleFactor { get; init; }
    public bool BlindFallback { get; init; } = true;
    public double ExposureSeconds { get; init; } = 5;
    public double CenteringToleranceArcseconds { get; init; } = 30;
    public int MaxCenteringAttempts { get; init; } = 5;

    /// <summary>How far a rotation may be off the angle that was asked for, in degrees; half a degree unless chosen.</summary>
    public double RotationToleranceDegrees { get; init; } = Sidera.Runtime.Astrometry.RotationService.DefaultToleranceDegrees;

    /// <summary>How often a rotation is corrected.</summary>
    public int MaxRotationAttempts { get; init; } = Sidera.Runtime.Astrometry.RotationService.DefaultMaxAttempts;

    public string? Problem => Backend != "ASTAP" ? "Select the ASTAP backend."
        : !Valid(TimeoutSeconds, 1, 3600) ? "Timeout must be from 1 to 3600 seconds."
        : !Valid(SearchRadiusDegrees, .01, 180) ? "Search radius must be from 0.01 to 180 degrees."
        : DownsampleFactor is not (0 or 1 or 2 or 4) ? "Downsample must be Auto, 1x, 2x or 4x."
        : !Valid(ExposureSeconds, .001, 3600) ? "Exposure must be from 0.001 to 3600 seconds."
        : !Valid(CenteringToleranceArcseconds, .01, 3600) ? "Tolerance must be from 0.01 to 3600 arcseconds."
        : MaxCenteringAttempts is < 1 or > 100 ? "Centering attempts must be from 1 to 100."
        : !Valid(RotationToleranceDegrees, .001, 90) ? "Rotation tolerance must be from 0.001 to 90 degrees."
        : MaxRotationAttempts is < 1 or > 20 ? "Rotation attempts must be from 1 to 20." : null;

    public PlateSolveDefaults Defaults() => new()
    {
        Timeout = TimeSpan.FromSeconds(TimeoutSeconds), SearchRadiusDegrees = SearchRadiusDegrees,
        Downsample = DownsampleFactor == 0 ? DownsamplePolicy.Auto : DownsamplePolicy.Of(DownsampleFactor), BlindAllowed = BlindFallback
    };
    private static bool Valid(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
}

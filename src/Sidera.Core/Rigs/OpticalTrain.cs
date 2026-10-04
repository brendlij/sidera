namespace Sidera.Core.Rigs;

/// <summary>The optics and sensor geometry of a rig. Plain, immutable data; nothing is derived from it yet.</summary>
public sealed record OpticalTrain
{
    public OpticalTrain(
        double focalLengthMm,
        double apertureMm,
        double pixelSizeMicrons,
        double sensorWidthMm,
        double sensorHeightMm,
        int resolutionWidth,
        int resolutionHeight
    )
    {
        FocalLengthMm = RequirePositive(focalLengthMm, nameof(focalLengthMm));
        ApertureMm = RequirePositive(apertureMm, nameof(apertureMm));
        PixelSizeMicrons = RequirePositive(pixelSizeMicrons, nameof(pixelSizeMicrons));
        SensorWidthMm = RequirePositive(sensorWidthMm, nameof(sensorWidthMm));
        SensorHeightMm = RequirePositive(sensorHeightMm, nameof(sensorHeightMm));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolutionWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(resolutionHeight, 0);
        ResolutionWidth = resolutionWidth;
        ResolutionHeight = resolutionHeight;
    }

    public double FocalLengthMm { get; }
    public double ApertureMm { get; }
    public double PixelSizeMicrons { get; }
    public double SensorWidthMm { get; }
    public double SensorHeightMm { get; }
    public int ResolutionWidth { get; }
    public int ResolutionHeight { get; }

    // Also rejects NaN and infinity.
    private static double RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number greater than zero.");
        }

        return value;
    }
}

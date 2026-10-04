namespace Sidera.Core.Rigs;

/// <summary>
/// The optical train of a rig as it is configured: what a user knows and enters. Only the focal length is required; the aperture,
/// the pixel size and the number of pixels are optional, and a missing one is unknown, not zero. The pixel size has a value for each axis
/// because pixels are not assumed to be square. Nothing derived is kept here (pixel scale, sensor size, field of view): it is computed
/// by <see cref="OpticalTrainGeometry"/> from these inputs and from what the camera reports, so it cannot go out of date.
/// </summary>
public sealed record OpticalTrain
{
    /// <exception cref="ArgumentOutOfRangeException">A value that is given is not a finite number greater than zero.</exception>
    public OpticalTrain(
        double focalLengthMm,
        double? apertureMm = null,
        double? pixelSizeXMicrons = null,
        double? pixelSizeYMicrons = null,
        int? sensorWidthPixels = null,
        int? sensorHeightPixels = null
    )
    {
        FocalLengthMm = RequirePositive(focalLengthMm, nameof(focalLengthMm));
        ApertureMm = Optional(apertureMm, nameof(apertureMm));
        PixelSizeXMicrons = Optional(pixelSizeXMicrons, nameof(pixelSizeXMicrons));
        PixelSizeYMicrons = Optional(pixelSizeYMicrons, nameof(pixelSizeYMicrons));
        SensorWidthPixels = OptionalCount(sensorWidthPixels, nameof(sensorWidthPixels));
        SensorHeightPixels = OptionalCount(sensorHeightPixels, nameof(sensorHeightPixels));
    }

    /// <summary>The focal length of the telescope with its reducer or extender, in millimeters. It belongs to the rig, not to the camera.</summary>
    public double FocalLengthMm { get; }

    public double? ApertureMm { get; }

    /// <summary>The focal ratio; <c>null</c> without an aperture.</summary>
    public double? FocalRatio => ApertureMm is { } aperture ? FocalLengthMm / aperture : null;

    /// <summary>The width of a pixel in micrometers, as configured; <c>null</c> when the camera reports it instead.</summary>
    public double? PixelSizeXMicrons { get; }

    public double? PixelSizeYMicrons { get; }

    public int? SensorWidthPixels { get; }

    public int? SensorHeightPixels { get; }

    // Also rejects NaN and infinity.
    private static double RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number greater than zero.");
        }

        return value;
    }

    private static double? Optional(double? value, string name) => value is null ? null : RequirePositive(value.Value, name);

    private static int? OptionalCount(int? value, string name)
    {
        if (value is <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be greater than zero.");
        }

        return value;
    }
}

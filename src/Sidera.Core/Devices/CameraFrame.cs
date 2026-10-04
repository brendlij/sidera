namespace Sidera.Core.Devices;

/// <summary>
/// A completed camera exposure: raw 16-bit grayscale pixels in row-major order
/// (index = y * Width + x), 0 = black, 65535 = saturated.
/// </summary>
public sealed class CameraFrame
{
    private readonly ushort[] _pixels;

    /// <param name="pixels">Takes ownership of the array; the caller must not modify it afterwards.</param>
    public CameraFrame(int width, int height, ushort[] pixels, TimeSpan exposureDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != width * height)
        {
            throw new ArgumentException(
                $"Expected {width * height} pixels for {width}x{height}, got {pixels.Length}.",
                nameof(pixels)
            );
        }

        Width = width;
        Height = height;
        _pixels = pixels;
        ExposureDuration = exposureDuration;
    }

    public int Width { get; }
    public int Height { get; }
    public TimeSpan ExposureDuration { get; }
    public ReadOnlyMemory<ushort> Pixels => _pixels;

    /// <summary>
    /// The acquisition settings the frame was taken with, as far as the camera reported them; <c>null</c> for a camera that does
    /// not say. Metadata for what comes later (diagnostics, image history, calibration matching): nothing here is derived from pixels.
    /// </summary>
    public FrameAcquisition? Acquisition { get; init; }
}

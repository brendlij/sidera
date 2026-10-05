using System;
using Sidera.Core.Devices;

namespace Sidera.Desktop.Imaging;

/// <summary>The three numbers of an automatic stretch: the value shown as black, the value shown as white (both in ADU) and the midtone balance (0 to 1) that bends everything between.</summary>
public readonly record struct StretchParameters(double Black, double White, double Midtone);

/// <summary>
/// How a frame is made into something to look at. Only for display: no pixel of the frame is ever changed, and what is saved as FITS is the frame as it was taken.
/// <list type="bullet">
/// <item><b>Linear</b>: the darkest value of the frame is black, the brightest is white, and everything in between is proportional.</item>
/// <item><b>Auto stretch</b>: the black point is a little below the background (the median minus 2.8 robust standard deviations, from the median absolute deviation), the white
/// point is the 99.995th percentile (so a hot pixel does not decide it), and a midtone transfer function brings the background to a quarter of the brightness range.</item>
/// </list>
/// Both are deterministic: the same frame always gives the same picture.
/// </summary>
public static class ImageStretch
{
    /// <summary>Where the background (the median) is put in the stretched picture, between 0 and 1.</summary>
    public const double TargetBackground = 0.25;

    private const double ShadowClipSigmas = 2.8;
    private const double WhitePercentile = 0.99995;

    /// <summary>The midtone transfer function: 0 stays 0, 1 stays 1, and <paramref name="midtone"/> is mapped to 0.5.</summary>
    public static double MidtonesTransfer(double midtone, double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        if (midtone == 0.5)
        {
            return x;
        }

        return (midtone - 1) * x / ((2 * midtone - 1) * x - midtone);
    }

    /// <summary>The parameters of the automatic stretch of this frame.</summary>
    public static StretchParameters AutoParameters(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var pixels = frame.Pixels.Span;
        var histogram = Histogram(pixels);
        var median = Percentile(histogram, pixels.Length, 0.5);

        // The median absolute deviation, from the histogram too, so that it does not need a second pass over the pixels.
        var deviations = new long[ushort.MaxValue + 1];
        for (var value = 0; value < histogram.Length; value++)
        {
            if (histogram[value] != 0)
            {
                deviations[Math.Abs(value - median)] += histogram[value];
            }
        }

        var mad = Percentile(deviations, pixels.Length, 0.5);
        var minimum = 0;
        while (minimum < histogram.Length - 1 && histogram[minimum] == 0)
        {
            minimum++;
        }

        var black = Math.Max(minimum, median - ShadowClipSigmas * 1.4826 * mad);
        var white = Math.Max(Percentile(histogram, pixels.Length, WhitePercentile), black + 1);
        var normalizedMedian = Math.Clamp((median - black) / (white - black), 1e-6, 1 - 1e-6);

        // The midtone balance that puts the median at the target: the transfer function is its own inverse with the arguments swapped.
        var midtone = MidtonesTransfer(TargetBackground, normalizedMedian);
        return new StretchParameters(black, white, Math.Clamp(midtone, 1e-6, 1 - 1e-6));
    }

    /// <summary>The picture of the frame in 8-bit gray: auto stretched, or linear from the darkest to the brightest value.</summary>
    public static byte[] ToGray8(CameraFrame frame, bool autoStretch)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var pixels = frame.Pixels.Span;
        var result = new byte[pixels.Length];

        if (autoStretch)
        {
            var p = AutoParameters(frame);
            var lookup = new byte[ushort.MaxValue + 1];
            for (var value = 0; value < lookup.Length; value++)
            {
                var x = (value - p.Black) / (p.White - p.Black);
                lookup[value] = (byte)Math.Round(MidtonesTransfer(p.Midtone, x) * 255);
            }

            for (var i = 0; i < pixels.Length; i++)
            {
                result[i] = lookup[pixels[i]];
            }

            return result;
        }

        ushort min = ushort.MaxValue, max = 0;
        foreach (var pixel in pixels)
        {
            if (pixel < min) min = pixel;
            if (pixel > max) max = pixel;
        }

        var range = Math.Max(max - min, 1);
        for (var i = 0; i < pixels.Length; i++)
        {
            result[i] = (byte)Math.Round((pixels[i] - min) / (double)range * 255);
        }

        return result;
    }

    private static long[] Histogram(ReadOnlySpan<ushort> pixels)
    {
        var histogram = new long[ushort.MaxValue + 1];
        foreach (var pixel in pixels)
        {
            histogram[pixel]++;
        }

        return histogram;
    }

    private static int Percentile(long[] histogram, int total, double fraction)
    {
        var target = (long)Math.Ceiling(total * fraction);
        long seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (seen >= target)
            {
                return value;
            }
        }

        return histogram.Length - 1;
    }
}

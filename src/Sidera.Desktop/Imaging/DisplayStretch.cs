using System;
using Sidera.Core.Devices;

namespace Sidera.Desktop.Imaging;

/// <summary>Preview-only conversion of raw 16-bit data to 8-bit grayscale. The frame is not modified.</summary>
public static class DisplayStretch
{
    private const double BlackPercentile = 0.50; // the median is the sky background
    private const double WhitePercentile = 0.998;

    public static byte[] ToGray8(CameraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var pixels = frame.Pixels.Span;
        var histogram = new int[ushort.MaxValue + 1];
        foreach (var pixel in pixels)
        {
            histogram[pixel]++;
        }

        var black = Percentile(histogram, pixels.Length, BlackPercentile);
        var white = Math.Max(Percentile(histogram, pixels.Length, WhitePercentile), black + 1);

        var result = new byte[pixels.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            var linear = Math.Clamp((pixels[i] - black) / (double)(white - black), 0.0, 1.0);
            result[i] = (byte)Math.Round(Math.Sqrt(linear) * 255);
        }

        return result;
    }

    private static int Percentile(int[] histogram, int total, double fraction)
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

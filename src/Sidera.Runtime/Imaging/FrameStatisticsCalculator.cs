using Sidera.Core.Devices;
using Sidera.Core.Imaging;

namespace Sidera.Runtime.Imaging;

/// <summary>
/// Statistics of a frame from histograms. The pixels are 16-bit, so a histogram of 65536 counts holds everything the
/// statistics need: the frame is read once for it (and once more for the deviations from the median, which are again
/// at most 65535), and nothing is sorted. The background is the median and the noise 1.4826 times the median absolute
/// deviation, so that the stars, which are positive outliers, do not move either.
/// </summary>
public static class FrameStatisticsCalculator
{
    /// <summary>The factor that turns a median absolute deviation into a standard deviation for normally distributed noise.</summary>
    public const double MadToSigma = 1.4826;

    private const int Bins = ushort.MaxValue + 1;
    private const int CancellationChunk = 1 << 20;

    /// <exception cref="OperationCanceledException">The calculation was cancelled.</exception>
    public static FrameStatistics Compute(CameraFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var pixels = frame.Pixels.Span;
        var count = pixels.Length;
        if (count == 0)
        {
            throw new ArgumentException("A frame without pixels has no statistics.", nameof(frame));
        }

        var histogram = new int[Bins];
        for (var start = 0; start < count; start += CancellationChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(count, start + CancellationChunk);
            for (var i = start; i < end; i++)
            {
                histogram[pixels[i]]++;
            }
        }

        var min = 0;
        while (histogram[min] == 0)
        {
            min++;
        }

        var max = Bins - 1;
        while (histogram[max] == 0)
        {
            max--;
        }

        double sum = 0;
        for (var value = min; value <= max; value++)
        {
            sum += (double)value * histogram[value];
        }

        var mean = sum / count;
        double squares = 0;
        for (var value = min; value <= max; value++)
        {
            var deviation = value - mean;
            squares += deviation * deviation * histogram[value];
        }

        var median = Median(histogram, count);

        // The deviations from the median, as a histogram of their own.
        var medianLevel = (int)Math.Round(median);
        var deviations = new int[Bins];
        for (var value = min; value <= max; value++)
        {
            var distance = Math.Abs(value - medianLevel);
            deviations[distance] += histogram[value];
        }

        var mad = Median(deviations, count);

        return new FrameStatistics(count, min, max, mean, median, Math.Sqrt(squares / count), median, MadToSigma * mad);
    }

    // The median of the values a histogram counts; for an even count the mean of the two in the middle.
    private static double Median(int[] histogram, int count)
    {
        var lowIndex = (count - 1) / 2;
        var highIndex = count / 2;
        double low = -1;
        double high = -1;
        long seen = 0;
        for (var value = 0; value < histogram.Length; value++)
        {
            seen += histogram[value];
            if (low < 0 && seen > lowIndex)
            {
                low = value;
            }

            if (seen > highIndex)
            {
                high = value;
                break;
            }
        }

        return (low + high) / 2;
    }
}

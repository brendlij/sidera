using System.Runtime.CompilerServices;
using Astra.Core.Devices;
using Astra.Core.Imaging;

namespace Astra.Runtime.Imaging;

/// <summary>The aggregate of the stars of a frame: what focus and the imaging page show.</summary>
public static class FrameMetricsCalculator
{
    public static FrameMetrics From(FrameStatistics statistics, IReadOnlyList<DetectedStar> stars)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(stars);

        var usable = stars.Where(star => star.IsUsable).ToList();
        var saturated = stars.Count(star => star.IsSaturated);
        if (usable.Count == 0)
        {
            return new FrameMetrics(stars.Count, 0, saturated, null, null, null, statistics.Background, statistics.BackgroundSigma);
        }

        // Sorted first, so that the sums do not depend on the order the stars came in.
        var hfrs = usable.Select(star => star.Hfr).Order().ToArray();
        return new FrameMetrics(
            stars.Count,
            usable.Count,
            saturated,
            Median(hfrs),
            hfrs.Sum() / hfrs.Length,
            Median(usable.Select(star => star.Flux)),
            statistics.Background,
            statistics.BackgroundSigma);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

/// <summary>
/// The one analysis of a frame: robust statistics, the stars, and their aggregate. The result of a frame is kept for as
/// long as the frame lives, in a table that does not keep the frame alive and does not touch it, so a frame that
/// autofocus and the imaging page both want is analysed once.
/// <para>
/// The analysis is CPU work done on the calling thread (a frame of a few megapixels takes some tens of milliseconds); it
/// watches the cancellation token between rows. It never runs on the user interface thread by itself: the callers that
/// could be on it start it elsewhere.
/// </para>
/// </summary>
public sealed class FrameAnalyzer : IFrameAnalyzer
{
    private readonly IStarDetector _detector;
    private readonly ConditionalWeakTable<CameraFrame, FrameAnalysisResult> _results = new();

    public FrameAnalyzer(FrameAnalysisOptions? options = null, IStarDetector? detector = null)
    {
        Options = options ?? new FrameAnalysisOptions();
        Options.Validate();
        _detector = detector ?? new StarDetector();
    }

    public FrameAnalysisOptions Options { get; }

    public FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_results.TryGetValue(frame, out var known))
        {
            return known;
        }

        var statistics = FrameStatisticsCalculator.Compute(frame, cancellationToken);
        var stars = _detector.Detect(frame, statistics, Options, cancellationToken);
        var result = new FrameAnalysisResult(statistics, stars, FrameMetricsCalculator.From(statistics, stars));

        // A cancelled analysis threw before this point and leaves nothing behind.
        return _results.GetValue(frame, _ => result);
    }
}

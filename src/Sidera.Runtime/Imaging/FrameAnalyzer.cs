using System.Diagnostics;
using System.Runtime.CompilerServices;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Imaging;

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
    private readonly ILogger _logger;
    private readonly ConditionalWeakTable<CameraFrame, FrameAnalysisResult> _results = new();

    /// <param name="logger">Where each analysis is reported (Debug), and frames without enough stars (Warning).</param>
    public FrameAnalyzer(FrameAnalysisOptions? options = null, IStarDetector? detector = null, ILogger<FrameAnalyzer>? logger = null)
    {
        Options = options ?? new FrameAnalysisOptions();
        Options.Validate();
        _detector = detector ?? new StarDetector();
        _logger = logger ?? NullLogger<FrameAnalyzer>.Instance;
    }

    public FrameAnalysisOptions Options { get; }

    public FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_results.TryGetValue(frame, out var known))
        {
            return known;
        }

        var started = Stopwatch.GetTimestamp();
        var statistics = FrameStatisticsCalculator.Compute(frame, cancellationToken);
        var stars = _detector.Detect(frame, statistics, Options, cancellationToken);
        var result = new FrameAnalysisResult(statistics, stars, FrameMetricsCalculator.From(statistics, stars));
        Report(frame, result.Metrics, Stopwatch.GetElapsedTime(started));

        // A cancelled analysis threw before this point and leaves nothing behind.
        return _results.GetValue(frame, _ => result);
    }

    // One line per analysis; only the aggregate, never a star or a pixel.
    private void Report(CameraFrame frame, FrameMetrics metrics, TimeSpan duration)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Frame {Width}x{Height} analyzed in {DurationMs:0} ms: {StarCount} stars, {UsableStarCount} usable, " +
                "{SaturatedStarCount} saturated, median HFR {MedianHfr:0.00} px, background {Background:0} ADU, noise {Noise:0.0} ADU",
                frame.Width, frame.Height, duration.TotalMilliseconds, metrics.StarCount, metrics.UsableStarCount,
                metrics.SaturatedStarCount, metrics.MedianHfr, metrics.Background, metrics.BackgroundSigma);
        }

        if (metrics.StarCount == 0)
        {
            _logger.LogWarning("No stars were detected in a {Width}x{Height} frame", frame.Width, frame.Height);
        }
        else if (metrics.UsableStarCount < Options.MinimumUsableStars)
        {
            _logger.LogWarning(
                "Only {UsableStarCount} of {StarCount} stars are usable in a {Width}x{Height} frame; {MinimumUsableStars} are needed for focus",
                metrics.UsableStarCount, metrics.StarCount, frame.Width, frame.Height, Options.MinimumUsableStars);
        }
    }
}

using Sidera.Core.Devices;

namespace Sidera.Core.Imaging;

/// <summary>
/// What the analysis of a frame is configured with. One set of options for the whole pipeline (statistics, detection and
/// the aggregate that focus uses); the defaults suit a 16-bit mono frame of a few hundred to a few thousand pixels in each
/// direction. Nothing here is a user setting yet.
/// </summary>
/// <param name="DetectionSigma">A pixel belongs to a star when it is more than this many noise sigmas above the background.</param>
/// <param name="MinStarPixels">A bright region smaller than this is not a star: a hot pixel or a cosmic ray.</param>
/// <param name="MaxStarRadius">
/// The largest star, in pixels: a bright region whose equivalent radius is bigger is no star (a galaxy, a gradient, a
/// satellite trail), and a star is measured within at most this radius.
/// </param>
/// <param name="EdgeMargin">A star whose measured region comes closer than this to the edge of the frame is clipped, and not used.</param>
/// <param name="SaturationLevel">A star with a pixel at or above this value is saturated, and not used for focus.</param>
/// <param name="MinimumUsableStars">Focus needs at least this many usable stars: one star is not a measurement.</param>
/// <param name="MaxStars">At most this many stars are measured, the brightest first.</param>
public sealed record FrameAnalysisOptions(
    double DetectionSigma = 5,
    int MinStarPixels = 5,
    int MaxStarRadius = 20,
    int EdgeMargin = 4,
    int SaturationLevel = ushort.MaxValue,
    int MinimumUsableStars = 5,
    int MaxStars = 200
)
{
    /// <exception cref="ArgumentException">An option is not usable; the message says which.</exception>
    public void Validate()
    {
        if (!double.IsFinite(DetectionSigma) || DetectionSigma <= 0)
        {
            throw new ArgumentException("The detection threshold must be a number of sigmas greater than 0.", nameof(DetectionSigma));
        }

        if (MinStarPixels < 2)
        {
            throw new ArgumentException("A star needs at least 2 pixels.", nameof(MinStarPixels));
        }

        if (MaxStarRadius < 2)
        {
            throw new ArgumentException("The largest star radius must be at least 2 pixels.", nameof(MaxStarRadius));
        }

        if (EdgeMargin < 0)
        {
            throw new ArgumentException("The edge margin cannot be negative.", nameof(EdgeMargin));
        }

        if (SaturationLevel is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("The saturation level must be a 16-bit value.", nameof(SaturationLevel));
        }

        if (MinimumUsableStars < 1)
        {
            throw new ArgumentException("At least one usable star is needed.", nameof(MinimumUsableStars));
        }

        if (MaxStars < 1)
        {
            throw new ArgumentException("At least one star has to be analysed.", nameof(MaxStars));
        }
    }
}

/// <summary>
/// Robust statistics of a frame, in ADU. The background is the median and the noise the median absolute deviation scaled
/// to a standard deviation (1.4826 · MAD), so the stars, which are positive outliers, do not move them.
/// </summary>
/// <param name="Background">The median: the sky level.</param>
/// <param name="BackgroundSigma">1.4826 · median(|pixel − median|): the noise of the sky, not the spread of the whole frame.</param>
public sealed record FrameStatistics(
    int PixelCount,
    int Min,
    int Max,
    double Mean,
    double Median,
    double StandardDeviation,
    double Background,
    double BackgroundSigma
);

/// <summary>
/// A star found in a frame, measured from the pixels. <see cref="Hfr"/> is the half flux radius: the radius around the
/// centroid that holds half of the star's flux (background subtracted), in pixels.
/// </summary>
/// <param name="X">Flux-weighted centroid, column; pixel centres are whole numbers.</param>
/// <param name="Y">Flux-weighted centroid, row.</param>
/// <param name="Peak">The brightest pixel, in ADU (not background subtracted).</param>
/// <param name="Flux">The background-subtracted flux in the measured region, in ADU.</param>
/// <param name="Background">The background that was subtracted.</param>
/// <param name="PixelCount">Pixels above the detection threshold.</param>
/// <param name="Radius">The radius of the measured region, in pixels.</param>
/// <param name="Elongation">Ratio of the long to the short axis of the star, 1 for a round one.</param>
/// <param name="IsSaturated">A pixel of the star reached the saturation level.</param>
/// <param name="TouchesEdge">The measured region would have reached beyond the edge margin of the frame.</param>
public sealed record DetectedStar(
    double X,
    double Y,
    double Peak,
    double Flux,
    double Background,
    int PixelCount,
    double Radius,
    double Hfr,
    double Elongation,
    bool IsSaturated,
    bool TouchesEdge
)
{
    /// <summary>Elongation above which a region is a streak or two stars, not a star to focus with.</summary>
    public const double MaxElongation = 2.5;

    public bool IsElongated => Elongation > MaxElongation;

    /// <summary>
    /// The star can be used to judge focus: it is not saturated (a clipped core cannot be measured), not clipped by the
    /// edge, and round.
    /// </summary>
    public bool IsUsable => !IsSaturated && !TouchesEdge && !IsElongated;
}

/// <summary>
/// The aggregate of a frame that focus works with. The median HFR of the usable stars is the focus metric: unlike the
/// best star, or the mean, it does not follow one odd detection.
/// </summary>
/// <param name="StarCount">Every star that was found, usable or not.</param>
/// <param name="UsableStarCount">The stars <see cref="DetectedStar.IsUsable"/>.</param>
/// <param name="SaturatedStarCount">Stars left out because they are saturated.</param>
/// <param name="MedianHfr">The median HFR of the usable stars; <c>null</c> when there are none.</param>
/// <param name="MeanHfr">The mean HFR of the usable stars; <c>null</c> when there are none.</param>
/// <param name="MedianFlux">The median flux of the usable stars; <c>null</c> when there are none.</param>
public sealed record FrameMetrics(
    int StarCount,
    int UsableStarCount,
    int SaturatedStarCount,
    double? MedianHfr,
    double? MeanHfr,
    double? MedianFlux,
    double Background,
    double BackgroundSigma
);

/// <summary>Everything the analysis of one frame found.</summary>
public sealed record FrameAnalysisResult(FrameStatistics Statistics, IReadOnlyList<DetectedStar> Stars, FrameMetrics Metrics);

/// <summary>Finds the stars of a frame. Deterministic: the same frame gives the same stars, in the same order.</summary>
public interface IStarDetector
{
    /// <summary>The stars of <paramref name="frame"/>, brightest first.</summary>
    /// <exception cref="OperationCanceledException">The detection was cancelled.</exception>
    IReadOnlyList<DetectedStar> Detect(
        CameraFrame frame, FrameStatistics statistics, FrameAnalysisOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// The one analysis of a frame: statistics, stars and the aggregate. Autofocus, the imaging page and anything that judges
/// frames later all use this, and a frame that was analysed once is not analysed again.
/// </summary>
public interface IFrameAnalyzer
{
    FrameAnalysisOptions Options { get; }

    /// <exception cref="OperationCanceledException">The analysis was cancelled.</exception>
    FrameAnalysisResult Analyze(CameraFrame frame, CancellationToken cancellationToken = default);
}

/// <summary>A frame could not be analysed into a usable result. <see cref="Reason"/> is a fragment for a sentence.</summary>
public class FrameAnalysisException(string reason) : InvalidOperationException($"Frame analysis failed: {reason}.")
{
    /// <summary>What went wrong, for example "no stars were detected": lower case, without a full stop.</summary>
    public string Reason { get; } = reason;
}

/// <summary>There is no star in the frame at all.</summary>
public sealed class NoStarsDetectedException() : FrameAnalysisException("no stars were detected");

/// <summary>There are stars, but fewer usable ones than a measurement needs.</summary>
public sealed class InsufficientStarsException(int usable, int required)
    : FrameAnalysisException(
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"only {usable} usable {(usable == 1 ? "star was" : "stars were")} detected; at least {required} {(required == 1 ? "is" : "are")} required"))
{
    public int UsableStars { get; } = usable;
    public int RequiredStars { get; } = required;
}

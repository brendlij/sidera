using Sidera.Core.Focusing;
using Sidera.Core.Imaging;

namespace Sidera.Runtime.Focusing;

/// <summary>
/// The focus metric that looks at the pixels: the frame of the exposure is analysed (stars found, each measured), and the
/// median HFR of the usable stars is the measurement. It knows nothing of where focus might be, and nothing of a
/// simulation: what it is given is a <see cref="Sidera.Core.Devices.CameraFrame"/> and the focuser position it was taken at.
/// <para>
/// A measurement needs enough stars. No star at all is <see cref="NoStarsDetectedException"/>, too few usable ones
/// (saturated, clipped by the edge, elongated ones do not count) is <see cref="InsufficientStarsException"/>; the metric
/// is never computed from fewer.
/// </para>
/// </summary>
public sealed class StarHfrFocusMetricProvider : IFocusMetricProvider
{
    private readonly IFrameAnalyzer _analyzer;
    private readonly int _minimumUsableStars;

    /// <param name="minimumUsableStars">How many usable stars a measurement needs; the analyzer's option when not given.</param>
    public StarHfrFocusMetricProvider(IFrameAnalyzer analyzer, int? minimumUsableStars = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
        _minimumUsableStars = minimumUsableStars ?? analyzer.Options.MinimumUsableStars;
        ArgumentOutOfRangeException.ThrowIfLessThan(_minimumUsableStars, 1);
    }

    /// <exception cref="NoStarsDetectedException">There is no star in the frame.</exception>
    /// <exception cref="InsufficientStarsException">There are fewer usable stars than a measurement needs.</exception>
    public Task<FocusMeasurement> MeasureAsync(FocusMetricInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        var result = _analyzer.Analyze(input.Frame, cancellationToken);
        if (result.Stars.Count == 0)
        {
            throw new NoStarsDetectedException();
        }

        var metrics = result.Metrics;
        if (metrics.UsableStarCount < _minimumUsableStars || metrics.MedianHfr is not { } medianHfr)
        {
            throw new InsufficientStarsException(metrics.UsableStarCount, _minimumUsableStars);
        }

        return Task.FromResult(new FocusMeasurement(input.FocuserPosition, medianHfr));
    }
}

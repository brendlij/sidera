using Sidera.Core.Guiding;

namespace Sidera.Runtime.Devices;

/// <summary>One measurement of the total guide error, in guide camera pixels, taken at a monotonic time.</summary>
internal readonly record struct GuideErrorObservation(TimeSpan Timestamp, double ErrorPixels);

internal enum GuidingSettleProgress
{
    Settling,
    Settled,
    TimedOut
}

/// <summary>
/// Applies <see cref="GuidingSettleOptions"/> to a stream of guide-error observations. All times are monotonic
/// offsets from the same clock.
/// <para>
/// An observation is usable only if it is present, finite and not negative, taken no earlier than the start of
/// the settle wait, not in the future, not older than the previous one, and no older than the maximum age. A
/// usable observation within the threshold starts the stable interval at its timestamp (or continues it, unless
/// it follows the previous observation by more than the maximum age); anything else ends the interval. Guiding
/// has settled once usable observations within the threshold span the stable duration. The timeout counts from
/// the start, whatever the observations say.
/// </para>
/// </summary>
internal sealed class GuidingSettleTracker
{
    private readonly GuidingSettleOptions _options;
    private readonly TimeSpan _start;
    private readonly TimeSpan _maximumAge;
    private TimeSpan? _stableSince;
    private TimeSpan? _lastTimestamp;

    public GuidingSettleTracker(GuidingSettleOptions options, TimeSpan start, TimeSpan maximumObservationAge)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumObservationAge, TimeSpan.Zero);

        _options = options;
        _start = start;
        _maximumAge = maximumObservationAge;
    }

    /// <summary>Evaluates the latest observation (or its absence) at <paramref name="now"/>.</summary>
    public GuidingSettleProgress Evaluate(TimeSpan now, GuideErrorObservation? observation)
    {
        if (observation is { } o && IsUsable(now, o))
        {
            if (o.ErrorPixels <= _options.MaximumErrorPixels)
            {
                if (_stableSince is null || o.Timestamp - _lastTimestamp!.Value > _maximumAge)
                {
                    _stableSince = o.Timestamp;
                }
            }
            else
            {
                _stableSince = null;
            }

            _lastTimestamp = o.Timestamp;

            if (_stableSince is { } since && o.Timestamp - since >= _options.StableDuration)
            {
                return GuidingSettleProgress.Settled;
            }
        }
        else
        {
            _stableSince = null;
        }

        return now - _start >= _options.Timeout ? GuidingSettleProgress.TimedOut : GuidingSettleProgress.Settling;
    }

    private bool IsUsable(TimeSpan now, GuideErrorObservation o) =>
        double.IsFinite(o.ErrorPixels)
        && o.ErrorPixels >= 0
        && o.Timestamp >= _start
        && o.Timestamp <= now
        && now - o.Timestamp <= _maximumAge
        && (_lastTimestamp is not { } last || o.Timestamp >= last);
}

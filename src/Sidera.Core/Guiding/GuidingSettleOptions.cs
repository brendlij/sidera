namespace Sidera.Core.Guiding;

/// <summary>
/// What "guiding has settled" means for one settle wait. Immutable and validated on construction.
/// <para>
/// Guiding has settled once the measured guide error has stayed at or below <see cref="MaximumErrorPixels"/>
/// continuously for <see cref="StableDuration"/>: a single observation above the threshold, or a gap without a
/// usable observation, starts the interval again. If that does not happen within <see cref="Timeout"/> the wait
/// fails. Both durations are measured on a monotonic clock, not on the wall clock.
/// </para>
/// </summary>
public sealed class GuidingSettleOptions
{
    /// <param name="maximumErrorPixels">
    /// Largest acceptable guide error, in pixels of the guide camera (not of an imaging camera, and not in
    /// arcseconds). Must be finite and positive.
    /// </param>
    /// <param name="stableDuration">How long the error must stay within the threshold without interruption. Must be positive.</param>
    /// <param name="timeout">Overall limit for the settle wait. Must be longer than <paramref name="stableDuration"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside the ranges described above.</exception>
    public GuidingSettleOptions(double maximumErrorPixels, TimeSpan stableDuration, TimeSpan timeout)
    {
        if (!double.IsFinite(maximumErrorPixels) || maximumErrorPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumErrorPixels), maximumErrorPixels,
                "The settle threshold must be a finite, positive number of guider pixels.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stableDuration, TimeSpan.Zero);

        if (timeout <= stableDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout), timeout, "The settle timeout must be longer than the stable duration.");
        }

        MaximumErrorPixels = maximumErrorPixels;
        StableDuration = stableDuration;
        Timeout = timeout;
    }

    /// <summary>Largest acceptable guide error, in guide camera pixels.</summary>
    public double MaximumErrorPixels { get; }

    /// <summary>How long the guide error must stay within the threshold without interruption.</summary>
    public TimeSpan StableDuration { get; }

    /// <summary>Overall limit for the settle wait, measured from its start.</summary>
    public TimeSpan Timeout { get; }
}

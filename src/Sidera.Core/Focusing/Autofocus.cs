using System.Globalization;
using Sidera.Core.Events;
using Sidera.Core.Rigs;

namespace Sidera.Core.Focusing;

/// <summary>
/// What one autofocus run does: a symmetrical set of focuser positions around the current one, an exposure at each, a
/// fit through the HFR values, a move to the best position and, by default, one more exposure there to check it.
/// </summary>
/// <param name="ExposureDuration">The exposure at each position.</param>
/// <param name="StepSize">The distance between two sample positions, in focuser steps.</param>
/// <param name="SampleCount">How many positions are sampled around the centre: odd, so that the pattern is symmetrical.</param>
/// <param name="Verify">Expose once more at the best position and refuse the result if it is not better.</param>
/// <param name="SettleDelay">How long to wait after each focuser move before exposing.</param>
/// <param name="MaxAttempts">
/// How often the sample pattern is put around a new centre when the minimum lies outside of it, at most. One means
/// the minimum has to be within the first pattern.
/// </param>
public sealed record AutofocusOptions(
    TimeSpan ExposureDuration,
    int StepSize,
    int SampleCount,
    bool Verify = true,
    TimeSpan SettleDelay = default,
    int MaxAttempts = 2
)
{
    /// <summary>A fit needs at least this many valid positions.</summary>
    public const int MinimumSampleCount = 5;

    public const int MaximumSampleCount = 21;

    /// <exception cref="ArgumentException">An option is not usable; the message says which.</exception>
    public void Validate()
    {
        if (ExposureDuration <= TimeSpan.Zero)
        {
            throw new ArgumentException("Autofocus exposure must be longer than 0 s.", nameof(ExposureDuration));
        }

        if (StepSize <= 0)
        {
            throw new ArgumentException("Autofocus step size must be greater than 0.", nameof(StepSize));
        }

        if (SampleCount is < MinimumSampleCount or > MaximumSampleCount || SampleCount % 2 == 0)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Autofocus samples must be an odd number between {MinimumSampleCount} and {MaximumSampleCount}."),
                nameof(SampleCount));
        }

        if (SettleDelay < TimeSpan.Zero)
        {
            throw new ArgumentException("Autofocus settle delay cannot be negative.", nameof(SettleDelay));
        }

        if (MaxAttempts < 1)
        {
            throw new ArgumentException("Autofocus needs at least one attempt.", nameof(MaxAttempts));
        }
    }
}

/// <summary>
/// The outcome of a run that found focus. Failure is not a result: it is an <see cref="AutofocusFailedException"/>.
/// </summary>
/// <param name="InitialPosition">Where the focuser stood when autofocus started.</param>
/// <param name="BestPosition">The position the fit found, and the focuser was moved to.</param>
/// <param name="BestHfr">
/// The HFR at the best position: the verification measurement when there was one, otherwise the minimum the fit
/// predicts (which was not measured).
/// </param>
/// <param name="FittedHfr">The minimum the fit predicts.</param>
/// <param name="Measurements">Every sample, in the order it was taken, of all attempts.</param>
/// <param name="Verification">The exposure at the best position, when verification was on.</param>
/// <param name="FinalPosition">Where the focuser stands now: it has arrived there.</param>
/// <param name="Attempts">How many sample patterns were used.</param>
public sealed record AutofocusResult(
    int InitialPosition,
    int BestPosition,
    double BestHfr,
    double FittedHfr,
    IReadOnlyList<FocusMeasurement> Measurements,
    FocusMeasurement? Verification,
    int FinalPosition,
    int Attempts
);

/// <summary>Autofocus did not find a focus it can stand behind. The message is one sentence for the user.</summary>
public sealed class AutofocusFailedException(string message) : InvalidOperationException(message)
{
    public const string NoMinimum = "Autofocus failed: no reliable focus minimum was found.";
}

/// <summary>Where an autofocus run is: what it is doing right now.</summary>
public enum AutofocusPhase
{
    /// <summary>Exposing at a sample position (or about to).</summary>
    Measuring,

    /// <summary>Fitting a curve through the samples.</summary>
    Fitting,

    /// <summary>Moving to the best position.</summary>
    Moving,

    /// <summary>Exposing at the best position to check it.</summary>
    Verifying,

    /// <summary>Focus was found and the focuser stands at it.</summary>
    Completed,

    /// <summary>The run ended without a result: cancelled or failed.</summary>
    Stopped
}

/// <summary>
/// What an autofocus run reports while it runs. Only what is known: the position and HFR are those of the sample that
/// was just taken, and are absent when there is no such sample.
/// </summary>
/// <param name="Attempt">Which sample pattern, from 1.</param>
/// <param name="SampleIndex">The sample just taken, from 1; 0 before the first.</param>
/// <param name="SampleCount">How many samples the current pattern has.</param>
/// <param name="BestPosition">The position found; set from <see cref="AutofocusPhase.Moving"/> on.</param>
/// <param name="BestHfr">The HFR at it; set when completed.</param>
public sealed record AutofocusProgress(
    AutofocusPhase Phase,
    int Attempt,
    int SampleIndex,
    int SampleCount,
    int? Position = null,
    double? Hfr = null,
    int? BestPosition = null,
    double? BestHfr = null
);

/// <summary>The progress of the autofocus of a rig. Published on the event bus; nothing in a device state.</summary>
public sealed record AutofocusProgressChanged(RigId RigId, AutofocusProgress Progress) : ISideraEvent;

/// <summary>Exposes at the current focuser position and measures the focus there. Used by the autofocus algorithm.</summary>
public interface IFocusMeasurer
{
    Task<FocusMeasurement> MeasureAsync(TimeSpan exposureDuration, CancellationToken cancellationToken = default);
}

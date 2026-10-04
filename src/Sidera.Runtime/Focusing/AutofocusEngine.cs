using Sidera.Core.Focusing;
using Sidera.Core.Focusers;

namespace Sidera.Runtime.Focusing;

/// <summary>Receives the progress of an autofocus run, in order. Awaited by the engine.</summary>
public delegate Task AutofocusReporter(AutofocusProgress progress, CancellationToken cancellationToken);

/// <summary>
/// The autofocus algorithm. It knows a focuser and something that measures focus (<see cref="IFocusMeasurer"/>), and
/// nothing about where the measurement comes from. It holds no state: everything of a run is local to the call.
/// <para>
/// A run puts a symmetrical pattern of positions around the current one (step size, sample count), skips positions the
/// focuser cannot reach, visits the rest from the lowest to the highest and measures at each. A parabola through the
/// squares of the HFR values (see <see cref="FocusCurveFit"/>) gives the position of the minimum. When that lies inside
/// the pattern the focuser moves there and, if asked, one more measurement checks it. When it lies outside, the
/// pattern is put around it once more (up to <see cref="AutofocusOptions.MaxAttempts"/> patterns); the samples of all
/// patterns are kept in the result, only the last one is fitted.
/// </para>
/// <para>
/// Nothing is restored when a run ends early: after a cancellation or a failure the focuser stays where it last
/// arrived, which is where the run left it, not where it started.
/// </para>
/// </summary>
public static class AutofocusEngine
{
    /// <exception cref="ArgumentException">The options are not usable.</exception>
    /// <exception cref="AutofocusFailedException">No reliable focus was found, or there is not enough travel for the samples.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static async Task<AutofocusResult> RunAsync(
        IFocuser focuser,
        IFocusMeasurer measurer,
        AutofocusOptions options,
        AutofocusReporter? report = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(focuser);
        ArgumentNullException.ThrowIfNull(measurer);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        async Task Report(AutofocusProgress progress)
        {
            if (report is not null)
            {
                await report(progress, cancellationToken);
            }
        }

        var initial = focuser.Position;
        var all = new List<FocusMeasurement>();
        var center = initial;
        FocusFit? fit = null;
        var attempt = 0;

        while (true)
        {
            attempt++;
            var positions = SamplePositions(center, options.StepSize, options.SampleCount, focuser.MinPosition, focuser.MaxPosition);
            if (positions.Count < AutofocusOptions.MinimumSampleCount)
            {
                // At the start the focuser simply has too little travel; later the minimum the fit pointed at is
                // too close to the end of the range to be sampled, which means it was not found.
                throw new AutofocusFailedException(
                    attempt == 1
                        ? "Autofocus failed: the focuser does not have enough travel here for the samples."
                        : AutofocusFailedException.NoMinimum);
            }

            await Report(new AutofocusProgress(AutofocusPhase.Measuring, attempt, 0, positions.Count));

            var window = new List<FocusMeasurement>(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await MoveTo(focuser, positions[i], options.SettleDelay, cancellationToken);
                var sample = await measurer.MeasureAsync(options.ExposureDuration, cancellationToken);
                window.Add(sample);
                all.Add(sample);
                await Report(new AutofocusProgress(
                    AutofocusPhase.Measuring, attempt, i + 1, positions.Count, sample.FocuserPosition, sample.Hfr));
            }

            await Report(new AutofocusProgress(AutofocusPhase.Fitting, attempt, positions.Count, positions.Count));
            fit = FocusCurveFit.Fit(window);
            if (fit is null)
            {
                throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
            }

            var lowest = window.Min(s => s.FocuserPosition);
            var highest = window.Max(s => s.FocuserPosition);
            if (fit.Vertex >= lowest && fit.Vertex <= highest)
            {
                break;
            }

            // The minimum is outside the pattern: put the pattern around it, if there is an attempt left and that
            // is somewhere new and within reach.
            var next = (int)Math.Round(Math.Clamp(fit.Vertex, focuser.MinPosition, focuser.MaxPosition));
            if (attempt >= options.MaxAttempts || next == center)
            {
                throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
            }

            center = next;
        }

        var best = (int)Math.Round(fit.Vertex);
        await Report(new AutofocusProgress(AutofocusPhase.Moving, attempt, 0, 0, BestPosition: best));
        await MoveTo(focuser, best, options.SettleDelay, cancellationToken);

        FocusMeasurement? verification = null;
        if (options.Verify)
        {
            await Report(new AutofocusProgress(AutofocusPhase.Verifying, attempt, 0, 0, BestPosition: best));
            verification = await measurer.MeasureAsync(options.ExposureDuration, cancellationToken);

            // A focus that is not better than the samples it was fitted from is not a focus the run can stand behind.
            if (verification.Hfr > 1.5 * all.Min(s => s.Hfr))
            {
                throw new AutofocusFailedException("Autofocus failed: the focus at the best position was not better than at the samples.");
            }
        }

        var bestHfr = verification?.Hfr ?? fit.FittedHfr;
        var result = new AutofocusResult(
            initial, best, bestHfr, fit.FittedHfr, all, verification, focuser.Position, attempt);
        await Report(new AutofocusProgress(
            AutofocusPhase.Completed, attempt, 0, 0, focuser.Position, verification?.Hfr, best, bestHfr));
        return result;
    }

    /// <summary>
    /// The positions of a pattern around <paramref name="center"/>, lowest first: <paramref name="count"/> positions
    /// <paramref name="step"/> apart, symmetrical around the centre, without those the focuser cannot reach.
    /// </summary>
    internal static IReadOnlyList<int> SamplePositions(int center, int step, int count, int min, int max)
    {
        var half = count / 2;
        var positions = new List<int>(count);
        for (var i = -half; i <= half; i++)
        {
            var position = (long)center + (long)i * step;
            if (position >= min && position <= max)
            {
                positions.Add((int)position);
            }
        }

        return positions;
    }

    private static async Task MoveTo(IFocuser focuser, int position, TimeSpan settle, CancellationToken cancellationToken)
    {
        if (focuser.Position != position)
        {
            await focuser.MoveToAsync(position, cancellationToken);
            if (settle > TimeSpan.Zero)
            {
                await Task.Delay(settle, cancellationToken);
            }
        }
    }
}

using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Focusing;

/// <summary>
/// The autofocus that searches (<see cref="AutofocusMode.Search"/>). It starts where the focuser stands and measures there, steps toward the side where the stars get smaller and turns round when they get
/// larger, until it has measured on both sides of the lowest HFR (as many samples on each side as the pattern would have). A position where no star can be measured (blurred beyond use) is not fatal: it
/// ends the search on that side. The curve is fitted through what was measured (the widest run of samples that a hyperbola explains, or the lines of the two sides, see <see cref="FocusFitMethod"/>); a
/// minimum outside of the samples is searched for by stepping to it. At the end the focuser goes to the fitted position and, if asked, one more measurement checks it: a focus that is worse than the best
/// sample falls back to that sample instead of failing the run.
/// <para>
/// When no star can be measured at the position where the focuser stands, the last position that was in focus is tried (if the application remembers it), then two and four steps to either side.
/// Nothing is restored when a run ends early, as for the pattern.
/// </para>
/// </summary>
public static class SearchAutofocusEngine
{
    /// <exception cref="AutofocusFailedException">No reliable focus was found.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public static async Task<AutofocusResult> RunAsync(
        IFocuser focuser,
        IFocusMeasurer measurer,
        AutofocusOptions options,
        RigId? rig = null,
        AutofocusReporter? report = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(focuser);
        ArgumentNullException.ThrowIfNull(measurer);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var strategy = options.Strategy;
        var step = options.StepSize;
        var perSide = options.SampleCount / 2;
        var maxSamples = strategy.MaxSamples > 0 ? strategy.MaxSamples : options.SampleCount * 4 + 6;
        var good = new List<FocusMeasurement>();
        var tried = new List<int>();
        var taken = 0;
        var failedInRow = 0;
        var leftExhausted = false;
        var rightExhausted = false;
        var attempt = 1;
        var initial = focuser.Position;

        async Task Report(AutofocusProgress progress)
        {
            if (report is not null)
            {
                await report(progress, cancellationToken);
            }
        }

        // Exposes at a position and measures; null when no star can be measured there. A run of positions that cannot be measured ends it, once it has a minimum to stand on.
        async Task<FocusMeasurement?> Take(int position, bool countFailures = true)
        {
            position = Math.Clamp(position, focuser.MinPosition, focuser.MaxPosition);
            cancellationToken.ThrowIfCancellationRequested();
            tried.Add(position);
            await MoveTo(focuser, position, options.SettleDelay, cancellationToken);
            taken++;
            try
            {
                var sample = await measurer.MeasureAsync(options.ExposureDuration, cancellationToken);
                good.Add(sample);
                failedInRow = 0;
                await Report(new AutofocusProgress(AutofocusPhase.Measuring, attempt, taken, Math.Max(options.SampleCount, taken), sample.FocuserPosition, sample.Hfr));
                return sample;
            }
            catch (AutofocusFailedException) when (!cancellationToken.IsCancellationRequested)
            {
                failedInRow++;
                await Report(new AutofocusProgress(AutofocusPhase.Measuring, attempt, taken, Math.Max(options.SampleCount, taken), position, null));
                if (countFailures && failedInRow > strategy.MaxFailedSamples)
                {
                    throw new AutofocusFailedException(
                        $"Autofocus failed: no star could be measured at {failedInRow} positions in a row. Check that the sky is clear and the exposure is long enough.");
                }

                return null;
            }
        }

        // Where the focuser stands; when that cannot be measured, where it was in focus the last time, then a little to either side.
        var first = await Take(initial, countFailures: false);
        if (first is null && strategy.UseRememberedPosition && rig is { } known && strategy.History?.LastPosition(known) is { } remembered && remembered != initial)
        {
            first = await Take(remembered, countFailures: false);
        }

        if (first is null)
        {
            foreach (var offset in new[] { 2 * step, -2 * step, 4 * step, -4 * step })
            {
                first = await Take(initial + offset, countFailures: false);
                if (first is not null)
                {
                    break;
                }
            }
        }

        if (first is null)
        {
            throw new AutofocusFailedException(
                "Autofocus failed: no star could be measured where the focuser stands or near it. Move to a star field, or focus roughly by hand first.");
        }

        failedInRow = 0;
        FocusFit? fit = null;
        while (true)
        {
            // Samples on both sides of the lowest HFR.
            while (taken < maxSamples)
            {
                var lowest = good.MinBy(s => s.Hfr)!;
                var left = good.Count(s => s.FocuserPosition < lowest.FocuserPosition);
                var right = good.Count(s => s.FocuserPosition > lowest.FocuserPosition);
                if (left >= perSide && right >= perSide && good.Count >= AutofocusOptions.MinimumSampleCount)
                {
                    break;
                }

                var nextLeft = tried.Min() - step;
                var nextRight = tried.Max() + step;
                var canLeft = !leftExhausted && nextLeft >= focuser.MinPosition;
                var canRight = !rightExhausted && nextRight <= focuser.MaxPosition;
                if (!canLeft && !canRight)
                {
                    break;
                }

                // Toward the side where the stars get smaller first; then the side with fewer samples.
                bool goRight;
                if (right == 0 && canRight)
                {
                    goRight = true;
                }
                else if (left == 0 && canLeft)
                {
                    goRight = false;
                }
                else if (!canLeft)
                {
                    goRight = true;
                }
                else if (!canRight)
                {
                    goRight = false;
                }
                else
                {
                    goRight = right <= left;
                }

                var sample = await Take(goRight ? nextRight : nextLeft);
                if (sample is null)
                {
                    // Blurred beyond use on this side: it goes no further.
                    if (goRight)
                    {
                        rightExhausted = true;
                    }
                    else
                    {
                        leftExhausted = true;
                    }
                }
            }

            await Report(new AutofocusProgress(AutofocusPhase.Fitting, attempt, taken, Math.Max(options.SampleCount, taken)));
            fit = FocusCurveFit.Fit(good, strategy.FitMethod);
            if (fit is null)
            {
                if (good.Count >= AutofocusOptions.MinimumSampleCount && (good.Max(s => s.Hfr) - good.Min(s => s.Hfr)) / good.Min(s => s.Hfr) < FocusCurveFit.MinimumRelativeSpread)
                {
                    throw new AutofocusFailedException("Autofocus failed: the HFR hardly changes between the positions. Use a larger step size.");
                }

                // Not a curve that can be trusted yet: more samples around the lowest, then again.
                if (attempt > options.MaxAttempts || taken >= maxSamples)
                {
                    throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
                }

                attempt++;
                perSide++;
                continue;
            }

            var lo = good.Min(s => s.FocuserPosition);
            var hi = good.Max(s => s.FocuserPosition);
            if (fit.Vertex >= lo && fit.Vertex <= hi)
            {
                break;
            }

            // The minimum lies beyond the samples: step to it.
            if (attempt > options.MaxAttempts || taken >= maxSamples)
            {
                throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
            }

            attempt++;
            var target = (int)Math.Round(Math.Clamp(fit.Vertex, focuser.MinPosition, focuser.MaxPosition));
            if (tried.Contains(target))
            {
                target += fit.Vertex > hi ? step : -step;
            }

            if (target < focuser.MinPosition || target > focuser.MaxPosition)
            {
                throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
            }

            if (await Take(target) is null)
            {
                throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
            }
        }

        var best = (int)Math.Round(fit.Vertex);
        await Report(new AutofocusProgress(AutofocusPhase.Moving, attempt, 0, 0, BestPosition: best));
        await MoveTo(focuser, best, options.SettleDelay, cancellationToken);

        FocusMeasurement? verification = null;
        var fellBack = false;
        var bestSample = good.MinBy(s => s.Hfr)!;
        if (options.Verify)
        {
            await Report(new AutofocusProgress(AutofocusPhase.Verifying, attempt, 0, 0, BestPosition: best));
            try
            {
                verification = await measurer.MeasureAsync(options.ExposureDuration, cancellationToken);
            }
            catch (AutofocusFailedException) when (!cancellationToken.IsCancellationRequested)
            {
                verification = null;
            }

            // A focus that is not as good as the best sample that was measured is not one to stand behind: go back to that sample.
            if (verification is null || verification.Hfr > 1.15 * bestSample.Hfr)
            {
                fellBack = true;
                best = bestSample.FocuserPosition;
                await MoveTo(focuser, best, options.SettleDelay, cancellationToken);
                verification = bestSample;
            }
        }

        var bestHfr = verification?.Hfr ?? fit.FittedHfr;
        var result = new AutofocusResult(initial, best, bestHfr, fit.FittedHfr, good, verification, focuser.Position, attempt) { FellBackToBestSample = fellBack };
        await Report(new AutofocusProgress(AutofocusPhase.Completed, attempt, 0, 0, focuser.Position, verification?.Hfr, best, bestHfr));
        return result;
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

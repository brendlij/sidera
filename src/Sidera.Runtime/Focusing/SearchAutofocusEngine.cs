using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Focusing;

/// <summary>
/// The autofocus that searches (<see cref="AutofocusMode.Search"/>), in two passes like a manual focus: a coarse one with the step size, and a fine one (a quarter of the step) around what the coarse one found.
/// <para>
/// A pass starts where the focuser stands (and tries the position where focus was found last first when the stars here are far worse), steps toward the side where the stars get smaller, turns round when
/// they get larger and takes bigger steps while the HFR does not change at all (far out of focus the HFR tells nothing), until it has samples on both sides of the lowest HFR. It never goes further than the
/// travel limit from where it started and never takes more samples than the limit. A position where no star can be measured is not fatal: it ends the search on that side. The curve is fitted through what was
/// measured (the widest run of samples that a hyperbola explains, or the lines of the two sides, see <see cref="FocusFitMethod"/>); a minimum outside of the samples is stepped to.
/// </para>
/// <para>
/// A result has to be a clear minimum: the stars are clearly larger on both sides of it, and not far larger than where focus was found last. Otherwise the run fails with the reason, instead of reporting
/// a focus it did not find. At the end the focuser goes to the fitted position and, if asked, one more measurement checks it: a focus that is worse than the best sample falls back to that sample.
/// </para>
/// <para>
/// When no star can be measured at the position where the focuser stands, the last position that was in focus is tried, then two and four steps to either side. Nothing is restored when a run ends early.
/// </para>
/// </summary>
public static class SearchAutofocusEngine
{
    /// <summary>The stars at the start have to be this much larger than where focus was found last for that position to be tried first.</summary>
    private const double FarFromTheLastFocus = 2.0;

    /// <summary>A result with an HFR this many times the one found last is not believed.</summary>
    private const double ImplausiblyWorseThanTheLastFocus = 3.0;

    /// <summary>The stars at the two ends of the samples (on average) have to be at least this much larger than at the minimum for it to be a minimum.</summary>
    private const double ClearMinimumRise = 1.3;

    /// <summary>Samples whose HFR stays within this much of their mean are flat: the stars tell nothing at that distance.</summary>
    private const double FlatTolerance = 0.08;

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
        var coarseStep = options.StepSize;
        var maxSamples = strategy.MaxSamples > 0 ? strategy.MaxSamples : Math.Max(30, options.SampleCount * 3);
        var travel = strategy.MaxTravelSteps > 0 ? strategy.MaxTravelSteps : 10 * coarseStep;
        var all = new List<FocusMeasurement>();
        var good = new List<FocusMeasurement>();
        var tried = new List<int>();
        var taken = 0;
        var failedInRow = 0;
        var attempt = 1;
        var initial = focuser.Position;
        var remembered = strategy.UseRememberedPosition && rig is { } known ? strategy.History?.Last(known) : null;

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
                all.Add(sample);
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

        // One pass: samples around the lowest HFR until there are enough on both sides, fitted. The samples of the pass are in <good>; the pass starts with what is already in it.
        async Task<FocusFit> Pass(int step, int perSide, bool clearMinimumRequired)
        {
            var origin = good.MinBy(s => s.Hfr)!.FocuserPosition;
            var lowLimit = Math.Max(focuser.MinPosition, origin - travel);
            var highLimit = Math.Min(focuser.MaxPosition, origin + travel);
            var leftExhausted = false;
            var rightExhausted = false;
            failedInRow = 0;
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

                    // Far out of focus the HFR does not change from one sample to the next: bigger steps, to get out of the flat.
                    var stepNow = IsFlat(good) ? Math.Min(step * 2, step * 8) : step;
                    var reached = tried.Where(t => t >= lowLimit && t <= highLimit).DefaultIfEmpty(origin).ToList();
                    var nextLeft = reached.Min() - stepNow;
                    var nextRight = reached.Max() + stepNow;
                    var canLeft = !leftExhausted && nextLeft >= lowLimit && nextLeft >= focuser.MinPosition;
                    var canRight = !rightExhausted && nextRight <= highLimit && nextRight <= focuser.MaxPosition;
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
                var fit = FocusCurveFit.Fit(good, strategy.FitMethod);
                var lowestHfr = good.Min(s => s.Hfr);
                if (fit is null)
                {
                    if (good.Count >= AutofocusOptions.MinimumSampleCount && (good.Max(s => s.Hfr) - lowestHfr) / lowestHfr < FocusCurveFit.MinimumRelativeSpread)
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
                if (fit.Vertex < lo || fit.Vertex > hi)
                {
                    // The minimum lies beyond the samples: step to it.
                    if (attempt > options.MaxAttempts || taken >= maxSamples)
                    {
                        throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
                    }

                    attempt++;
                    var floor = Math.Max(focuser.MinPosition, lowLimit);
                    var ceiling = Math.Min(focuser.MaxPosition, highLimit);
                    var target = (int)Math.Round(Math.Clamp(fit.Vertex, floor, ceiling));
                    if (tried.Contains(target))
                    {
                        target += fit.Vertex > hi ? step : -step;
                    }

                    if (target < floor || target > ceiling)
                    {
                        throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
                    }

                    if (await Take(target) is null)
                    {
                        throw new AutofocusFailedException(AutofocusFailedException.NoMinimum);
                    }

                    continue;
                }

                if (clearMinimumRequired && !IsClearMinimum(good))
                {
                    // A dip in noise is not a minimum: more samples around it, or the honest answer.
                    if (attempt > options.MaxAttempts || taken >= maxSamples)
                    {
                        throw new AutofocusFailedException(
                            "Autofocus failed: the stars do not get clearly larger on both sides of the best position, so there is no focus curve to fit. " +
                            "Focus roughly by hand first, use a smaller step size or a longer exposure.");
                    }

                    attempt++;
                    perSide++;
                    continue;
                }

                return fit;
            }
        }

        // Where the focuser stands; when that cannot be measured, where it was in focus the last time, then a little to either side.
        var first = await Take(initial, countFailures: false);
        if (first is null && remembered is { } last && last.Position != initial)
        {
            first = await Take(last.Position, countFailures: false);
        }

        if (first is null)
        {
            foreach (var offset in new[] { 2 * coarseStep, -2 * coarseStep, 4 * coarseStep, -4 * coarseStep })
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

        // Far worse than where focus was found last: that position first; the pass then goes on from whichever of the two is better.
        if (remembered is { } was && Math.Abs(was.Position - first.FocuserPosition) > coarseStep && first.Hfr > FarFromTheLastFocus * was.Hfr && !tried.Contains(was.Position))
        {
            await Take(was.Position, countFailures: false);
        }

        failedInRow = 0;
        var perSideCoarse = options.SampleCount / 2;
        var fit = await Pass(coarseStep, perSideCoarse, clearMinimumRequired: true);
        var finalSamples = good.ToList();
        var best = (int)Math.Round(Math.Clamp(fit.Vertex, focuser.MinPosition, focuser.MaxPosition));

        var coarseLowest = good.Min(s => s.Hfr);
        if (remembered is { } reference && coarseLowest > ImplausiblyWorseThanTheLastFocus * reference.Hfr)
        {
            throw new AutofocusFailedException(
                $"Autofocus failed: the best HFR found ({coarseLowest:0.0} px) is far above the {reference.Hfr:0.0} px of the last focus, so the stars are still much too blurred to find the focus. " +
                $"Move the focuser toward {reference.Position}, or focus roughly by hand first.");
        }

        // The fine pass: a quarter of the step around what the coarse pass found. It refines the result and never takes it away: when it finds no curve, the coarse result stands.
        var fineStep = Math.Max(1, coarseStep / 4);
        if (strategy.FinePass && fineStep < coarseStep && taken < maxSamples)
        {
            attempt++;
            good.Clear();
            tried.Clear();
            try
            {
                if (await Take(best, countFailures: false) is not null)
                {
                    var fineFit = await Pass(fineStep, Math.Max(2, perSideCoarse - 1), clearMinimumRequired: false);
                    if (good.Count >= AutofocusOptions.MinimumSampleCount && fineFit.Vertex >= good.Min(s => s.FocuserPosition) && fineFit.Vertex <= good.Max(s => s.FocuserPosition))
                    {
                        fit = fineFit;
                        finalSamples = good.ToList();
                        best = (int)Math.Round(Math.Clamp(fit.Vertex, focuser.MinPosition, focuser.MaxPosition));
                    }
                }
            }
            catch (AutofocusFailedException) when (!cancellationToken.IsCancellationRequested)
            {
                // The fine pass found no curve: the coarse result stands.
            }
        }

        await Report(new AutofocusProgress(AutofocusPhase.Moving, attempt, 0, 0, BestPosition: best));
        await MoveTo(focuser, best, options.SettleDelay, cancellationToken);

        FocusMeasurement? verification = null;
        var fellBack = false;
        var bestSample = finalSamples.MinBy(s => s.Hfr)!;
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
        var result = new AutofocusResult(initial, best, bestHfr, fit.FittedHfr, all, verification, focuser.Position, attempt) { FellBackToBestSample = fellBack };
        await Report(new AutofocusProgress(AutofocusPhase.Completed, attempt, 0, 0, focuser.Position, verification?.Hfr, best, bestHfr));
        return result;
    }

    // The latest samples (at least three) show no change in the HFR: far out of focus, where the stars say nothing about the direction.
    private static bool IsFlat(List<FocusMeasurement> samples)
    {
        if (samples.Count < 3)
        {
            return false;
        }

        var tail = samples.Skip(samples.Count - 3).Select(s => s.Hfr).ToList();
        var mean = tail.Average();
        return tail.All(h => Math.Abs(h - mean) <= FlatTolerance * mean);
    }

    // The stars at the two ends of the samples are clearly larger than at the lowest HFR, and there are samples on both sides of it; a dip between samples that are all about the same is noise.
    private static bool IsClearMinimum(List<FocusMeasurement> samples)
    {
        var lowest = samples.MinBy(s => s.Hfr)!;
        var left = samples.Where(s => s.FocuserPosition < lowest.FocuserPosition).OrderBy(s => s.FocuserPosition).ToList();
        var right = samples.Where(s => s.FocuserPosition > lowest.FocuserPosition).OrderBy(s => s.FocuserPosition).ToList();
        return left.Count > 0 && right.Count > 0 && (left[0].Hfr + right[^1].Hfr) / 2 >= ClearMinimumRise * lowest.Hfr;
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

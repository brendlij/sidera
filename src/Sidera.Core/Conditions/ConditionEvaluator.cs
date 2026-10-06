using System.Globalization;
using Sidera.Core.Astronomy;
using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Core.Conditions;

/// <summary>What a condition is evaluated against: the clock, the place, the target, and where its scope began.</summary>
/// <param name="UtcNow">The time, UTC. Everything is calculated from it; nothing reads the clock itself, so a test sets it.</param>
/// <param name="Site">The observing site; <c>null</c> when none is known (a condition that needs it says so).</param>
/// <param name="Target">The target of the workflow; <c>null</c> when there is none.</param>
/// <param name="ScopeStartedUtc">When the scope that owns the condition started: the Wait step, the block's imaging, the target's imaging. Durations and times of day count from it.</param>
/// <param name="FramesCompleted">Frames the block has taken.</param>
public sealed record ConditionContext(DateTime UtcNow, ObservingSite? Site, CelestialCoordinates? Target, DateTime ScopeStartedUtc, int FramesCompleted = 0);

/// <summary>Whether a condition can ever be decided with what is known.</summary>
public enum ConditionAvailability
{
    Available,

    /// <summary>It needs the observing site and there is none.</summary>
    MissingSite,

    /// <summary>It needs a target and there is none.</summary>
    MissingTarget,

    /// <summary>It will not become true within the search of the sky: the Sun does not go below the altitude (a summer night that never gets dark), or never comes up through it.</summary>
    NotWithinReach
}

/// <summary>The answer for one condition: whether it holds, how to say where it stands, and whether it can be decided at all.</summary>
public readonly record struct ConditionResult(bool Met, string Text, ConditionAvailability Availability = ConditionAvailability.Available);

/// <summary>
/// Decides conditions. One evaluator belongs to one scope (a Wait step, a block, a target) and remembers what must not change while that scope lives: a condition that was met stays met
/// ("trigger once": an altitude that wobbles around its threshold cannot start and stop imaging again and again), and a time of day or a dawn is resolved to a UTC instant once, when it is
/// first looked at. The calculations are the ones of <see cref="SkyAltitude"/>; the clock comes in through the <see cref="ConditionContext"/>.
/// </summary>
public sealed class ConditionEvaluator
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkflowCondition, bool> _latched = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WorkflowCondition, SunCrossing> _crossings = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<WorkflowCondition, DateTime> _instants = new(ReferenceEqualityComparer.Instance);

    public ConditionResult Evaluate(WorkflowCondition condition, ConditionContext context)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(context);
        lock (_gate)
        {
            if (_latched.ContainsKey(condition))
            {
                return new ConditionResult(true, condition.Summary + " · reached");
            }

            var result = Decide(condition, context);
            if (result.Met && condition is not DurationCondition and not TimeCondition)
            {
                _latched[condition] = true;
            }

            return result;
        }
    }

    /// <summary>The first of the conditions that holds, or <c>null</c> (ANY).</summary>
    public (WorkflowCondition Condition, ConditionResult Result)? FirstMet(IReadOnlyList<WorkflowCondition> conditions, ConditionContext context)
    {
        foreach (var condition in conditions)
        {
            var result = Evaluate(condition, context);
            if (result.Met)
            {
                return (condition, result);
            }
        }

        return null;
    }

    /// <summary>The conditions that do not hold yet, with where they stand; empty when all hold (ALL).</summary>
    public IReadOnlyList<(WorkflowCondition Condition, ConditionResult Result)> Unmet(IReadOnlyList<WorkflowCondition> conditions, ConditionContext context) =>
        conditions.Select(condition => (Condition: condition, Result: Evaluate(condition, context))).Where(pair => !pair.Result.Met).ToList();

    private ConditionResult Decide(WorkflowCondition condition, ConditionContext context)
    {
        switch (condition)
        {
            case TimeCondition time:
            {
                if (!_instants.TryGetValue(condition, out var instant))
                {
                    instant = time.ResolveUtc(context.ScopeStartedUtc);
                    _instants[condition] = instant;
                }

                return context.UtcNow >= instant
                    ? new ConditionResult(true, condition.Summary + " · reached")
                    : new ConditionResult(false, $"Waiting for {condition.Summary} · in {Span(instant - context.UtcNow)}");
            }

            case DurationCondition duration:
            {
                var elapsed = context.UtcNow - context.ScopeStartedUtc;
                return elapsed >= duration.Duration
                    ? new ConditionResult(true, condition.Summary + " · reached")
                    : new ConditionResult(false, $"{condition.Summary} · {Span(duration.Duration - elapsed)} left");
            }

            case FrameCountCondition frames:
                return context.FramesCompleted >= frames.Frames
                    ? new ConditionResult(true, condition.Summary + " · reached")
                    : new ConditionResult(false, $"{context.FramesCompleted} / {frames.Frames} frames");

            case TargetAltitudeCondition target:
            {
                if (context.Site is null)
                {
                    return new ConditionResult(false, "Needs the observing site", ConditionAvailability.MissingSite);
                }

                if (context.Target is null)
                {
                    return new ConditionResult(false, "Needs a target", ConditionAvailability.MissingTarget);
                }

                var altitude = SkyAltitude.TargetDegrees(context.Target.RightAscensionHours, context.Target.DeclinationDegrees, context.UtcNow, context.Site);
                var met = target.Direction == ThresholdDirection.Above ? altitude >= target.Degrees : altitude <= target.Degrees;
                return new ConditionResult(
                    met,
                    string.Create(CultureInfo.InvariantCulture, $"Target altitude {altitude:0.0}° · needs {(target.Direction == ThresholdDirection.Above ? "≥" : "≤")} {target.Degrees:0.#}°"));
            }

            case SunAltitudeCondition sun:
            {
                if (context.Site is null)
                {
                    return new ConditionResult(false, "Needs the observing site", ConditionAvailability.MissingSite);
                }

                var altitude = SkyAltitude.SunDegrees(context.UtcNow, context.Site);
                var met = sun.Direction == ThresholdDirection.Above ? altitude >= sun.Degrees : altitude <= sun.Degrees;
                return new ConditionResult(
                    met,
                    string.Create(CultureInfo.InvariantCulture, $"Sun altitude {altitude:0.0}° · needs {(sun.Direction == ThresholdDirection.Above ? "≥" : "≤")} {sun.Degrees:0.#}°"));
            }

            case TwilightCondition twilight:
                return DecideTwilight(twilight, context);

            default:
                throw new NotSupportedException($"The condition {condition.GetType().Name} is not known to the evaluator.");
        }
    }

    private ConditionResult DecideTwilight(TwilightCondition twilight, ConditionContext context)
    {
        if (context.Site is null)
        {
            return new ConditionResult(false, "Needs the observing site", ConditionAvailability.MissingSite);
        }

        var threshold = twilight.Twilight.ThresholdDegrees();
        var altitude = SkyAltitude.SunDegrees(context.UtcNow, context.Site);
        var sunText = string.Create(CultureInfo.InvariantCulture, $"Sun altitude {altitude:0.0}°");
        if (twilight.Event == TwilightEvent.Dusk)
        {
            // Darkness is a state: the Sun is at or below the altitude. Already dark counts as dusk having come.
            if (altitude <= threshold)
            {
                return new ConditionResult(true, twilight.Summary + " · reached");
            }

            if (!_crossings.TryGetValue(twilight, out var dusk))
            {
                dusk = SunCrossings.NextDusk(context.Site, context.UtcNow, twilight.Twilight);
                _crossings[twilight] = dusk;
            }

            return dusk.IsFound
                ? new ConditionResult(false, $"Waiting for {twilight.Summary.ToLowerInvariant()} · {sunText} · in {Span(dusk.Utc!.Value - context.UtcNow)}")
                : new ConditionResult(false, $"No {twilight.Twilight.Title()} darkness within {SunCrossings.DefaultSearchHours:0} h at this site · {sunText}", ConditionAvailability.NotWithinReach);
        }

        // Dawn is an event: the next time the Sun comes up through the altitude after the scope began, found once.
        if (!_crossings.TryGetValue(twilight, out var dawn))
        {
            dawn = SunCrossings.NextDawn(context.Site, context.ScopeStartedUtc, twilight.Twilight);
            _crossings[twilight] = dawn;
        }

        if (!dawn.IsFound)
        {
            return new ConditionResult(false, $"No {twilight.Twilight.Title()} dawn within {SunCrossings.DefaultSearchHours:0} h at this site · {sunText}", ConditionAvailability.NotWithinReach);
        }

        return context.UtcNow >= dawn.Utc!.Value
            ? new ConditionResult(true, twilight.Summary + " · reached")
            : new ConditionResult(false, $"{twilight.Summary} in {Span(dawn.Utc.Value - context.UtcNow)}");
    }

    private static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{(int)span.TotalSeconds} s";
    }
}

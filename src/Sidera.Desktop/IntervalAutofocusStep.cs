using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop;

/// <summary>Words the editor and the builder share for what an autofocus policy does.</summary>
internal static class BuilderText
{
    /// <summary>"track start + every 60 min", "filter change", or "no trigger".</summary>
    public static string AutofocusWhen(bool atStart, bool afterFilterChange, double intervalMinutes)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (atStart)
        {
            parts.Add("track start");
        }

        if (afterFilterChange)
        {
            parts.Add("filter change");
        }

        if (intervalMinutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"every {intervalMinutes:0.##} min"));
        }

        return parts.Count == 0 ? "no trigger" : string.Join(" + ", parts);
    }
}

/// <summary>
/// When a rig last focused, as one Rig Track sees it: set by every autofocus of the track (the one at its start, after a filter change, by the interval, or one that was written into the
/// sequence), so that two reasons that fall together are one autofocus and the interval counts from the last one, whatever started it. It belongs to one track of one run.
/// </summary>
public sealed class AutofocusClock
{
    /// <summary>When the rig focused last, or when the track first looked at the clock without having focused; <c>null</c> before that.</summary>
    public DateTimeOffset? LastRun { get; private set; }

    internal void Mark(DateTimeOffset now) => LastRun = now;
}

/// <summary>
/// The autofocus interval policy, put before each exposure of a Rig Track (a safe point: between two things the track does, never inside an exposure). When the rig has not focused for the
/// interval it runs the autofocus of the track and starts the interval again; otherwise it does nothing. Before any autofocus has run, the interval counts from the first exposure, so a track
/// does not focus the moment it starts just because no time has been noted.
/// </summary>
public sealed class IntervalAutofocusStep : ISequenceStep
{
    private readonly AutofocusAction _action;
    private readonly AutofocusClock _clock;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;

    public IntervalAutofocusStep(AutofocusAction action, AutofocusClock clock, TimeSpan interval, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _action = action;
        _clock = clock;
        _interval = interval;
        _time = time;
    }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Autofocus every {_interval.TotalMinutes:0.##} min");

    public AutofocusAction Autofocus => _action;

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (_clock.LastRun is not { } last)
        {
            _clock.Mark(now);
            return new SequenceStepResult();
        }

        if (now - last < _interval)
        {
            return new SequenceStepResult();
        }

        await context.ExecuteChildAsync(_action, 0, 1, cancellationToken);
        _clock.Mark(_time.GetUtcNow());
        return new SequenceStepResult();
    }
}

/// <summary>Put after an autofocus of a track that has an interval: notes that the rig has just focused, whatever the reason was.</summary>
public sealed class AutofocusStampStep(AutofocusClock clock, TimeProvider time) : ISequenceStep
{
    public string Name => "Autofocus done";

    public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        clock.Mark(time.GetUtcNow());
        return Task.FromResult(new SequenceStepResult());
    }
}

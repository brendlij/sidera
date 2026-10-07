using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Sessions;

/// <summary>
/// How a block repeats its actions: a number of times, until something holds, or both (whichever comes first). "40 times", "for 4 hours" (a duration), "until 03:30" (a time), "while the target is above
/// 30°" (a target altitude that ends it when it falls below), "until any of: 40 frames, dawn, altitude below 25°".
/// </summary>
/// <param name="Count">How many times; <c>null</c> when only the conditions end it.</param>
/// <param name="Until">What ends it: the first of these.</param>
public sealed record RepeatRule(int? Count, IReadOnlyList<WorkflowCondition> Until)
{
    public static RepeatRule Times(int count) => new(count, []);

    /// <summary>The rule has something that ends it; a repeat that never ends is not a rule.</summary>
    public bool HasEnd => Count is >= 1 || Until.Count > 0;
}

/// <summary>When a block focuses by itself, so that it is not an action that someone has to place again and again. Each trigger runs at a safe point.</summary>
/// <param name="AtBlockStart">Focus before the block makes its first frame.</param>
/// <param name="EveryMinutes">Focus again when this many minutes have passed since the last focus; 0 is off.</param>
/// <param name="AfterFilterChange">Focus after the block turned the filter wheel to another filter.</param>
public sealed record FocusAutomation(bool AtBlockStart, double EveryMinutes, bool AfterFilterChange, FocusSettings Settings)
{
    public bool IsActive => AtBlockStart || EveryMinutes > 0 || AfterFilterChange;
}

/// <summary>Dither once for every <paramref name="EveryFrames"/> completed exposures. Every setup that shares the mount or the guider waits for it at a safe point.</summary>
public sealed record DitherAutomation(int EveryFrames, DitherSettings Settings);

/// <summary>What a block does by itself between its actions.</summary>
public sealed record BlockAutomation(DitherAutomation? Dither = null, FocusAutomation? Focus = null)
{
    public static BlockAutomation None { get; } = new();

    public bool IsEmpty => Dither is null && Focus is not { IsActive: true };
}

/// <summary>
/// A block: actions that run in order, a repeat rule, automation and limits. <b>Where repeating starts:</b> the actions before the first <see cref="ExposureAction"/> (Set Filter, Wait Until, Autofocus,
/// Move Focuser) run once when the block starts; from the first exposure on, the actions are the body that repeats. The editor shows that point.
/// </summary>
/// <param name="Name">What the user called it; <c>null</c> shows as "Block 3".</param>
/// <param name="Limits">What ends this block early (dawn, an altitude, a time, a duration); the limits of the target are separate.</param>
public sealed record SequenceBlock(
    Guid Id, string? Name, bool Enabled, IReadOnlyList<SessionAction> Actions, RepeatRule Repeat, BlockAutomation Automation, IReadOnlyList<WorkflowCondition> Limits)
{
    /// <summary>A block that images with one filter (or none): Set Filter, then an Exposure, repeated <paramref name="frames"/> times.</summary>
    public static SequenceBlock Imaging(Guid? id, int? filterSlot, double exposureSeconds, int frames, string? name = null)
    {
        var actions = new List<SessionAction>();
        if (filterSlot is { } slot)
        {
            actions.Add(new SetFilterAction(Guid.NewGuid(), slot));
        }

        actions.Add(new ExposureAction(Guid.NewGuid(), exposureSeconds));
        return new SequenceBlock(id ?? Guid.NewGuid(), name, true, actions, RepeatRule.Times(frames), BlockAutomation.None, []);
    }

    /// <summary>The index of the first exposure: the actions before it run once, the others repeat. -1 for a block that makes no frame.</summary>
    public int BodyStart => Actions.ToList().FindIndex(a => a is ExposureAction);

    public ExposureAction? FirstExposure => Actions.OfType<ExposureAction>().FirstOrDefault();

    public SetFilterAction? FirstFilter => Actions.OfType<SetFilterAction>().FirstOrDefault();
}

/// <summary>The sequence of one imaging setup under a target: its blocks, one after another. Lanes of one target run side by side wherever their devices allow.</summary>
/// <param name="Setup">The imaging path (<see cref="ImagingBindingId"/>), or <c>null</c> for "the only setup there is". Never a name.</param>
public sealed record SetupLane(Guid Id, ImagingBindingId? Setup, IReadOnlyList<SequenceBlock> Blocks);

/// <summary>
/// A target: where the telescopes point, what is prepared once for all its lanes, the sequence of each imaging setup, and what ends the target. The coordinates are the target's: an action that needs them (Slew
/// &amp; Center) takes them from here, never repeats them.
/// </summary>
public sealed record SessionTarget(
    Guid Id,
    string Name,
    double RightAscensionHours,
    double DeclinationDegrees,
    double? RotationDegrees,
    bool Enabled,
    IReadOnlyList<SessionAction> Preparation,
    IReadOnlyList<SetupLane> Lanes,
    IReadOnlyList<WorkflowCondition> Limits)
{
    public static SessionTarget New(string name, double raHours, double decDegrees, double? rotationDegrees = null) =>
        new(Guid.NewGuid(), name, raHours, decDegrees, rotationDegrees, true, [], [], []);
}

/// <summary>What the session does by itself, whatever the blocks say. Today: the meridian flip (<c>null</c> follows the application's settings).</summary>
public sealed record SessionAutomation(MeridianFlipSettings? Flip)
{
    public static SessionAutomation Defaults { get; } = new((MeridianFlipSettings?)null);

    public bool UsesDefaultFlip => Flip is null;
}

/// <summary>
/// An imaging session: what it does by itself, what happens first, the targets one after another, and what happens last. This is what the sequencer edits and what is saved; it knows nothing of the screen and
/// nothing of devices (a lane says which imaging setup it is for, never which camera). <c>SessionCompiler</c> turns it into the steps the runtime runs.
/// </summary>
public sealed record SessionDefinition(
    SessionAutomation Automation, IReadOnlyList<SessionAction> Start, IReadOnlyList<SessionTarget> Targets, IReadOnlyList<SessionAction> End)
{
    public static SessionDefinition Empty { get; } = new(SessionAutomation.Defaults, [], [], []);

    public bool IsEmpty => Start.Count == 0 && Targets.Count == 0 && End.Count == 0;
}

/// <summary>A line about a block in the words of the person who made it: "300 s × 40 · Dither 3 · AF 60 m · stops: dawn, alt&lt;25°".</summary>
public static class BlockSummary
{
    public static string Title(SequenceBlock block, int number, Func<int, string?>? filterName = null)
    {
        if (!string.IsNullOrWhiteSpace(block.Name))
        {
            return block.Name!;
        }

        return block.FirstFilter is { } filter && filterName?.Invoke(filter.Slot) is { Length: > 0 } name ? name : $"Block {number}";
    }

    public static string Line(SequenceBlock block)
    {
        var parts = new List<string>();
        var exposure = block.FirstExposure;
        parts.Add(exposure is null ? "no exposure" : Format(exposure.Seconds) + " s" + Repeat(block.Repeat));
        if (block.Automation.Dither is { } dither)
        {
            parts.Add($"Dither {dither.EveryFrames}");
        }

        if (block.Automation.Focus is { IsActive: true } focus)
        {
            var triggers = new List<string>();
            if (focus.EveryMinutes > 0)
            {
                triggers.Add(Format(focus.EveryMinutes) + " m");
            }

            if (focus.AtBlockStart)
            {
                triggers.Add("start");
            }

            if (focus.AfterFilterChange)
            {
                triggers.Add("filter");
            }

            parts.Add("AF " + string.Join("/", triggers));
        }

        if (block.Limits.Count > 0)
        {
            parts.Add("stops: " + string.Join(", ", block.Limits.Select(Brief)));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>"× 40", " until 03:30", "× 40 or until dawn".</summary>
    public static string Repeat(RepeatRule rule)
    {
        var until = rule.Until.Count > 0 ? string.Join(", ", rule.Until.Select(Brief)) : null;
        return (rule.Count, until) switch
        {
            ({ } n, null) => $" × {n}",
            ({ } n, { } text) => $" × {n} or until {text}",
            (null, { } text) => $" until {text}",
            _ => string.Empty,
        };
    }

    /// <summary>A condition in a few characters, for a collapsed line: "dawn", "alt&lt;25°", "04:30", "4 h".</summary>
    public static string Brief(WorkflowCondition condition) => condition switch
    {
        TimeCondition { TimeOfDay: { } time } => time.ToString("HH':'mm", System.Globalization.CultureInfo.InvariantCulture),
        TimeCondition { AbsoluteUtc: { } utc } => utc.ToString("yyyy-MM-dd HH':'mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC",
        DurationCondition duration => duration.Duration.TotalHours >= 1 ? Format(duration.Duration.TotalHours) + " h" : Format(duration.Duration.TotalMinutes) + " min",
        TargetAltitudeCondition altitude => $"alt{(altitude.Direction == ThresholdDirection.Below ? "<" : ">")}{Format(altitude.Degrees)}°",
        SunAltitudeCondition sun => $"sun{(sun.Direction == ThresholdDirection.Below ? "<" : ">")}{Format(sun.Degrees)}°",
        TwilightCondition twilight => (twilight.Twilight == Sidera.Core.Astronomy.Twilight.Astronomical ? string.Empty : twilight.Twilight.Title() + " ")
            + (twilight.Event == TwilightEvent.Dawn ? "dawn" : "dusk"),
        FrameCountCondition frames => $"{frames.Frames} frames",
        _ => condition.Summary,
    };

    private static string Format(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

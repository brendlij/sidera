using System.Globalization;
using Sidera.Core.Astronomy;

namespace Sidera.Core.Conditions;

/// <summary>Which side of a threshold a condition is about.</summary>
public enum ThresholdDirection
{
    /// <summary>Met when the value is at or above the threshold.</summary>
    Above,

    /// <summary>Met when the value is at or below the threshold.</summary>
    Below
}

/// <summary>Sunset side or sunrise side of a twilight.</summary>
public enum TwilightEvent
{
    /// <summary>Evening: the Sun has gone down through the altitude of the twilight. A state: it stays true until the morning.</summary>
    Dusk,

    /// <summary>Morning: the Sun comes up through the altitude of the twilight. An event: the next time it happens after the condition starts to be watched.</summary>
    Dawn
}

/// <summary>
/// A fact about the sky, the clock or the progress of imaging that a workflow can wait for or stop at. A closed set of typed values, not an expression language: each kind says what it is about
/// and carries its own numbers, and <see cref="ConditionEvaluator"/> is the one place that decides whether it holds. Conditions do not say whether they start, stop or wait for anything: the
/// place they are put does (a block's start list, its stop list, a Wait step), so one condition type is never ambiguous. Later kinds (the Moon, the weather, the guiding error) are new records
/// here and a branch in the evaluator; nothing else needs to know them.
/// </summary>
public abstract record WorkflowCondition
{
    /// <summary>The condition in a few words, for rows and status: "Target altitude below 25°".</summary>
    public abstract string Summary { get; }

    /// <summary>What is wrong with the values; <c>null</c> when it is a condition that can be evaluated.</summary>
    public virtual string? Problem => null;
}

/// <summary>
/// A moment: either an absolute instant (UTC), or a time of day on a clock. A time of day is the next time the clock reads it after the condition starts to be watched (a block that starts at
/// 22:00 and stops at 04:30 stops at 04:30 the next morning); the zone is named (<see cref="TimeZoneId"/>, the computer's zone when it is <c>null</c>), so what the user sees as local time and
/// the UTC instant that is calculated with are one and the same thing.
/// </summary>
public sealed record TimeCondition(DateTime? AbsoluteUtc, TimeOnly? TimeOfDay, string? TimeZoneId = null) : WorkflowCondition
{
    public static TimeCondition AtUtc(DateTime utc) => new(DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc), null);

    public static TimeCondition AtLocalTime(TimeOnly timeOfDay, string? timeZoneId = null) => new(null, timeOfDay, timeZoneId);

    public override string Summary => AbsoluteUtc is { } utc
        ? string.Create(CultureInfo.InvariantCulture, $"Time {utc:yyyy-MM-dd HH:mm} UTC")
        : string.Create(CultureInfo.InvariantCulture, $"Time {TimeOfDay:HH\\:mm}");

    public override string? Problem =>
        (AbsoluteUtc is null) == (TimeOfDay is null) ? "A time condition is either an instant or a time of day."
        : TimeZoneId is { } id && Zone(id) is null ? $"The time zone '{id}' is not known on this computer."
        : null;

    /// <summary>The zone the time of day is read on.</summary>
    public TimeZoneInfo Zone() => (TimeZoneId is null ? null : Zone(TimeZoneId)) ?? TimeZoneInfo.Local;

    private static TimeZoneInfo? Zone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>The instant: the absolute one, or the first time at or after <paramref name="after"/> that the clock of the zone reads the time of day.</summary>
    public DateTime ResolveUtc(DateTime after)
    {
        if (AbsoluteUtc is { } utc)
        {
            return utc;
        }

        var zone = Zone();
        var start = after.ToUniversalTime();
        var localDate = TimeZoneInfo.ConvertTimeFromUtc(start, zone).Date;
        for (var day = 0; day < 3; day++)
        {
            var local = DateTime.SpecifyKind(localDate.AddDays(day) + TimeOfDay!.Value.ToTimeSpan(), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                local = local.AddHours(1); // the hour that the clocks skip: the first moment after it
            }

            var candidate = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            if (candidate >= start)
            {
                return candidate;
            }
        }

        return start;
    }
}

/// <summary>Time that has passed since what the condition is part of started: a block's imaging, or the target's.</summary>
public sealed record DurationCondition(TimeSpan Duration) : WorkflowCondition
{
    public override string Summary => $"Duration {FormatSpan(Duration)}";

    public override string? Problem => Duration <= TimeSpan.Zero ? "A duration must be longer than 0." : null;

    internal static string FormatSpan(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        return minutes >= 60 && minutes % 60 == 0 ? $"{minutes / 60} h"
            : minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min"
            : minutes >= 1 ? $"{minutes} min"
            : string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.#} s");
    }
}

/// <summary>The altitude of the target of the workflow, computed from its coordinates, the observing site and the time: never read from a mount.</summary>
public sealed record TargetAltitudeCondition(double Degrees, ThresholdDirection Direction) : WorkflowCondition
{
    public override string Summary => string.Create(CultureInfo.InvariantCulture, $"Target altitude {(Direction == ThresholdDirection.Above ? "above" : "below")} {Degrees:0.#}°");

    public override string? Problem => !double.IsFinite(Degrees) || Degrees < -90 || Degrees > 90 ? "A target altitude must be from -90° to +90°." : null;
}

/// <summary>The altitude of the Sun's centre; "custom" twilight for those who know the number. <see cref="TwilightCondition"/> is the one to use for dusk and dawn.</summary>
public sealed record SunAltitudeCondition(double Degrees, ThresholdDirection Direction) : WorkflowCondition
{
    public override string Summary => string.Create(CultureInfo.InvariantCulture, $"Sun altitude {(Direction == ThresholdDirection.Above ? "above" : "below")} {Degrees:0.#}°");

    public override string? Problem => !double.IsFinite(Degrees) || Degrees < -90 || Degrees > 90 ? "A Sun altitude must be from -90° to +90°." : null;
}

/// <summary>
/// Dusk or dawn of a twilight. Dusk means darkness: the Sun is at or below the altitude of the twilight, which stays true all night. Dawn means the Sun coming up through that altitude: the
/// next morning after the condition starts to be watched, never "the Sun is above -18°", which is true all day.
/// </summary>
public sealed record TwilightCondition(Twilight Twilight, TwilightEvent Event) : WorkflowCondition
{
    public override string Summary => Event == TwilightEvent.Dusk ? $"{Name} darkness" : $"{Name} dawn";

    private string Name => char.ToUpperInvariant(Twilight.Title()[0]) + Twilight.Title()[1..];
}

/// <summary>Frames of the block that have been taken.</summary>
public sealed record FrameCountCondition(int Frames) : WorkflowCondition
{
    public override string Summary => $"{Frames} frames";

    public override string? Problem => Frames < 1 ? "A frame count must be at least 1." : null;
}

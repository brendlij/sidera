using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Mounts;

/// <summary>What a failed flip does, after the attempts of <see cref="MeridianFlipSettings.MaxFlipAttempts"/> are used.</summary>
public enum MeridianFlipFailureBehavior
{
    /// <summary>
    /// The setups of the mount group stay where they are, held at their safe points, and the flip waits for the user to retry it or abort it. Nothing images after a failed flip or centering
    /// by itself. Mount groups that do not belong to the flip are not touched.
    /// </summary>
    PauseSession,

    /// <summary>The flip fails, and with it the sequence.</summary>
    AbortSession,
}

/// <summary>
/// How a session handles the target crossing the meridian: when new exposures are held, when the mount flips, how late it may, and what is done after it. All times are minutes, counted from the
/// moment the target is on the meridian (hour angle 0) and measured by the sky, not by the mount's pier side.
/// <para>
/// <see cref="PauseBeforeMeridianMinutes"/> is a guard, not a delay: from that long before the meridian a new exposure only starts when it ends before the flip is due; before that moment it only
/// starts when it ends before <see cref="LatestAllowedFlipMinutes"/>. <see cref="FlipAfterMeridianMinutes"/> is when the flip is due, once every setup of the mount is at a safe point. When the
/// flip has not started <see cref="LatestAllowedFlipMinutes"/> after the meridian, the failure behavior applies.
/// </para>
/// </summary>
public sealed record MeridianFlipSettings
{
    public const int MaximumMinutes = 180;

    /// <summary>Whether the session flips at all. A disabled policy changes nothing.</summary>
    public bool Enabled { get; init; }

    // ---- timing
    public double PauseBeforeMeridianMinutes { get; init; } = 5;
    public double FlipAfterMeridianMinutes { get; init; } = 2;
    public double LatestAllowedFlipMinutes { get; init; } = 15;

    // ---- before the flip

    /// <summary>
    /// An exposure that is running when the flip comes due is let to finish. Switching it off (aborting exposures for a flip) is not supported yet: <see cref="Problems"/> says so.
    /// </summary>
    public bool FinishCurrentExposure { get; init; } = true;

    /// <summary>Guiding of the mount group is stopped before the mount moves.</summary>
    public bool StopGuidingBeforeFlip { get; init; } = true;

    // ---- after the flip

    /// <summary>Center the target again (plate solve and correct) with the pointing setup. Never synchronizes the mount.</summary>
    public bool RecenterAfterFlip { get; init; } = true;

    /// <summary>Verify the sky rotation with the rotator of the pointing setup, where it has one and the target names a rotation.</summary>
    public bool VerifyRotationAfterFlip { get; init; } = true;

    /// <summary>Focus every setup of the mount group that has a focuser, after the final pointing and rotation.</summary>
    public bool AutofocusAfterFlip { get; init; }

    /// <summary>Start the guider again, and wait for it to settle, when it was guiding before the flip.</summary>
    public bool RestartGuidingAfterFlip { get; init; } = true;

    /// <summary>One coordinated dither after the guiding has settled again.</summary>
    public bool DitherAfterFlip { get; init; }

    /// <summary>An additional pause after everything that was asked for; it is not a replacement for settling.</summary>
    public double PauseAfterFlipMinutes { get; init; }

    // ---- failure

    /// <summary>How often the mount flip and centering are tried before the failure behavior applies.</summary>
    public int MaxFlipAttempts { get; init; } = 2;

    public MeridianFlipFailureBehavior FailureBehavior { get; init; } = MeridianFlipFailureBehavior.PauseSession;

    // ---- centering after the flip (the same settings as a Slew & Center)
    public double CenteringToleranceArcseconds { get; init; } = 60;
    public int MaxCenteringAttempts { get; init; } = 5;
    public double SolveExposureSeconds { get; init; } = 5;

    /// <summary>What is wrong with the settings, in sentences; empty when they can be used. A disabled policy is not looked at.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (!Enabled)
        {
            return problems;
        }

        void Range(double value, string label, double min, double max)
        {
            if (!double.IsFinite(value) || value < min || value > max)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{label} must be between {min:0.##} and {max:0.##} minutes."));
            }
        }

        Range(PauseBeforeMeridianMinutes, "Pause before the meridian", 0, MaximumMinutes);
        Range(FlipAfterMeridianMinutes, "Flip after the meridian", 0, MaximumMinutes);
        Range(LatestAllowedFlipMinutes, "The latest allowed flip", 0, MaximumMinutes);
        Range(PauseAfterFlipMinutes, "Pause after the flip", 0, 60);
        if (double.IsFinite(FlipAfterMeridianMinutes) && double.IsFinite(LatestAllowedFlipMinutes) && LatestAllowedFlipMinutes < FlipAfterMeridianMinutes)
        {
            problems.Add("The latest allowed flip cannot be earlier than the flip.");
        }

        if (MaxFlipAttempts is < 1 or > 5)
        {
            problems.Add("The flip attempts must be between 1 and 5.");
        }

        if (!FinishCurrentExposure)
        {
            problems.Add("Aborting an exposure for a flip is not supported yet: leave 'Finish the current exposure' on.");
        }

        if (!double.IsFinite(CenteringToleranceArcseconds) || CenteringToleranceArcseconds <= 0 || MaxCenteringAttempts < 1
            || !double.IsFinite(SolveExposureSeconds) || SolveExposureSeconds <= 0)
        {
            problems.Add("The centering after the flip needs a tolerance, an exposure and a number of attempts that are greater than 0.");
        }

        return problems;
    }
}

/// <summary>Where a target is relative to its meridian, as the settings read it.</summary>
public enum MeridianFlipPhase
{
    /// <summary>Far from the guard: nothing is held.</summary>
    Monitoring,

    /// <summary>Inside the guard: a new exposure only starts when it ends before the flip is due.</summary>
    Approaching,

    /// <summary>The flip is due: it happens once every setup of the mount is at a safe point.</summary>
    FlipDue,

    /// <summary>The latest allowed flip has passed without a flip.</summary>
    Overdue,
}

/// <summary>
/// The sky side of a flip: hour angle and the phases and exposure guard that follow from it. Pure: it knows no mount, no clock and no session. The hour angle is the astronomical one (local
/// sidereal time minus right ascension), authoritative for timing; the pier side a driver reports is only a check afterwards.
/// </summary>
public static class MeridianFlipTiming
{
    /// <summary>The local sidereal time in hours, 0 to 24, at <paramref name="utc"/> for an east-positive longitude in degrees.</summary>
    public static double LocalSiderealTimeHours(DateTime utc, double longitudeDegrees)
    {
        var days = utc.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        var gmst = 18.697374558 + 24.06570982441908 * days;
        var lst = (gmst + longitudeDegrees / 15) % 24;
        return lst < 0 ? lst + 24 : lst;
    }

    /// <summary>Normalizes an angle in hours to the half-open range from -12 (exclusive) to +12 (inclusive).</summary>
    public static double NormalizeHours(double hours)
    {
        var h = hours % 24;
        if (h > 12)
        {
            h -= 24;
        }
        else if (h <= -12)
        {
            h += 24;
        }

        return h;
    }

    /// <summary>The hour angle in hours: negative east of the meridian (the target will cross it), 0 on it, positive west of it (it has crossed).</summary>
    public static double HourAngleHours(double rightAscensionHours, double localSiderealTimeHours) => NormalizeHours(localSiderealTimeHours - rightAscensionHours);

    /// <summary>The hour angle of a target at a place and a time.</summary>
    public static double HourAngleHours(double rightAscensionHours, DateTime utc, double longitudeDegrees) =>
        HourAngleHours(rightAscensionHours, LocalSiderealTimeHours(utc, longitudeDegrees));

    public static MeridianFlipPhase PhaseOf(MeridianFlipSettings settings, double hourAngleMinutes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (hourAngleMinutes > settings.LatestAllowedFlipMinutes)
        {
            return MeridianFlipPhase.Overdue;
        }

        if (hourAngleMinutes >= settings.FlipAfterMeridianMinutes)
        {
            return MeridianFlipPhase.FlipDue;
        }

        return hourAngleMinutes >= -settings.PauseBeforeMeridianMinutes ? MeridianFlipPhase.Approaching : MeridianFlipPhase.Monitoring;
    }

    /// <summary>
    /// Whether an exposure of <paramref name="exposureSeconds"/> may start now, with the target at <paramref name="hourAngleMinutes"/>: inside the guard it has to end before the flip is due, and
    /// before the guard before the latest allowed flip. A target whose flip is due or overdue starts nothing: the flip comes first.
    /// </summary>
    public static bool CanStartExposure(MeridianFlipSettings settings, double hourAngleMinutes, double exposureSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ends = hourAngleMinutes + exposureSeconds / 60;
        return PhaseOf(settings, hourAngleMinutes) switch
        {
            MeridianFlipPhase.Monitoring => ends <= settings.LatestAllowedFlipMinutes,
            MeridianFlipPhase.Approaching => ends <= settings.FlipAfterMeridianMinutes,
            _ => false,
        };
    }

    /// <summary>Minutes until the guard starts (the pause before the meridian); negative once it has; the hour angle is in minutes.</summary>
    public static double MinutesUntilGuard(MeridianFlipSettings settings, double hourAngleMinutes) =>
        -hourAngleMinutes - settings.PauseBeforeMeridianMinutes;

    /// <summary>Minutes until the target is on the meridian; negative after it.</summary>
    public static double MinutesUntilMeridian(double hourAngleMinutes) => -hourAngleMinutes;
}

/// <summary>What a flip is doing, for the status of the session and for the log.</summary>
public enum MeridianFlipState
{
    /// <summary>Watching the target; nothing is held yet.</summary>
    Monitoring,

    /// <summary>Inside the guard: new exposures only start when they fit before the flip.</summary>
    Approaching,

    /// <summary>The flip is due and waits for every setup of the mount to be at a safe point.</summary>
    HoldingForSafePoint,

    StoppingGuiding,
    Flipping,
    Solving,
    Centering,
    Rotating,
    Autofocusing,
    StartingGuiding,
    Settling,
    Dithering,
    PostFlipPause,

    /// <summary>Done: imaging resumes.</summary>
    Completed,

    /// <summary>A step failed or the window passed; the failure behavior decides.</summary>
    Failed,
}

/// <summary>
/// The state of the flip of one mount, published on the event bus whenever it changes. <paramref name="Message"/> is a sentence for the status ("Waiting for Main Rig"), and
/// <paramref name="HourAngleHours"/> is where the target is, when it is known.
/// </summary>
public sealed record MeridianFlipStateChanged(
    DeviceId MountId, MeridianFlipState State, string Message, double? HourAngleHours, int Attempt, IReadOnlyList<string> Setups) : ISideraEvent;

/// <summary>A meridian flip failed in a way that is told to the user; <see cref="Exception.Message"/> is the sentence.</summary>
public sealed class MeridianFlipFailedException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

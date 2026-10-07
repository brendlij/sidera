using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop;

public enum SequenceStepKind
{
    PlateSolve,
    SlewAndCenter,
    SyncMountToSolved,
    Exposure,
    Delay,
    Slew,
    StartGuiding,
    StopGuiding,
    Dither,
    Repeat,

    /// <summary>An exposure with the camera of the rig of its track; only exists inside a Rig Track.</summary>
    RigExposure,

    /// <summary>Imaging with several rigs at once, one Rig Track each.</summary>
    MultiRig,

    /// <summary>One rig of a Multi-Rig block; not a step that can be added to a sequence.</summary>
    RigTrack,

    /// <summary>Moves a focuser, selected by the step, to an absolute position.</summary>
    MoveFocuser,

    /// <summary>Turns a filter wheel, selected by the step, to a slot.</summary>
    ChangeFilter,

    /// <summary>Moves the focuser of the rig of its track; only exists inside a Rig Track.</summary>
    RigMoveFocuser,

    /// <summary>Turns the filter wheel of the rig of its track; only exists inside a Rig Track.</summary>
    RigChangeFilter,

    /// <summary>Focuses the camera and focuser of a rig, selected by the step.</summary>
    Autofocus,

    /// <summary>Focuses the rig of its track; only exists inside a Rig Track.</summary>
    RigAutofocus,

    /// <summary>Turns the rotator of a rig so that the sky has an angle in the image, by the calibration; does not solve.</summary>
    RotateToAngle,

    /// <summary>Rotates, solves and corrects until the sky has the angle within a tolerance.</summary>
    RotateAndVerify,

    /// <summary>Centers a target and rotates to an angle, verified by solves.</summary>
    CenterAndRotate,

    /// <summary>Waits until all of its conditions hold: a time, an altitude of the target or the Sun, darkness.</summary>
    WaitUntil,

    /// <summary>Cools or warms a camera, parks or unparks a mount, switches tracking. Added in version 9.</summary>
    DeviceOperation
}

/// <summary>
/// One step of a user-defined sequence as plain values, in the units a user thinks in (seconds, pixels, hours,
/// degrees). It holds no runtime objects and no UI state: <see cref="SequenceDraftBuilder"/> validates it and turns
/// it into a fresh runtime step every time the sequence runs. <see cref="Id"/> is local to the Desktop editor; it
/// ties a row of the editor and of a running sequence to the same draft step.
/// </summary>
public abstract record SequenceStepDraft(Guid Id)
{
    public abstract SequenceStepKind Kind { get; }

    /// <summary>The devices the step uses, as far as they are selected.</summary>
    public abstract IEnumerable<DeviceId> DeviceIds { get; }

    private protected static IEnumerable<DeviceId> Of(params DeviceId?[] ids) => ids.OfType<DeviceId>();
}

/// <summary>A step that does one thing and has no children: everything a <see cref="RepeatStepDraft"/> may contain.</summary>
public abstract record LeafStepDraft(Guid Id) : SequenceStepDraft(Id);

public sealed record SlewAndCenterStepDraft(
    Guid Id, DeviceId? MountId, RigId? RigId, double RightAscensionHours, double DeclinationDegrees, double ToleranceArcseconds, int MaxAttempts, double ExposureSeconds,
    string? TargetName = null, double? DesiredRotationDegrees = null)
    : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.SlewAndCenter;
    public override IEnumerable<DeviceId> DeviceIds => Of(MountId);
}

/// <summary>Turns the rotator of a rig to a sky rotation (degrees, the one of a plate solve) by the calibration of the rig. Does not solve.</summary>
public sealed record RotateToAngleStepDraft(Guid Id, RigId? RigId, double SkyRotationDegrees) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RotateToAngle;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>Rotates to a sky rotation and corrects until a plate solve shows it within <paramref name="ToleranceDegrees"/>.</summary>
public sealed record RotateAndVerifyStepDraft(
    Guid Id, RigId? RigId, double SkyRotationDegrees, double ToleranceDegrees, int MaxAttempts, double ExposureSeconds) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RotateAndVerify;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>Centers a target and rotates the sky to an angle; both the pointing and the rotation are verified by plate solves.</summary>
public sealed record CenterAndRotateStepDraft(
    Guid Id, DeviceId? MountId, RigId? RigId, double RightAscensionHours, double DeclinationDegrees, double ToleranceArcseconds, int MaxCenteringAttempts,
    double SkyRotationDegrees, double RotationToleranceDegrees, int MaxRotationAttempts, int MaxRounds, double ExposureSeconds, string? TargetName = null)
    : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.CenterAndRotate;
    public override IEnumerable<DeviceId> DeviceIds => Of(MountId);
}

public sealed record SyncMountStepDraft(Guid Id, DeviceId? MountId) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.SyncMountToSolved;
    public override IEnumerable<DeviceId> DeviceIds => Of(MountId);
}

/// <summary>
/// An operation on one camera or one mount that does not move across the sky: <see cref="Sidera.Runtime.Sequencing.DeviceOperation.CoolCamera"/> (to <see cref="Celsius"/>, in steps over
/// <see cref="RampMinutes"/>), warm, park, unpark, tracking on or off. The device is a camera for the first two and a mount for the others. Added in version 9.
/// </summary>
public sealed record DeviceOperationStepDraft(Guid Id, Sidera.Runtime.Sequencing.DeviceOperation Operation, DeviceId? DeviceId, double Celsius = 0, double RampMinutes = 0) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.DeviceOperation;
    public override IEnumerable<DeviceId> DeviceIds => Of(DeviceId);

    /// <summary>The device is a camera (cool, warm); otherwise a mount.</summary>
    public bool IsCamera => Operation is Sidera.Runtime.Sequencing.DeviceOperation.CoolCamera or Sidera.Runtime.Sequencing.DeviceOperation.WarmCamera;
}

public sealed record PlateSolveStepDraft(Guid Id, RigId? RigId, double ExposureSeconds) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.PlateSolve;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

public sealed record ExposureStepDraft(Guid Id, DeviceId? CameraId, double Seconds) : LeafStepDraft(Id)
{
    /// <summary>
    /// What the exposure asks of the camera besides its duration: only what is set is an override, the rest is inherited from
    /// the defaults of the camera. Plain values, no hardware knowledge: it is checked against the camera when the sequence runs.
    /// </summary>
    public AcquisitionIntent Acquisition { get; init; } = AcquisitionIntent.Default;

    public override SequenceStepKind Kind => SequenceStepKind.Exposure;
    public override IEnumerable<DeviceId> DeviceIds => Of(CameraId);
}

public sealed record DelayStepDraft(Guid Id, double Seconds) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.Delay;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

public sealed record SlewStepDraft(Guid Id, DeviceId? MountId, double RightAscensionHours, double DeclinationDegrees)
    : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.Slew;
    public override IEnumerable<DeviceId> DeviceIds => Of(MountId);
}

public sealed record StartGuidingStepDraft(Guid Id, DeviceId? GuiderId) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.StartGuiding;
    public override IEnumerable<DeviceId> DeviceIds => Of(GuiderId);
}

public sealed record StopGuidingStepDraft(Guid Id, DeviceId? GuiderId) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.StopGuiding;
    public override IEnumerable<DeviceId> DeviceIds => Of(GuiderId);
}

/// <summary>
/// A dither that waits for guiding to settle. <see cref="CameraId"/> is the one camera the dither disturbs; the
/// coordination between branches of a parallel sequence is not part of a linear draft.
/// </summary>
public sealed record DitherStepDraft(
    Guid Id,
    DeviceId? GuiderId,
    DeviceId? MountId,
    DeviceId? CameraId,
    double AmplitudePixels,
    double SettleThresholdPixels,
    double SettleStableSeconds,
    double SettleTimeoutSeconds
) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.Dither;
    public override IEnumerable<DeviceId> DeviceIds => Of(GuiderId, MountId, CameraId);
}

/// <summary>
/// The one container of the editor: its <see cref="Children"/> run in order, <see cref="Count"/> times. Children are
/// leaf steps only, which is how the model keeps a Repeat from containing another Repeat.
/// </summary>
public sealed record RepeatStepDraft(Guid Id, int Count, IReadOnlyList<LeafStepDraft> Children, StopConditionsDraft? Stop = null) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.Repeat;
    public override IEnumerable<DeviceId> DeviceIds => Children.SelectMany(child => child.DeviceIds);
}

/// <summary>
/// An exposure inside a Rig Track. The camera is not part of it: it is the camera of the rig of the track, which a rig
/// has exactly one of, so there is nothing to select and nothing that could disagree with the rig.
/// </summary>
public sealed record RigExposureStepDraft(Guid Id, double Seconds) : LeafStepDraft(Id)
{
    /// <summary>The same acquisition intent as an exposure outside a track has; the camera is the camera of the rig.</summary>
    public AcquisitionIntent Acquisition { get; init; } = AcquisitionIntent.Default;

    public override SequenceStepKind Kind => SequenceStepKind.RigExposure;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>Moves one focuser to an absolute position, in focuser steps. For a step outside a Rig Track.</summary>
public sealed record MoveFocuserStepDraft(Guid Id, DeviceId? FocuserId, int Position) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.MoveFocuser;
    public override IEnumerable<DeviceId> DeviceIds => Of(FocuserId);
}

/// <summary>
/// Turns one filter wheel to a slot. The slot is identified by its index, which is what a filter wheel offers that does
/// not change; the name the user sees is only looked up. For a step outside a Rig Track.
/// </summary>
public sealed record ChangeFilterStepDraft(Guid Id, DeviceId? FilterWheelId, int SlotIndex) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.ChangeFilter;
    public override IEnumerable<DeviceId> DeviceIds => Of(FilterWheelId);
}

/// <summary>
/// A focuser move inside a Rig Track. The focuser is not part of it: it is the focuser of the rig of the track, so the
/// sequence stays the same when the rig is given another focuser, and nothing can name the focuser of another rig. It
/// is a distinct kind of step from <see cref="MoveFocuserStepDraft"/>, whose meaning does not depend on where it is.
/// </summary>
public sealed record RigMoveFocuserStepDraft(Guid Id, int Position) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RigMoveFocuser;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// A filter change inside a Rig Track, on the filter wheel of the rig of the track, to the slot with this index. The
/// counterpart of <see cref="RigMoveFocuserStepDraft"/>.
/// </summary>
public sealed record RigChangeFilterStepDraft(Guid Id, int SlotIndex) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RigChangeFilter;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// Focuses a rig: samples the focus at positions around the current focuser position and moves to the best one. The
/// rig selects the camera and the focuser, so there are none to choose and none that could disagree with the rig. For
/// a step outside a Rig Track; a rig that is imaged in a Rig Track uses <see cref="RigAutofocusStepDraft"/>.
/// </summary>
/// <param name="RigId">The rig, or <c>null</c> when none is selected. Kept as chosen even if no such rig is registered.</param>
/// <param name="ExposureSeconds">The exposure at each sample position.</param>
/// <param name="StepSize">The distance between two sample positions, in focuser steps.</param>
/// <param name="SampleCount">How many positions are sampled, symmetrically around the current one.</param>
public sealed record AutofocusStepDraft(Guid Id, RigId? RigId, double ExposureSeconds, int StepSize, int SampleCount)
    : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.Autofocus;

    // The camera and the focuser are the rig's: see SequenceDraftBuilder.RequiredDeviceIds.
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// Autofocus inside a Rig Track: it focuses the rig of the track, with that rig camera and focuser. A distinct kind of
/// step from <see cref="AutofocusStepDraft"/>, whose meaning does not depend on where it is.
/// </summary>
public sealed record RigAutofocusStepDraft(Guid Id, double ExposureSeconds, int StepSize, int SampleCount) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RigAutofocus;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// When the rig of a Rig Track focuses by itself, and how: the user's intent, not steps. With the policy enabled, an
/// autofocus is generated at the start of the track (<see cref="AtTrackStart"/>: once, before the first step of the
/// track that does something) and after every Change Filter step of the track (<see cref="AfterFilterChange"/>), at the
/// place in the sequence where that step runs. An explicit autofocus step in the place of a generated one is used
/// instead of it: there is never both. The fields are kept when the policy is not <see cref="Enabled"/>, and then have
/// no effect. Nothing here is a step: generated autofocus runs are neither listed nor saved.
/// </summary>
public sealed record RigAutofocusPolicyDraft(
    bool Enabled,
    bool AtTrackStart,
    bool AfterFilterChange,
    double ExposureSeconds,
    int StepSize,
    int SampleCount,
    double IntervalMinutes = 0
)
{
    /// <summary>No automatic autofocus, with the values a user starts from when switching it on.</summary>
    public static RigAutofocusPolicyDraft Default { get; } = new(false, false, false, 1, 400, 7);

    /// <summary>The policy has something to do: it is enabled and at least one trigger is selected.</summary>
    public bool IsActive => Enabled && (AtTrackStart || AfterFilterChange || IntervalMinutes > 0);

    /// <summary>
    /// Focus again when this many minutes have passed since the rig last focused (by any trigger or by an explicit step); 0 is off. The check is made before each exposure of the track, which
    /// is a safe point, so an exposure that is running is never interrupted. Added in version 8.
    /// </summary>
    public bool HasInterval => IntervalMinutes > 0;
}

/// <summary>Why an autofocus that nobody wrote into the sequence runs.</summary>
public enum AutofocusOrigin
{
    TrackStart,
    AfterFilterChange,

    /// <summary>The interval since the last autofocus of the rig has passed.</summary>
    Interval
}

/// <summary>
/// One imaging rig of a Multi-Rig block and what that rig does: its steps run in order, next to the other tracks. A
/// track is not a step of the sequence and cannot be put anywhere else. <see cref="Steps"/> are exposures with the rig
/// camera, delays, moves of the rig focuser, changes of the rig filter wheel and Repeats of those; the validation
/// refuses anything else, because a track must not do what
/// belongs to the whole session (moving the shared mount, guiding).
/// </summary>
/// <param name="RigId">The rig, or <c>null</c> when none is selected. Kept as chosen even if no such rig is registered.</param>
public sealed record RigTrackDraft(
    Guid Id,
    RigId? RigId,
    IReadOnlyList<SequenceStepDraft> Steps,
    RigAutofocusPolicyDraft? AutofocusPolicy = null
) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RigTrack;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// When a Multi-Rig block dithers the shared mount, and how. The frames of the <see cref="TriggerRigId"/> are counted:
/// after every <see cref="EveryNFrames"/>th exposure of that rig that completed, one dither of the shared mount is
/// requested, which every other track of the block waits for at a safe point before the mount moves. It says what
/// the user wants; how that is orchestrated is for the builder. The fields are kept when the policy is not
/// <see cref="Enabled"/>, and then have no effect.
/// </summary>
/// <param name="TriggerRigId">The rig whose frames are counted; <c>null</c> when none is selected. Kept as chosen.</param>
public sealed record MultiRigDitherPolicyDraft(
    bool Enabled,
    RigId? TriggerRigId,
    int EveryNFrames,
    double AmplitudePixels,
    double SettleThresholdPixels,
    double SettleStableSeconds,
    double SettleTimeoutSeconds
)
{
    /// <summary>No dithering, with the values a user starts from when switching it on.</summary>
    public static MultiRigDitherPolicyDraft Default { get; } = new(false, null, 3, 1.5, 0.5, 1, 10);
}

/// <summary>
/// Imaging with several rigs of one session at once, one <see cref="RigTrackDraft"/> per rig. The mount and the guider
/// are shared by the session and are not part of the tracks. A Multi-Rig block is only found at the top level, and
/// is finished when all of its tracks are. A <see cref="DitherPolicy"/> of <c>null</c> is the default one: no dithering.
/// </summary>
/// <summary>
/// The meridian flip of a Multi-Rig block: the settings, and what the flip needs to know of the session: the target it slews back to and keeps centered, the rotation it wants, the setup whose
/// camera solves for a mount (<c>null</c>: the first track on the mount, in the order of the block), and how a settled guider is recognized after it started again. The flip happens once for
/// each mount of the block, for every setup on it together; a disabled policy changes nothing. Added in version 8.
/// </summary>
public sealed record MeridianFlipPolicyDraft(
    Sidera.Core.Mounts.MeridianFlipSettings Settings,
    double RightAscensionHours,
    double DeclinationDegrees,
    string? TargetName = null,
    double? DesiredRotationDegrees = null,
    RigId? PointingRigId = null,
    double DitherAmplitudePixels = 1.5,
    double SettleThresholdPixels = 0.5,
    double SettleStableSeconds = 1,
    double SettleTimeoutSeconds = 60)
{
    public bool IsEnabled => Settings.Enabled;
}

/// <param name="SingleTrack">
/// The block may have one track: it is then the imaging of one setup with its policies (a workflow makes such a block for a single imaging setup). The editor of Multi-Rig Imaging still
/// asks for two tracks when it is false, which is what a block that somebody builds by hand is. Added in version 8.
/// </param>
public sealed record MultiRigStepDraft(
    Guid Id,
    IReadOnlyList<RigTrackDraft> Tracks,
    MultiRigDitherPolicyDraft? DitherPolicy = null,
    bool SingleTrack = false,
    MeridianFlipPolicyDraft? MeridianFlip = null,
    StopConditionsDraft? TargetStop = null
) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.MultiRig;

    // The cameras of the rigs are known to the rig registry, not to the draft: see SequenceDraftBuilder.RequiredDeviceIds.
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>The equipment that the whole session shares: one mount and, usually, one guider.</summary>
public sealed record SharedEquipmentDraft(DeviceId? MountId, DeviceId? GuiderId)
{
    /// <summary>
    /// What the rigs have in common, as the fallback for a step whose rig names no mount or guider: the one mount that every rig with a mount names, and likewise the guider. Rigs on different
    /// mounts have no common one, so nothing is shared and each rig-local step uses its rig's own. Without any rig the given defaults are used (a session with devices and no rigs).
    /// </summary>
    public static SharedEquipmentDraft FromRigs(IEnumerable<Sidera.Core.Rigs.Rig> rigs, DeviceId? defaultMount, DeviceId? defaultGuider, bool useDefaults)
    {
        var all = rigs.ToList();
        DeviceId? Single(IEnumerable<DeviceId?> ids) => ids.OfType<DeviceId>().Distinct().ToList() is [var only] ? only : null;
        var mount = Single(all.Select(r => r.MountId));
        var guider = Single(all.Select(r => r.GuiderId));
        return useDefaults ? new SharedEquipmentDraft(defaultMount, defaultGuider) : new SharedEquipmentDraft(mount, guider);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop;

public enum SequenceStepKind
{
    PlateSolve,
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
    RigAutofocus
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
public sealed record RepeatStepDraft(Guid Id, int Count, IReadOnlyList<LeafStepDraft> Children) : SequenceStepDraft(Id)
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
    int SampleCount
)
{
    /// <summary>No automatic autofocus, with the values a user starts from when switching it on.</summary>
    public static RigAutofocusPolicyDraft Default { get; } = new(false, false, false, 1, 400, 7);

    /// <summary>The policy has something to do: it is enabled and at least one trigger is selected.</summary>
    public bool IsActive => Enabled && (AtTrackStart || AfterFilterChange);
}

/// <summary>Why an autofocus that nobody wrote into the sequence runs.</summary>
public enum AutofocusOrigin
{
    TrackStart,
    AfterFilterChange
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
public sealed record MultiRigStepDraft(
    Guid Id,
    IReadOnlyList<RigTrackDraft> Tracks,
    MultiRigDitherPolicyDraft? DitherPolicy = null
) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.MultiRig;

    // The cameras of the rigs are known to the rig registry, not to the draft: see SequenceDraftBuilder.RequiredDeviceIds.
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>The equipment that the whole session shares: one mount and, usually, one guider.</summary>
public sealed record SharedEquipmentDraft(DeviceId? MountId, DeviceId? GuiderId);

using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Rigs;

namespace Astra.Desktop;

public enum SequenceStepKind
{
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
    RigTrack
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

public sealed record ExposureStepDraft(Guid Id, DeviceId? CameraId, double Seconds) : LeafStepDraft(Id)
{
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
    public override SequenceStepKind Kind => SequenceStepKind.RigExposure;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// One imaging rig of a Multi-Rig block and what that rig does: its steps run in order, next to the other tracks. A
/// track is not a step of the sequence and cannot be put anywhere else. <see cref="Steps"/> are exposures with the rig
/// camera, delays and Repeats of those; the validation refuses anything else, because a track must not do what
/// belongs to the whole session (moving the shared mount, guiding).
/// </summary>
/// <param name="RigId">The rig, or <c>null</c> when none is selected. Kept as chosen even if no such rig is registered.</param>
public sealed record RigTrackDraft(Guid Id, RigId? RigId, IReadOnlyList<SequenceStepDraft> Steps) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.RigTrack;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>
/// Imaging with several rigs of one session at once, one <see cref="RigTrackDraft"/> per rig. The mount and the guider
/// are shared by the session and are not part of the tracks. A Multi-Rig block is only found at the top level, and
/// is finished when all of its tracks are.
/// </summary>
public sealed record MultiRigStepDraft(Guid Id, IReadOnlyList<RigTrackDraft> Tracks) : SequenceStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.MultiRig;

    // The cameras of the rigs are known to the rig registry, not to the draft: see SequenceDraftBuilder.RequiredDeviceIds.
    public override IEnumerable<DeviceId> DeviceIds => [];
}

/// <summary>The equipment that the whole session shares: one mount and, usually, one guider.</summary>
public sealed record SharedEquipmentDraft(DeviceId? MountId, DeviceId? GuiderId);

using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;

namespace Astra.Desktop;

public enum SequenceStepKind
{
    Exposure,
    Delay,
    Slew,
    StartGuiding,
    StopGuiding,
    Dither,
    Repeat
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

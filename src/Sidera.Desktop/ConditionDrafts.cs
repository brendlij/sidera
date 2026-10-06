using System;
using System.Collections.Generic;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;

namespace Sidera.Desktop;

/// <summary>The target that the altitude conditions of a step are computed for: plain coordinates, so that a step carries what it needs and a saved sequence means the same wherever it is opened.</summary>
public sealed record ConditionTargetDraft(string? Name, double RightAscensionHours, double DeclinationDegrees);

/// <summary>
/// When imaging stops, as a list: it stops as soon as <b>any</b> of the conditions holds (ANY, never ALL). On a block of frames it is the block's own list; on a Multi-Rig block it is the
/// target's, which stops every track of it. The frame count of a Repeat is not in the list: it is the Repeat's own count, one place for it.
/// </summary>
public sealed record StopConditionsDraft(IReadOnlyList<WorkflowCondition> Any, ConditionTargetDraft? Target = null)
{
    public bool IsEmpty => Any.Count == 0;
}

/// <summary>
/// Waits until <b>all</b> of its conditions hold (a time, an altitude, darkness): a step like any other, in the Prepare part of a sequence, before a block, or in a rig track. Added in version 8
/// with the conditions.
/// </summary>
public sealed record WaitUntilStepDraft(Guid Id, IReadOnlyList<WorkflowCondition> Conditions, ConditionTargetDraft? Target = null) : LeafStepDraft(Id)
{
    public override SequenceStepKind Kind => SequenceStepKind.WaitUntil;
    public override IEnumerable<DeviceId> DeviceIds => [];
}

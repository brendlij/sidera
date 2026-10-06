using System;
using System.Collections.Generic;
using Sidera.Core.Devices;

namespace Sidera.Desktop.Documents;

/// <summary>
/// An Sidera sequence document: what a <c>.astraseq</c> file holds, as plain values and independent of how any version
/// of the format encodes it. It is the editable sequence (intent and configuration) and nothing about running one: no
/// execution state, no device names, no validation results. It is neither the editor's draft nor a wire format;
/// <see cref="SequenceDocumentMapper"/> converts it to and from drafts, and a <see cref="ISequenceDocumentSerializer"/>
/// to and from bytes.
/// </summary>
public sealed record SequenceDocument(
    string? Name,
    IReadOnlyList<DocumentStep> Steps,
    SharedEquipmentDocument? SharedEquipment = null,
    Sidera.Desktop.Workflows.WorkflowDefinition? Workflow = null)
{
    /// <summary>The value every Sidera sequence document carries to say what it is.</summary>
    public const string FormatId = "astra-sequence";

    /// <summary>
    /// The version of the format that serializers write today. Documents in memory are always this version; what an
    /// older version could not say (shared equipment and Multi-Rig Imaging before 2, focuser and filter wheel steps
    /// before 3, autofocus before 4, the autofocus policy of a track before 5, the acquisition settings of an exposure before 6,
    /// plate solving before 7, the autofocus interval of a track and the workflow before 8) is simply absent from a document read from it. A writer always writes the current version,
    /// so an older Sidera refuses a file that a newer one saved as newer instead of opening it as if nothing had been added; every older version is still read, as it always was.
    /// </summary>
    public const int CurrentVersion = 8;



    public string Format => FormatId;
    public int Version => CurrentVersion;
}

/// <summary>The mount and the guider that the whole session shares; <c>null</c> for one that is not selected.</summary>
public sealed record SharedEquipmentDocument(string? MountId, string? GuiderId);

/// <summary>A step of a document. Its <see cref="Id"/> is the identity of the editable step and survives saving and opening.</summary>
public abstract record DocumentStep(Guid Id);

/// <summary>A step that has no children: everything a <see cref="RepeatDocumentStep"/> may contain.</summary>
public abstract record DocumentLeafStep(Guid Id) : DocumentStep(Id);

/// <remarks>
/// Device ids are kept as written, <c>null</c> for "no device selected". Whether a device exists is not the
/// document's concern: a document that names equipment this installation does not have is still a valid document.
/// </remarks>
public sealed record ExposureDocumentStep(Guid Id, string? CameraId, double ExposureSeconds, AcquisitionIntent? Acquisition = null)
    : DocumentLeafStep(Id);

public sealed record DelayDocumentStep(Guid Id, double DurationSeconds) : DocumentLeafStep(Id);

/// <summary>Waits until all of its conditions hold. Added in version 8 with the conditions.</summary>
public sealed record WaitUntilDocumentStep(Guid Id, IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> Conditions, ConditionTargetDraft? Target = null) : DocumentLeafStep(Id);

public sealed record SlewDocumentStep(Guid Id, string? MountId, double RaHours, double DecDegrees) : DocumentLeafStep(Id);

public sealed record StartGuidingDocumentStep(Guid Id, string? GuiderId) : DocumentLeafStep(Id);

public sealed record StopGuidingDocumentStep(Guid Id, string? GuiderId) : DocumentLeafStep(Id);

public sealed record DitherDocumentStep(
    Guid Id,
    string? GuiderId,
    string? MountId,
    string? CameraId,
    double AmplitudePixels,
    double SettleThresholdPixels,
    double SettleStableSeconds,
    double SettleTimeoutSeconds
) : DocumentLeafStep(Id);

/// <summary>Moves a focuser to an absolute position in focuser steps. Added in version 3.</summary>
public sealed record MoveFocuserDocumentStep(Guid Id, string? FocuserId, int Position) : DocumentLeafStep(Id);

/// <summary>Turns a filter wheel to the slot with this index. Added in version 3.</summary>
public sealed record ChangeFilterDocumentStep(Guid Id, string? FilterWheelId, int SlotIndex) : DocumentLeafStep(Id);

/// <summary>A focuser move inside a rig track, on the focuser of that track's rig. Added in version 3.</summary>
public sealed record RigMoveFocuserDocumentStep(Guid Id, int Position) : DocumentLeafStep(Id);

/// <summary>A filter change inside a rig track, on the filter wheel of that track's rig, to the slot with this index. Added in version 3.</summary>
public sealed record RigChangeFilterDocumentStep(Guid Id, int SlotIndex) : DocumentLeafStep(Id);

/// <summary>Focuses a rig: its camera and its focuser. The rig is an id, or <c>null</c>. Added in version 4.</summary>
public sealed record AutofocusDocumentStep(Guid Id, string? RigId, double ExposureSeconds, int StepSize, int SampleCount)
    : DocumentLeafStep(Id);

/// <summary>Autofocus inside a rig track, on that track's rig; the rig is not repeated. Added in version 4.</summary>
public sealed record RigAutofocusDocumentStep(Guid Id, double ExposureSeconds, int StepSize, int SampleCount)
    : DocumentLeafStep(Id);

/// <summary>The one container of version 1: leaf steps only, so a Repeat cannot contain a Repeat.</summary>
public sealed record RepeatDocumentStep(Guid Id, int Count, IReadOnlyList<DocumentLeafStep> Children, StopConditionsDraft? Stop = null) : DocumentStep(Id);

/// <summary>An exposure inside a rig track, with the camera of that track's rig.</summary>
public sealed record RigExposureDocumentStep(Guid Id, double ExposureSeconds, AcquisitionIntent? Acquisition = null) : DocumentLeafStep(Id);

/// <summary>
/// When the rig of a track focuses by itself: see <c>RigAutofocusPolicyDraft</c>. A track without one does not. Added
/// in version 5; it changes what a sequence does, which is why an older Sidera must not open it as if it did not.
/// </summary>
public sealed record AutofocusPolicyDocument(
    bool Enabled,
    bool AtTrackStart,
    bool AfterFilterChange,
    double ExposureSeconds,
    int StepSize,
    int SampleCount,
    double IntervalMinutes = 0
);

/// <summary>One rig of a Multi-Rig block: the rig (an id, or <c>null</c>) and what runs on it, in order.</summary>
public sealed record RigTrackDocument(
    Guid Id,
    string? RigId,
    IReadOnlyList<DocumentStep> Steps,
    AutofocusPolicyDocument? AutofocusPolicy = null
);

/// <summary>
/// When the block dithers the shared mount, and how: see <c>MultiRigDitherPolicyDraft</c>. The rig is an id, or
/// <c>null</c>. A block without a policy does not dither.
/// </summary>
public sealed record DitherPolicyDocument(
    bool Enabled,
    string? TriggerRigId,
    int EveryNFrames,
    double AmplitudePixels,
    double SettleThresholdPixels,
    double SettleStableSeconds,
    double SettleTimeoutSeconds
);

/// <summary>Imaging with several rigs at once; only found at the top level. Added in version 2.</summary>
public sealed record MultiRigDocumentStep(
    Guid Id,
    IReadOnlyList<RigTrackDocument> Tracks,
    DitherPolicyDocument? DitherPolicy = null,
    bool SingleTrack = false,
    MeridianFlipPolicyDraft? MeridianFlip = null,
    StopConditionsDraft? TargetStop = null
) : DocumentStep(Id);

public enum SequenceDocumentErrorKind
{
    /// <summary>The file could not be read or written, or is not a readable Sidera document at all.</summary>
    Container,

    /// <summary>It is an Sidera document of a version this Sidera reads, but its content is malformed.</summary>
    Structure,

    /// <summary>It was written by a newer Sidera.</summary>
    NewerVersion,
}

/// <summary>
/// A document could not be read or written. <see cref="Exception.Message"/> is one concise sentence for the user; the
/// technical cause, if there is one, is the inner exception.
/// </summary>
public sealed class SequenceDocumentException : Exception
{
    public SequenceDocumentException(SequenceDocumentErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public SequenceDocumentErrorKind Kind { get; }
}

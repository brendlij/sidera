using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Documents;

/// <summary>
/// Converts between the editor's drafts and the persisted <see cref="SequenceDocument"/>. Neither knows the other:
/// the draft model can change with the editor and the document with the format, and this is the one place that has to
/// follow both. It is independent of how a document is encoded. Mapping copies values only: it never validates (a
/// draft that cannot run is still saved, a document naming unknown equipment is still opened), and every call returns
/// new objects.
/// </summary>
public static class SequenceDocumentMapper
{
    public static SequenceDocument ToDocument(
        IReadOnlyList<SequenceStepDraft> steps, string? name = null, SharedEquipmentDraft? shared = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return new SequenceDocument(
            name,
            steps.Select(ToDocumentStep).ToList(),
            shared is null ? null : new SharedEquipmentDocument(shared.MountId?.Value, shared.GuiderId?.Value));
    }

    /// <summary>The session's shared equipment as the document has it; <c>null</c> for a document that says nothing about it.</summary>
    /// <exception cref="SequenceDocumentException">The document holds a device id that no draft can have.</exception>
    public static SharedEquipmentDraft? ToSharedEquipment(SequenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            return document.SharedEquipment is { } shared ? new SharedEquipmentDraft(Device(shared.MountId), Device(shared.GuiderId)) : null;
        }
        catch (ArgumentException ex)
        {
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Structure, "Invalid device ID in the sequence.", ex);
        }
    }

    /// <exception cref="SequenceDocumentException">The document holds a value that no draft can have.</exception>
    public static IReadOnlyList<SequenceStepDraft> ToDrafts(SequenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        try
        {
            return document.Steps.Select(ToDraftStep).ToList();
        }
        catch (ArgumentException ex)
        {
            // A device id that is empty: the serializers do not produce one, another producer of documents might.
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Structure, "Invalid device ID in the sequence.", ex);
        }
    }

    private static DocumentStep ToDocumentStep(SequenceStepDraft step) => step switch
    {
        MultiRigStepDraft m => new MultiRigDocumentStep(
            m.Id,
            m.Tracks.Select(track => new RigTrackDocument(
                track.Id, track.RigId?.Value, track.Steps.Select(ToDocumentStep).ToList(),
                ToDocumentAutofocusPolicy(track.AutofocusPolicy))).ToList(),
            ToDocumentPolicy(m.DitherPolicy)),
        RepeatStepDraft r => new RepeatDocumentStep(r.Id, r.Count, r.Children.Select(ToDocumentLeaf).ToList()),
        LeafStepDraft leaf => ToDocumentLeaf(leaf),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static DocumentLeafStep ToDocumentLeaf(LeafStepDraft step) => step switch
    {
        SlewAndCenterStepDraft c => new SlewAndCenterDocumentStep(c.Id, c.MountId?.Value, c.RigId?.Value, c.RightAscensionHours, c.DeclinationDegrees, c.ToleranceArcseconds, c.MaxAttempts, c.ExposureSeconds, c.TargetName, c.DesiredRotationDegrees),
        RotateToAngleStepDraft r => new RotateToAngleDocumentStep(r.Id, r.RigId?.Value, r.SkyRotationDegrees),
        RotateAndVerifyStepDraft r => new RotateAndVerifyDocumentStep(r.Id, r.RigId?.Value, r.SkyRotationDegrees, r.ToleranceDegrees, r.MaxAttempts, r.ExposureSeconds),
        CenterAndRotateStepDraft c => new CenterAndRotateDocumentStep(
            c.Id, c.MountId?.Value, c.RigId?.Value, c.RightAscensionHours, c.DeclinationDegrees, c.ToleranceArcseconds, c.MaxCenteringAttempts,
            c.SkyRotationDegrees, c.RotationToleranceDegrees, c.MaxRotationAttempts, c.MaxRounds, c.ExposureSeconds, c.TargetName),
        SyncMountStepDraft m => new SyncMountDocumentStep(m.Id, m.MountId?.Value),
        PlateSolveStepDraft p => new PlateSolveDocumentStep(p.Id, p.RigId?.Value, p.ExposureSeconds),
        ExposureStepDraft e => new ExposureDocumentStep(e.Id, e.CameraId?.Value, e.Seconds, e.Acquisition.IsDefault ? null : e.Acquisition),
        RigExposureStepDraft r => new RigExposureDocumentStep(r.Id, r.Seconds, r.Acquisition.IsDefault ? null : r.Acquisition),
        MoveFocuserStepDraft f => new MoveFocuserDocumentStep(f.Id, f.FocuserId?.Value, f.Position),
        ChangeFilterStepDraft c => new ChangeFilterDocumentStep(c.Id, c.FilterWheelId?.Value, c.SlotIndex),
        RigMoveFocuserStepDraft f => new RigMoveFocuserDocumentStep(f.Id, f.Position),
        AutofocusStepDraft a => new AutofocusDocumentStep(a.Id, a.RigId?.Value, a.ExposureSeconds, a.StepSize, a.SampleCount),
        RigAutofocusStepDraft a => new RigAutofocusDocumentStep(a.Id, a.ExposureSeconds, a.StepSize, a.SampleCount),
        RigChangeFilterStepDraft c => new RigChangeFilterDocumentStep(c.Id, c.SlotIndex),
        DelayStepDraft d => new DelayDocumentStep(d.Id, d.Seconds),
        SlewStepDraft s => new SlewDocumentStep(s.Id, s.MountId?.Value, s.RightAscensionHours, s.DeclinationDegrees),
        StartGuidingStepDraft g => new StartGuidingDocumentStep(g.Id, g.GuiderId?.Value),
        StopGuidingStepDraft g => new StopGuidingDocumentStep(g.Id, g.GuiderId?.Value),
        DitherStepDraft d => new DitherDocumentStep(
            d.Id, d.GuiderId?.Value, d.MountId?.Value, d.CameraId?.Value,
            d.AmplitudePixels, d.SettleThresholdPixels, d.SettleStableSeconds, d.SettleTimeoutSeconds),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static SequenceStepDraft ToDraftStep(DocumentStep step) => step switch
    {
        MultiRigDocumentStep m => new MultiRigStepDraft(
            m.Id,
            m.Tracks.Select(track => new RigTrackDraft(
                track.Id, track.RigId is null ? null : new RigId(track.RigId), track.Steps.Select(ToDraftStep).ToList(),
                ToDraftAutofocusPolicy(track.AutofocusPolicy))).ToList(),
            ToDraftPolicy(m.DitherPolicy)),
        RepeatDocumentStep r => new RepeatStepDraft(r.Id, r.Count, r.Children.Select(ToDraftLeaf).ToList()),
        DocumentLeafStep leaf => ToDraftLeaf(leaf),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static LeafStepDraft ToDraftLeaf(DocumentLeafStep step) => step switch
    {
        SlewAndCenterDocumentStep c => new SlewAndCenterStepDraft(c.Id, Device(c.MountId), c.RigId is null ? null : new RigId(c.RigId), c.RaHours, c.DecDegrees, c.ToleranceArcseconds, c.MaxAttempts, c.ExposureSeconds, c.TargetName, c.DesiredRotationDegrees),
        RotateToAngleDocumentStep r => new RotateToAngleStepDraft(r.Id, r.RigId is null ? null : new RigId(r.RigId), r.SkyRotationDegrees),
        RotateAndVerifyDocumentStep r => new RotateAndVerifyStepDraft(r.Id, r.RigId is null ? null : new RigId(r.RigId), r.SkyRotationDegrees, r.ToleranceDegrees, r.MaxAttempts, r.ExposureSeconds),
        CenterAndRotateDocumentStep c => new CenterAndRotateStepDraft(
            c.Id, Device(c.MountId), c.RigId is null ? null : new RigId(c.RigId), c.RaHours, c.DecDegrees, c.ToleranceArcseconds, c.MaxCenteringAttempts,
            c.SkyRotationDegrees, c.RotationToleranceDegrees, c.MaxRotationAttempts, c.MaxRounds, c.ExposureSeconds, c.TargetName),
        SyncMountDocumentStep m => new SyncMountStepDraft(m.Id, Device(m.MountId)),
        PlateSolveDocumentStep p => new PlateSolveStepDraft(p.Id, p.RigId is null ? null : new RigId(p.RigId), p.ExposureSeconds),
        ExposureDocumentStep e => new ExposureStepDraft(e.Id, Device(e.CameraId), e.ExposureSeconds) { Acquisition = e.Acquisition ?? AcquisitionIntent.Default },
        RigExposureDocumentStep r => new RigExposureStepDraft(r.Id, r.ExposureSeconds) { Acquisition = r.Acquisition ?? AcquisitionIntent.Default },
        MoveFocuserDocumentStep f => new MoveFocuserStepDraft(f.Id, Device(f.FocuserId), f.Position),
        ChangeFilterDocumentStep c => new ChangeFilterStepDraft(c.Id, Device(c.FilterWheelId), c.SlotIndex),
        RigMoveFocuserDocumentStep f => new RigMoveFocuserStepDraft(f.Id, f.Position),
        AutofocusDocumentStep a => new AutofocusStepDraft(
            a.Id, a.RigId is null ? null : new RigId(a.RigId), a.ExposureSeconds, a.StepSize, a.SampleCount),
        RigAutofocusDocumentStep a => new RigAutofocusStepDraft(a.Id, a.ExposureSeconds, a.StepSize, a.SampleCount),
        RigChangeFilterDocumentStep c => new RigChangeFilterStepDraft(c.Id, c.SlotIndex),
        DelayDocumentStep d => new DelayStepDraft(d.Id, d.DurationSeconds),
        SlewDocumentStep s => new SlewStepDraft(s.Id, Device(s.MountId), s.RaHours, s.DecDegrees),
        StartGuidingDocumentStep g => new StartGuidingStepDraft(g.Id, Device(g.GuiderId)),
        StopGuidingDocumentStep g => new StopGuidingStepDraft(g.Id, Device(g.GuiderId)),
        DitherDocumentStep d => new DitherStepDraft(
            d.Id, Device(d.GuiderId), Device(d.MountId), Device(d.CameraId),
            d.AmplitudePixels, d.SettleThresholdPixels, d.SettleStableSeconds, d.SettleTimeoutSeconds),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static DeviceId? Device(string? id) => id is null ? null : new DeviceId(id);

    // The default policy is what a document without one means, so it is not written.
    private static DitherPolicyDocument? ToDocumentPolicy(MultiRigDitherPolicyDraft? policy) =>
        policy is null || policy == MultiRigDitherPolicyDraft.Default
            ? null
            : new DitherPolicyDocument(
                policy.Enabled, policy.TriggerRigId?.Value, policy.EveryNFrames, policy.AmplitudePixels,
                policy.SettleThresholdPixels, policy.SettleStableSeconds, policy.SettleTimeoutSeconds);

    // The default policy is what a document without one means, so it is not written.
    private static AutofocusPolicyDocument? ToDocumentAutofocusPolicy(RigAutofocusPolicyDraft? policy) =>
        policy is null || policy == RigAutofocusPolicyDraft.Default
            ? null
            : new AutofocusPolicyDocument(
                policy.Enabled, policy.AtTrackStart, policy.AfterFilterChange, policy.ExposureSeconds, policy.StepSize, policy.SampleCount);

    private static RigAutofocusPolicyDraft? ToDraftAutofocusPolicy(AutofocusPolicyDocument? policy) =>
        policy is null
            ? null
            : new RigAutofocusPolicyDraft(
                policy.Enabled, policy.AtTrackStart, policy.AfterFilterChange, policy.ExposureSeconds, policy.StepSize, policy.SampleCount);

    private static MultiRigDitherPolicyDraft? ToDraftPolicy(DitherPolicyDocument? policy) =>
        policy is null
            ? null
            : new MultiRigDitherPolicyDraft(
                policy.Enabled, policy.TriggerRigId is null ? null : new RigId(policy.TriggerRigId), policy.EveryNFrames,
                policy.AmplitudePixels, policy.SettleThresholdPixels, policy.SettleStableSeconds, policy.SettleTimeoutSeconds);
}

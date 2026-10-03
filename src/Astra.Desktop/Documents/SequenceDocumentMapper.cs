using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;

namespace Astra.Desktop.Documents;

/// <summary>
/// Converts between the editor's drafts and the persisted <see cref="SequenceDocument"/>. Neither knows the other:
/// the draft model can change with the editor and the document with the format, and this is the one place that has to
/// follow both. It is independent of how a document is encoded. Mapping copies values only: it never validates (a
/// draft that cannot run is still saved, a document naming unknown equipment is still opened), and every call returns
/// new objects.
/// </summary>
public static class SequenceDocumentMapper
{
    public static SequenceDocument ToDocument(IReadOnlyList<SequenceStepDraft> steps, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        return new SequenceDocument(name, steps.Select(ToDocumentStep).ToList());
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
        RepeatStepDraft r => new RepeatDocumentStep(r.Id, r.Count, r.Children.Select(ToDocumentLeaf).ToList()),
        LeafStepDraft leaf => ToDocumentLeaf(leaf),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static DocumentLeafStep ToDocumentLeaf(LeafStepDraft step) => step switch
    {
        ExposureStepDraft e => new ExposureDocumentStep(e.Id, e.CameraId?.Value, e.Seconds),
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
        RepeatDocumentStep r => new RepeatStepDraft(r.Id, r.Count, r.Children.Select(ToDraftLeaf).ToList()),
        DocumentLeafStep leaf => ToDraftLeaf(leaf),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static LeafStepDraft ToDraftLeaf(DocumentLeafStep step) => step switch
    {
        ExposureDocumentStep e => new ExposureStepDraft(e.Id, Device(e.CameraId), e.ExposureSeconds),
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
}

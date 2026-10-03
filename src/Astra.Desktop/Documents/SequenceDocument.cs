using System;
using System.Collections.Generic;

namespace Astra.Desktop.Documents;

/// <summary>
/// An Astra sequence document: what a <c>.astraseq</c> file holds, as plain values and independent of how any version
/// of the format encodes it. It is the editable sequence (intent and configuration) and nothing about running one: no
/// execution state, no device names, no validation results. It is neither the editor's draft nor a wire format;
/// <see cref="SequenceDocumentMapper"/> converts it to and from drafts, and a <see cref="ISequenceDocumentSerializer"/>
/// to and from bytes.
/// </summary>
public sealed record SequenceDocument(string? Name, IReadOnlyList<DocumentStep> Steps)
{
    /// <summary>The value every Astra sequence document carries to say what it is.</summary>
    public const string FormatId = "astra-sequence";

    /// <summary>The version of the format that serializers write today. Documents in memory are always this version.</summary>
    public const int CurrentVersion = 1;

    public string Format => FormatId;
    public int Version => CurrentVersion;
}

/// <summary>A step of a document. Its <see cref="Id"/> is the identity of the editable step and survives saving and opening.</summary>
public abstract record DocumentStep(Guid Id);

/// <summary>A step that has no children: everything a <see cref="RepeatDocumentStep"/> may contain.</summary>
public abstract record DocumentLeafStep(Guid Id) : DocumentStep(Id);

/// <remarks>
/// Device ids are kept as written, <c>null</c> for "no device selected". Whether a device exists is not the
/// document's concern: a document that names equipment this installation does not have is still a valid document.
/// </remarks>
public sealed record ExposureDocumentStep(Guid Id, string? CameraId, double ExposureSeconds) : DocumentLeafStep(Id);

public sealed record DelayDocumentStep(Guid Id, double DurationSeconds) : DocumentLeafStep(Id);

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

/// <summary>The one container of version 1: leaf steps only, so a Repeat cannot contain a Repeat.</summary>
public sealed record RepeatDocumentStep(Guid Id, int Count, IReadOnlyList<DocumentLeafStep> Children) : DocumentStep(Id);

public enum SequenceDocumentErrorKind
{
    /// <summary>The file could not be read or written, or is not a readable Astra document at all.</summary>
    Container,

    /// <summary>It is an Astra document of a version this Astra reads, but its content is malformed.</summary>
    Structure,

    /// <summary>It was written by a newer Astra.</summary>
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

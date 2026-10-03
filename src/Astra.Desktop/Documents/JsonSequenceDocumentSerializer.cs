using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Astra.Desktop.Documents;

/// <summary>
/// Versions 1 and 2 of the Astra sequence document format, which happen to be encoded as JSON text. This class is the only
/// place that knows that: the property names, the step discriminators and the JSON parsing rules below are the
/// version 1 file format, and nothing else in Astra should depend on them.
/// <para>
/// Reading and writing is done with the JSON reader and writer directly, on purpose. The format is a contract of its
/// own and does not follow any class: there are no type names or <c>$type</c> in it, every discriminator is spelled
/// out, and everything that is not explicitly understood is either rejected or deliberately ignored (see
/// <see cref="ReadStep"/>).
/// </para>
/// <para>
/// The output is deterministic and readable: fixed property order, two-space indentation, "\n" line ends, numbers in
/// their invariant shortest form. A step is <c>{ "type", "id", ...fields }</c>; a Repeat holds its leaf steps in
/// <c>children</c>. A device that is not selected is <c>null</c>.
/// </para>
/// <para>
/// Reading dispatches on the version of the document. Version 1 knows the leaf steps and Repeat; version 2, which is
/// what is written, adds <c>sharedEquipment</c>, the Multi-Rig block (<c>multiRig</c>, with <c>tracks</c> of a
/// <c>rigId</c> and <c>steps</c>) and the exposure of a rig track (<c>rigExposure</c>). A version 1 document is read
/// by the same code with the version 2 additions switched off, so it means exactly what it did.
/// </para>
/// </summary>
public sealed class JsonSequenceDocumentSerializer : ISequenceDocumentSerializer
{
    // The discriminators of version 1. They are part of the file format: never derive them from a class name.
    private const string ExposureType = "exposure";
    private const string DelayType = "delay";
    private const string SlewType = "slew";
    private const string StartGuidingType = "startGuiding";
    private const string StopGuidingType = "stopGuiding";
    private const string DitherType = "dither";
    private const string RepeatType = "repeat";
    private const string MultiRigType = "multiRig";
    private const string RigExposureType = "rigExposure";

    private const string InvalidDocument = "Invalid Astra sequence document.";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    // Writing

    public async Task SaveAsync(Stream stream, SequenceDocument document, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(document);

        // Everything is written to memory first: nothing reaches the stream unless the whole document was written.
        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                WriteDocument(writer, document);
            }
        }
        catch (ArgumentException ex)
        {
            // The writer refuses values JSON cannot hold, such as NaN.
            throw new SequenceDocumentException(
                SequenceDocumentErrorKind.Structure, "The sequence contains a value that cannot be saved.", ex);
        }

        buffer.WriteByte((byte)'\n');
        buffer.Position = 0;
        await buffer.CopyToAsync(stream, cancellationToken);
    }

    private static void WriteDocument(Utf8JsonWriter w, SequenceDocument document)
    {
        w.WriteStartObject();
        w.WriteString("format", SequenceDocument.FormatId);
        w.WriteNumber("version", SequenceDocument.CurrentVersion);
        if (document.Name is not null)
        {
            w.WriteString("name", document.Name);
        }

        if (document.SharedEquipment is { } shared)
        {
            w.WriteStartObject("sharedEquipment");
            Device(w, "mountId", shared.MountId);
            Device(w, "guiderId", shared.GuiderId);
            w.WriteEndObject();
        }

        w.WriteStartArray("steps");
        foreach (var step in document.Steps)
        {
            WriteStep(w, step);
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteStep(Utf8JsonWriter w, DocumentStep step)
    {
        w.WriteStartObject();
        switch (step)
        {
            case ExposureDocumentStep e:
                Header(w, ExposureType, e.Id);
                Device(w, "cameraId", e.CameraId);
                w.WriteNumber("exposureSeconds", e.ExposureSeconds);
                break;
            case DelayDocumentStep d:
                Header(w, DelayType, d.Id);
                w.WriteNumber("durationSeconds", d.DurationSeconds);
                break;
            case SlewDocumentStep s:
                Header(w, SlewType, s.Id);
                Device(w, "mountId", s.MountId);
                w.WriteNumber("raHours", s.RaHours);
                w.WriteNumber("decDegrees", s.DecDegrees);
                break;
            case StartGuidingDocumentStep g:
                Header(w, StartGuidingType, g.Id);
                Device(w, "guiderId", g.GuiderId);
                break;
            case StopGuidingDocumentStep g:
                Header(w, StopGuidingType, g.Id);
                Device(w, "guiderId", g.GuiderId);
                break;
            case DitherDocumentStep d:
                Header(w, DitherType, d.Id);
                Device(w, "guiderId", d.GuiderId);
                Device(w, "mountId", d.MountId);
                Device(w, "cameraId", d.CameraId);
                w.WriteNumber("amplitudePixels", d.AmplitudePixels);
                w.WriteNumber("settleThresholdPixels", d.SettleThresholdPixels);
                w.WriteNumber("settleStableSeconds", d.SettleStableSeconds);
                w.WriteNumber("settleTimeoutSeconds", d.SettleTimeoutSeconds);
                break;
            case RigExposureDocumentStep r:
                Header(w, RigExposureType, r.Id);
                w.WriteNumber("exposureSeconds", r.ExposureSeconds);
                break;
            case MultiRigDocumentStep m:
                Header(w, MultiRigType, m.Id);
                w.WriteStartArray("tracks");
                foreach (var track in m.Tracks)
                {
                    w.WriteStartObject();
                    w.WriteString("id", track.Id.ToString("D", CultureInfo.InvariantCulture));
                    Device(w, "rigId", track.RigId);
                    w.WriteStartArray("steps");
                    foreach (var inner in track.Steps)
                    {
                        WriteStep(w, inner);
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                if (m.DitherPolicy is { } policy)
                {
                    w.WriteStartObject("ditherPolicy");
                    w.WriteBoolean("enabled", policy.Enabled);
                    Device(w, "triggerRigId", policy.TriggerRigId);
                    w.WriteNumber("everyNFrames", policy.EveryNFrames);
                    w.WriteNumber("amplitudePixels", policy.AmplitudePixels);
                    w.WriteNumber("settleThresholdPixels", policy.SettleThresholdPixels);
                    w.WriteNumber("settleStableSeconds", policy.SettleStableSeconds);
                    w.WriteNumber("settleTimeoutSeconds", policy.SettleTimeoutSeconds);
                    w.WriteEndObject();
                }

                break;
            case RepeatDocumentStep r:
                Header(w, RepeatType, r.Id);
                w.WriteNumber("count", r.Count);
                w.WriteStartArray("children");
                foreach (var child in r.Children)
                {
                    WriteStep(w, child);
                }

                w.WriteEndArray();
                break;
            default:
                throw new SequenceDocumentException(
                    SequenceDocumentErrorKind.Structure, $"Unsupported sequence step '{step.GetType().Name}'.");
        }

        w.WriteEndObject();
    }

    private static void Header(Utf8JsonWriter w, string type, Guid id)
    {
        w.WriteString("type", type);
        w.WriteString("id", id.ToString("D", CultureInfo.InvariantCulture));
    }

    private static void Device(Utf8JsonWriter w, string name, string? id)
    {
        if (id is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, id);
        }
    }

    // Reading

    public async Task<SequenceDocument> LoadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        JsonDocument json;
        try
        {
            json = await JsonDocument.ParseAsync(stream, ReaderOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            // Includes an empty file.
            throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, InvalidDocument, ex);
        }

        using (json)
        {
            return ReadDocument(json.RootElement);
        }
    }

    private static SequenceDocument ReadDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Container(InvalidDocument);
        }

        if (!root.TryGetProperty("format", out var format)
            || format.ValueKind != JsonValueKind.String
            || format.GetString() != SequenceDocument.FormatId)
        {
            throw Container("This file is not an Astra sequence document.");
        }

        if (!root.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version)
            || version <= 0)
        {
            throw Container("Invalid Astra sequence document: the version is missing or not valid.");
        }

        return version switch
        {
            1 or 2 => ReadBody(root, version),
            _ => throw new SequenceDocumentException(
                SequenceDocumentErrorKind.NewerVersion, "This sequence was created by a newer Astra version."),
        };
    }

    private static SequenceDocument ReadBody(JsonElement root, int version)
    {
        string? name = null;
        if (root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind != JsonValueKind.Null)
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                throw Structure("The sequence name must be text.");
            }

            name = nameElement.GetString();
        }

        if (!root.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
        {
            throw Structure("The sequence has no list of steps.");
        }

        var shared = version >= 2 ? ReadSharedEquipment(root) : null;

        var ids = new HashSet<Guid>();
        var steps = new List<DocumentStep>();
        foreach (var element in stepsElement.EnumerateArray())
        {
            steps.Add(ReadStep(element, Place.Top, version, ids));
        }

        return new SequenceDocument(name, steps, shared);
    }

    private static SharedEquipmentDocument? ReadSharedEquipment(JsonElement root)
    {
        if (!root.TryGetProperty("sharedEquipment", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Structure("'sharedEquipment' must be an object.");
        }

        return new SharedEquipmentDocument(
            ReadDevice(element, "sharedEquipment", "mountId"), ReadDevice(element, "sharedEquipment", "guiderId"));
    }

    // Where a step is found decides which steps may be there.
    private enum Place
    {
        Top,
        InRepeat,
        InTrack,
        InTrackRepeat,
    }

    // Unknown properties are ignored: a later writer of version 1 may add information that does not change what a
    // step is. What does change it is rejected: an unknown step type, a leaf that has children, a Repeat in a Repeat.
    private static DocumentStep ReadStep(JsonElement element, Place place, int version, HashSet<Guid> ids)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Structure("A sequence step must be an object.");
        }

        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw Structure("A sequence step has no type.");
        }

        var type = typeElement.GetString()!;
        var known = type is ExposureType or DelayType or SlewType or StartGuidingType or StopGuidingType or DitherType or RepeatType
            || (version >= 2 && type is MultiRigType or RigExposureType);
        if (!known)
        {
            throw Structure($"Unknown sequence step type '{type}'.");
        }

        var inTrack = place is Place.InTrack or Place.InTrackRepeat;
        var inRepeat = place is Place.InRepeat or Place.InTrackRepeat;

        if (type == RepeatType && inRepeat)
        {
            throw Structure("Repeat steps cannot contain another Repeat.");
        }

        if (type == MultiRigType && place != Place.Top)
        {
            throw Structure("Multi-Rig steps can only be placed at the top level of a sequence.");
        }

        if (type == RigExposureType && !inTrack)
        {
            throw Structure($"A '{type}' step can only be used inside a rig track.");
        }

        if (inTrack && type is ExposureType or SlewType or StartGuidingType or StopGuidingType or DitherType)
        {
            throw Structure($"A '{type}' step cannot be used inside a rig track.");
        }

        if (type is not (RepeatType or MultiRigType) && element.TryGetProperty("children", out _))
        {
            throw Structure($"A '{type}' step cannot contain other steps.");
        }

        var id = ReadId(element, type, ids);

        return type switch
        {
            ExposureType => new ExposureDocumentStep(
                id, ReadDevice(element, type, "cameraId"), ReadNumber(element, type, "exposureSeconds")),
            DelayType => new DelayDocumentStep(id, ReadNumber(element, type, "durationSeconds")),
            SlewType => new SlewDocumentStep(
                id, ReadDevice(element, type, "mountId"),
                ReadNumber(element, type, "raHours"), ReadNumber(element, type, "decDegrees")),
            StartGuidingType => new StartGuidingDocumentStep(id, ReadDevice(element, type, "guiderId")),
            StopGuidingType => new StopGuidingDocumentStep(id, ReadDevice(element, type, "guiderId")),
            RigExposureType => new RigExposureDocumentStep(id, ReadNumber(element, type, "exposureSeconds")),
            MultiRigType => ReadMultiRig(element, id, version, ids),
            DitherType => new DitherDocumentStep(
                id,
                ReadDevice(element, type, "guiderId"),
                ReadDevice(element, type, "mountId"),
                ReadDevice(element, type, "cameraId"),
                ReadNumber(element, type, "amplitudePixels"),
                ReadNumber(element, type, "settleThresholdPixels"),
                ReadNumber(element, type, "settleStableSeconds"),
                ReadNumber(element, type, "settleTimeoutSeconds")),
            _ => ReadRepeat(element, id, place, version, ids),
        };
    }

    private static MultiRigDocumentStep ReadMultiRig(JsonElement element, Guid id, int version, HashSet<Guid> ids)
    {
        if (!element.TryGetProperty("tracks", out var tracksElement) || tracksElement.ValueKind != JsonValueKind.Array)
        {
            throw Structure($"A '{MultiRigType}' step is missing its list of 'tracks'.");
        }

        var tracks = new List<RigTrackDocument>();
        foreach (var track in tracksElement.EnumerateArray())
        {
            if (track.ValueKind != JsonValueKind.Object)
            {
                throw Structure("A rig track must be an object.");
            }

            var trackId = ReadId(track, "rigTrack", ids);
            var rigId = ReadDevice(track, "rigTrack", "rigId");
            if (!track.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
            {
                throw Structure("A rig track is missing its list of 'steps'.");
            }

            var steps = new List<DocumentStep>();
            foreach (var step in stepsElement.EnumerateArray())
            {
                steps.Add(ReadStep(step, Place.InTrack, version, ids));
            }

            tracks.Add(new RigTrackDocument(trackId, rigId, steps));
        }

        return new MultiRigDocumentStep(id, tracks, ReadDitherPolicy(element));
    }

    // Optional: a block without one does not dither. When it is there, it is complete.
    private static DitherPolicyDocument? ReadDitherPolicy(JsonElement block)
    {
        if (!block.TryGetProperty("ditherPolicy", out var policy) || policy.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (policy.ValueKind != JsonValueKind.Object)
        {
            throw Structure("'ditherPolicy' must be an object.");
        }

        if (!policy.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw Structure("'enabled' of a 'ditherPolicy' must be true or false.");
        }

        var everyNFrames = ReadNumber(policy, "ditherPolicy", "everyNFrames");
        if (everyNFrames != Math.Floor(everyNFrames) || Math.Abs(everyNFrames) > int.MaxValue)
        {
            throw Structure("'everyNFrames' of a 'ditherPolicy' must be a whole number.");
        }

        return new DitherPolicyDocument(
            enabled.GetBoolean(),
            ReadDevice(policy, "ditherPolicy", "triggerRigId"),
            (int)everyNFrames,
            ReadNumber(policy, "ditherPolicy", "amplitudePixels"),
            ReadNumber(policy, "ditherPolicy", "settleThresholdPixels"),
            ReadNumber(policy, "ditherPolicy", "settleStableSeconds"),
            ReadNumber(policy, "ditherPolicy", "settleTimeoutSeconds"));
    }

    private static RepeatDocumentStep ReadRepeat(JsonElement element, Guid id, Place place, int version, HashSet<Guid> ids)
    {
        if (!element.TryGetProperty("count", out var countElement))
        {
            throw Structure($"A '{RepeatType}' step is missing 'count'.");
        }

        // Whether the count makes sense (at least 1) is for the editor to say; it has to be a whole number.
        if (countElement.ValueKind != JsonValueKind.Number || !countElement.TryGetInt32(out var count))
        {
            throw Structure($"'count' of a '{RepeatType}' step must be a whole number.");
        }

        if (!element.TryGetProperty("children", out var childrenElement) || childrenElement.ValueKind != JsonValueKind.Array)
        {
            throw Structure($"A '{RepeatType}' step is missing its list of 'children'.");
        }

        var children = new List<DocumentLeafStep>();
        foreach (var child in childrenElement.EnumerateArray())
        {
            children.Add((DocumentLeafStep)ReadStep(
                child, place == Place.InTrack ? Place.InTrackRepeat : Place.InRepeat, version, ids));
        }

        return new RepeatDocumentStep(id, count, children);
    }

    private static Guid ReadId(JsonElement element, string type, HashSet<Guid> ids)
    {
        if (!element.TryGetProperty("id", out var idElement))
        {
            throw Structure($"A '{type}' step is missing 'id'.");
        }

        var text = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : idElement.ToString();
        if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(text, "D", out var id))
        {
            throw Structure($"Invalid sequence step ID '{text}'.");
        }

        if (!ids.Add(id))
        {
            throw Structure("Duplicate sequence step ID.");
        }

        return id;
    }

    private static double ReadNumber(JsonElement element, string type, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Structure($"A '{type}' step is missing '{name}'.");
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            throw Structure($"'{name}' of a '{type}' step must be a number.");
        }

        return number;
    }

    // The property must be there; its value is a device id, or null for "no device selected".
    private static string? ReadDevice(JsonElement element, string type, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Structure($"A '{type}' step is missing '{name}'.");
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()):
                return value.GetString()!.Trim();
            default:
                throw Structure($"'{name}' of a '{type}' step must be a device ID or null.");
        }
    }

    private static SequenceDocumentException Container(string message) => new(SequenceDocumentErrorKind.Container, message);

    private static SequenceDocumentException Structure(string message) => new(SequenceDocumentErrorKind.Structure, message);
}

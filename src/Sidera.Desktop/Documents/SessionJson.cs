using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Sidera.Core.Conditions;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.Documents;

/// <summary>
/// The <c>session</c> of a version 9 document: the automation of the session, the actions of its start and end, and its targets with their preparation, lanes, blocks and limits. The names are part of the
/// file format, as are the action types: nothing is derived from a class name, and nothing of the screen is saved. A reader of an older version never sees this object, and a newer file is refused by it.
/// </summary>
internal static class SessionJson
{
    private static readonly (SessionActionKind Kind, string Type)[] Types =
    [
        (SessionActionKind.Exposure, "exposure"),
        (SessionActionKind.SetFilter, "setFilter"),
        (SessionActionKind.Autofocus, "autofocus"),
        (SessionActionKind.MoveFocuser, "moveFocuser"),
        (SessionActionKind.Wait, "wait"),
        (SessionActionKind.WaitUntil, "waitUntil"),
        (SessionActionKind.StartGuiding, "startGuiding"),
        (SessionActionKind.StopGuiding, "stopGuiding"),
        (SessionActionKind.DitherNow, "ditherNow"),
        (SessionActionKind.Slew, "slew"),
        (SessionActionKind.SlewAndCenter, "slewAndCenter"),
        (SessionActionKind.PlateSolve, "plateSolve"),
        (SessionActionKind.CenterAndRotate, "centerAndRotate"),
        (SessionActionKind.SyncMount, "syncMount"),
        (SessionActionKind.CoolCamera, "coolCamera"),
        (SessionActionKind.WarmCamera, "warmCamera"),
        (SessionActionKind.Park, "park"),
        (SessionActionKind.Unpark, "unpark"),
        (SessionActionKind.SetTracking, "setTracking"),
    ];

    private static SequenceDocumentException Structure(string message) => new(SequenceDocumentErrorKind.Structure, "Invalid session: " + message);

    // ---- writing

    public static void Write(Utf8JsonWriter w, SessionDefinition session)
    {
        w.WriteStartObject("session");

        w.WriteStartObject("automation");
        w.WriteStartObject("meridianFlip");
        if (session.Automation.Flip is { } flip)
        {
            WorkflowJson.WriteSettingsBody(w, flip);
        }
        else
        {
            // Follows the application's settings: nothing of the flip is copied into the document.
            w.WriteBoolean("useDefaults", true);
        }

        w.WriteEndObject();
        w.WriteEndObject();

        WriteActions(w, "start", session.Start);

        w.WriteStartArray("targets");
        foreach (var target in session.Targets)
        {
            w.WriteStartObject();
            w.WriteString("id", target.Id.ToString("D", CultureInfo.InvariantCulture));
            w.WriteString("name", target.Name);
            w.WriteNumber("raHours", target.RightAscensionHours);
            w.WriteNumber("decDegrees", target.DeclinationDegrees);
            if (target.RotationDegrees is { } rotation)
            {
                w.WriteNumber("rotationDegrees", rotation);
            }

            w.WriteBoolean("enabled", target.Enabled);
            WriteActions(w, "preparation", target.Preparation);
            w.WriteStartArray("lanes");
            foreach (var lane in target.Lanes)
            {
                WriteLane(w, lane);
            }

            w.WriteEndArray();
            WorkflowJson.WriteConditions(w, "limits", target.Limits);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        WriteActions(w, "end", session.End);
        w.WriteEndObject();
    }

    private static void WriteLane(Utf8JsonWriter w, SetupLane lane)
    {
        w.WriteStartObject();
        w.WriteString("id", lane.Id.ToString("D", CultureInfo.InvariantCulture));
        if (lane.Setup is { } setup)
        {
            w.WriteString("setup", setup.Value);
        }

        w.WriteStartArray("blocks");
        foreach (var block in lane.Blocks)
        {
            w.WriteStartObject();
            w.WriteString("id", block.Id.ToString("D", CultureInfo.InvariantCulture));
            if (block.Name is { Length: > 0 } name)
            {
                w.WriteString("name", name);
            }

            w.WriteBoolean("enabled", block.Enabled);
            WriteActions(w, "actions", block.Actions);

            w.WriteStartObject("repeat");
            if (block.Repeat.Count is { } count)
            {
                w.WriteNumber("count", count);
            }

            WorkflowJson.WriteConditions(w, "until", block.Repeat.Until);
            w.WriteEndObject();

            if (!block.Automation.IsEmpty)
            {
                w.WriteStartObject("automation");
                if (block.Automation.Dither is { } dither)
                {
                    w.WriteStartObject("dither");
                    w.WriteNumber("everyFrames", dither.EveryFrames);
                    WriteDither(w, dither.Settings);
                    w.WriteEndObject();
                }

                if (block.Automation.Focus is { IsActive: true } focus)
                {
                    w.WriteStartObject("autofocus");
                    w.WriteBoolean("atBlockStart", focus.AtBlockStart);
                    w.WriteNumber("everyMinutes", focus.EveryMinutes);
                    w.WriteBoolean("afterFilterChange", focus.AfterFilterChange);
                    WriteFocus(w, focus.Settings);
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            WorkflowJson.WriteConditions(w, "limits", block.Limits);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteFocus(Utf8JsonWriter w, FocusSettings settings)
    {
        w.WriteNumber("exposureSeconds", settings.ExposureSeconds);
        w.WriteNumber("stepSize", settings.StepSize);
        w.WriteNumber("samples", settings.SampleCount);
    }

    private static void WriteDither(Utf8JsonWriter w, DitherSettings settings)
    {
        w.WriteNumber("amplitudePixels", settings.AmplitudePixels);
        w.WriteNumber("settleThresholdPixels", settings.SettleThresholdPixels);
        w.WriteNumber("settleStableSeconds", settings.SettleStableSeconds);
        w.WriteNumber("settleTimeoutSeconds", settings.SettleTimeoutSeconds);
    }

    private static void WriteActions(Utf8JsonWriter w, string name, IReadOnlyList<SessionAction> actions)
    {
        w.WriteStartArray(name);
        foreach (var action in actions)
        {
            w.WriteStartObject();
            w.WriteString("id", action.Id.ToString("D", CultureInfo.InvariantCulture));
            w.WriteString("type", System.Array.Find(Types, t => t.Kind == action.Kind).Type);
            w.WriteBoolean("enabled", action.Enabled);
            if (action.Setup is { } setup)
            {
                w.WriteString("setup", setup.Value);
            }

            switch (action)
            {
                case ExposureAction exposure:
                    w.WriteNumber("seconds", exposure.Seconds);
                    break;
                case SetFilterAction filter:
                    w.WriteNumber("slot", filter.Slot);
                    break;
                case AutofocusAction autofocus:
                    WriteFocus(w, autofocus.Settings);
                    break;
                case MoveFocuserAction focuser:
                    w.WriteNumber("position", focuser.Position);
                    break;
                case WaitAction wait:
                    w.WriteNumber("seconds", wait.Seconds);
                    break;
                case WaitUntilAction until:
                    WorkflowJson.WriteConditions(w, "conditions", until.Conditions);
                    break;
                case DitherNowAction dither:
                    WriteDither(w, dither.Settings);
                    break;
                case SlewAndCenterAction center:
                    w.WriteNumber("toleranceArcseconds", center.ToleranceArcseconds);
                    w.WriteNumber("maxAttempts", center.MaxAttempts);
                    w.WriteNumber("solveExposureSeconds", center.SolveExposureSeconds);
                    break;
                case CenterAndRotateAction rotate:
                    w.WriteNumber("toleranceArcseconds", rotate.ToleranceArcseconds);
                    w.WriteNumber("maxAttempts", rotate.MaxAttempts);
                    w.WriteNumber("solveExposureSeconds", rotate.SolveExposureSeconds);
                    break;
                case PlateSolveAction solve:
                    w.WriteNumber("exposureSeconds", solve.ExposureSeconds);
                    break;
                case CoolCameraAction cool:
                    w.WriteNumber("targetCelsius", cool.TargetCelsius);
                    w.WriteNumber("rampMinutes", cool.RampMinutes);
                    break;
                case WarmCameraAction warm:
                    w.WriteNumber("rampMinutes", warm.RampMinutes);
                    break;
                case SetTrackingAction tracking:
                    w.WriteBoolean("on", tracking.On);
                    break;
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    // ---- reading

    /// <summary>The session of a document, or <c>null</c> for one that has none (every document before version 9, and a sequence made as a tree of steps).</summary>
    public static SessionDefinition? Read(JsonElement root)
    {
        if (!root.TryGetProperty("session", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Structure("'session' must be an object.");
        }

        var ids = new HashSet<Guid>();
        MeridianFlipSettings? flip = null;
        if (element.TryGetProperty("automation", out var automation) && automation.ValueKind == JsonValueKind.Object
            && automation.TryGetProperty("meridianFlip", out var flipElement) && flipElement.ValueKind == JsonValueKind.Object
            && !(flipElement.TryGetProperty("useDefaults", out var useDefaults) && useDefaults.ValueKind == JsonValueKind.True))
        {
            flip = WorkflowJson.ReadSettingsBody(flipElement);
        }

        var targets = new List<SessionTarget>();
        foreach (var target in Array(element, "targets"))
        {
            var lanes = new List<SetupLane>();
            foreach (var lane in Array(target, "lanes"))
            {
                var blocks = new List<SequenceBlock>();
                foreach (var block in Array(lane, "blocks"))
                {
                    blocks.Add(ReadBlock(block, ids));
                }

                lanes.Add(new SetupLane(IdOf(lane, ids), OptionalBinding(lane, "setup"), blocks));
            }

            targets.Add(new SessionTarget(
                IdOf(target, ids), Text(target, "name"), Num(target, "raHours"), Num(target, "decDegrees"),
                target.TryGetProperty("rotationDegrees", out _) ? Num(target, "rotationDegrees") : null,
                Flag(target, "enabled", true), ReadActions(target, "preparation", ids), lanes, WorkflowJson.ReadConditions(target, "limits")));
        }

        return new SessionDefinition(new SessionAutomation(flip), ReadActions(element, "start", ids), targets, ReadActions(element, "end", ids));
    }

    private static SequenceBlock ReadBlock(JsonElement block, HashSet<Guid> ids)
    {
        var id = IdOf(block, ids);
        var repeat = block.TryGetProperty("repeat", out var repeatElement) && repeatElement.ValueKind == JsonValueKind.Object ? repeatElement : default;
        var rule = repeat.ValueKind == JsonValueKind.Object
            ? new RepeatRule(repeat.TryGetProperty("count", out _) ? Whole(repeat, "count") : null, WorkflowJson.ReadConditions(repeat, "until"))
            : new RepeatRule(null, []);

        DitherAutomation? dither = null;
        FocusAutomation? focus = null;
        if (block.TryGetProperty("automation", out var automation) && automation.ValueKind == JsonValueKind.Object)
        {
            if (automation.TryGetProperty("dither", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                dither = new DitherAutomation(Whole(d, "everyFrames"), ReadDither(d));
            }

            if (automation.TryGetProperty("autofocus", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                focus = new FocusAutomation(Flag(f, "atBlockStart", false), NumOr(f, "everyMinutes", 0), Flag(f, "afterFilterChange", false), ReadFocus(f));
            }
        }

        return new SequenceBlock(
            id, block.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null, Flag(block, "enabled", true),
            ReadActions(block, "actions", ids), rule, new BlockAutomation(dither, focus), WorkflowJson.ReadConditions(block, "limits"));
    }

    private static List<SessionAction> ReadActions(JsonElement parent, string name, HashSet<Guid> ids)
    {
        var actions = new List<SessionAction>();
        foreach (var e in Array(parent, name))
        {
            var id = IdOf(e, ids);
            var type = Text(e, "type");
            var kind = System.Array.Find(Types, t => t.Type == type);
            if (kind.Type is null)
            {
                throw Structure($"unknown action '{type}'.");
            }

            SessionAction action = kind.Kind switch
            {
                SessionActionKind.Exposure => new ExposureAction(id, Num(e, "seconds")),
                SessionActionKind.SetFilter => new SetFilterAction(id, Whole(e, "slot")),
                SessionActionKind.Autofocus => new AutofocusAction(id, ReadFocus(e)),
                SessionActionKind.MoveFocuser => new MoveFocuserAction(id, Whole(e, "position")),
                SessionActionKind.Wait => new WaitAction(id, Num(e, "seconds")),
                SessionActionKind.WaitUntil => new WaitUntilAction(id, WorkflowJson.ReadConditions(e, "conditions")),
                SessionActionKind.StartGuiding => new StartGuidingAction(id),
                SessionActionKind.StopGuiding => new StopGuidingAction(id),
                SessionActionKind.DitherNow => new DitherNowAction(id, ReadDither(e)),
                SessionActionKind.Slew => new SlewAction(id),
                SessionActionKind.SlewAndCenter => new SlewAndCenterAction(id, NumOr(e, "toleranceArcseconds", 60), WholeOr(e, "maxAttempts", 5), NumOr(e, "solveExposureSeconds", 5)),
                SessionActionKind.PlateSolve => new PlateSolveAction(id, NumOr(e, "exposureSeconds", 5)),
                SessionActionKind.CenterAndRotate => new CenterAndRotateAction(id, NumOr(e, "toleranceArcseconds", 60), WholeOr(e, "maxAttempts", 5), NumOr(e, "solveExposureSeconds", 5)),
                SessionActionKind.SyncMount => new SyncMountAction(id),
                SessionActionKind.CoolCamera => new CoolCameraAction(id, Num(e, "targetCelsius"), NumOr(e, "rampMinutes", 5)),
                SessionActionKind.WarmCamera => new WarmCameraAction(id, NumOr(e, "rampMinutes", 10)),
                SessionActionKind.Park => new ParkAction(id),
                SessionActionKind.Unpark => new UnparkAction(id),
                SessionActionKind.SetTracking => new SetTrackingAction(id, Flag(e, "on", true)),
                _ => throw Structure($"unknown action '{type}'."),
            };

            actions.Add(action with { Enabled = Flag(e, "enabled", true), Setup = OptionalBinding(e, "setup") });
        }

        return actions;
    }

    private static FocusSettings ReadFocus(JsonElement e) => new(NumOr(e, "exposureSeconds", 1), WholeOr(e, "stepSize", 400), WholeOr(e, "samples", 7));

    private static DitherSettings ReadDither(JsonElement e) =>
        new(NumOr(e, "amplitudePixels", 1.5), NumOr(e, "settleThresholdPixels", 0.5), NumOr(e, "settleStableSeconds", 1), NumOr(e, "settleTimeoutSeconds", 10));

    // ---- values

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : throw Structure($"'{name}' must be a list.");
    }

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw Structure($"'{name}' must be text.");

    private static double Num(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Structure($"'{name}' must be a number.");

    private static double NumOr(JsonElement parent, string name, double fallback) => parent.TryGetProperty(name, out _) ? Num(parent, name) : fallback;

    private static int Whole(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Structure($"'{name}' must be a whole number.");

    private static int WholeOr(JsonElement parent, string name, int fallback) => parent.TryGetProperty(name, out _) ? Whole(parent, name) : fallback;

    private static bool Flag(JsonElement parent, string name, bool fallback) =>
        !parent.TryGetProperty(name, out var value) ? fallback : value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Structure($"'{name}' must be true or false.");

    private static ImagingBindingId? OptionalBinding(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? new ImagingBindingId(text) : null;

    private static Guid IdOf(JsonElement element, HashSet<Guid> ids)
    {
        if (!Guid.TryParse(Text(element, "id"), out var id))
        {
            throw Structure("an id is not valid.");
        }

        return ids.Add(id) ? id : throw Structure("two elements have the same id.");
    }
}

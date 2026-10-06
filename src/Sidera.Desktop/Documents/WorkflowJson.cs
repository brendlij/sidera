using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Workflows;

namespace Sidera.Desktop.Documents;

/// <summary>
/// The <c>workflow</c> of a version 8 document: the target, the Prepare, Imaging and Finish sections, the dither policy and the autofocus policies of the setups. The discriminators and names
/// are part of the file format. The steps of the document are what the workflow compiles to; they are written too, so that an older reader of the steps and the Advanced editor have them, but
/// a Sidera that reads a workflow compiles it again and does not trust the stored steps to still be what it makes.
/// </summary>
internal static class WorkflowJson
{
    private static readonly (WorkflowStepKind Kind, string Name)[] Kinds =
    [
        (WorkflowStepKind.SlewAndCenter, "slewAndCenter"),
        (WorkflowStepKind.Autofocus, "autofocus"),
        (WorkflowStepKind.StartGuiding, "startGuiding"),
        (WorkflowStepKind.StopGuiding, "stopGuiding"),
        (WorkflowStepKind.Wait, "wait"),
    ];

    private static SequenceDocumentException Structure(string message) => new(SequenceDocumentErrorKind.Structure, "Invalid workflow: " + message);

    public static void Write(Utf8JsonWriter w, WorkflowDefinition workflow)
    {
        w.WriteStartObject("workflow");

        w.WriteStartObject("target");
        w.WriteString("name", workflow.Target.Name);
        w.WriteNumber("raHours", workflow.Target.RightAscensionHours);
        w.WriteNumber("decDegrees", workflow.Target.DeclinationDegrees);
        if (workflow.Target.DesiredRotationDegrees is { } rotation)
        {
            w.WriteNumber("rotationDegrees", rotation);
        }

        if (workflow.Target.PointingSetup is { } pointing)
        {
            w.WriteString("pointingSetup", pointing.Value);
        }

        w.WriteEndObject();

        WriteSteps(w, "prepare", workflow.Prepare);

        w.WriteStartArray("imaging");
        foreach (var block in workflow.Imaging)
        {
            w.WriteStartObject();
            w.WriteString("id", block.Id.ToString("D", CultureInfo.InvariantCulture));
            if (block.Setup is { } setup)
            {
                w.WriteString("setup", setup.Value);
            }

            if (block.FilterSlot is { } slot)
            {
                w.WriteNumber("filterSlot", slot);
            }

            w.WriteNumber("exposureSeconds", block.ExposureSeconds);
            w.WriteNumber("frames", block.Frames);
            w.WriteBoolean("enabled", block.Enabled);
            WriteConditions(w, "startWhen", block.StartAll);
            WriteConditions(w, "stopWhen", block.StopAny);
            w.WriteEndObject();
        }

        w.WriteEndArray();

        WriteSteps(w, "finish", workflow.Finish);

        var dither = workflow.Dither;
        w.WriteStartObject("dither");
        w.WriteBoolean("enabled", dither.Enabled);
        w.WriteNumber("everyNFrames", dither.EveryNFrames);
        if (dither.CountedSetup is { } counted)
        {
            w.WriteString("countedSetup", counted.Value);
        }

        w.WriteNumber("amplitudePixels", dither.AmplitudePixels);
        w.WriteNumber("settleThresholdPixels", dither.SettleThresholdPixels);
        w.WriteNumber("settleStableSeconds", dither.SettleStableSeconds);
        w.WriteNumber("settleTimeoutSeconds", dither.SettleTimeoutSeconds);
        w.WriteEndObject();

        if (workflow.MeridianFlipUsesDefaults)
        {
            // Follows the application's settings: nothing of the flip is copied into the document.
            w.WriteStartObject("meridianFlip");
            w.WriteBoolean("useDefaults", true);
            w.WriteEndObject();
        }
        else if (workflow.MeridianFlip is { } flip)
        {
            w.WriteStartObject("meridianFlip");
            WriteSettingsBody(w, flip);
            w.WriteEndObject();
        }

        WriteConditions(w, "stopTargetWhen", workflow.TargetStopAny);

        w.WriteStartArray("autofocus");
        foreach (var policy in workflow.AutofocusPolicies)
        {
            w.WriteStartObject();
            w.WriteString("setup", policy.Setup.Value);
            w.WriteBoolean("enabled", policy.Enabled);
            w.WriteBoolean("atStart", policy.AtStart);
            w.WriteNumber("intervalMinutes", policy.IntervalMinutes);
            w.WriteBoolean("afterFilterChange", policy.AfterFilterChange);
            WriteSettings(w, policy.Settings);
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter w, AutofocusSettings settings)
    {
        w.WriteNumber("exposureSeconds", settings.ExposureSeconds);
        w.WriteNumber("stepSize", settings.StepSize);
        w.WriteNumber("samples", settings.SampleCount);
    }

    private static void WriteSteps(Utf8JsonWriter w, string name, IReadOnlyList<WorkflowStep> steps)
    {
        w.WriteStartArray(name);
        foreach (var step in steps)
        {
            w.WriteStartObject();
            w.WriteString("id", step.Id.ToString("D", CultureInfo.InvariantCulture));
            w.WriteString("kind", System.Array.Find(Kinds, k => k.Kind == step.Kind).Name);
            if (step.Setup is { } setup)
            {
                w.WriteString("setup", setup.Value);
            }

            w.WriteBoolean("enabled", step.Enabled);
            w.WriteNumber("seconds", step.Seconds);
            w.WriteNumber("toleranceArcseconds", step.ToleranceArcseconds);
            w.WriteNumber("maxAttempts", step.MaxAttempts);
            w.WriteNumber("solveExposureSeconds", step.SolveExposureSeconds);
            if (step.WaitMode != WorkflowWaitMode.Duration)
            {
                w.WriteString("waitMode", step.WaitMode == WorkflowWaitMode.UntilTime ? "untilTime" : "untilCondition");
                WriteConditions(w, "until", step.UntilAll);
            }

            if (step.Autofocus is { } autofocus)
            {
                w.WriteStartObject("autofocus");
                WriteSettings(w, autofocus);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    // ---- the meridian flip: its settings, and as a policy of a Multi-Rig block with the target it keeps pointing at

    internal static void WriteSettingsBody(Utf8JsonWriter w, MeridianFlipSettings s)
    {
        w.WriteBoolean("enabled", s.Enabled);
        w.WriteNumber("pauseBeforeMeridianMinutes", s.PauseBeforeMeridianMinutes);
        w.WriteNumber("flipAfterMeridianMinutes", s.FlipAfterMeridianMinutes);
        w.WriteNumber("latestAllowedFlipMinutes", s.LatestAllowedFlipMinutes);
        w.WriteBoolean("finishCurrentExposure", s.FinishCurrentExposure);
        w.WriteBoolean("stopGuidingBeforeFlip", s.StopGuidingBeforeFlip);
        w.WriteBoolean("recenterAfterFlip", s.RecenterAfterFlip);
        w.WriteBoolean("verifyRotationAfterFlip", s.VerifyRotationAfterFlip);
        w.WriteBoolean("autofocusAfterFlip", s.AutofocusAfterFlip);
        w.WriteBoolean("restartGuidingAfterFlip", s.RestartGuidingAfterFlip);
        w.WriteBoolean("ditherAfterFlip", s.DitherAfterFlip);
        w.WriteNumber("pauseAfterFlipMinutes", s.PauseAfterFlipMinutes);
        w.WriteNumber("maxFlipAttempts", s.MaxFlipAttempts);
        w.WriteString("failureBehavior", s.FailureBehavior == MeridianFlipFailureBehavior.AbortSession ? "abortSession" : "pauseSession");
        w.WriteNumber("centeringToleranceArcseconds", s.CenteringToleranceArcseconds);
        w.WriteNumber("maxCenteringAttempts", s.MaxCenteringAttempts);
        w.WriteNumber("solveExposureSeconds", s.SolveExposureSeconds);
    }

    // A setting that the file does not have is the default of the setting; one that has the wrong kind of value is an error.
    internal static MeridianFlipSettings ReadSettingsBody(JsonElement e)
    {
        var d = new MeridianFlipSettings();
        bool Flag(string name, bool fallback) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw Structure($"'{name}' must be true or false.");
        double Num(string name, double fallback) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw Structure($"'{name}' must be a number.");
        int Int(string name, int fallback) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw Structure($"'{name}' must be a whole number.");

        var behavior = e.TryGetProperty("failureBehavior", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (e.TryGetProperty("failureBehavior", out _) && behavior is not ("pauseSession" or "abortSession"))
        {
            throw Structure("'failureBehavior' must be 'pauseSession' or 'abortSession'.");
        }

        return new MeridianFlipSettings
        {
            Enabled = Flag("enabled", d.Enabled),
            PauseBeforeMeridianMinutes = Num("pauseBeforeMeridianMinutes", d.PauseBeforeMeridianMinutes),
            FlipAfterMeridianMinutes = Num("flipAfterMeridianMinutes", d.FlipAfterMeridianMinutes),
            LatestAllowedFlipMinutes = Num("latestAllowedFlipMinutes", d.LatestAllowedFlipMinutes),
            FinishCurrentExposure = Flag("finishCurrentExposure", d.FinishCurrentExposure),
            StopGuidingBeforeFlip = Flag("stopGuidingBeforeFlip", d.StopGuidingBeforeFlip),
            RecenterAfterFlip = Flag("recenterAfterFlip", d.RecenterAfterFlip),
            VerifyRotationAfterFlip = Flag("verifyRotationAfterFlip", d.VerifyRotationAfterFlip),
            AutofocusAfterFlip = Flag("autofocusAfterFlip", d.AutofocusAfterFlip),
            RestartGuidingAfterFlip = Flag("restartGuidingAfterFlip", d.RestartGuidingAfterFlip),
            DitherAfterFlip = Flag("ditherAfterFlip", d.DitherAfterFlip),
            PauseAfterFlipMinutes = Num("pauseAfterFlipMinutes", d.PauseAfterFlipMinutes),
            MaxFlipAttempts = Int("maxFlipAttempts", d.MaxFlipAttempts),
            FailureBehavior = behavior == "abortSession" ? MeridianFlipFailureBehavior.AbortSession : MeridianFlipFailureBehavior.PauseSession,
            CenteringToleranceArcseconds = Num("centeringToleranceArcseconds", d.CenteringToleranceArcseconds),
            MaxCenteringAttempts = Int("maxCenteringAttempts", d.MaxCenteringAttempts),
            SolveExposureSeconds = Num("solveExposureSeconds", d.SolveExposureSeconds),
        };
    }

    /// <summary>The <c>meridianFlip</c> of a Multi-Rig block: the settings, and the target the flip slews back to.</summary>
    public static void WriteMeridianFlip(Utf8JsonWriter w, MeridianFlipPolicyDraft policy)
    {
        w.WriteStartObject("meridianFlip");
        WriteSettingsBody(w, policy.Settings);
        w.WriteNumber("raHours", policy.RightAscensionHours);
        w.WriteNumber("decDegrees", policy.DeclinationDegrees);
        if (policy.TargetName is { } name)
        {
            w.WriteString("targetName", name);
        }

        if (policy.DesiredRotationDegrees is { } rotation)
        {
            w.WriteNumber("rotationDegrees", rotation);
        }

        if (policy.PointingRigId is { } pointing)
        {
            w.WriteString("pointingRigId", pointing.Value);
        }

        w.WriteNumber("ditherAmplitudePixels", policy.DitherAmplitudePixels);
        w.WriteNumber("settleThresholdPixels", policy.SettleThresholdPixels);
        w.WriteNumber("settleStableSeconds", policy.SettleStableSeconds);
        w.WriteNumber("settleTimeoutSeconds", policy.SettleTimeoutSeconds);
        w.WriteEndObject();
    }

    public static MeridianFlipPolicyDraft? ReadMeridianFlip(JsonElement block)
    {
        if (!block.TryGetProperty("meridianFlip", out var e) || e.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Structure("'meridianFlip' must be an object.");
        }

        return new MeridianFlipPolicyDraft(
            ReadSettingsBody(e), Number(e, "raHours"), Number(e, "decDegrees"),
            e.TryGetProperty("targetName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null,
            e.TryGetProperty("rotationDegrees", out _) ? Number(e, "rotationDegrees") : null,
            OptionalRig(e, "pointingRigId"),
            e.TryGetProperty("ditherAmplitudePixels", out _) ? Number(e, "ditherAmplitudePixels") : 1.5,
            e.TryGetProperty("settleThresholdPixels", out _) ? Number(e, "settleThresholdPixels") : 0.5,
            e.TryGetProperty("settleStableSeconds", out _) ? Number(e, "settleStableSeconds") : 1,
            e.TryGetProperty("settleTimeoutSeconds", out _) ? Number(e, "settleTimeoutSeconds") : 60);
    }

    /// <summary>The workflow of a document, or <c>null</c> for one that has none (every document before version 8, and a sequence made in the Advanced editor).</summary>
    public static WorkflowDefinition? Read(JsonElement root)
    {
        if (!root.TryGetProperty("workflow", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Structure("'workflow' must be an object.");
        }

        var target = Object(element, "target");
        var workflowTarget = new WorkflowTarget(
            Text(target, "name"), Number(target, "raHours"), Number(target, "decDegrees"),
            target.TryGetProperty("rotationDegrees", out _) ? Number(target, "rotationDegrees") : null,
            OptionalBinding(target, "pointingSetup"));

        var prepare = ReadSteps(element, "prepare");
        var finish = ReadSteps(element, "finish");

        var blocks = new List<ImagingBlock>();
        var ids = new HashSet<Guid>();
        foreach (var block in Array(element, "imaging"))
        {
            var id = IdOf(block, ids);
            blocks.Add(new ImagingBlock(
                id, OptionalBinding(block, "setup"), block.TryGetProperty("filterSlot", out _) ? Whole(block, "filterSlot") : null,
                Number(block, "exposureSeconds"), Whole(block, "frames"), Flag(block, "enabled"), ReadConditionsOrNull(block, "startWhen"), ReadConditionsOrNull(block, "stopWhen")));
        }

        foreach (var step in prepare)
        {
            if (!ids.Add(step.Id))
            {
                throw Structure("two steps have the same id.");
            }
        }

        foreach (var step in finish)
        {
            if (!ids.Add(step.Id))
            {
                throw Structure("two steps have the same id.");
            }
        }

        var dither = Object(element, "dither");
        var workflowDither = new WorkflowDither(
            Flag(dither, "enabled"), Whole(dither, "everyNFrames"), OptionalBinding(dither, "countedSetup"), Number(dither, "amplitudePixels"),
            Number(dither, "settleThresholdPixels"), Number(dither, "settleStableSeconds"), Number(dither, "settleTimeoutSeconds"));

        var policies = new List<SetupAutofocus>();
        foreach (var policy in Array(element, "autofocus"))
        {
            policies.Add(new SetupAutofocus(
                new ImagingBindingId(Text(policy, "setup")), Flag(policy, "enabled"), Flag(policy, "atStart"), Number(policy, "intervalMinutes"), Flag(policy, "afterFilterChange"), Settings(policy)));
        }

        MeridianFlipSettings? meridianFlip = null;
        var usesDefaults = false;
        if (element.TryGetProperty("meridianFlip", out var flipElement) && flipElement.ValueKind == JsonValueKind.Object)
        {
            usesDefaults = flipElement.TryGetProperty("useDefaults", out var use) && use.ValueKind == JsonValueKind.True;
            meridianFlip = usesDefaults ? null : ReadSettingsBody(flipElement);
        }

        return new WorkflowDefinition(workflowTarget, prepare, blocks, finish, workflowDither, policies, meridianFlip, usesDefaults, ReadConditionsOrNull(element, "stopTargetWhen"));
    }

    private static List<WorkflowStep> ReadSteps(JsonElement parent, string name)
    {
        var steps = new List<WorkflowStep>();
        foreach (var step in Array(parent, name))
        {
            var kindName = Text(step, "kind");
            var kind = System.Array.Find(Kinds, k => k.Name == kindName);
            if (kind.Name is null)
            {
                throw Structure($"unknown step '{kindName}'.");
            }

            steps.Add(new WorkflowStep(
                IdOf(step, null), kind.Kind, OptionalBinding(step, "setup"), Flag(step, "enabled"), Number(step, "seconds"), Number(step, "toleranceArcseconds"),
                Whole(step, "maxAttempts"), Number(step, "solveExposureSeconds"), step.TryGetProperty("autofocus", out var settings) && settings.ValueKind == JsonValueKind.Object ? Settings(settings) : null,
                step.TryGetProperty("waitMode", out var mode) && mode.ValueKind == JsonValueKind.String
                    ? mode.GetString() switch
                    {
                        "untilTime" => WorkflowWaitMode.UntilTime,
                        "untilCondition" => WorkflowWaitMode.UntilCondition,
                        _ => throw Structure($"unknown wait mode '{mode.GetString()}'."),
                    }
                    : WorkflowWaitMode.Duration,
                ReadConditionsOrNull(step, "until")));
        }

        return steps;
    }

    // ---- conditions: written only where there are some, so a workflow without them is the file it was before they existed

    internal static void WriteConditions(Utf8JsonWriter w, string name, IReadOnlyList<WorkflowCondition> conditions)
    {
        if (conditions.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var condition in conditions)
        {
            WriteCondition(w, condition);
        }

        w.WriteEndArray();
    }

    internal static void WriteCondition(Utf8JsonWriter w, WorkflowCondition condition)
    {
        w.WriteStartObject();
        switch (condition)
        {
            case TimeCondition { AbsoluteUtc: { } utc }:
                w.WriteString("type", "time");
                w.WriteString("utc", utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
                break;
            case TimeCondition time:
                w.WriteString("type", "time");
                w.WriteString("timeOfDay", time.TimeOfDay!.Value.ToString("HH':'mm", CultureInfo.InvariantCulture));
                if (time.TimeZoneId is { } zone)
                {
                    w.WriteString("zone", zone);
                }

                break;
            case DurationCondition duration:
                w.WriteString("type", "duration");
                w.WriteNumber("seconds", duration.Duration.TotalSeconds);
                break;
            case TargetAltitudeCondition target:
                w.WriteString("type", "targetAltitude");
                w.WriteNumber("degrees", target.Degrees);
                w.WriteString("when", target.Direction == ThresholdDirection.Above ? "above" : "below");
                break;
            case SunAltitudeCondition sun:
                w.WriteString("type", "sunAltitude");
                w.WriteNumber("degrees", sun.Degrees);
                w.WriteString("when", sun.Direction == ThresholdDirection.Above ? "above" : "below");
                break;
            case TwilightCondition twilight:
                w.WriteString("type", "twilight");
                w.WriteString("twilight", twilight.Twilight.Title());
                w.WriteString("event", twilight.Event == TwilightEvent.Dusk ? "dusk" : "dawn");
                break;
            case FrameCountCondition frames:
                w.WriteString("type", "frames");
                w.WriteNumber("count", frames.Frames);
                break;
            default:
                throw new SequenceDocumentException(SequenceDocumentErrorKind.Structure, $"The condition '{condition.GetType().Name}' cannot be saved.");
        }

        w.WriteEndObject();
    }

    // A workflow without conditions is the workflow it was before they existed: null, not an empty list.
    private static IReadOnlyList<WorkflowCondition>? ReadConditionsOrNull(JsonElement parent, string name) => ReadConditions(parent, name) is { Count: > 0 } list ? list : null;

    internal static IReadOnlyList<WorkflowCondition> ReadConditions(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Structure($"'{name}' must be a list of conditions.");
        }

        var conditions = new List<WorkflowCondition>();
        foreach (var element in list.EnumerateArray())
        {
            conditions.Add(ReadCondition(element));
        }

        return conditions;
    }

    internal static WorkflowCondition ReadCondition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Structure("a condition must be an object.");
        }

        var type = Text(element, "type");
        WorkflowCondition condition = type switch
        {
            "time" when element.TryGetProperty("utc", out _) => DateTime.TryParseExact(
                Text(element, "utc"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                ? TimeCondition.AtUtc(utc)
                : throw Structure("the time of a condition is not a date and time."),
            "time" => TimeOnly.TryParseExact(Text(element, "timeOfDay"), "HH':'mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timeOfDay)
                ? TimeCondition.AtLocalTime(timeOfDay, element.TryGetProperty("zone", out var zone) && zone.ValueKind == JsonValueKind.String ? zone.GetString() : null)
                : throw Structure("the time of day of a condition is not a time."),
            "duration" => new DurationCondition(TimeSpan.FromSeconds(Number(element, "seconds"))),
            "targetAltitude" => new TargetAltitudeCondition(Number(element, "degrees"), Direction(element)),
            "sunAltitude" => new SunAltitudeCondition(Number(element, "degrees"), Direction(element)),
            "twilight" => new TwilightCondition(
                Text(element, "twilight") switch { "civil" => Twilight.Civil, "nautical" => Twilight.Nautical, "astronomical" => Twilight.Astronomical, var other => throw Structure($"unknown twilight '{other}'.") },
                Text(element, "event") switch { "dusk" => TwilightEvent.Dusk, "dawn" => TwilightEvent.Dawn, var other => throw Structure($"unknown twilight event '{other}'.") }),
            "frames" => new FrameCountCondition(Whole(element, "count")),
            _ => throw Structure($"unknown condition '{type}'."),
        };

        return condition.Problem is { } problem ? throw Structure(problem) : condition;
    }

    private static ThresholdDirection Direction(JsonElement element) => Text(element, "when") switch
    {
        "above" => ThresholdDirection.Above,
        "below" => ThresholdDirection.Below,
        var other => throw Structure($"unknown comparison '{other}'."),
    };

    // The stop conditions of a block or a target, in a sequence: { "any": [ ... ], "target": { "name", "raHours", "decDegrees" } }
    internal static void WriteStop(Utf8JsonWriter w, string name, StopConditionsDraft? stop)
    {
        if (stop is null or { IsEmpty: true })
        {
            return;
        }

        w.WriteStartObject(name);
        WriteConditions(w, "any", stop.Any);
        WriteConditionTarget(w, stop.Target);
        w.WriteEndObject();
    }

    internal static StopConditionsDraft? ReadStop(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var any = ReadConditions(element, "any");
        return any.Count == 0 ? null : new StopConditionsDraft(any, ReadConditionTarget(element));
    }

    internal static void WriteConditionTarget(Utf8JsonWriter w, ConditionTargetDraft? target)
    {
        if (target is null)
        {
            return;
        }

        w.WriteStartObject("target");
        if (target.Name is { } targetName)
        {
            w.WriteString("name", targetName);
        }

        w.WriteNumber("raHours", target.RightAscensionHours);
        w.WriteNumber("decDegrees", target.DeclinationDegrees);
        w.WriteEndObject();
    }

    internal static ConditionTargetDraft? ReadConditionTarget(JsonElement parent)
    {
        if (!parent.TryGetProperty("target", out var target) || target.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ConditionTargetDraft(
            target.TryGetProperty("name", out var targetName) && targetName.ValueKind == JsonValueKind.String ? targetName.GetString() : null,
            Number(target, "raHours"), Number(target, "decDegrees"));
    }

    private static AutofocusSettings Settings(JsonElement element) => new(Number(element, "exposureSeconds"), Whole(element, "stepSize"), Whole(element, "samples"));

    private static Guid IdOf(JsonElement element, HashSet<Guid>? ids)
    {
        if (!Guid.TryParse(Text(element, "id"), out var id))
        {
            throw Structure("an id is not valid.");
        }

        if (ids is not null && !ids.Add(id))
        {
            throw Structure("two blocks have the same id.");
        }

        return id;
    }

    private static JsonElement Object(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : throw Structure($"'{name}' is missing or not an object.");

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : throw Structure($"'{name}' is missing or not a list.");

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { } text ? text : throw Structure($"'{name}' is missing or not text.");

    private static double Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw Structure($"'{name}' is missing or not a number.");

    private static int Whole(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : throw Structure($"'{name}' is missing or not a whole number.");

    private static bool Flag(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Structure($"'{name}' must be true or false.");

    // A reference to an imaging path or, in a document from before paths, to a setup by its id; both are the text that is stored.
    private static ImagingBindingId? OptionalBinding(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? new ImagingBindingId(text) : null;

    private static RigId? OptionalRig(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? new RigId(text) : null;
}

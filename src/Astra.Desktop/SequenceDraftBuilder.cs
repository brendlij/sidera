using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop;

/// <summary>The draft cannot make a sequence; <see cref="Problems"/> say why, one short sentence each.</summary>
public sealed class SequenceConfigurationException(IReadOnlyList<string> problems)
    : InvalidOperationException(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>How a step is shown: a title, and a one-line summary of its parameters.</summary>
public sealed record StepDescription(string Title, string Summary);

/// <summary>What is wrong with a draft: per step (also steps inside a Repeat), and about the sequence as a whole.</summary>
/// <param name="SequenceProblems">Problems of the sequence itself, for example that it has no steps.</param>
/// <param name="StepProblems">Problems by <see cref="SequenceStepDraft.Id"/>; steps without problems are absent.</param>
public sealed record DraftValidation(
    IReadOnlyList<string> SequenceProblems,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> StepProblems
)
{
    public bool IsValid => SequenceProblems.Count == 0 && StepProblems.Count == 0;

    public IReadOnlyList<string> ProblemsOf(Guid stepId) =>
        StepProblems.TryGetValue(stepId, out var problems) ? problems : [];
}

/// <summary>
/// A runtime step together with the draft step it was built from, and how that step was described. For a Repeat,
/// <see cref="Step"/> is the runtime <see cref="RepeatStep"/> and <see cref="Children"/> are the built steps inside
/// it, in order; the group the runtime needs around several children is an internal detail and has no entry.
/// </summary>
public sealed record BuiltStep(
    Guid DraftId,
    StepDescription Description,
    ISequenceStep Step,
    IReadOnlyList<BuiltStep>? Children = null
);

/// <summary>
/// A sequence built from a draft. <see cref="Steps"/> has one entry per step of <see cref="Sequence"/>, in the same
/// order, so a running position (its top-level index) maps back to the draft step without any ids in the runtime.
/// </summary>
public sealed record BuiltSequence(Sequence Sequence, IReadOnlyList<BuiltStep> Steps);

/// <summary>
/// Turns a list of <see cref="SequenceStepDraft"/>s into a runtime <see cref="Sequence"/>: one existing step per
/// draft step, in the draft's order, and for a Repeat a <see cref="RepeatStep"/> around a <see cref="SequenceGroup"/>
/// of its children (the repeat runs one child, the group is how that child becomes several). Every call makes new
/// step objects, so a run is never affected by a later edit. It checks everything itself and does not rely on what
/// the editor allowed.
/// <para>
/// Nothing runs next to anything else, also inside a Repeat. A <see cref="DitherAction"/> therefore needs no
/// coordination group and no safe points: it runs when its guider, mount and camera are free, exactly as the runtime
/// defines for a dither outside a coordination group.
/// </para>
/// <para>
/// Guiding is checked by playing the draft through: each guider is guiding, stopped, or unknown (before the first
/// step that says), and every Start, Stop and Dither is checked against what the steps before it did. A Repeat's
/// children are played twice when it repeats more than once, because the second pass is what sees the state the
/// first one left behind. That is exact for these rules, and a problem found in the second pass is reported as
/// coming from the previous repetition.
/// </para>
/// </summary>
public static class SequenceDraftBuilder
{
    public const string SequenceName = "Custom";
    public const string RepeatBodyName = "Repeat body";

    /// <summary>Describes a step for display. Never throws; a missing device is shown by its id or as "no camera".</summary>
    public static StepDescription Describe(DeviceRegistry registry, SequenceStepDraft step)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(step);

        return step switch
        {
            ExposureStepDraft e => new("Exposure", $"{DeviceName(registry, e.CameraId, "no camera")} · {Seconds(e.Seconds)}"),
            DelayStepDraft d => new("Delay", Seconds(d.Seconds)),
            SlewStepDraft s => new("Slew", string.Create(
                CultureInfo.InvariantCulture,
                $"RA {s.RightAscensionHours:0.###} h · Dec {s.DeclinationDegrees:+0.##;-0.##;0}°")),
            StartGuidingStepDraft g => new("Start Guiding", DeviceName(registry, g.GuiderId, "no guider")),
            StopGuidingStepDraft g => new("Stop Guiding", DeviceName(registry, g.GuiderId, "no guider")),
            DitherStepDraft d => new("Dither", string.Create(
                CultureInfo.InvariantCulture,
                $"{d.AmplitudePixels:0.##} px · settle ≤ {d.SettleThresholdPixels:0.##} px for {d.SettleStableSeconds:0.##} s")),
            RepeatStepDraft r => new(
                string.Create(CultureInfo.InvariantCulture, $"Repeat × {r.Count}"),
                r.Children.Count == 0 ? "no steps" : r.Children.Count == 1 ? "1 step" : $"{r.Children.Count} steps"),
            _ => new(step.Kind.ToString(), string.Empty),
        };
    }

    /// <summary>The number of a step as shown to the user: "2" for a top-level step, "2.1" for the first one in step 2.</summary>
    public static string Label(int index, int? childIndex = null) =>
        childIndex is { } child
            ? string.Create(CultureInfo.InvariantCulture, $"{index + 1}.{child + 1}")
            : (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>Checks the whole draft, one level of Repeat deep, without building anything.</summary>
    public static DraftValidation Validate(DeviceRegistry registry, IReadOnlyList<SequenceStepDraft> steps)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(steps);

        var sequenceProblems = new List<string>();
        var stepProblems = new Dictionary<Guid, List<string>>();

        void Report(Guid id, string problem)
        {
            if (!stepProblems.TryGetValue(id, out var list))
            {
                stepProblems[id] = list = [];
            }

            if (!list.Contains(problem))
            {
                list.Add(problem);
            }
        }

        if (steps.Count == 0)
        {
            sequenceProblems.Add("The sequence has no steps.");
        }

        var guiding = new Dictionary<DeviceId, GuidingFact>();
        var ids = new List<Guid>();

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            ids.Add(step.Id);

            if (step is not RepeatStepDraft repeat)
            {
                var problems = new List<string>();
                ValidateStep(registry, step, problems);
                problems.ForEach(p => Report(step.Id, p));
                ValidateGuidingOrder(step, Label(i), -1, 0, guiding, p => Report(step.Id, p));
                continue;
            }

            if (repeat.Count < 1)
            {
                Report(repeat.Id, "Repeat count must be at least 1.");
            }

            if (repeat.Children.Count == 0)
            {
                Report(repeat.Id, "Repeat must contain at least one step.");
            }

            foreach (var child in repeat.Children)
            {
                ids.Add(child.Id);
                var problems = new List<string>();
                ValidateStep(registry, child, problems);
                problems.ForEach(p => Report(child.Id, p));
            }

            // The body is played once, and a second time if it runs again: the second pass meets what the first left.
            for (var pass = 0; pass < (repeat.Count > 1 ? 2 : 1); pass++)
            {
                for (var j = 0; j < repeat.Children.Count; j++)
                {
                    var child = repeat.Children[j];
                    ValidateGuidingOrder(child, Label(i, j), i, pass, guiding, p => Report(child.Id, p));
                }
            }
        }

        if (ids.Distinct().Count() != ids.Count)
        {
            sequenceProblems.Add("Two steps share the same id.");
        }

        return new DraftValidation(
            sequenceProblems,
            stepProblems.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value));
    }

    /// <summary>The problems of <paramref name="validation"/> as sentences naming the step, in step order.</summary>
    public static IReadOnlyList<string> Sentences(IReadOnlyList<SequenceStepDraft> steps, DraftValidation validation)
    {
        var sentences = new List<string>(validation.SequenceProblems);

        void Add(SequenceStepDraft step, string label)
        {
            var title = TitleOf(step.Kind);
            sentences.AddRange(validation.ProblemsOf(step.Id).Select(p => $"Step {label} ({title}): {p}"));
        }

        for (var i = 0; i < steps.Count; i++)
        {
            Add(steps[i], Label(i));
            if (steps[i] is RepeatStepDraft repeat)
            {
                for (var j = 0; j < repeat.Children.Count; j++)
                {
                    Add(repeat.Children[j], Label(i, j));
                }
            }
        }

        return sentences;
    }

    /// <exception cref="SequenceConfigurationException">The draft is not valid.</exception>
    public static BuiltSequence Build(DeviceRegistry registry, IReadOnlyList<SequenceStepDraft> steps)
    {
        var validation = Validate(registry, steps);
        if (!validation.IsValid)
        {
            throw new SequenceConfigurationException(Sentences(steps, validation));
        }

        try
        {
            var built = steps.Select(step => BuildStep(registry, step)).ToList();
            return new BuiltSequence(new Sequence(SequenceName, built.Select(b => b.Step)), built);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // The steps validate their own arguments too; anything the checks above missed ends up here.
            throw new SequenceConfigurationException([UserFacingError.Describe(ex)]);
        }
    }

    private static BuiltStep BuildStep(DeviceRegistry registry, SequenceStepDraft step)
    {
        var description = Describe(registry, step);
        if (step is not RepeatStepDraft repeat)
        {
            return new BuiltStep(step.Id, description, CreateLeaf(registry, step));
        }

        // A RepeatStep repeats one child; the group makes the children of the draft one.
        var children = repeat.Children.Select(child => BuildStep(registry, child)).ToList();
        var body = new SequenceGroup(RepeatBodyName, children.Select(child => child.Step));
        return new BuiltStep(step.Id, description, new RepeatStep(repeat.Count, body), children);
    }

    private static ISequenceStep CreateLeaf(DeviceRegistry registry, SequenceStepDraft step) => step switch
    {
        ExposureStepDraft e => new CameraExposureAction(registry, e.CameraId!.Value, TimeSpan.FromSeconds(e.Seconds)),
        DelayStepDraft d => new DelayAction(TimeSpan.FromSeconds(d.Seconds)),
        SlewStepDraft s => new SlewAction(
            registry, s.MountId!.Value, new CelestialCoordinates(s.RightAscensionHours, s.DeclinationDegrees)),
        StartGuidingStepDraft g => new StartGuidingAction(registry, g.GuiderId!.Value),
        StopGuidingStepDraft g => new StopGuidingAction(registry, g.GuiderId!.Value),
        DitherStepDraft d => new DitherAction(
            registry, d.GuiderId!.Value, d.MountId!.Value, [d.CameraId!.Value], d.AmplitudePixels,
            new GuidingSettleOptions(
                d.SettleThresholdPixels,
                TimeSpan.FromSeconds(d.SettleStableSeconds),
                TimeSpan.FromSeconds(d.SettleTimeoutSeconds))),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static void ValidateStep(DeviceRegistry registry, SequenceStepDraft step, List<string> problems)
    {
        switch (step)
        {
            case ExposureStepDraft e:
                CheckDevice<ICamera>(registry, e.CameraId, "camera", problems);
                CheckDuration(e.Seconds, "Exposure", problems);
                break;
            case DelayStepDraft d:
                CheckDuration(d.Seconds, "Delay", problems);
                break;
            case SlewStepDraft s:
                CheckDevice<IMount>(registry, s.MountId, "mount", problems);
                try
                {
                    _ = new CelestialCoordinates(s.RightAscensionHours, s.DeclinationDegrees);
                }
                catch (ArgumentException ex)
                {
                    problems.Add(UserFacingError.Describe(ex));
                }

                break;
            case StartGuidingStepDraft g:
                CheckDevice<IGuider>(registry, g.GuiderId, "guider", problems);
                break;
            case StopGuidingStepDraft g:
                CheckDevice<IGuider>(registry, g.GuiderId, "guider", problems);
                break;
            case DitherStepDraft d:
                ValidateDither(registry, d, problems);
                break;
            default:
                problems.Add($"Unsupported step '{step.GetType().Name}'.");
                break;
        }
    }

    // What DitherAction and GuidingSettleOptions require, plus what the dither command asks of the guider when it runs.
    private static void ValidateDither(DeviceRegistry registry, DitherStepDraft d, List<string> problems)
    {
        CheckDevice<IGuider>(registry, d.GuiderId, "guider", problems);
        CheckDevice<IMount>(registry, d.MountId, "mount", problems);
        CheckDevice<ICamera>(registry, d.CameraId, "camera", problems);

        if (d.GuiderId is { } id && registry.TryGet(id, out var guider) && guider is IGuider)
        {
            if (guider is not IDitherGuider)
            {
                problems.Add($"Guider '{id}' does not support dithering.");
            }
            else if (guider is not IGuidingSettler)
            {
                problems.Add($"Guider '{id}' does not support settling.");
            }
        }

        CheckPositive(d.AmplitudePixels, "Dither amplitude", "px", problems);

        var usable = IsPositive(d.SettleThresholdPixels) && IsPositive(d.SettleStableSeconds) && IsPositive(d.SettleTimeoutSeconds);
        CheckPositive(d.SettleThresholdPixels, "Settle threshold", "px", problems);
        CheckPositive(d.SettleStableSeconds, "Settle stable time", "s", problems);
        CheckPositive(d.SettleTimeoutSeconds, "Settle timeout", "s", problems);
        if (usable && d.SettleTimeoutSeconds <= d.SettleStableSeconds)
        {
            problems.Add("Settle timeout must be longer than the stable time.");
        }
    }

    // What the last step that touched a guider did to it, where, and in which pass over which Repeat body (-1: none).
    private readonly record struct GuidingFact(bool IsGuiding, string Label, int Scope, int Pass);

    // Start needs a guider that is not guiding, stop and dither need one that is. That is only knowable from what
    // earlier steps of the draft did, so only a contradiction with an earlier step is reported.
    private static void ValidateGuidingOrder(
        SequenceStepDraft step,
        string label,
        int scope,
        int pass,
        Dictionary<DeviceId, GuidingFact> guiding,
        Action<string> report)
    {
        string Where(GuidingFact fact) =>
            fact.Pass < pass ? $"step {fact.Label} in the previous repetition" : $"step {fact.Label}";

        // In the second pass only what the first pass of this body left behind is new: whatever came from before
        // the Repeat was already met, and reported, in the first pass.
        void Report(GuidingFact fact, string problem)
        {
            if (pass == 0 || fact.Scope == scope)
            {
                report(problem);
            }
        }

        switch (step)
        {
            case StartGuidingStepDraft { GuiderId: { } id }:
                if (guiding.TryGetValue(id, out var started) && started.IsGuiding)
                {
                    Report(started, $"Guiding was already started by {Where(started)}.");
                }

                guiding[id] = new GuidingFact(true, label, scope, pass);
                break;
            case StopGuidingStepDraft { GuiderId: { } id }:
                if (guiding.TryGetValue(id, out var stopped) && !stopped.IsGuiding)
                {
                    Report(stopped, $"Guiding was already stopped by {Where(stopped)}.");
                }

                guiding[id] = new GuidingFact(false, label, scope, pass);
                break;
            case DitherStepDraft { GuiderId: { } id }:
                if (guiding.TryGetValue(id, out var state) && !state.IsGuiding)
                {
                    Report(state, $"Dither needs guiding, but {Where(state)} stopped it.");
                }

                break;
        }
    }

    private static void CheckDevice<T>(DeviceRegistry registry, DeviceId? id, string kind, List<string> problems)
        where T : class, IDevice
    {
        if (id is not { } deviceId)
        {
            problems.Add($"No {kind} selected.");
        }
        else if (!registry.TryGet(deviceId, out var device) || device is null)
        {
            problems.Add($"The {kind} '{deviceId}' is not available.");
        }
        else if (device is not T)
        {
            problems.Add($"'{deviceId}' is not a {kind}.");
        }
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static void CheckPositive(double value, string label, string unit, List<string> problems)
    {
        if (!IsPositive(value))
        {
            problems.Add($"{label} must be greater than 0 {unit}.");
        }
    }

    // A positive duration that a TimeSpan can hold.
    private static void CheckDuration(double seconds, string label, List<string> problems)
    {
        if (!IsPositive(seconds))
        {
            problems.Add($"{label} must be greater than 0 s.");
        }
        else if (seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            problems.Add($"{label} is too long.");
        }
    }

    private static string DeviceName(DeviceRegistry registry, DeviceId? id, string none) =>
        id is not { } deviceId
            ? none
            : registry.TryGet(deviceId, out var device) && device is not null ? device.Name : deviceId.Value;

    private static string Seconds(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{seconds:0.##} s");

    /// <summary>The title of a kind of step.</summary>
    public static string TitleOf(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.Exposure => "Exposure",
        SequenceStepKind.Delay => "Delay",
        SequenceStepKind.Slew => "Slew",
        SequenceStepKind.StartGuiding => "Start Guiding",
        SequenceStepKind.StopGuiding => "Stop Guiding",
        SequenceStepKind.Dither => "Dither",
        SequenceStepKind.Repeat => "Repeat",
        _ => kind.ToString(),
    };
}

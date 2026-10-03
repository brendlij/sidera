using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Astra.Core.Coordination;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;
using Astra.Runtime.Devices;
using Astra.Runtime.Rigs;
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

/// <summary>
/// What a draft is checked against besides the devices: the rigs a Rig Track can select, and the equipment the
/// session shares. Both are optional; without rigs no rig is available, without shared equipment nothing is compared.
/// </summary>
public sealed record SequenceDraftContext(RigRegistry? Rigs = null, SharedEquipmentDraft? Shared = null);

/// <summary>What is wrong with a draft: per step (steps inside containers, and tracks, included), and about the session.</summary>
/// <param name="SequenceProblems">Problems of the sequence itself, for example that it has no steps.</param>
/// <param name="StepProblems">Problems by <see cref="SequenceStepDraft.Id"/> (or track id); steps without problems are absent.</param>
/// <param name="SharedProblems">Problems of the shared equipment: a device that is not there, or of the wrong kind.</param>
public sealed record DraftValidation(
    IReadOnlyList<string> SequenceProblems,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> StepProblems,
    IReadOnlyList<string>? SharedProblems = null
)
{
    public bool IsValid => SequenceProblems.Count == 0 && StepProblems.Count == 0 && (SharedProblems?.Count ?? 0) == 0;

    public IReadOnlyList<string> ProblemsOf(Guid stepId) =>
        StepProblems.TryGetValue(stepId, out var problems) ? problems : [];
}

/// <summary>
/// A runtime step together with the draft step it was built from, and how that step was described. For a Repeat,
/// <see cref="Step"/> is the runtime <see cref="RepeatStep"/> and <see cref="Children"/> are the built steps inside
/// it, in order; the group the runtime needs around several children is an internal detail and has no entry. For a
/// Multi-Rig block it is the <see cref="ParallelStep"/> and the children are its tracks (a <see cref="RigTrackStep"/>
/// each, with the built steps of the track as its own children).
/// <para>
/// Among the children of a track or of a Repeat there can be steps the builder generated for orchestration, in the
/// place in which they run (safe points, the dither of a dither policy). They belong to no draft step: they have
/// <see cref="IsGenerated"/> set and no draft id, and they exist only here, never in the draft or in a document.
/// </para>
/// </summary>
public sealed record BuiltStep(
    Guid DraftId,
    StepDescription Description,
    ISequenceStep Step,
    IReadOnlyList<BuiltStep>? Children = null,
    bool IsGenerated = false
);

/// <summary>
/// A sequence built from a draft. <see cref="Steps"/> has one entry per step of <see cref="Sequence"/>, in the same
/// order, so a running position (its top-level index) maps back to the draft step without any ids in the runtime.
/// </summary>
public sealed record BuiltSequence(Sequence Sequence, IReadOnlyList<BuiltStep> Steps);

/// <summary>
/// Turns a list of <see cref="SequenceStepDraft"/>s into a runtime <see cref="Sequence"/>: one existing step per
/// draft step, in the draft's order; for a Repeat a <see cref="RepeatStep"/> around a <see cref="SequenceGroup"/> of
/// its children (the repeat runs one child, the group is how that child becomes several); for a Multi-Rig block a
/// <see cref="ParallelStep"/> with one <see cref="RigTrackStep"/> per Rig Track. Every call makes new step objects,
/// so a run is never affected by a later edit. It checks everything itself and does not rely on what the editor
/// allowed.
/// <para>
/// A dither outside a Multi-Rig block needs no coordination group and no safe points: it runs when its guider, mount
/// and camera are free, as the runtime defines for a dither outside a coordination group. Inside a Multi-Rig block it
/// would have to wait for every track's exposure to be at a safe point before the shared mount moves, which this
/// builder does not (yet) compile, so a dither, like everything else that moves the shared mount or the guider
/// (slewing, starting and stopping guiding), is not allowed in a Rig Track. The tracks run next to each other with
/// the camera of their rig as their only equipment, and so cannot get in each other's way.
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
    public const string MultiRigName = "Multi-Rig Imaging";

    /// <summary>Describes a step for display. Never throws; a missing device is shown by its id or as "no camera".</summary>
    public static StepDescription Describe(DeviceRegistry registry, SequenceStepDraft step, SequenceDraftContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(step);

        return step switch
        {
            ExposureStepDraft e => new("Exposure", $"{DeviceName(registry, e.CameraId, "no camera")} · {Seconds(e.Seconds)}"),
            RigExposureStepDraft e => new("Exposure", Seconds(e.Seconds)),
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
            MultiRigStepDraft m => new(
                MultiRigName,
                (m.Tracks.Count == 0 ? "no rig tracks" : m.Tracks.Count == 1 ? "1 rig track" : $"{m.Tracks.Count} rig tracks")
                + (m.DitherPolicy is { Enabled: true } policy ? "\n" + DescribePolicy(policy, context) : string.Empty)),
            _ => new(step.Kind.ToString(), string.Empty),
        };
    }

    /// <summary>
    /// Describes a Rig Track: the rig's name, and its camera. A rig that is not selected, or not there, is described
    /// as such, with the id it was selected by.
    /// </summary>
    public static StepDescription DescribeTrack(DeviceRegistry registry, RigTrackDraft track, SequenceDraftContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(track);

        if (track.RigId is not { } rigId)
        {
            return new("Rig Track", "no rig selected");
        }

        return TryGetRig(context, rigId, out var rig)
            ? new(rig.Name, DeviceName(registry, rig.CameraId, "no camera"))
            : new(rigId.Value, "rig not available");
    }

    // "Dither every 3 Wide Rig frames · 1.5 px · settle ≤ 0.5 px for 1 s"
    private static string DescribePolicy(MultiRigDitherPolicyDraft policy, SequenceDraftContext? context)
    {
        var rig = policy.TriggerRigId is { } id
            ? TryGetRig(context, id, out var found) ? found.Name : id.Value
            : "no rig";
        var when = policy.EveryNFrames == 1 ? $"after every {rig} frame" : $"every {policy.EveryNFrames} {rig} frames";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Dither {when} · {policy.AmplitudePixels:0.##} px · settle ≤ {policy.SettleThresholdPixels:0.##} px for {policy.SettleStableSeconds:0.##} s");
    }

    /// <summary>The number of a step as shown to the user: "2" for a top-level step, "2.1" for the first one in step 2.</summary>
    public static string Label(params int[] path) =>
        string.Join('.', path.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The devices a draft needs for running: those its steps name, and the camera of each rig of a Multi-Rig block.
    /// Only what is actually used; the shared equipment of the session is not needed unless a step uses it.
    /// </summary>
    public static IReadOnlyCollection<DeviceId> RequiredDeviceIds(IEnumerable<SequenceStepDraft> steps, SequenceDraftContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var ids = new List<DeviceId>();
        foreach (var step in steps)
        {
            if (step is MultiRigStepDraft multiRig)
            {
                foreach (var track in multiRig.Tracks)
                {
                    if (track.RigId is { } rigId && TryGetRig(context, rigId, out var rig))
                    {
                        ids.Add(rig.CameraId);
                    }

                    ids.AddRange(track.Steps.SelectMany(inner => inner.DeviceIds));
                }

                // A dither policy moves the shared mount and uses the shared guider; without one they are not used.
                if (multiRig.DitherPolicy is { Enabled: true } && context?.Shared is { } shared)
                {
                    ids.AddRange(new[] { shared.MountId, shared.GuiderId }.OfType<DeviceId>());
                }
            }
            else
            {
                ids.AddRange(step.DeviceIds);
            }
        }

        return ids.Distinct().ToList();
    }

    /// <summary>Checks the whole draft, containers and tracks included, without building anything.</summary>
    public static DraftValidation Validate(
        DeviceRegistry registry, IReadOnlyList<SequenceStepDraft> steps, SequenceDraftContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(steps);
        return new Validator(registry, context).Run(steps);
    }

    /// <summary>The problems of <paramref name="validation"/> as sentences naming the step, in step order.</summary>
    public static IReadOnlyList<string> Sentences(IReadOnlyList<SequenceStepDraft> steps, DraftValidation validation)
    {
        var sentences = new List<string>(validation.SequenceProblems);
        sentences.AddRange(validation.SharedProblems ?? []);

        void Add(Guid id, string label, string title) =>
            sentences.AddRange(validation.ProblemsOf(id).Select(p => $"Step {label} ({title}): {p}"));

        void AddStep(SequenceStepDraft step, int[] path)
        {
            Add(step.Id, Label(path), TitleOf(step.Kind));
            switch (step)
            {
                case RepeatStepDraft repeat:
                    for (var j = 0; j < repeat.Children.Count; j++)
                    {
                        AddStep(repeat.Children[j], [.. path, j]);
                    }

                    break;
                case MultiRigStepDraft multiRig:
                    for (var t = 0; t < multiRig.Tracks.Count; t++)
                    {
                        var track = multiRig.Tracks[t];
                        Add(track.Id, Label([.. path, t]), "Rig Track");
                        for (var s = 0; s < track.Steps.Count; s++)
                        {
                            AddStep(track.Steps[s], [.. path, t, s]);
                        }
                    }

                    break;
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            AddStep(steps[i], [i]);
        }

        return sentences;
    }

    /// <exception cref="SequenceConfigurationException">The draft is not valid.</exception>
    public static BuiltSequence Build(
        DeviceRegistry registry, IReadOnlyList<SequenceStepDraft> steps, SequenceDraftContext? context = null)
    {
        var validation = Validate(registry, steps, context);
        if (!validation.IsValid)
        {
            throw new SequenceConfigurationException(Sentences(steps, validation));
        }

        try
        {
            var built = steps.Select(step => BuildStep(registry, step, context, null, null, null)).ToList();
            return new BuiltSequence(new Sequence(SequenceName, built.Select(b => b.Step)), built);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // The steps validate their own arguments too; anything the checks above missed ends up here.
            throw new SequenceConfigurationException([UserFacingError.Describe(ex)]);
        }
    }

    // What a Multi-Rig block with a dither policy is orchestrated with.
    private sealed record Orchestration(
        CoordinationGroupId Group,
        MultiRigDitherPolicyDraft Policy,
        DeviceId Mount,
        DeviceId Guider,
        IReadOnlyList<DeviceId> Cameras
    );

    // rigCamera: the camera of the rig of the track the step is in; null outside a track. orchestration: that of the block
    // the step is in, if its policy dithers; counter: the frame counter of the track, if this is the trigger rig's.
    private static BuiltStep BuildStep(
        DeviceRegistry registry,
        SequenceStepDraft step,
        SequenceDraftContext? context,
        DeviceId? rigCamera,
        Orchestration? orchestration,
        FrameCounter? counter)
    {
        var description = Describe(registry, step, context);
        switch (step)
        {
            case MultiRigStepDraft multiRig:
            {
                var orchestrated = Orchestrate(multiRig, context);
                var tracks = multiRig.Tracks.Select(track => BuildTrack(registry, track, context, orchestrated)).ToList();

                // With a policy the tracks are the participants of a coordination group, so that a dither of the
                // shared mount can wait for every one of them. Without one they have nothing to wait for.
                var parallel = new ParallelStep(MultiRigName, tracks.Select(t => t.Step), orchestrated?.Group);
                return new BuiltStep(step.Id, description, parallel, tracks);
            }
            case RepeatStepDraft repeat:
            {
                // A RepeatStep repeats one child; the group makes the children of the draft one.
                var children = BuildSteps(registry, repeat.Children, context, rigCamera, orchestration, counter);
                var body = new SequenceGroup(RepeatBodyName, children.Select(child => child.Step));
                return new BuiltStep(step.Id, description, new RepeatStep(repeat.Count, body), children);
            }
            default:
                return new BuiltStep(step.Id, description, CreateLeaf(registry, step, rigCamera));
        }
    }

    // The steps of a track or of a Repeat, and with a policy the orchestration between them: a safe point after each
    // exposure and each delay (where the track is between two things it does, and can wait for a dither), and after
    // each exposure of the trigger rig the step that counts it. Safe points only hold a track back while a dither is
    // pending; otherwise they cost nothing, so the tracks stay independent of each other.
    private static List<BuiltStep> BuildSteps(
        DeviceRegistry registry,
        IReadOnlyList<SequenceStepDraft> steps,
        SequenceDraftContext? context,
        DeviceId? rigCamera,
        Orchestration? orchestration,
        FrameCounter? counter)
    {
        var built = new List<BuiltStep>();
        foreach (var step in steps)
        {
            built.Add(BuildStep(registry, step, context, rigCamera, orchestration, counter));
            if (orchestration is null)
            {
                continue;
            }

            if (step is RigExposureStepDraft && counter is not null)
            {
                built.Add(TriggerStep(registry, orchestration, counter));
            }

            if (step is RigExposureStepDraft or DelayStepDraft)
            {
                built.Add(Generated(new SafePointStep()));
            }
        }

        return built;
    }

    private static BuiltStep Generated(ISequenceStep step, IReadOnlyList<BuiltStep>? children = null) =>
        new(Guid.Empty, new StepDescription(step.Name, string.Empty), step, children, IsGenerated: true);

    private static BuiltStep TriggerStep(DeviceRegistry registry, Orchestration orchestration, FrameCounter counter)
    {
        var policy = orchestration.Policy;
        var dither = new DitherAction(
            registry, orchestration.Guider, orchestration.Mount, orchestration.Cameras, policy.AmplitudePixels,
            new GuidingSettleOptions(
                policy.SettleThresholdPixels,
                TimeSpan.FromSeconds(policy.SettleStableSeconds),
                TimeSpan.FromSeconds(policy.SettleTimeoutSeconds)));
        return Generated(new DitherEveryNthFrameStep(counter, policy.EveryNFrames, dither), [Generated(dither)]);
    }

    // Validated before: an enabled policy has a trigger rig of the block, and shared equipment.
    private static Orchestration? Orchestrate(MultiRigStepDraft multiRig, SequenceDraftContext? context)
    {
        if (multiRig.DitherPolicy is not { Enabled: true } policy)
        {
            return null;
        }

        var cameras = multiRig.Tracks
            .Select(track => TryGetRig(context, track.RigId!.Value, out var rig) ? rig.CameraId : (DeviceId?)null)
            .OfType<DeviceId>()
            .Distinct()
            .ToList();
        return new Orchestration(
            new CoordinationGroupId($"multirig.{multiRig.Id:N}"), policy, context!.Shared!.MountId!.Value,
            context.Shared.GuiderId!.Value, cameras);
    }

    private static BuiltStep BuildTrack(
        DeviceRegistry registry, RigTrackDraft track, SequenceDraftContext? context, Orchestration? orchestration)
    {
        // Validated before: the rig is selected and there.
        TryGetRig(context, track.RigId!.Value, out var rig);
        var counter = orchestration is not null && orchestration.Policy.TriggerRigId == track.RigId ? new FrameCounter() : null;
        var steps = BuildSteps(registry, track.Steps, context, rig.CameraId, orchestration, counter);
        var runtime = new RigTrackStep(track.Id, rig.Name, steps.Select(step => step.Step), counter);
        return new BuiltStep(track.Id, DescribeTrack(registry, track, context), runtime, steps);
    }

    private static ISequenceStep CreateLeaf(DeviceRegistry registry, SequenceStepDraft step, DeviceId? rigCamera) => step switch
    {
        ExposureStepDraft e => new CameraExposureAction(registry, e.CameraId!.Value, TimeSpan.FromSeconds(e.Seconds)),
        RigExposureStepDraft e => new CameraExposureAction(registry, rigCamera!.Value, TimeSpan.FromSeconds(e.Seconds)),
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

    private static bool TryGetRig(SequenceDraftContext? context, RigId id, out Rig rig)
    {
        if (context?.Rigs is { } rigs && rigs.TryGet(id, out var found) && found is not null)
        {
            rig = found;
            return true;
        }

        rig = null!;
        return false;
    }

    // The checks of one validation run, with what has been found so far.
    private sealed class Validator(DeviceRegistry registry, SequenceDraftContext? context)
    {
        // What the last step that touched a guider did to it, where, and in which pass over which Repeat body (-1: none).
        private readonly record struct GuidingFact(bool IsGuiding, string Label, int Scope, int Pass);

        private readonly Dictionary<Guid, List<string>> _problems = new();
        private readonly List<Guid> _ids = [];
        private readonly Dictionary<DeviceId, GuidingFact> _guiding = new();
        private readonly SharedEquipmentDraft? _shared = context?.Shared;

        public DraftValidation Run(IReadOnlyList<SequenceStepDraft> steps)
        {
            var sequenceProblems = new List<string>();
            var sharedProblems = new List<string>();

            if (steps.Count == 0)
            {
                sequenceProblems.Add("The sequence has no steps.");
            }

            if (_shared is not null)
            {
                CheckDevice<IMount>(_shared.MountId, "mount", sharedProblems, required: false, shared: true);
                CheckDevice<IGuider>(_shared.GuiderId, "guider", sharedProblems, required: false, shared: true);
            }

            for (var i = 0; i < steps.Count; i++)
            {
                TopLevel(steps[i], i);
            }

            if (_ids.Distinct().Count() != _ids.Count)
            {
                sequenceProblems.Add("Two steps share the same id.");
            }

            return new DraftValidation(
                sequenceProblems,
                _problems.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value),
                sharedProblems);
        }

        private void Report(Guid id, string problem)
        {
            if (!_problems.TryGetValue(id, out var list))
            {
                _problems[id] = list = [];
            }

            if (!list.Contains(problem))
            {
                list.Add(problem);
            }
        }

        private void TopLevel(SequenceStepDraft step, int index)
        {
            _ids.Add(step.Id);
            switch (step)
            {
                case MultiRigStepDraft multiRig:
                    MultiRig(multiRig, Label(index));
                    break;
                case RepeatStepDraft repeat:
                    Repeat(repeat, index, inTrack: false);
                    break;
                default:
                    Own(step, Label(index), -1, 0);
                    break;
            }
        }

        // A step outside tracks: its own values, the shared equipment, and what it does to the guiding.
        private void Own(SequenceStepDraft step, string label, int scope, int pass)
        {
            if (pass == 0)
            {
                var problems = new List<string>();
                ValidateStep(step, problems);
                problems.ForEach(p => Report(step.Id, p));
                SharedMismatches(step);
            }

            ValidateGuidingOrder(step, label, scope, pass, p => Report(step.Id, p));
        }

        private void Repeat(RepeatStepDraft repeat, int index, bool inTrack)
        {
            if (repeat.Count < 1)
            {
                Report(repeat.Id, "Repeat count must be at least 1.");
            }

            if (repeat.Children.Count == 0)
            {
                Report(repeat.Id, "Repeat must contain at least one step.");
            }

            if (inTrack)
            {
                foreach (var child in repeat.Children)
                {
                    _ids.Add(child.Id);
                    TrackLeaf(child);
                }

                return;
            }

            foreach (var child in repeat.Children)
            {
                _ids.Add(child.Id);
            }

            // The body is played once, and a second time if it runs again: the second pass meets what the first left.
            for (var pass = 0; pass < (repeat.Count > 1 ? 2 : 1); pass++)
            {
                for (var j = 0; j < repeat.Children.Count; j++)
                {
                    Own(repeat.Children[j], Label(index, j), index, pass);
                }
            }
        }

        private void MultiRig(MultiRigStepDraft multiRig, string label)
        {
            if (multiRig.Tracks.Count < 2)
            {
                Report(multiRig.Id, "Multi-Rig Imaging needs at least two Rig Tracks.");
            }

            DitherPolicy(multiRig, label);

            var rigs = new HashSet<RigId>();
            var cameras = new HashSet<DeviceId>();
            foreach (var track in multiRig.Tracks)
            {
                _ids.Add(track.Id);
                Track(track, rigs, cameras);
            }
        }

        // The dither policy of a block, when it is on: what a dither of the shared mount needs, and a trigger rig that has
        // frames to count. A policy that is off is not looked at.
        private void DitherPolicy(MultiRigStepDraft multiRig, string label)
        {
            if (multiRig.DitherPolicy is not { Enabled: true } policy)
            {
                return;
            }

            var problems = new List<string>();

            if (_shared?.MountId is null)
            {
                problems.Add("Dither needs a shared mount: select one in the shared equipment.");
            }

            if (_shared?.GuiderId is not { } guiderId)
            {
                problems.Add("Dither needs a shared guider: select one in the shared equipment.");
            }
            else if (registry.TryGet(guiderId, out var guider) && guider is IGuider)
            {
                if (guider is not IDitherGuider)
                {
                    problems.Add($"Guider '{guiderId}' does not support dithering.");
                }
                else if (guider is not IGuidingSettler)
                {
                    problems.Add($"Guider '{guiderId}' does not support settling.");
                }
            }

            if (policy.TriggerRigId is not { } trigger)
            {
                problems.Add("No trigger rig selected.");
            }
            else if (multiRig.Tracks.FirstOrDefault(track => track.RigId == trigger) is not { } triggerTrack)
            {
                problems.Add($"The trigger rig '{trigger}' is not a track of this block.");
            }
            else if (!HasExposureToCount(triggerTrack))
            {
                problems.Add($"The trigger rig '{trigger}' has no exposure to count: dithering would never start.");
            }

            if (policy.EveryNFrames < 1)
            {
                problems.Add("Dither interval must be at least 1 frame.");
            }

            var usable = IsPositive(policy.SettleThresholdPixels) && IsPositive(policy.SettleStableSeconds)
                && IsPositive(policy.SettleTimeoutSeconds);
            CheckPositive(policy.AmplitudePixels, "Dither amplitude", "px", problems);
            CheckPositive(policy.SettleThresholdPixels, "Settle threshold", "px", problems);
            CheckPositive(policy.SettleStableSeconds, "Settle stable time", "s", problems);
            CheckPositive(policy.SettleTimeoutSeconds, "Settle timeout", "s", problems);
            if (usable && policy.SettleTimeoutSeconds <= policy.SettleStableSeconds)
            {
                problems.Add("Settle timeout must be longer than the stable time.");
            }

            problems.ForEach(p => Report(multiRig.Id, p));

            // The dither is as much a dither of the shared guider as a Dither step: it needs guiding, which an earlier
            // Stop Guiding of the sequence has ended.
            if (_shared?.GuiderId is { } guiding)
            {
                ValidateGuidingOrder(
                    new DitherStepDraft(multiRig.Id, guiding, _shared.MountId, null, 1, 1, 1, 2),
                    label, -1, 0, p => Report(multiRig.Id, p));
            }
        }

        // An exposure the track will really make: one of its own, or one inside a Repeat that runs at least once.
        private static bool HasExposureToCount(RigTrackDraft track) =>
            track.Steps.Any(step => step is RigExposureStepDraft
                || step is RepeatStepDraft { Count: >= 1 } repeat && repeat.Children.Any(child => child is RigExposureStepDraft));

        private void Track(RigTrackDraft track, HashSet<RigId> rigs, HashSet<DeviceId> cameras)
        {
            if (track.RigId is not { } rigId)
            {
                Report(track.Id, "No rig selected.");
            }
            else if (!TryGetRig(context, rigId, out var rig))
            {
                Report(track.Id, $"The rig '{rigId}' is not available.");
            }
            else if (!rigs.Add(rigId))
            {
                Report(track.Id, $"The rig '{rigId}' is already used by another track.");
            }
            else if (!registry.TryGet(rig.CameraId, out var camera) || camera is not ICamera)
            {
                Report(track.Id, $"The camera '{rig.CameraId}' of rig '{rigId}' is not available.");
            }
            else if (!cameras.Add(rig.CameraId))
            {
                // Two rigs that name the same camera would expose it twice at once.
                Report(track.Id, $"The camera '{rig.CameraId}' is already used by another track.");
            }

            if (track.Steps.Count == 0)
            {
                Report(track.Id, "A Rig Track needs at least one step.");
            }

            foreach (var step in track.Steps)
            {
                _ids.Add(step.Id);
                switch (step)
                {
                    case RepeatStepDraft repeat:
                        Repeat(repeat, 0, inTrack: true);
                        break;
                    default:
                        TrackLeaf(step);
                        break;
                }
            }
        }

        // What a Rig Track may hold: exposures with the rig camera, delays, and Repeats of those.
        private void TrackLeaf(SequenceStepDraft step)
        {
            var problems = new List<string>();
            switch (step)
            {
                case RigExposureStepDraft e:
                    CheckDuration(e.Seconds, "Exposure", problems);
                    break;
                case DelayStepDraft d:
                    CheckDuration(d.Seconds, "Delay", problems);
                    break;
                case ExposureStepDraft:
                    problems.Add("Use an exposure of the track here: its camera is the camera of the rig.");
                    break;
                case SlewStepDraft:
                    problems.Add("Slewing moves the shared mount and cannot be done inside a Rig Track.");
                    break;
                case StartGuidingStepDraft or StopGuidingStepDraft:
                    problems.Add("Guiding is shared by the whole session and cannot be started or stopped inside a Rig Track.");
                    break;
                case DitherStepDraft:
                    problems.Add("Dither is not available inside Multi-Rig Imaging yet: it has to wait until every rig is at a safe point.");
                    break;
                case MultiRigStepDraft:
                    problems.Add("Multi-Rig Imaging cannot be placed inside a Rig Track.");
                    break;
                case RepeatStepDraft:
                    problems.Add("A Repeat inside a Rig Track cannot contain another Repeat.");
                    break;
                default:
                    problems.Add($"Unsupported step '{step.GetType().Name}'.");
                    break;
            }

            problems.ForEach(p => Report(step.Id, p));
        }

        private void ValidateStep(SequenceStepDraft step, List<string> problems)
        {
            switch (step)
            {
                case ExposureStepDraft e:
                    CheckDevice<ICamera>(e.CameraId, "camera", problems);
                    CheckDuration(e.Seconds, "Exposure", problems);
                    break;
                case RigExposureStepDraft:
                    problems.Add("An exposure with the camera of a rig can only be used inside a Rig Track.");
                    break;
                case DelayStepDraft d:
                    CheckDuration(d.Seconds, "Delay", problems);
                    break;
                case SlewStepDraft s:
                    CheckDevice<IMount>(s.MountId, "mount", problems);
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
                    CheckDevice<IGuider>(g.GuiderId, "guider", problems);
                    break;
                case StopGuidingStepDraft g:
                    CheckDevice<IGuider>(g.GuiderId, "guider", problems);
                    break;
                case DitherStepDraft d:
                    ValidateDither(d, problems);
                    break;
                case MultiRigStepDraft:
                    problems.Add("Multi-Rig Imaging can only be placed at the top level of a sequence.");
                    break;
                default:
                    problems.Add($"Unsupported step '{step.GetType().Name}'.");
                    break;
            }
        }

        // What DitherAction and GuidingSettleOptions require, plus what the dither command asks of the guider when it runs.
        private void ValidateDither(DitherStepDraft d, List<string> problems)
        {
            CheckDevice<IGuider>(d.GuiderId, "guider", problems);
            CheckDevice<IMount>(d.MountId, "mount", problems);
            CheckDevice<ICamera>(d.CameraId, "camera", problems);

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

        // A step that moves the mount or runs the guider must use the mount and guider the session says it shares.
        private void SharedMismatches(SequenceStepDraft step)
        {
            if (_shared is null)
            {
                return;
            }

            void Mismatch(DeviceId? used, DeviceId? shared, string kind)
            {
                if (used is { } usedId && shared is { } sharedId && usedId != sharedId)
                {
                    Report(step.Id, $"The {kind} '{usedId}' is not the session's shared {kind} '{sharedId}'.");
                }
            }

            switch (step)
            {
                case SlewStepDraft s:
                    Mismatch(s.MountId, _shared.MountId, "mount");
                    break;
                case StartGuidingStepDraft g:
                    Mismatch(g.GuiderId, _shared.GuiderId, "guider");
                    break;
                case StopGuidingStepDraft g:
                    Mismatch(g.GuiderId, _shared.GuiderId, "guider");
                    break;
                case DitherStepDraft d:
                    Mismatch(d.GuiderId, _shared.GuiderId, "guider");
                    Mismatch(d.MountId, _shared.MountId, "mount");
                    break;
            }
        }

        // Start needs a guider that is not guiding, stop and dither need one that is. That is only knowable from what
        // earlier steps of the draft did, so only a contradiction with an earlier step is reported.
        private void ValidateGuidingOrder(SequenceStepDraft step, string label, int scope, int pass, Action<string> report)
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
                    if (_guiding.TryGetValue(id, out var started) && started.IsGuiding)
                    {
                        Report(started, $"Guiding was already started by {Where(started)}.");
                    }

                    _guiding[id] = new GuidingFact(true, label, scope, pass);
                    break;
                case StopGuidingStepDraft { GuiderId: { } id }:
                    if (_guiding.TryGetValue(id, out var stopped) && !stopped.IsGuiding)
                    {
                        Report(stopped, $"Guiding was already stopped by {Where(stopped)}.");
                    }

                    _guiding[id] = new GuidingFact(false, label, scope, pass);
                    break;
                case DitherStepDraft { GuiderId: { } id }:
                    if (_guiding.TryGetValue(id, out var state) && !state.IsGuiding)
                    {
                        Report(state, $"Dither needs guiding, but {Where(state)} stopped it.");
                    }

                    break;
            }
        }

        private void CheckDevice<T>(DeviceId? id, string kind, List<string> problems, bool required = true, bool shared = false)
            where T : class, IDevice
        {
            var what = shared ? $"shared {kind}" : kind;
            if (id is not { } deviceId)
            {
                if (required)
                {
                    problems.Add($"No {kind} selected.");
                }
            }
            else if (!registry.TryGet(deviceId, out var device) || device is null)
            {
                problems.Add($"The {what} '{deviceId}' is not available.");
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
        SequenceStepKind.RigExposure => "Exposure",
        SequenceStepKind.Delay => "Delay",
        SequenceStepKind.Slew => "Slew",
        SequenceStepKind.StartGuiding => "Start Guiding",
        SequenceStepKind.StopGuiding => "Stop Guiding",
        SequenceStepKind.Dither => "Dither",
        SequenceStepKind.Repeat => "Repeat",
        SequenceStepKind.MultiRig => MultiRigName,
        SequenceStepKind.RigTrack => "Rig Track",
        _ => kind.ToString(),
    };
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Focusing;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;
using Sidera.Runtime.Sequencing;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop;

/// <summary>The draft cannot make a sequence; <see cref="Problems"/> say why, one short sentence each.</summary>
public sealed class SequenceConfigurationException(IReadOnlyList<string> problems)
    : InvalidOperationException(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>How a step is shown: a title, and a one-line summary of its parameters.</summary>
public sealed record StepDescription(string Title, string Summary);

/// <summary>
/// What a draft is checked against besides the devices: the rigs a Rig Track can select, the equipment the session
/// shares, and what autofocus measures focus with and reports its progress to. All are optional; without rigs no rig is
/// available, without shared equipment nothing is compared, without a focus metric autofocus is not available.
/// </summary>
public sealed record SequenceDraftContext(
    RigRegistry? Rigs = null,
    SharedEquipmentDraft? Shared = null,
    IFocusMetricProvider? FocusMetrics = null,
    IEventPublisher? Events = null,
    ILoggerFactory? Loggers = null,
    IAcquisitionDefaultsSource? AcquisitionDefaults = null,
    Sidera.Runtime.Astrometry.PlateSolveService? PlateSolving = null,
    Func<Sidera.Core.Astrometry.PlateSolveDefaults>? PlateSolveDefaults = null,
    Sidera.Runtime.Astrometry.RotationService? Rotation = null
);

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
    bool IsGenerated = false,
    AutofocusOrigin? AutofocusOrigin = null
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
    public static StepDescription Describe(
        DeviceRegistry registry, SequenceStepDraft step, SequenceDraftContext? context = null, Rig? rig = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(step);

        return step switch
        {
            SlewAndCenterStepDraft c => new(c.TargetName is { } framed ? $"Slew & Center · {framed}" : "Slew & Center", string.Create(
                CultureInfo.InvariantCulture,
                $"RA {c.RightAscensionHours:0.###} h · Dec {c.DeclinationDegrees:+0.##;-0.##;0}° · within {c.ToleranceArcseconds:0.##} arcsec · {c.MaxAttempts} attempts")),
            RotateToAngleStepDraft r => new("Rotate to Angle", string.Create(
                CultureInfo.InvariantCulture, $"{r.RigId?.Value ?? "no rig"} · sky rotation {r.SkyRotationDegrees:0.##}° · does not solve")),
            RotateAndVerifyStepDraft r => new("Rotate & Verify", string.Create(
                CultureInfo.InvariantCulture, $"{r.RigId?.Value ?? "no rig"} · sky rotation {r.SkyRotationDegrees:0.##}° ± {r.ToleranceDegrees:0.##}° · {r.MaxAttempts} attempts")),
            CenterAndRotateStepDraft c => new(c.TargetName is { } rotated ? $"Center & Rotate · {rotated}" : "Center & Rotate", string.Create(
                CultureInfo.InvariantCulture,
                $"RA {c.RightAscensionHours:0.###} h · Dec {c.DeclinationDegrees:+0.##;-0.##;0}° · within {c.ToleranceArcseconds:0.##} arcsec · rotation {c.SkyRotationDegrees:0.##}° ± {c.RotationToleranceDegrees:0.##}°")),
            SyncMountStepDraft m => new("Sync Mount to Solved Position", $"{DeviceName(registry, m.MountId, "no mount")} · uses the last successful plate solve"),
            PlateSolveStepDraft p => new("Plate Solve", $"{p.RigId?.Value ?? "no rig"} · {Seconds(p.ExposureSeconds)}"),
            ExposureStepDraft e => new("Exposure", $"{DeviceName(registry, e.CameraId, "no camera")} · {Seconds(e.Seconds)}{AcquisitionSummary(e.Acquisition, registry, e.CameraId)}"),
            RigExposureStepDraft e => new("Exposure", $"{Seconds(e.Seconds)}{AcquisitionSummary(e.Acquisition, registry, rig?.CameraId)}"),
            DelayStepDraft d => new("Delay", Seconds(d.Seconds)),
            SlewStepDraft s => new("Slew", string.Create(
                CultureInfo.InvariantCulture,
                $"RA {s.RightAscensionHours:0.###} h · Dec {s.DeclinationDegrees:+0.##;-0.##;0}°")),
            StartGuidingStepDraft g => new("Start Guiding", DeviceName(registry, g.GuiderId, "no guider")),
            StopGuidingStepDraft g => new("Stop Guiding", DeviceName(registry, g.GuiderId, "no guider")),
            DitherStepDraft d => new("Dither", string.Create(
                CultureInfo.InvariantCulture,
                $"{d.AmplitudePixels:0.##} px · settle ≤ {d.SettleThresholdPixels:0.##} px for {d.SettleStableSeconds:0.##} s")),
            MoveFocuserStepDraft f => new("Move Focuser", string.Create(
                CultureInfo.InvariantCulture, $"{DeviceName(registry, f.FocuserId, "no focuser")} · {f.Position}")),
            ChangeFilterStepDraft c => new("Change Filter",
                $"{DeviceName(registry, c.FilterWheelId, "no filter wheel")} · {FilterName(registry, c.FilterWheelId, c.SlotIndex)}"),
            RigMoveFocuserStepDraft f => new("Move Focuser", RigFocuserText(registry, rig, f.Position)),
            RigChangeFilterStepDraft c => new("Change Filter", RigFilterText(registry, rig, c.SlotIndex)),
            AutofocusStepDraft a => new("Autofocus", $"{AutofocusRigName(context, a.RigId)} · {AutofocusSettings(a.ExposureSeconds, a.StepSize, a.SampleCount)}"),
            RigAutofocusStepDraft a => new("Autofocus", rig is not null && rig.FocuserId is null
                ? "the rig has no focuser"
                : AutofocusSettings(a.ExposureSeconds, a.StepSize, a.SampleCount)),
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

        if (!TryGetRig(context, rigId, out var rig))
        {
            return new(rigId.Value, "rig not available");
        }

        // A track whose rig focuses by itself says when; one that does not says nothing.
        var summary = DeviceName(registry, rig.CameraId, "no camera");
        if (track.AutofocusPolicy is { Enabled: true } policy)
        {
            var when = policy is { AtTrackStart: true, AfterFilterChange: true } ? "track start + filter change"
                : policy.AtTrackStart ? "track start"
                : policy.AfterFilterChange ? "filter change"
                : "no trigger";
            summary += $"\nAutofocus: {when}";
        }

        return new(rig.Name, summary);
    }

    // The name of a slot of a wheel, or "slot 4" when the wheel is not known or has no such slot.
    private static string FilterName(DeviceRegistry registry, DeviceId? wheelId, int slotIndex) =>
        wheelId is { } id && registry.TryGet(id, out var device) && device is IFilterWheel wheel
        && slotIndex >= 0 && slotIndex < wheel.Slots.Count
            ? wheel.Slots[slotIndex].Name
            : string.Create(CultureInfo.InvariantCulture, $"slot {slotIndex}");

    // "1 s · step 400 · 7 samples"
    private static string AutofocusSettings(double exposureSeconds, int stepSize, int sampleCount) =>
        string.Create(CultureInfo.InvariantCulture, $"{exposureSeconds:0.##} s · step {stepSize} · {sampleCount} samples");

    private static string AutofocusRigName(SequenceDraftContext? context, RigId? rigId) =>
        rigId is not { } id ? "no rig" : TryGetRig(context, id, out var rig) ? rig.Name : id.Value;

    // "EAF Main · 18350", or what is missing: no rig known, or a rig without a focuser.
    private static string RigFocuserText(DeviceRegistry registry, Rig? rig, int position)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{position}");
        return rig is null ? text
            : rig.FocuserId is not { } focuser ? "the rig has no focuser"
            : $"{DeviceName(registry, focuser, "no focuser")} · {text}";
    }

    private static string RigFilterText(DeviceRegistry registry, Rig? rig, int slotIndex) =>
        rig is null ? string.Create(CultureInfo.InvariantCulture, $"slot {slotIndex}")
        : rig.FilterWheelId is not { } wheel ? "the rig has no filter wheel"
        : $"{DeviceName(registry, wheel, "no filter wheel")} · {FilterName(registry, wheel, slotIndex)}";

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
    /// The devices a draft needs for running, resolved from the rigs the steps work for (see <see cref="StepScopes"/>): a rig-local step needs the devices of its rig (its mount
    /// and not another rig's), a step that names a device needs that one, and a Multi-Rig block needs the camera of each rig of its tracks and what the steps of the track use.
    /// Only what is actually used.
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
                    TryGetRig(context, track.RigId ?? default, out var rig);
                    if (track.RigId is not null && rig is not null)
                    {
                        ids.Add(rig.CameraId);
                        foreach (var inner in track.Steps)
                        {
                            ids.AddRange(StepScopes.Resolve(inner, context, rig).Select(d => d.Device));
                        }

                        if (rig.FocuserId is { } autofocusFocuser && track.AutofocusPolicy is { IsActive: true })
                        {
                            ids.Add(autofocusFocuser);
                        }
                    }
                    else
                    {
                        ids.AddRange(track.Steps.SelectMany(inner => inner.DeviceIds));
                    }
                }

                // A dither policy moves the mount of the trigger rig and uses its guider; without one they are not used.
                if (multiRig.DitherPolicy is { Enabled: true } policy && DitherDomain(multiRig, policy, context) is { } domain)
                {
                    ids.AddRange(new[] { domain.Mount, domain.Guider }.OfType<DeviceId>());
                }
            }
            else
            {
                ids.AddRange(step.DeviceIds);
                ids.AddRange(StepScopes.Resolve(step, context).Select(d => d.Device));
            }
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// What a Multi-Rig dither policy moves and uses: the mount and the guider of its trigger rig (for a rig that names none, the session's shared ones), and the tracks whose rigs sit on that
    /// mount. Rigs on another mount are not disturbed by it and are not asked to wait for it. <c>null</c> when the policy has no usable trigger rig.
    /// </summary>
    public static DitherDomain? DitherDomain(MultiRigStepDraft multiRig, MultiRigDitherPolicyDraft policy, SequenceDraftContext? context)
    {
        if (policy.TriggerRigId is not { } trigger || !TryGetRig(context, trigger, out var triggerRig))
        {
            return null;
        }

        var mount = StepScopes.EffectiveMount(triggerRig, null, context?.Shared);
        var guider = StepScopes.EffectiveGuider(triggerRig, null, context?.Shared);
        var affected = multiRig.Tracks
            .Where(track => track.RigId is { } id && TryGetRig(context, id, out var rig)
                && (id == trigger || (mount is not null && StepScopes.EffectiveMount(rig, null, context?.Shared) == mount)))
            .Select(track => track.Id)
            .ToList();
        return new DitherDomain(mount, guider, affected);
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
            var built = steps.Select(step => BuildStep(registry, step, context, null, null, null, null)).ToList();
            return new BuiltSequence(new Sequence(SequenceName, built.Select(b => b.Step)), built);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // The steps validate their own arguments too; anything the checks above missed ends up here.
            throw new SequenceConfigurationException([UserFacingError.Describe(ex)]);
        }
    }

    // What the autofocus policy of a Rig Track asks for, once it is known to be enabled with a trigger and the rig is known.
    private sealed record AutofocusPlan(Rig Rig, AutofocusOptions Options, bool AtTrackStart, bool AfterFilterChange);

    private static AutofocusPlan? PlanOf(RigTrackDraft track, Rig? rig) =>
        rig is not null && track.AutofocusPolicy is { IsActive: true } policy
            ? new AutofocusPlan(
                rig,
                new AutofocusOptions(TimeSpan.FromSeconds(policy.ExposureSeconds), policy.StepSize, policy.SampleCount),
                policy.AtTrackStart,
                policy.AfterFilterChange)
            : null;

    // The first step of the track that does something, in the order it runs: the first step, or the first one in the
    // Repeat when the track starts with a Repeat.
    private static SequenceStepDraft? FirstExecutableStep(IReadOnlyList<SequenceStepDraft> steps) =>
        steps.FirstOrDefault() is RepeatStepDraft repeat ? repeat.Children.FirstOrDefault() : steps.FirstOrDefault();

    // An autofocus the policy asks for: the existing action, labelled with why it runs, followed by the safe point that
    // every atomic step of an orchestrated track has. It is no draft step and has no id.
    private static List<BuiltStep> GeneratedAutofocus(
        DeviceRegistry registry, AutofocusPlan plan, SequenceDraftContext? context, Orchestration? orchestration, AutofocusOrigin origin)
    {
        var action = AutofocusAction.ForRig(
            registry, plan.Rig, plan.Options, context!.FocusMetrics!, context.Events, context.Loggers?.CreateLogger<AutofocusAction>(), context.AcquisitionDefaults);
        var description = new StepDescription(
            "Autofocus", origin == AutofocusOrigin.TrackStart ? "automatic · track start" : "automatic · after filter change");
        var built = new List<BuiltStep> { new(Guid.Empty, description, action, null, IsGenerated: true, AutofocusOrigin: origin) };
        if (orchestration is not null)
        {
            built.Add(Generated(new SafePointStep()));
        }

        return built;
    }

    // What a Multi-Rig block with a dither policy is orchestrated with.
    private sealed record Orchestration(
        CoordinationGroupId Group,
        MultiRigDitherPolicyDraft Policy,
        DeviceId Mount,
        DeviceId Guider,
        IReadOnlyList<DeviceId> Cameras,
        IReadOnlySet<Guid> Participants
    );

    // rig: the rig of the track the step is in; null outside a track. orchestration: that of the block
    // the step is in, if its policy dithers; counter: the frame counter of the track, if this is the trigger rig's.
    private static BuiltStep BuildStep(
        DeviceRegistry registry,
        SequenceStepDraft step,
        SequenceDraftContext? context,
        Rig? rig,
        Orchestration? orchestration,
        FrameCounter? counter,
        AutofocusPlan? autofocus)
    {
        var description = Describe(registry, step, context, rig);
        switch (step)
        {
            case MultiRigStepDraft multiRig:
            {
                var orchestrated = Orchestrate(multiRig, context);

                // Only the tracks on the mount that the policy dithers are orchestrated: they hold safe points and wait for the dither. A rig on another mount is not disturbed by it and runs on
                // as if the policy were not there.
                var tracks = multiRig.Tracks
                    .Select(track => BuildTrack(registry, track, context, orchestrated is { } o && o.Participants.Contains(track.Id) ? orchestrated : null))
                    .ToList();

                // With a policy the tracks of the dithered mount are the participants of a coordination group, so that a dither can wait for every one of them. Without one they have nothing to wait for.
                if (orchestrated is null || tracks.Count(t => orchestrated.Participants.Contains(t.DraftId)) == tracks.Count)
                {
                    var parallel = new ParallelStep(MultiRigName, tracks.Select(t => t.Step), orchestrated?.Group);
                    return new BuiltStep(step.Id, description, parallel, tracks);
                }

                var members = tracks.Where(t => orchestrated.Participants.Contains(t.DraftId)).ToList();
                var others = tracks.Where(t => !orchestrated.Participants.Contains(t.DraftId)).ToList();
                if (members.Count < 2)
                {
                    // The trigger rig is alone on its mount: nobody else has to wait for its dither.
                    return new BuiltStep(step.Id, description, new ParallelStep(MultiRigName, tracks.Select(t => t.Step)), tracks);
                }

                var group = new ParallelStep(MultiRigName + " (dithered mount)", members.Select(t => t.Step), orchestrated.Group);
                var branches = new List<BuiltStep> { Generated(group, members) };
                branches.AddRange(others);
                return new BuiltStep(step.Id, description, new ParallelStep(MultiRigName, branches.Select(b => b.Step)), branches);
            }
            case RepeatStepDraft repeat:
            {
                // A RepeatStep repeats one child; the group makes the children of the draft one.
                var children = BuildSteps(registry, repeat.Children, context, rig, orchestration, counter, autofocus);
                var body = new SequenceGroup(RepeatBodyName, children.Select(child => child.Step));
                return new BuiltStep(step.Id, description, new RepeatStep(repeat.Count, body), children);
            }
            default:
                return new BuiltStep(step.Id, description, CreateLeaf(registry, step, rig, context));
        }
    }

    // The steps of a track or of a Repeat, and with a policy the orchestration between them: a safe point after each
    // exposure, delay, focuser move and filter change (where the track is between two things it does, and can wait
    // for a dither: those are atomic, so a dither that becomes pending meanwhile waits for them to end), and after
    // each exposure of the trigger rig the step that counts it. Safe points only hold a track back while a dither is
    // pending; otherwise they cost nothing, so the tracks stay independent of each other.
    private static List<BuiltStep> BuildSteps(
        DeviceRegistry registry,
        IReadOnlyList<SequenceStepDraft> steps,
        SequenceDraftContext? context,
        Rig? rig,
        Orchestration? orchestration,
        FrameCounter? counter,
        AutofocusPlan? autofocus)
    {
        var built = new List<BuiltStep>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            built.Add(BuildStep(registry, step, context, rig, orchestration, counter, autofocus));

            if (orchestration is not null)
            {
                if (step is RigExposureStepDraft && counter is not null)
                {
                    built.Add(TriggerStep(registry, orchestration, counter, context?.Loggers));
                }

                if (step is RigExposureStepDraft or DelayStepDraft or RigMoveFocuserStepDraft or RigChangeFilterStepDraft
                    or RigAutofocusStepDraft)
                {
                    built.Add(Generated(new SafePointStep()));
                }
            }

            // After a Change Filter that went through (a step that failed or was cancelled does not get here), the
            // policy focuses before the next step, wherever this step runs: in a Repeat once per iteration. An explicit
            // autofocus right after it is that focus: the rule looks at the next step of the same list, nothing else.
            if (step is RigChangeFilterStepDraft && autofocus is { AfterFilterChange: true }
                && !(i + 1 < steps.Count && steps[i + 1] is RigAutofocusStepDraft))
            {
                built.AddRange(GeneratedAutofocus(registry, autofocus, context, orchestration, AutofocusOrigin.AfterFilterChange));
            }
        }

        return built;
    }

    private static BuiltStep Generated(ISequenceStep step, IReadOnlyList<BuiltStep>? children = null) =>
        new(Guid.Empty, new StepDescription(step.Name, string.Empty), step, children, IsGenerated: true);

    private static BuiltStep TriggerStep(
        DeviceRegistry registry, Orchestration orchestration, FrameCounter counter, ILoggerFactory? loggers)
    {
        var policy = orchestration.Policy;
        var dither = new DitherAction(
            registry, orchestration.Guider, orchestration.Mount, orchestration.Cameras, policy.AmplitudePixels,
            new GuidingSettleOptions(
                policy.SettleThresholdPixels,
                TimeSpan.FromSeconds(policy.SettleStableSeconds),
                TimeSpan.FromSeconds(policy.SettleTimeoutSeconds)),
            loggers?.CreateLogger<DitherAction>());
        return Generated(new DitherEveryNthFrameStep(counter, policy.EveryNFrames, dither), [Generated(dither)]);
    }

    // Validated before: an enabled policy has a trigger rig of the block, and shared equipment.
    private static Orchestration? Orchestrate(MultiRigStepDraft multiRig, SequenceDraftContext? context)
    {
        if (multiRig.DitherPolicy is not { Enabled: true } policy)
        {
            return null;
        }

        // Validated before: the trigger rig has a mount and a guider. The cameras that wait for a dither, and the tracks that take part in it, are those on the same mount.
        var domain = DitherDomain(multiRig, policy, context)!;
        var cameras = multiRig.Tracks
            .Where(track => domain.Tracks.Contains(track.Id))
            .Select(track => TryGetRig(context, track.RigId!.Value, out var rig) ? rig.CameraId : (DeviceId?)null)
            .OfType<DeviceId>()
            .Distinct()
            .ToList();
        return new Orchestration(
            new CoordinationGroupId($"multirig.{multiRig.Id:N}"), policy, domain.Mount!.Value, domain.Guider!.Value, cameras, domain.Tracks.ToHashSet());
    }

    private static BuiltStep BuildTrack(
        DeviceRegistry registry, RigTrackDraft track, SequenceDraftContext? context, Orchestration? orchestration)
    {
        // Validated before: the rig is selected and there.
        TryGetRig(context, track.RigId!.Value, out var rig);
        var counter = orchestration is not null && orchestration.Policy.TriggerRigId == track.RigId ? new FrameCounter() : null;
        var plan = PlanOf(track, rig);
        var steps = BuildSteps(registry, track.Steps, context, rig, orchestration, counter, plan);

        // At the start of the track, once: before the first step that does something, unless that is an autofocus.
        if (plan is { AtTrackStart: true } && FirstExecutableStep(track.Steps) is not RigAutofocusStepDraft)
        {
            steps.InsertRange(0, GeneratedAutofocus(registry, plan, context, orchestration, AutofocusOrigin.TrackStart));
        }

        var runtime = new RigTrackStep(
            track.Id, rig.Name, steps.Select(step => step.Step), counter, rig.Id, context?.Loggers?.CreateLogger<RigTrackStep>());
        return new BuiltStep(track.Id, DescribeTrack(registry, track, context), runtime, steps);
    }

    private static ISequenceStep CreateLeaf(DeviceRegistry registry, SequenceStepDraft step, Rig? rig, SequenceDraftContext? context) => step switch
    {
        // Validated before: the rig is there and has a focuser, and the context has something to measure focus with.
        SlewAndCenterStepDraft c => new SlewAndCenterAction(context!.PlateSolving!,
            TryGetRig(context, c.RigId!.Value, out var centerRig) ? centerRig : null!, StepScopes.EffectiveMount(centerRig, c.MountId, context.Shared)!.Value,
            new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees), c.ToleranceArcseconds, c.MaxAttempts,
            TimeSpan.FromSeconds(c.ExposureSeconds), context.PlateSolveDefaults?.Invoke() ?? new()),
        RotateToAngleStepDraft r => new RotateToAngleAction(context!.Rotation!, TryGetRig(context, r.RigId!.Value, out var rotateRig) ? rotateRig : null!, r.SkyRotationDegrees),
        RotateAndVerifyStepDraft r => new RotateAndVerifyAction(context!.Rotation!,
            TryGetRig(context, r.RigId!.Value, out var verifyRig) ? verifyRig : null!, StepScopes.EffectiveMount(verifyRig, null, context.Shared), r.SkyRotationDegrees, r.ToleranceDegrees, r.MaxAttempts,
            TimeSpan.FromSeconds(r.ExposureSeconds), context.PlateSolveDefaults?.Invoke() ?? new()),
        CenterAndRotateStepDraft c => new CenterAndRotateAction(context!.Rotation!,
            TryGetRig(context, c.RigId!.Value, out var crRig) ? crRig : null!, StepScopes.EffectiveMount(crRig, c.MountId, context.Shared)!.Value, new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees),
            c.SkyRotationDegrees, c.ToleranceArcseconds, c.RotationToleranceDegrees, c.MaxCenteringAttempts, c.MaxRotationAttempts, c.MaxRounds,
            TimeSpan.FromSeconds(c.ExposureSeconds), context.PlateSolveDefaults?.Invoke() ?? new()),
        SyncMountStepDraft m => new SyncMountToSolvedPositionAction(context!.PlateSolving!, m.MountId!.Value),
        PlateSolveStepDraft p => new PlateSolveAction(context!.PlateSolving!,
            TryGetRig(context, p.RigId!.Value, out var solveRig) ? solveRig : null!, StepScopes.EffectiveMount(solveRig, null, context.Shared),
            TimeSpan.FromSeconds(p.ExposureSeconds), context.PlateSolveDefaults?.Invoke() ?? new()),
        AutofocusStepDraft a => AutofocusAction.ForRig(
            registry, TryGetRig(context, a.RigId!.Value, out var autofocusRig) ? autofocusRig : null!,
            new AutofocusOptions(TimeSpan.FromSeconds(a.ExposureSeconds), a.StepSize, a.SampleCount),
            context!.FocusMetrics!, context.Events, context.Loggers?.CreateLogger<AutofocusAction>(), context.AcquisitionDefaults),
        RigAutofocusStepDraft a => AutofocusAction.ForRig(
            registry, rig!,
            new AutofocusOptions(TimeSpan.FromSeconds(a.ExposureSeconds), a.StepSize, a.SampleCount),
            context!.FocusMetrics!, context.Events, context.Loggers?.CreateLogger<AutofocusAction>(), context.AcquisitionDefaults),
        ExposureStepDraft e => new CameraExposureAction(
            registry, e.CameraId!.Value, TimeSpan.FromSeconds(e.Seconds), e.Acquisition, context?.AcquisitionDefaults,
            context?.Loggers?.CreateLogger<CameraExposureAction>()),
        RigExposureStepDraft e => new CameraExposureAction(
            registry, rig!.CameraId, TimeSpan.FromSeconds(e.Seconds), e.Acquisition, context?.AcquisitionDefaults,
            context?.Loggers?.CreateLogger<CameraExposureAction>()),
        MoveFocuserStepDraft f => new MoveFocuserAction(registry, f.FocuserId!.Value, f.Position),
        ChangeFilterStepDraft c => new ChangeFilterAction(registry, c.FilterWheelId!.Value, c.SlotIndex),
        // Resolved here, at build time: the track names a rig, and the rig names its focuser and its filter wheel.
        RigMoveFocuserStepDraft f => new MoveFocuserAction(registry, rig!.FocuserId!.Value, f.Position),
        RigChangeFilterStepDraft c => new ChangeFilterAction(registry, rig!.FilterWheelId!.Value, c.SlotIndex),
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
                TimeSpan.FromSeconds(d.SettleTimeoutSeconds)),
            context?.Loggers?.CreateLogger<DitherAction>()),
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

        // The rig of the Rig Track that is being checked, when it is selected and registered.
        private Rig? _trackRig;

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

            // A dither moves the mount of the trigger rig and uses its guider; a rig without its own uses the session's shared ones.
            var domain = DitherDomain(multiRig, policy, context);
            var triggerName = policy.TriggerRigId is { } triggerId && TryGetRig(context, triggerId, out var triggerRig) ? $"the rig '{triggerRig.Name}'" : "the trigger rig";
            if (policy.TriggerRigId is not null && domain is not null && domain.Mount is null)
            {
                problems.Add($"Dither needs a mount: {triggerName} has none. Give the rig a mount on the Equipment page.");
            }

            if (policy.TriggerRigId is not null && domain is not null && domain.Guider is null)
            {
                problems.Add($"Dither needs a guider: {triggerName} has none. Give the rig a guider on the Equipment page.");
            }
            else if (domain?.Guider is { } guiderId && registry.TryGet(guiderId, out var guider) && guider is IGuider)
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
            if (domain?.Guider is { } guiding)
            {
                ValidateGuidingOrder(
                    new DitherStepDraft(multiRig.Id, guiding, domain.Mount, null, 1, 1, 1, 2),
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

            _trackRig = track.RigId is { } selected && TryGetRig(context, selected, out var selectedRig) ? selectedRig : null;
            CheckAutofocusPolicy(track);
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

            _trackRig = null;
        }

        // What a Rig Track may hold: exposures with the rig camera, delays, moves of the rig focuser, changes of the
        // rig filter wheel, and Repeats of those.
        private void TrackLeaf(SequenceStepDraft step)
        {
            var problems = new List<string>();
            switch (step)
            {
                case RigMoveFocuserStepDraft f:
                    RigFocuser(f, problems);
                    break;
                case RigChangeFilterStepDraft c:
                    RigFilter(c, problems);
                    break;
                case RigAutofocusStepDraft a:
                    CheckAutofocus(_trackRig, a.ExposureSeconds, a.StepSize, a.SampleCount, problems);
                    break;
                case AutofocusStepDraft:
                    problems.Add("Use Autofocus of the track here: its rig is the rig of the track.");
                    break;
                case PlateSolveStepDraft or SlewAndCenterStepDraft or SyncMountStepDraft or RotateToAngleStepDraft or RotateAndVerifyStepDraft or CenterAndRotateStepDraft:
                    problems.Add("Plate solving steps move or synchronize the shared mount and must be outside a Rig Track.");
                    break;
                case MoveFocuserStepDraft:
                    problems.Add("Use Move Focuser of the track here: its focuser is the focuser of the rig.");
                    break;
                case ChangeFilterStepDraft:
                    problems.Add("Use Change Filter of the track here: its filter wheel is the filter wheel of the rig.");
                    break;
                case RigExposureStepDraft e:
                    CheckDuration(e.Seconds, "Exposure", problems);
                    CheckAcquisition(e.Acquisition, _trackRig?.CameraId, e.Seconds, problems);
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
                case SlewAndCenterStepDraft c:
                    CheckRigMount(c.RigId, c.MountId, problems);
                    CheckDuration(c.ExposureSeconds, "Solve exposure", problems);
                    if (!double.IsFinite(c.ToleranceArcseconds) || c.ToleranceArcseconds <= 0) problems.Add("The tolerance must be greater than zero.");
                    if (c.MaxAttempts is < 1 or > 100) problems.Add("The attempts must be from 1 to 100.");
                    try
                    {
                        _ = new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees);
                    }
                    catch (ArgumentException ex)
                    {
                        problems.Add(UserFacingError.Describe(ex));
                    }

                    if (context?.PlateSolving is null) problems.Add("No plate solver configured.");
                    if (c.RigId is not { } centerRigId || !TryGetRig(context, centerRigId, out var centerRig)) problems.Add("Select an available rig.");
                    else CheckDevice<ICamera>(centerRig.CameraId, "camera", problems);
                    break;
                case RotateToAngleStepDraft r:
                    CheckAngle(r.SkyRotationDegrees, problems);
                    CheckRotationRig(r.RigId, problems);
                    if (context?.Rotation is null) problems.Add("No plate solver configured.");
                    break;
                case RotateAndVerifyStepDraft r:
                    CheckAngle(r.SkyRotationDegrees, problems);
                    CheckRotationTolerance(r.ToleranceDegrees, r.MaxAttempts, problems);
                    CheckDuration(r.ExposureSeconds, "Solve exposure", problems);
                    CheckRotationRig(r.RigId, problems);
                    if (context?.Rotation is null) problems.Add("No plate solver configured.");
                    break;
                case CenterAndRotateStepDraft c:
                    CheckRigMount(c.RigId, c.MountId, problems);
                    CheckAngle(c.SkyRotationDegrees, problems);
                    CheckRotationTolerance(c.RotationToleranceDegrees, c.MaxRotationAttempts, problems);
                    CheckDuration(c.ExposureSeconds, "Solve exposure", problems);
                    if (!double.IsFinite(c.ToleranceArcseconds) || c.ToleranceArcseconds <= 0) problems.Add("The tolerance must be greater than zero.");
                    if (c.MaxCenteringAttempts is < 1 or > 100) problems.Add("The centering attempts must be from 1 to 100.");
                    if (c.MaxRounds is < 1 or > 20) problems.Add("The rounds must be from 1 to 20.");
                    try
                    {
                        _ = new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees);
                    }
                    catch (ArgumentException ex)
                    {
                        problems.Add(UserFacingError.Describe(ex));
                    }

                    CheckRotationRig(c.RigId, problems);
                    if (context?.Rotation is null) problems.Add("No plate solver configured.");
                    break;
                case SyncMountStepDraft m:
                    CheckDevice<IMount>(m.MountId, "mount", problems);
                    if (context?.PlateSolving is null) problems.Add("No plate solver configured.");
                    break;
                case PlateSolveStepDraft p:
                    CheckDuration(p.ExposureSeconds, "Solve exposure", problems);
                    if (context?.PlateSolving is null) problems.Add("No plate solver configured.");
                    if (p.RigId is not { } solveId || !TryGetRig(context, solveId, out var solveRig)) problems.Add("Select an available rig.");
                    else CheckDevice<ICamera>(solveRig.CameraId, "camera", problems);
                    break;
                case ExposureStepDraft e:
                    CheckDevice<ICamera>(e.CameraId, "camera", problems);
                    CheckDuration(e.Seconds, "Exposure", problems);
                    CheckAcquisition(e.Acquisition, e.CameraId, e.Seconds, problems);
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
                case MoveFocuserStepDraft f:
                    CheckDevice<IFocuser>(f.FocuserId, "focuser", problems);
                    CheckFocuserPosition(f.FocuserId, f.Position, problems);
                    break;
                case ChangeFilterStepDraft c:
                    CheckDevice<IFilterWheel>(c.FilterWheelId, "filter wheel", problems);
                    CheckSlot(c.FilterWheelId, c.SlotIndex, problems);
                    break;
                case AutofocusStepDraft a:
                    if (a.RigId is not { } autofocusRigId)
                    {
                        problems.Add("No rig selected.");
                    }
                    else if (!TryGetRig(context, autofocusRigId, out _))
                    {
                        problems.Add($"The rig '{autofocusRigId}' is not available.");
                    }

                    CheckAutofocus(
                        a.RigId is { } selected && TryGetRig(context, selected, out var found) ? found : null,
                        a.ExposureSeconds, a.StepSize, a.SampleCount, problems);
                    break;
                case RigAutofocusStepDraft:
                    problems.Add("An autofocus of a rig can only be used inside a Rig Track.");
                    break;
                case RigMoveFocuserStepDraft:
                    problems.Add("A focuser move of a rig can only be used inside a Rig Track.");
                    break;
                case RigChangeFilterStepDraft:
                    problems.Add("A filter change of a rig can only be used inside a Rig Track.");
                    break;
                case MultiRigStepDraft:
                    problems.Add("Multi-Rig Imaging can only be placed at the top level of a sequence.");
                    break;
                default:
                    problems.Add($"Unsupported step '{step.GetType().Name}'.");
                    break;
            }
        }

        // The policy of a track, when it is enabled: something to trigger it, settings an autofocus can run with, a rig
        // that can focus, and for a trigger on filter changes a filter wheel. A policy that is off is not looked at.
        private void CheckAutofocusPolicy(RigTrackDraft track)
        {
            if (track.AutofocusPolicy is not { Enabled: true } policy)
            {
                return;
            }

            var problems = new List<string>();
            if (!policy.AtTrackStart && !policy.AfterFilterChange)
            {
                problems.Add("Enable at least one Autofocus trigger.");
            }

            CheckAutofocus(_trackRig, policy.ExposureSeconds, policy.StepSize, policy.SampleCount, problems);
            if (policy.AfterFilterChange && _trackRig is { FilterWheelId: null } rig)
            {
                problems.Add($"The rig '{rig.Id}' has no filter wheel, so Autofocus cannot follow a filter change.");
            }

            problems.ForEach(p => Report(track.Id, p));
        }

        // The settings of an autofocus, and what the rig it focuses has to have: a camera and a focuser that are there,
        // and enough focuser travel for the samples. Whether anything is connected is for the readiness to say.
        private void CheckAutofocus(Rig? rig, double exposureSeconds, int stepSize, int sampleCount, List<string> problems)
        {
            CheckDuration(exposureSeconds, "Autofocus exposure", problems);
            if (stepSize <= 0)
            {
                problems.Add("Autofocus step size must be greater than 0.");
            }

            if (sampleCount is < AutofocusOptions.MinimumSampleCount or > AutofocusOptions.MaximumSampleCount || sampleCount % 2 == 0)
            {
                problems.Add(
                    $"Autofocus samples must be an odd number between {AutofocusOptions.MinimumSampleCount} and {AutofocusOptions.MaximumSampleCount}.");
            }

            if (context?.FocusMetrics is null)
            {
                problems.Add("Autofocus is not available: there is nothing to measure focus with.");
            }

            if (rig is null)
            {
                return; // the track or the step says what is wrong with its rig
            }

            if (rig.FocuserId is not { } focuserId)
            {
                problems.Add($"The rig '{rig.Id}' has no focuser.");
            }
            else if (!registry.TryGet(focuserId, out var device) || device is not IFocuser focuser)
            {
                problems.Add($"The focuser '{focuserId}' of rig '{rig.Id}' is not available.");
            }
            else if (stepSize > 0 && (long)focuser.MaxPosition - focuser.MinPosition < (long)(AutofocusOptions.MinimumSampleCount - 1) * stepSize)
            {
                problems.Add($"The focuser '{focuserId}' does not have enough travel for autofocus with a step size of {stepSize}.");
            }

            if (!registry.TryGet(rig.CameraId, out var camera) || camera is not ICamera)
            {
                problems.Add($"The camera '{rig.CameraId}' of rig '{rig.Id}' is not available.");
            }
        }

        // The focuser is the one of the rig of the track: the rig must have one, and it must be there.
        private void RigFocuser(RigMoveFocuserStepDraft step, List<string> problems)
        {
            if (_trackRig is not { } rig)
            {
                return; // the track says what is wrong with its rig
            }

            if (rig.FocuserId is not { } focuserId)
            {
                problems.Add($"The rig '{rig.Id}' has no focuser.");
            }
            else if (!registry.TryGet(focuserId, out var focuser) || focuser is not IFocuser)
            {
                problems.Add($"The focuser '{focuserId}' of rig '{rig.Id}' is not available.");
            }
            else
            {
                CheckFocuserPosition(focuserId, step.Position, problems);
            }
        }

        private void RigFilter(RigChangeFilterStepDraft step, List<string> problems)
        {
            if (_trackRig is not { } rig)
            {
                return;
            }

            if (rig.FilterWheelId is not { } wheelId)
            {
                problems.Add($"The rig '{rig.Id}' has no filter wheel.");
            }
            else if (!registry.TryGet(wheelId, out var wheel) || wheel is not IFilterWheel)
            {
                problems.Add($"The filter wheel '{wheelId}' of rig '{rig.Id}' is not available.");
            }
            else
            {
                CheckSlot(wheelId, step.SlotIndex, problems);
            }
        }

        // The range is the focuser's own, when it is there to ask.
        private void CheckFocuserPosition(DeviceId? focuserId, int position, List<string> problems)
        {
            if (focuserId is { } id && registry.TryGet(id, out var device) && device is IFocuser focuser)
            {
                if (position < focuser.MinPosition || position > focuser.MaxPosition)
                {
                    problems.Add($"Focuser position must be between {focuser.MinPosition} and {focuser.MaxPosition}.");
                }
            }
            else if (position < 0)
            {
                problems.Add("Focuser position cannot be negative.");
            }
        }

        private void CheckSlot(DeviceId? wheelId, int slotIndex, List<string> problems)
        {
            if (slotIndex < 0)
            {
                problems.Add("Filter slot cannot be negative.");
            }
            else if (wheelId is { } id && registry.TryGet(id, out var device) && device is IFilterWheel wheel
                     && slotIndex >= wheel.Slots.Count)
            {
                problems.Add($"The filter wheel '{id}' has no slot {slotIndex}.");
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
                case SyncMountStepDraft m:
                    Mismatch(m.MountId, _shared.MountId, "mount");
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

        // The acquisition settings of an exposure against the capabilities of its camera. A camera that is not connected has none
        // that are known: then nothing is said, the step is not invalid because of that, and the run checks again before it exposes.
        private void CheckAcquisition(AcquisitionIntent intent, DeviceId? cameraId, double seconds, List<string> problems)
        {
            if (cameraId is not { } id || !registry.TryGet(id, out var device) || device is not ICameraControl control
                || !IsPositive(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds)
            {
                return;
            }

            var plan = AcquisitionResolver.Resolve(
                intent, context?.AcquisitionDefaults?.DefaultsFor(id), TimeSpan.FromSeconds(seconds), control.Capabilities, control.Settings);
            problems.AddRange(plan.Problems);
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
        // The mount of a rig-local step is the mount of its rig. A step of an older file that names one is kept when the rig has none; naming another one than the rig's is a mistake that is reported.
        private void CheckRigMount(RigId? rigId, DeviceId? named, List<string> problems)
        {
            Rig? rig = null;
            if (rigId is { } id)
            {
                TryGetRig(context, id, out rig);
            }

            if (rig?.MountId is { } rigMount && named is { } other && other != rigMount)
            {
                problems.Add($"The step names the mount '{other}', but the rig '{rig.Name}' is on '{rigMount}'.");
                return;
            }

            var mount = StepScopes.EffectiveMount(rig, named, _shared);
            if (mount is null)
            {
                problems.Add(rig is null ? "No mount selected." : $"The rig '{rig.Name}' has no mount. Give the rig a mount on the Equipment page.");
                return;
            }

            CheckDevice<IMount>(mount, "mount", problems);
        }

        private static void CheckAngle(double degrees, List<string> problems)
        {
            if (!double.IsFinite(degrees)) problems.Add("The sky rotation must be a number of degrees.");
        }

        private static void CheckRotationTolerance(double tolerance, int attempts, List<string> problems)
        {
            if (!double.IsFinite(tolerance) || tolerance <= 0 || tolerance > 90) problems.Add("The rotation tolerance must be greater than 0 and at most 90 degrees.");
            if (attempts is < 1 or > 20) problems.Add("The rotation attempts must be from 1 to 20.");
        }

        // A rotation needs a rig with a rotator, a camera to solve with, and a calibration: without one the position of the rotator says nothing about the sky.
        private void CheckRotationRig(RigId? rigId, List<string> problems)
        {
            if (rigId is not { } id || !TryGetRig(context, id, out var rig))
            {
                problems.Add("Select an available rig.");
                return;
            }

            CheckDevice<ICamera>(rig.CameraId, "camera", problems);
            if (rig.RotatorId is not { } rotatorId)
            {
                problems.Add($"The rig '{rig.Name}' has no rotator.");
            }
            else
            {
                CheckDevice<Sidera.Core.Rotators.IRotator>(rotatorId, "rotator", problems);
                if (rig.RotatorModel is null) problems.Add($"The rotator of the rig '{rig.Name}' is not calibrated: calibrate it with a plate solve first.");
            }
        }

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

    /// <summary>
    /// The explicit acquisition settings of an exposure for the row: " · Gain 100 · Bin 2x2", or " · Camera defaults" when it
    /// overrides nothing. Only what the step sets is shown, never what the camera would inherit.
    /// </summary>
    public static string AcquisitionSummary(AcquisitionIntent intent, DeviceRegistry? registry = null, DeviceId? cameraId = null)
    {
        var parts = new List<string>();
        if (intent.FrameType != FrameType.Light)
        {
            parts.Add(intent.FrameType.ToString());
        }

        if (intent.Gain is { } gain)
        {
            parts.Add($"Gain {gain}");
        }

        if (intent.Offset is { } offset)
        {
            parts.Add($"Offset {offset}");
        }

        if (intent.BinX is { } bx || intent.BinY is not null)
        {
            var x = intent.BinX ?? intent.BinY!.Value;
            var y = intent.BinY ?? x;
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Bin {x}×{y}"));
        }

        if (intent.Region is { } region)
        {
            parts.Add(region.IsFullFrame ? "Full frame" : $"Region {region}");
        }

        if (intent.ReadoutMode is { } readout)
        {
            parts.Add($"Readout {readout}");
        }

        if (intent.FastReadout is { } fast)
        {
            parts.Add(fast ? "Fast readout" : "Normal readout speed");
        }

        return parts.Count == 0 ? " · Camera defaults" : " · " + string.Join(" · ", parts);
    }

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
        SequenceStepKind.MoveFocuser or SequenceStepKind.RigMoveFocuser => "Move Focuser",
        SequenceStepKind.ChangeFilter or SequenceStepKind.RigChangeFilter => "Change Filter",
        SequenceStepKind.PlateSolve => "Plate Solve",
        SequenceStepKind.SlewAndCenter => "Slew & Center",
        SequenceStepKind.RotateToAngle => "Rotate to Angle",
        SequenceStepKind.RotateAndVerify => "Rotate & Verify",
        SequenceStepKind.CenterAndRotate => "Center & Rotate",
        SequenceStepKind.SyncMountToSolved => "Sync Mount to Solved Position",
        SequenceStepKind.Autofocus or SequenceStepKind.RigAutofocus => "Autofocus",
        SequenceStepKind.Repeat => "Repeat",
        SequenceStepKind.MultiRig => MultiRigName,
        SequenceStepKind.RigTrack => "Rig Track",
        _ => kind.ToString(),
    };
}

/// <summary>What a Multi-Rig dither moves and uses, and the tracks (by id) that sit on that mount.</summary>
public sealed record DitherDomain(DeviceId? Mount, DeviceId? Guider, IReadOnlyList<Guid> Tracks);

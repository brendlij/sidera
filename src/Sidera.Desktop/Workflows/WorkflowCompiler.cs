using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Workflows;

/// <summary>Something wrong with a workflow, in words about setups and steps; <see cref="ElementId"/> is the step or block it is about, or <c>null</c> when it is about the workflow.</summary>
public sealed record WorkflowProblem(Guid? ElementId, string Message);

/// <summary>
/// What a workflow compiles to: the steps of the existing sequence model, the problems found on the way, and for each compiled draft step (and track) the step or block of the workflow it
/// came from, so that a problem of the sequence can be shown where the user made it.
/// </summary>
public sealed record WorkflowCompilation(
    IReadOnlyList<SequenceStepDraft> Steps,
    IReadOnlyList<WorkflowProblem> Problems,
    IReadOnlyDictionary<Guid, Guid> Origins)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Compiles a <see cref="WorkflowDefinition"/> into the steps the editor, the document and the runtime already have, so nothing below it knows about workflows:
/// <code>
/// Prepare   →  Slew &amp; Center / Autofocus / Start Guiding / Wait, in order, with the setup's devices resolved
/// Imaging   →  one Multi-Rig block: a Rig Track for each setup (blocks of one setup in order: a filter change and a Repeat of exposures),
///              the dither policy of the block, the autofocus policy of each track
/// Finish    →  Stop Guiding / Wait, in order
/// </code>
/// Compiling is deterministic: the same workflow gives the same steps with the same ids (they are derived from the ids of the workflow's steps and blocks), so a recompile keeps the editor's
/// selection and a document that is saved and opened compiles to what it did. Devices come from the setup (the rig): the camera, focuser, filter wheel, rotator, mount and guider the rig names.
/// Guiding is started once for each guider, however many setups use it. Setups on one mount are centered once; setups on different mounts are separate pointing systems and are centered one
/// after the other (each with its own plate-solving setup), and a dither only makes the setups on the dithered mount wait.
/// </summary>
public static class WorkflowCompiler
{
    /// <summary>A stable id for a draft step that is made from <paramref name="source"/>, told apart from the others made from it by <paramref name="purpose"/>.</summary>
    public static Guid Derive(Guid source, string purpose)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(source.ToString("N", CultureInfo.InvariantCulture) + "/" + purpose));
        return new Guid(bytes);
    }

    public static WorkflowCompilation Compile(WorkflowDefinition workflow, RigRegistry? rigs, SequenceDraftDefaults? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        var problems = new List<WorkflowProblem>();
        var origins = new Dictionary<Guid, Guid>();
        var all = rigs?.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        Rig? Find(RigId? id) => id is { } rigId && rigs is not null && rigs.TryGet(rigId, out var rig) ? rig : null;

        // ---- the setups that are imaged, in the order they first appear
        var setups = new List<Rig>();
        var blocksOf = new Dictionary<RigId, List<ImagingBlock>>();
        foreach (var block in workflow.Imaging.Where(b => b.Enabled))
        {
            var rig = block.Setup is null ? (all.Count == 1 ? all[0] : null) : Find(block.Setup);
            if (rig is null)
            {
                problems.Add(new WorkflowProblem(block.Id, block.Setup is null
                    ? "Choose an imaging setup for this block."
                    : $"The imaging setup '{block.Setup}' does not exist any more. Choose another one."));
                continue;
            }

            if (!setups.Contains(rig))
            {
                setups.Add(rig);
                blocksOf[rig.Id] = [];
            }

            blocksOf[rig.Id].Add(block);
            if (!double.IsFinite(block.ExposureSeconds) || block.ExposureSeconds <= 0)
            {
                problems.Add(new WorkflowProblem(block.Id, "The exposure must be longer than 0 seconds."));
            }

            if (block.Frames < 1)
            {
                problems.Add(new WorkflowProblem(block.Id, "A block needs at least 1 frame."));
            }

            if (block.FilterSlot is not null && rig.FilterWheelId is null)
            {
                problems.Add(new WorkflowProblem(block.Id, $"{rig.Name} has no filter wheel, so it cannot use a filter. Choose no filter or give the setup a filter wheel."));
            }
        }

        // ---- Prepare and Finish
        var steps = new List<SequenceStepDraft>();
        var started = new HashSet<DeviceId>();
        var stopped = new HashSet<DeviceId>();
        foreach (var step in workflow.Prepare.Where(s => s.Enabled))
        {
            CompileStep(step, "prepare");
        }

        // ---- Imaging: one Multi-Rig block with a track for each setup
        if (setups.Count > 0)
        {
            var tracks = new List<RigTrackDraft>();
            foreach (var rig in setups)
            {
                var trackId = Derive(Guid.Empty, "track:" + rig.Id.Value);
                var trackSteps = new List<SequenceStepDraft>();
                int? slot = null;
                foreach (var block in blocksOf[rig.Id])
                {
                    if (block.FilterSlot is { } wanted && wanted != slot && rig.FilterWheelId is not null)
                    {
                        var change = Derive(block.Id, "filter");
                        origins[change] = block.Id;
                        trackSteps.Add(new RigChangeFilterStepDraft(change, wanted));
                    }

                    slot = block.FilterSlot ?? slot;
                    var exposure = Derive(block.Id, "exposure");
                    var repeat = Derive(block.Id, "repeat");
                    origins[exposure] = block.Id;
                    origins[repeat] = block.Id;
                    trackSteps.Add(new RepeatStepDraft(repeat, Math.Max(1, block.Frames), [new RigExposureStepDraft(exposure, Math.Max(double.Epsilon, block.ExposureSeconds))]));
                }

                origins[trackId] = blocksOf[rig.Id][0].Id;
                tracks.Add(new RigTrackDraft(trackId, rig.Id, trackSteps, PolicyOf(workflow, rig, blocksOf[rig.Id][0].Id, problems)));
            }

            steps.Add(new MultiRigStepDraft(
                Derive(Guid.Empty, "imaging"), tracks, DitherOf(workflow, setups, blocksOf, problems), SingleTrack: true, MeridianFlip: FlipOf(workflow, setups, blocksOf, problems)));
        }

        foreach (var step in workflow.Finish.Where(s => s.Enabled))
        {
            CompileStep(step, "finish");
        }

        return new WorkflowCompilation(steps, problems, origins);

        // ----

        void CompileStep(WorkflowStep step, string section)
        {
            var explicitRig = step.Setup is null ? null : Find(step.Setup);
            if (step.Setup is not null && explicitRig is null)
            {
                problems.Add(new WorkflowProblem(step.Id, $"The imaging setup '{step.Setup}' does not exist any more. Choose another one."));
                return;
            }

            // "Auto" means the setups that are imaged; before there are any, the only setup there is.
            var scope = explicitRig is not null ? [explicitRig] : setups.Count > 0 ? setups : all.Count == 1 ? all : (IReadOnlyList<Rig>)[];
            switch (step.Kind)
            {
                case WorkflowStepKind.Wait:
                {
                    var id = Derive(step.Id, section);
                    origins[id] = step.Id;
                    steps.Add(new DelayStepDraft(id, step.Seconds));
                    break;
                }

                case WorkflowStepKind.StartGuiding or WorkflowStepKind.StopGuiding:
                {
                    var guiders = scope.Select(r => r.GuiderId).OfType<DeviceId>().Distinct().ToList();
                    if (guiders.Count == 0)
                    {
                        problems.Add(new WorkflowProblem(step.Id, explicitRig is not null
                            ? $"{explicitRig.Name} has no guider. Give the setup a guider on the Equipment page."
                            : "No imaging setup has a guider. Give a setup a guider on the Equipment page."));
                        break;
                    }

                    var seen = step.Kind == WorkflowStepKind.StartGuiding ? started : stopped;
                    foreach (var guider in guiders.Where(seen.Add))
                    {
                        var id = Derive(step.Id, guider.Value);
                        origins[id] = step.Id;
                        steps.Add(step.Kind == WorkflowStepKind.StartGuiding ? new StartGuidingStepDraft(id, guider) : new StopGuidingStepDraft(id, guider));
                    }

                    break;
                }

                case WorkflowStepKind.Autofocus:
                {
                    var focusable = scope.Where(r => r.FocuserId is not null).ToList();
                    if (focusable.Count == 0)
                    {
                        problems.Add(new WorkflowProblem(step.Id, explicitRig is not null
                            ? $"{explicitRig.Name} has no focuser. Give the setup a focuser on the Equipment page."
                            : "No imaging setup has a focuser. Give a setup a focuser on the Equipment page."));
                        break;
                    }

                    var settings = step.Autofocus ?? AutofocusSettings.Default;
                    foreach (var rig in focusable)
                    {
                        var id = Derive(step.Id, rig.Id.Value);
                        origins[id] = step.Id;
                        steps.Add(new AutofocusStepDraft(id, rig.Id, settings.ExposureSeconds, settings.StepSize, settings.SampleCount));
                    }

                    break;
                }

                case WorkflowStepKind.SlewAndCenter:
                    CompileCenter(step, explicitRig);
                    break;
            }
        }

        void CompileCenter(WorkflowStep step, Rig? explicitRig)
        {
            var candidates = explicitRig is not null ? [explicitRig] : setups.Count > 0 ? setups : all.Count == 1 ? all : (IReadOnlyList<Rig>)[];
            var groups = candidates.Where(r => r.MountId is not null).GroupBy(r => r.MountId!.Value).ToList();
            if (explicitRig is not null && explicitRig.MountId is null)
            {
                problems.Add(new WorkflowProblem(step.Id, $"{explicitRig.Name} has no mount, so it cannot slew. Give the setup a mount on the Equipment page."));
                return;
            }

            if (groups.Count == 0)
            {
                problems.Add(new WorkflowProblem(step.Id, "No imaging setup has a mount, so there is nothing to center on the target. Give a setup a mount, or remove this step."));
                return;
            }

            var target = workflow.Target;
            foreach (var group in groups)
            {
                // The setup that solves for this mount: the one the target names, else the one of this step, else the first of the mount.
                var pointing = group.FirstOrDefault(r => r.Id == target.PointingSetup) ?? group.First();
                var id = Derive(step.Id, "center:" + group.Key.Value);
                origins[id] = step.Id;
                if (target.DesiredRotationDegrees is { } rotation && pointing.RotatorId is not null)
                {
                    steps.Add(new CenterAndRotateStepDraft(
                        id, null, pointing.Id, target.RightAscensionHours, target.DeclinationDegrees, step.ToleranceArcseconds, step.MaxAttempts, rotation,
                        RotationService.DefaultToleranceDegrees, RotationService.DefaultMaxAttempts, RotationService.DefaultMaxRounds, step.SolveExposureSeconds, target.Name));
                }
                else
                {
                    steps.Add(new SlewAndCenterStepDraft(
                        id, null, pointing.Id, target.RightAscensionHours, target.DeclinationDegrees, step.ToleranceArcseconds, step.MaxAttempts, step.SolveExposureSeconds, target.Name,
                        target.DesiredRotationDegrees));
                }
            }
        }
    }

    // The meridian flip of the Imaging section: for every mount of the imaged setups, once, for all the setups on it. The target it slews back to is the target of the workflow.
    private static MeridianFlipPolicyDraft? FlipOf(
        WorkflowDefinition workflow, IReadOnlyList<Rig> setups, IReadOnlyDictionary<RigId, List<ImagingBlock>> blocksOf, List<WorkflowProblem> problems)
    {
        var settings = workflow.FlipSettings;
        if (!settings.Enabled)
        {
            return null;
        }

        foreach (var problem in settings.Problems())
        {
            problems.Add(new WorkflowProblem(null, "Meridian flip: " + problem));
        }

        if (!setups.Any(r => r.MountId is not null))
        {
            problems.Add(new WorkflowProblem(blocksOf[setups[0].Id][0].Id, "The meridian flip needs a setup with a mount. Give a setup a mount on the Equipment page, or turn the flip off."));
        }

        var target = workflow.Target;
        var dither = workflow.Dither;
        return new MeridianFlipPolicyDraft(
            settings, target.RightAscensionHours, target.DeclinationDegrees, target.Name, target.DesiredRotationDegrees, target.PointingSetup,
            dither.AmplitudePixels, dither.SettleThresholdPixels, dither.SettleStableSeconds, Math.Max(dither.SettleTimeoutSeconds, 60));
    }

    // The autofocus policy of a setup as the track has it; nothing for a setup that does not focus by itself.
    private static RigAutofocusPolicyDraft? PolicyOf(WorkflowDefinition workflow, Rig rig, Guid blockId, List<WorkflowProblem> problems)
    {
        var policy = workflow.AutofocusOf(rig.Id);
        if (!policy.Enabled)
        {
            return null;
        }

        if (!policy.AtStart && !policy.AfterFilterChange && policy.IntervalMinutes <= 0)
        {
            problems.Add(new WorkflowProblem(blockId, $"Autofocus of {rig.Name} is on but has no trigger. Choose when it should focus."));
        }

        if (rig.FocuserId is null)
        {
            problems.Add(new WorkflowProblem(blockId, $"{rig.Name} has no focuser, so it cannot focus by itself. Give the setup a focuser or turn Autofocus off."));
        }

        if (policy.AfterFilterChange && rig.FilterWheelId is null)
        {
            problems.Add(new WorkflowProblem(blockId, $"{rig.Name} has no filter wheel, so Autofocus cannot follow a filter change."));
        }

        return new RigAutofocusPolicyDraft(
            true, policy.AtStart, policy.AfterFilterChange, policy.Settings.ExposureSeconds, policy.Settings.StepSize, policy.Settings.SampleCount, Math.Max(0, policy.IntervalMinutes));
    }

    // The dither policy of the Imaging section: counted on one setup, which needs a mount and a guider; nothing is chosen but that setup, and by default it is the first one.
    private static MultiRigDitherPolicyDraft? DitherOf(
        WorkflowDefinition workflow, IReadOnlyList<Rig> setups, IReadOnlyDictionary<RigId, List<ImagingBlock>> blocksOf, List<WorkflowProblem> problems)
    {
        var dither = workflow.Dither;
        if (!dither.Enabled)
        {
            return null;
        }

        var counted = dither.CountedSetup is { } named ? setups.FirstOrDefault(r => r.Id == named) : setups[0];
        if (counted is null)
        {
            problems.Add(new WorkflowProblem(null, "Dither counts the frames of an imaging setup that is not imaged. Choose one that is."));
            return null;
        }

        var anchor = blocksOf[counted.Id][0].Id;
        if (counted.MountId is null)
        {
            problems.Add(new WorkflowProblem(anchor, $"Dither needs a mount: {counted.Name} has none. Give the setup a mount on the Equipment page."));
        }

        if (counted.GuiderId is null)
        {
            problems.Add(new WorkflowProblem(anchor, $"Dither needs a guider: {counted.Name} has none. Give the setup a guider on the Equipment page."));
        }

        if (dither.EveryNFrames < 1)
        {
            problems.Add(new WorkflowProblem(anchor, "Dither every N frames needs N of at least 1."));
        }

        return new MultiRigDitherPolicyDraft(
            true, counted.Id, Math.Max(1, dither.EveryNFrames), dither.AmplitudePixels, dither.SettleThresholdPixels, dither.SettleStableSeconds, dither.SettleTimeoutSeconds);
    }
}

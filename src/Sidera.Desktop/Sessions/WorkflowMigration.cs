using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Workflows;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Sessions;

/// <summary>
/// Turns the workflow of a version 8 document into a session, deterministically: the same workflow always gives the same session with the same ids (they are the ids of the steps and blocks it had, or derived
/// from them), so a file that is migrated, saved and opened again does not change. Nothing is lost that the session can say:
/// <list type="bullet">
/// <item>The Prepare steps become the <b>preparation of the one target</b>; the Wait steps that start Prepare (a wait for darkness) are the <b>start of the session</b>, which runs before it. Finish becomes the end.</item>
/// <item>The imaging blocks become <b>lanes</b>, one for each setup (blocks of one setup in their order). A lane is for the setup its blocks named; with one setup to image with, "Auto" stays "Auto".</item>
/// <item>A block's start conditions become a leading <b>Wait Until</b> action, its filter a <b>Set Filter</b>, its exposure an <b>Exposure</b>; its frames are the repeat; its stop conditions its <b>limits</b>.</item>
/// <item>The autofocus policy of a setup becomes the <b>autofocus automation</b> of each block of its lane ("at the start" only on the first block, as it was once for the track); the dither policy the
/// <b>dither automation</b> of each block of the lane the frames were counted on.</item>
/// <item>The meridian flip and the target stop become the session automation and the limits of the target; the pointing setup goes first among the lanes (the setup whose camera solves).</item>
/// </list>
/// A reference to a setup is kept as it was when it cannot be resolved: it is reported when the session is compiled, never given to another setup.
/// </summary>
public static class WorkflowMigration
{
    /// <summary>The session that a workflow means. <paramref name="setups"/> says which blocks belong to one setup (an "Auto" block and one that names the only setup); without it blocks are grouped by what they name.</summary>
    public static SessionDefinition ToSession(WorkflowDefinition workflow, ISetupSource? setups = null, IReadOnlySet<RigId>? usable = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        workflow = WorkflowBindings.Canonical(workflow, setups);

        var automation = workflow.MeridianFlipUsesDefaults ? SessionAutomation.Defaults : new SessionAutomation(workflow.MeridianFlip ?? new MeridianFlipSettings());
        var empty = workflow.Prepare.Count == 0 && workflow.Imaging.Count == 0 && workflow.Finish.Count == 0 && workflow.TargetStopAny.Count == 0;
        if (empty)
        {
            return new SessionDefinition(automation, [], [], []);
        }

        var all = setups?.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        var pool = usable is null ? all : all.Where(r => usable.Contains(r.Id)).ToList();
        Rig? Resolve(ImagingBindingId? binding) =>
            binding is { } named ? (setups is not null && setups.TryResolve(named, out var found) ? found : null) : pool.Count == 1 ? pool[0] : null;

        // Which lane a binding belongs to: the setup it means, else what it names.
        (string Key, ImagingBindingId? Binding) LaneOf(ImagingBindingId? binding)
        {
            if (Resolve(binding) is { } rig)
            {
                return ("rig:" + rig.Id.Value, pool.Count > 1 ? ImagingBindingId.Of(rig) : null);
            }

            return ("raw:" + (binding?.Value ?? string.Empty), binding);
        }

        // ---- the lanes, in the order their setups first appear; the pointing setup first
        var order = new List<string>();
        var bindingOf = new Dictionary<string, ImagingBindingId?>();
        var blocksOf = new Dictionary<string, List<ImagingBlock>>();
        foreach (var block in workflow.Imaging)
        {
            var (key, binding) = LaneOf(block.Setup);
            if (!blocksOf.TryGetValue(key, out var list))
            {
                order.Add(key);
                bindingOf[key] = binding;
                blocksOf[key] = list = [];
            }

            list.Add(block);
        }

        if (workflow.Target.PointingSetup is { } pointing && LaneOf(pointing).Key is var pointingKey && order.IndexOf(pointingKey) > 0)
        {
            order.Remove(pointingKey);
            order.Insert(0, pointingKey);
        }

        // The setup the dither frames were counted on: the one that is named, else the first that is imaged.
        var countedKey = workflow.Dither.Enabled
            ? (workflow.Dither.CountedSetup is { } counted ? LaneOf(counted).Key : workflow.Imaging.Where(b => b.Enabled).Select(b => LaneOf(b.Setup).Key).FirstOrDefault())
            : null;

        var lanes = new List<SetupLane>();
        foreach (var key in order)
        {
            var binding = bindingOf[key];
            var policy = PolicyOf(workflow, key, LaneOf);
            var firstEnabled = blocksOf[key].FirstOrDefault(b => b.Enabled)?.Id;
            var dither = key == countedKey
                ? new DitherAutomation(workflow.Dither.EveryNFrames, new DitherSettings(workflow.Dither.AmplitudePixels, workflow.Dither.SettleThresholdPixels, workflow.Dither.SettleStableSeconds, workflow.Dither.SettleTimeoutSeconds))
                : null;

            var blocks = blocksOf[key].Select(block => ToBlock(block, dither, policy is null ? null : new FocusAutomation(
                policy.AtStart && block.Id == firstEnabled, policy.IntervalMinutes, policy.AfterFilterChange, new FocusSettings(policy.Settings.ExposureSeconds, policy.Settings.StepSize, policy.Settings.SampleCount)))).ToList();
            lanes.Add(new SetupLane(SessionCompiler.Derive(blocks[0].Id, "lane"), binding, blocks));
        }

        // ---- Prepare, Finish
        var prepare = workflow.Prepare.ToList();
        var leading = prepare.TakeWhile(s => s.Kind == WorkflowStepKind.Wait).ToList();
        var start = leading.Select(ToAction).ToList();
        var preparation = prepare.Skip(leading.Count).Select(ToAction).ToList();
        var end = workflow.Finish.Select(ToAction).ToList();

        var anchor = workflow.Imaging.Select(b => b.Id).Concat(workflow.Prepare.Select(s => s.Id)).Concat(workflow.Finish.Select(s => s.Id)).FirstOrDefault();
        var t = workflow.Target;
        var target = new SessionTarget(
            SessionCompiler.Derive(anchor, "target"), t.Name, t.RightAscensionHours, t.DeclinationDegrees, t.DesiredRotationDegrees, true, preparation, lanes,
            workflow.TargetStopAny);
        return new SessionDefinition(automation, start, [target], end);
    }

    // The autofocus policy a lane had: the one of its setup, if it was on.
    private static SetupAutofocus? PolicyOf(WorkflowDefinition workflow, string laneKey, Func<ImagingBindingId?, (string Key, ImagingBindingId? Binding)> laneOf) =>
        workflow.AutofocusPolicies.FirstOrDefault(p => p.Enabled && laneOf(p.Setup).Key == laneKey);

    private static SequenceBlock ToBlock(ImagingBlock block, DitherAutomation? dither, FocusAutomation? focus)
    {
        var actions = new List<SessionAction>();
        if (block.StartAll.Count > 0)
        {
            actions.Add(new WaitUntilAction(SessionCompiler.Derive(block.Id, "start"), block.StartAll));
        }

        if (block.FilterSlot is { } slot)
        {
            actions.Add(new SetFilterAction(SessionCompiler.Derive(block.Id, "filter"), slot));
        }

        actions.Add(new ExposureAction(SessionCompiler.Derive(block.Id, "exposure"), block.ExposureSeconds));
        return new SequenceBlock(
            block.Id, null, block.Enabled, actions, RepeatRule.Times(block.Frames), new BlockAutomation(dither, focus is { IsActive: true } ? focus : null), block.StopAny);
    }

    private static SessionAction ToAction(WorkflowStep step)
    {
        SessionAction action = step.Kind switch
        {
            WorkflowStepKind.Wait when step.WaitMode == WorkflowWaitMode.Duration => new WaitAction(step.Id, step.Seconds),
            WorkflowStepKind.Wait => new WaitUntilAction(step.Id, step.UntilAll),
            WorkflowStepKind.SlewAndCenter => new SlewAndCenterAction(step.Id, step.ToleranceArcseconds, step.MaxAttempts, step.SolveExposureSeconds),
            WorkflowStepKind.Autofocus => new AutofocusAction(
                step.Id, step.Autofocus is { } a ? new FocusSettings(a.ExposureSeconds, a.StepSize, a.SampleCount) : FocusSettings.Default),
            WorkflowStepKind.StartGuiding => new StartGuidingAction(step.Id),
            _ => new StopGuidingAction(step.Id),
        };

        return action with { Enabled = step.Enabled, Setup = step.Setup };
    }
}

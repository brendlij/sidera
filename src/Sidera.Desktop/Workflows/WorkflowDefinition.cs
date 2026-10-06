using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Rigs;

namespace Sidera.Desktop.Workflows;

/// <summary>What a step of the Prepare or Finish section of a workflow does. Each happens once, at its place.</summary>
public enum WorkflowStepKind
{
    /// <summary>Slews to the target and centers it by plate solving; with a desired rotation and a rotator it also rotates.</summary>
    SlewAndCenter,

    /// <summary>Focuses once, now.</summary>
    Autofocus,

    /// <summary>Starts guiding with the guider of the setup (or of every setup that is imaged).</summary>
    StartGuiding,

    /// <summary>Stops guiding with the guider of the setup (or of every setup that was imaged).</summary>
    StopGuiding,

    /// <summary>Waits: for a time, until a moment, or until a condition (see <see cref="WorkflowWaitMode"/>).</summary>
    Wait
}

/// <summary>What a Wait step waits for.</summary>
public enum WorkflowWaitMode
{
    /// <summary>A number of seconds.</summary>
    Duration,

    /// <summary>A moment: a time of day or an instant.</summary>
    UntilTime,

    /// <summary>Until conditions of the sky hold (the target above an altitude, darkness).</summary>
    UntilCondition
}

/// <summary>The sections of a workflow, in the order they run.</summary>
public enum WorkflowSection
{
    Prepare,
    Imaging,
    Finish
}

/// <summary>
/// The target of a workflow: where the telescopes point. A setup that shares its mount with another one is centered once for both; the <see cref="PointingSetup"/> is the one whose camera
/// plate-solves for it (<c>null</c>: the first one imaged, never a guess made again at each run).
/// </summary>
public sealed record WorkflowTarget(
    string Name, double RightAscensionHours, double DeclinationDegrees, double? DesiredRotationDegrees = null, ImagingBindingId? PointingSetup = null)
{
    public static WorkflowTarget Default { get; } = new("Target", SequenceDraftDefaults.TargetRightAscensionHours, SequenceDraftDefaults.TargetDeclinationDegrees);
}

/// <summary>The measurements of an autofocus: the exposure at each sample, the distance between samples in focuser steps, and how many samples.</summary>
public sealed record AutofocusSettings(double ExposureSeconds, int StepSize, int SampleCount)
{
    public static AutofocusSettings Default { get; } = new(1, 400, 7);
}

/// <summary>
/// A step of the Prepare or Finish section. <see cref="Setup"/> is the imaging setup it works with, or <c>null</c> for "Auto": the setup of the workflow where that is unambiguous (every
/// setup that is imaged, for guiding and autofocus). The remaining fields are only used by the kinds they belong to.
/// </summary>
public sealed record WorkflowStep(
    Guid Id,
    WorkflowStepKind Kind,
    ImagingBindingId? Setup = null,
    bool Enabled = true,
    double Seconds = 2,
    double ToleranceArcseconds = 60,
    int MaxAttempts = 5,
    double SolveExposureSeconds = 5,
    AutofocusSettings? Autofocus = null,
    WorkflowWaitMode WaitMode = WorkflowWaitMode.Duration,
    IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition>? Until = null)
{
    /// <summary>What a Wait step waits until (all of it); empty for a Wait of a duration.</summary>
    public IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> UntilAll => Until ?? [];
}

/// <summary>
/// One program of the Imaging section: with this setup, optionally through this filter (the index of its slot), take <see cref="Frames"/> exposures of <see cref="ExposureSeconds"/>.
/// Blocks of different setups run at the same time; blocks of one setup run one after another, in the order they are listed.
/// </summary>
public sealed record ImagingBlock(
    Guid Id,
    ImagingBindingId? Setup,
    int? FilterSlot,
    double ExposureSeconds,
    int Frames,
    bool Enabled = true,
    IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition>? StartWhen = null,
    IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition>? StopWhen = null)
{
    /// <summary>The block does not begin until <b>all</b> of these hold.</summary>
    public IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> StartAll => StartWhen ?? [];

    /// <summary>The block stops when <b>any</b> of these holds (and when its frames are taken, which is its own count).</summary>
    public IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> StopAny => StopWhen ?? [];
}

/// <summary>
/// Dithering as a policy of the Imaging section: after every <see cref="EveryNFrames"/> frames of the <see cref="CountedSetup"/> (<c>null</c>: the first one imaged) the mount of that
/// setup is dithered by its guider, and every setup on that mount waits at a safe point. Which guider that is follows from the setup; nobody chooses it.
/// </summary>
public sealed record WorkflowDither(
    bool Enabled = false,
    int EveryNFrames = 3,
    ImagingBindingId? CountedSetup = null,
    double AmplitudePixels = 1.5,
    double SettleThresholdPixels = 0.5,
    double SettleStableSeconds = 1,
    double SettleTimeoutSeconds = 10)
{
    public static WorkflowDither Off { get; } = new();
}

/// <summary>
/// When one setup focuses by itself during imaging: at the start, every <see cref="IntervalMinutes"/> minutes (0: not by time) and after a filter change. All of them are one policy; the
/// reasons that fall together are one autofocus.
/// </summary>
public sealed record SetupAutofocus(ImagingBindingId Setup, bool Enabled, bool AtStart, double IntervalMinutes, bool AfterFilterChange, AutofocusSettings Settings);

/// <summary>
/// A session as the user thinks of it: a target, what to prepare, what to image, what to finish with, and the policies that apply while imaging. It is plain values; the one place that turns
/// it into the steps the runtime knows is <see cref="WorkflowCompiler"/>. It is what a document of version 8 holds, so the same workflow always compiles to the same steps.
/// </summary>
public sealed record WorkflowDefinition(
    WorkflowTarget Target,
    IReadOnlyList<WorkflowStep> Prepare,
    IReadOnlyList<ImagingBlock> Imaging,
    IReadOnlyList<WorkflowStep> Finish,
    WorkflowDither Dither,
    IReadOnlyList<SetupAutofocus> AutofocusPolicies,
    Sidera.Core.Mounts.MeridianFlipSettings? MeridianFlip = null,
    bool MeridianFlipUsesDefaults = false,
    IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition>? StopTargetWhen = null)
{
    /// <summary>Imaging of the whole target stops when <b>any</b> of these holds: every block of it ends after its current exposure, and the Finish part follows.</summary>
    public IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition> TargetStopAny => StopTargetWhen ?? [];

    /// <summary>The meridian flip that the workflow has of its own; off when it has none.</summary>
    public Sidera.Core.Mounts.MeridianFlipSettings FlipSettings => MeridianFlip ?? new Sidera.Core.Mounts.MeridianFlipSettings();

    /// <summary>
    /// The meridian flip that counts: the application's defaults when the workflow uses them (<see cref="MeridianFlipUsesDefaults"/>), else its own. A workflow with its own settings never
    /// follows the defaults, so changing them rewrites nothing that was set for this workflow. A document without either is a workflow with its own, disabled flip, as before defaults existed.
    /// </summary>
    public Sidera.Core.Mounts.MeridianFlipSettings EffectiveFlip(Sidera.Core.Mounts.MeridianFlipSettings applicationDefaults) =>
        MeridianFlipUsesDefaults ? applicationDefaults : FlipSettings;

    /// <summary>A workflow with a target and nothing else.</summary>
    public static WorkflowDefinition Empty { get; } = new(WorkflowTarget.Default, [], [], [], WorkflowDither.Off, []);

    /// <summary>The autofocus policy that the defaults propose for a setup.</summary>
    public static SetupAutofocus AutofocusPolicyOf(ImagingBindingId setup, Sidera.Desktop.Settings.AutofocusDefaults d) =>
        new(setup, d.PolicyEnabled, d.PolicyAtStart, d.PolicyIntervalMinutes, d.PolicyAfterFilterChange, new AutofocusSettings(d.ExposureSeconds, d.StepSize, d.SampleCount));

    /// <summary>The autofocus policy of a setup; off when it has none.</summary>
    public SetupAutofocus AutofocusOf(ImagingBindingId setup) =>
        AutofocusPolicies.FirstOrDefault(p => p.Setup == setup) ?? new SetupAutofocus(setup, false, false, 0, false, AutofocusSettings.Default);

    /// <summary>The autofocus policy of the imaging path of a setup. Bindings are compared as paths (see <see cref="Canonical"/>), never by the id of the setup object.</summary>
    public SetupAutofocus AutofocusOf(Rig rig) => AutofocusOf(ImagingBindingId.Of(rig));

    /// <summary>A starting point for the rigs there are: center, focus and guide (where the first setup has what it takes), image with the first setup, stop guiding.</summary>
    /// <summary>The application defaults that a new workflow starts from: what to focus with, whether to guide and dither. Existing workflows are never changed by them.</summary>
    public sealed record StartDefaults(Sidera.Desktop.Settings.AutofocusDefaults Autofocus, Sidera.Desktop.Settings.GuidingDefaults Guiding);

    /// <summary>A new workflow, empty, with the dither values of the defaults and the meridian flip following the application's.</summary>
    public static WorkflowDefinition NewEmpty(StartDefaults? start = null)
    {
        var g = start?.Guiding ?? new Sidera.Desktop.Settings.GuidingDefaults();
        return Empty with
        {
            Dither = new WorkflowDither(g.DitherByDefault, g.DitherEveryNFrames, null, g.DitherAmplitudePixels, g.SettleThresholdPixels, g.SettleStableSeconds, g.SettleTimeoutSeconds),
            MeridianFlipUsesDefaults = true,
        };
    }

    public static WorkflowDefinition Template(IReadOnlyList<Rig> rigs, SequenceDraftDefaults defaults, StartDefaults? start = null)
    {
        ArgumentNullException.ThrowIfNull(rigs);
        ArgumentNullException.ThrowIfNull(defaults);
        var first = rigs.OrderByDescending(r => r.MountId is not null).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var prepare = new List<WorkflowStep>();
        var finish = new List<WorkflowStep>();
        if (first?.MountId is not null)
        {
            prepare.Add(new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.SlewAndCenter));
        }

        var autofocus = start?.Autofocus ?? new Sidera.Desktop.Settings.AutofocusDefaults();
        var guiding = start?.Guiding ?? new Sidera.Desktop.Settings.GuidingDefaults();
        if (first?.FocuserId is not null)
        {
            prepare.Add(new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.Autofocus, Autofocus: new AutofocusSettings(autofocus.ExposureSeconds, autofocus.StepSize, autofocus.SampleCount)));
        }

        if (first?.GuiderId is not null)
        {
            if (guiding.StartBeforeImaging)
            {
                prepare.Add(new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StartGuiding));
            }

            if (guiding.StopWhenDone)
            {
                finish.Add(new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StopGuiding));
            }
        }

        var blocks = first is null ? [] : new List<ImagingBlock> { new(Guid.NewGuid(), first.Id, null, defaults.ExposureSeconds, 10) };
        var policies = first is not null && first.FocuserId is not null && autofocus.PolicyEnabled
            ? [AutofocusPolicyOf(first.Id, autofocus)]
            : new List<SetupAutofocus>();
        return NewEmpty(start) with { Prepare = prepare, Imaging = blocks, Finish = finish, AutofocusPolicies = policies };
    }
}

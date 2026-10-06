using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Sequencing;
using Sidera.Desktop.Workflows;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.ViewModels;

/// <summary>What the document view model needs of the workflow editor: the workflow to save, and a way to give it the one that was opened.</summary>
public interface IWorkflowSource
{
    /// <summary>The workflow of the session; <c>null</c> when the session is a sequence of explicit steps (the Advanced editor).</summary>
    WorkflowDefinition? Definition { get; }

    /// <summary>Some field of the workflow does not read as a number; it cannot be saved faithfully.</summary>
    bool HasUnreadableFields { get; }

    /// <summary>Shows a workflow that was opened, or <c>null</c> for a sequence without one (it is then an Advanced sequence). Not a modification.</summary>
    void Load(WorkflowDefinition? definition);

    /// <summary>Starts a new, empty workflow: what a new session is.</summary>
    void StartNew();
}

/// <summary>A target that the framing hands to the session: where to point, with which setup, and the rotation it wants.</summary>
public sealed record WorkflowTargetRequest(string Name, double RightAscensionHours, double DeclinationDegrees, double? DesiredRotationDegrees, RigId? Setup);

/// <summary>
/// The default editor of the session: a target, what to prepare, what to image, what to finish with, and the policies that apply while imaging, as a table of rows with an inspector for the
/// selected one. It owns a <see cref="WorkflowDefinition"/> and compiles it (<see cref="WorkflowCompiler"/>) into the draft of the sequence every time it changes, so the runner, the
/// resource coordination, the document and the Advanced editor all see the one sequence they have always seen. There are not two models: the workflow is the source and the draft is
/// what it makes. Converting to Advanced drops the workflow and keeps the steps; a sequence that was never a workflow is Advanced from the start.
/// </summary>
public sealed partial class WorkflowEditorViewModel : ViewModelBase, IWorkflowSource, IDisposable
{
    private readonly SequenceDraftViewModel _draft;
    private readonly RigRegistry? _rigs;
    private readonly DeviceRegistry _registry;
    private readonly SequenceDraftDefaults _defaults;
    private readonly ExecutionOverviewViewModel? _execution;
    private readonly Sidera.Runtime.Events.EventBus? _events;
    private readonly Action<Action> _post;
    private readonly Func<Sidera.Core.Location.ObservingSite?>? _site;
    private IDisposable? _flipSubscription;
    private readonly Sidera.Desktop.Settings.SiteService? _settings;
    private bool _flipUsesDefaults;
    private (WorkflowDefinition Workflow, string Fingerprint)? _converted;
    private MeridianFlipSettings _flip = new();
    private List<string> _flipProblems = [];
    private readonly List<TrackLaneViewModel> _wiredLanes = [];
    private bool _loading;
    private WorkflowTarget _target = WorkflowTarget.Default;
    private WorkflowDither _dither = WorkflowDither.Off;
    private List<SetupAutofocus> _policies = [];

    public WorkflowEditorViewModel(
        SequenceDraftViewModel draft, RigRegistry? rigs, DeviceRegistry registry, SequenceDraftDefaults defaults, ExecutionOverviewViewModel? execution = null,
        Sidera.Runtime.Events.EventBus? events = null, Action<Action>? postToUi = null, Func<Sidera.Core.Location.ObservingSite?>? site = null,
        Sidera.Desktop.Settings.SiteService? settings = null)
    {
        _settings = settings;
        TargetStop = new ConditionTogglesViewModel(TargetStopEdited);
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
        }

        _events = events;
        _post = postToUi ?? (action => action());
        _site = site;
        draft.FlipGroupsChanged += OnFlipGroupsChanged;
        _flipSubscription = events?.Subscribe<MeridianFlipStateChanged>((_, _) =>
        {
            _post(RefreshFlipStatus);
            return System.Threading.Tasks.Task.CompletedTask;
        });
        _draft = draft;
        _rigs = rigs;
        _registry = registry;
        _defaults = defaults;
        _execution = execution;
        _draft.TargetSink = AddTarget;
        _draft.PropertyChanged += OnDraftPropertyChanged;
        _draft.Modified += OnDraftModified;
        _draft.Changed += OnDraftChanged;
        if (_execution is not null)
        {
            _execution.PropertyChanged += OnExecutionChanged;
        }
    }

    public void Dispose()
    {
        _draft.PropertyChanged -= OnDraftPropertyChanged;
        _draft.Modified -= OnDraftModified;
        _draft.Changed -= OnDraftChanged;
        _draft.FlipGroupsChanged -= OnFlipGroupsChanged;
        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        _flipSubscription?.Dispose();
        if (_execution is not null)
        {
            _execution.PropertyChanged -= OnExecutionChanged;
        }

        foreach (var lane in _wiredLanes)
        {
            lane.PropertyChanged -= OnLaneChanged;
        }
    }

    // ---- the state

    public ObservableCollection<WorkflowRowViewModel> PrepareRows { get; } = [];

    public ObservableCollection<WorkflowRowViewModel> ImagingRows { get; } = [];

    public ObservableCollection<WorkflowRowViewModel> FinishRows { get; } = [];

    public bool HasPrepareRows => PrepareRows.Count > 0;

    public bool HasImagingRows => ImagingRows.Count > 0;

    public bool HasFinishRows => FinishRows.Count > 0;

    private IEnumerable<WorkflowRowViewModel> AllRows => PrepareRows.Concat(ImagingRows).Concat(FinishRows);

    /// <summary>The workflow as the rows and fields say it now; <c>null</c> in the Advanced editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkflowMode), nameof(IsAdvancedMode), nameof(IsEmpty))]
    public partial WorkflowDefinition? Definition { get; private set; }

    public bool IsWorkflowMode => Definition is not null;

    public bool IsAdvancedMode => Definition is null;

    /// <summary>A workflow without any row: the editor offers a template and the add menu.</summary>
    public bool IsEmpty => Definition is not null && !AllRows.Any();

    /// <summary>The rows can be changed: no sequence is running.</summary>
    public bool IsEditable => _draft.IsEditable;

    public bool HasUnreadableFields => AllRows.Any(r => r.ParseProblems.Count > 0) || _unreadable;

    private bool _unreadable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedIsImaging), nameof(SelectedIsStep), nameof(HasPolicy), nameof(PolicySetupName), nameof(CanMoveUp), nameof(CanMoveDown))]
    public partial WorkflowRowViewModel? SelectedRow { get; private set; }

    public bool HasSelection => SelectedRow is not null;

    public bool SelectedIsImaging => SelectedRow is { IsImaging: true };

    public bool SelectedIsStep => SelectedRow is { IsImaging: false };

    /// <summary>What is wrong with the workflow, in sentences about setups and steps; empty when it can run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(CanRunText))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>Why the workflow cannot run yet, or what it will do.</summary>
    public string CanRunText => HasProblems ? Problems[0] : IsEmpty ? "Add an imaging block to start." : ImagingRows.Count == 0 ? "Add an imaging block: the workflow images nothing yet." : "The workflow is complete.";

    // ---- the target

    [ObservableProperty]
    public partial string TargetNameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetRaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetDecText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetRotationText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SetupChoice? SelectedPointing { get; set; }

    public ObservableCollection<SetupChoice> PointingChoices { get; } = [];

    /// <summary>The setups that can be chosen for a block (not "Auto").</summary>
    public ObservableCollection<SetupChoice> SetupChoices { get; } = [];

    // ---- the dither policy

    [ObservableProperty]
    public partial bool DitherEnabled { get; set; }

    [ObservableProperty]
    public partial string DitherEveryText { get; set; } = "3";

    [ObservableProperty]
    public partial SetupChoice? SelectedCounted { get; set; }

    public ObservableCollection<SetupChoice> CountedChoices { get; } = [];

    [ObservableProperty]
    public partial string DitherAmplitudeText { get; set; } = "1.5";

    [ObservableProperty]
    public partial string DitherThresholdText { get; set; } = "0.5";

    [ObservableProperty]
    public partial string DitherStableText { get; set; } = "1";

    [ObservableProperty]
    public partial string DitherTimeoutText { get; set; } = "10";

    /// <summary>"Every 3 frames of Main, for every setup on its mount; the guider follows from the setup."</summary>
    public string DitherSummary
    {
        get
        {
            if (!DitherEnabled)
            {
                return "Off";
            }

            var counted = SelectedCounted?.IsAuto == false ? SelectedCounted.Name : ImagingRows.FirstOrDefault()?.SetupLabel ?? "the first setup";
            return $"Every {DitherEveryText.Trim()} frames of {counted}; every setup on that mount waits for it. The guider is the one of the setup.";
        }
    }

    // ---- the autofocus policy of the setup of the selected block

    /// <summary>A block is selected whose setup is known: its autofocus policy can be edited.</summary>
    public bool HasPolicy => SelectedRow is { IsImaging: true } row && PolicySetupOf(row) is not null;

    public string PolicySetupName => SelectedRow is { IsImaging: true } row && PolicySetupOf(row) is { } rig ? rig.Name : string.Empty;

    [ObservableProperty]
    public partial bool PolicyEnabled { get; set; }

    [ObservableProperty]
    public partial bool PolicyAtStart { get; set; }

    [ObservableProperty]
    public partial string PolicyIntervalText { get; set; } = "0";

    [ObservableProperty]
    public partial bool PolicyAfterFilterChange { get; set; }

    [ObservableProperty]
    public partial string PolicyExposureText { get; set; } = "1";

    [ObservableProperty]
    public partial string PolicyStepText { get; set; } = "400";

    [ObservableProperty]
    public partial string PolicySamplesText { get; set; } = "7";

    // ---- the status while running

    /// <summary>What the shared devices are doing during a run (guiding, a dither, a slew); empty when nothing or not running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedActivity))]
    public partial string SharedActivityText { get; private set; } = string.Empty;

    public bool HasSharedActivity => SharedActivityText.Length > 0;

    public bool IsRunning => !_draft.IsEditable;

    // ---- the meridian flip: a policy of the imaging section, set once for the workflow

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlipFieldsVisible))]
    public partial bool FlipEnabled { get; set; }

    /// <summary>The fields of the workflow's own flip are shown: it has its own flip, and it is on.</summary>
    public bool FlipFieldsVisible => FlipEnabled && !_flipUsesDefaults;

    [ObservableProperty]
    public partial string FlipPauseBeforeText { get; set; } = "5";

    [ObservableProperty]
    public partial string FlipAfterText { get; set; } = "2";

    [ObservableProperty]
    public partial string FlipLatestText { get; set; } = "15";

    /// <summary>An exposure that is running when the flip comes due is let to finish. It is always on in this version; the box is shown, not changeable.</summary>
    public bool FlipFinishCurrentExposure => true;

    [ObservableProperty]
    public partial bool FlipStopGuiding { get; set; } = true;

    [ObservableProperty]
    public partial bool FlipRecenter { get; set; } = true;

    [ObservableProperty]
    public partial bool FlipRotation { get; set; } = true;

    [ObservableProperty]
    public partial bool FlipAutofocus { get; set; }

    [ObservableProperty]
    public partial bool FlipRestartGuiding { get; set; } = true;

    [ObservableProperty]
    public partial bool FlipDither { get; set; }

    [ObservableProperty]
    public partial string FlipPauseAfterText { get; set; } = "0";

    [ObservableProperty]
    public partial string FlipAttemptsText { get; set; } = "2";

    /// <summary>A failed flip holds the setups of its mount until the user retries or aborts (on), or ends the session (off).</summary>
    [ObservableProperty]
    public partial bool FlipPauseOnFailure { get; set; } = true;

    [ObservableProperty]
    public partial string FlipToleranceText { get; set; } = "60";

    [ObservableProperty]
    public partial string FlipCenterAttemptsText { get; set; } = "5";

    /// <summary>The flip in a sentence: "Hold new exposures 5 min before the meridian, flip 2 min after it, at the latest 15 min after."</summary>
    public string FlipSummary => FlipSentence(_flipUsesDefaults ? ApplicationFlip : _flip with { Enabled = FlipEnabled });

    private static string FlipSentence(MeridianFlipSettings s) => s.Enabled
        ? string.Create(CultureInfo.InvariantCulture, $"Hold new exposures {s.PauseBeforeMeridianMinutes:0.#} min before the meridian, flip {s.FlipAfterMeridianMinutes:0.#} min after it, at the latest {s.LatestAllowedFlipMinutes:0.#} min after.")
        : "Off";

    /// <summary>The workflow follows the application's meridian flip defaults (Settings); nothing of them is copied into it.</summary>
    public bool FlipUsesDefaults => _flipUsesDefaults;

    /// <summary>The workflow has a meridian flip of its own.</summary>
    public bool FlipIsCustom => !_flipUsesDefaults;

    /// <summary>"Using defaults · Hold −5m · Flip +2m · Recenter · Guiding", or that the defaults are off.</summary>
    public string FlipDefaultsSummary
    {
        get
        {
            var s = ApplicationFlip;
            if (!s.Enabled)
            {
                return "Using defaults · off (turn it on in Settings → Meridian Flip, or customize it for this workflow)";
            }

            var parts = new List<string>
            {
                string.Create(CultureInfo.InvariantCulture, $"Hold -{s.PauseBeforeMeridianMinutes:0.#}m"),
                string.Create(CultureInfo.InvariantCulture, $"Flip +{s.FlipAfterMeridianMinutes:0.#}m"),
            };
            if (s.RecenterAfterFlip)
            {
                parts.Add("Recenter");
            }

            if (s.VerifyRotationAfterFlip)
            {
                parts.Add("Rotation");
            }

            if (s.AutofocusAfterFlip)
            {
                parts.Add("Autofocus");
            }

            if (s.RestartGuidingAfterFlip)
            {
                parts.Add("Guiding");
            }

            if (s.DitherAfterFlip)
            {
                parts.Add("Dither");
            }

            return "Using defaults · " + string.Join(" · ", parts);
        }
    }

    /// <summary>Follows the application's meridian flip defaults from now on; what was set for this workflow is dropped from it.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void UseFlipDefaults()
    {
        if (_flipUsesDefaults)
        {
            return;
        }

        _flipUsesDefaults = true;
        RaiseFlipModeChanged();
        Recompile(true);
    }

    /// <summary>Gives this workflow a meridian flip of its own, starting from the defaults it followed; from then on the defaults do not touch it.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void CustomizeFlip()
    {
        if (!_flipUsesDefaults)
        {
            return;
        }

        _flip = ApplicationFlip;
        _flipUsesDefaults = false;
        var wasLoading = _loading;
        _loading = true;
        try
        {
            ReadFlipFields();
        }
        finally
        {
            _loading = wasLoading;
        }

        RaiseFlipModeChanged();
        Recompile(true);
    }

    private void RaiseFlipModeChanged()
    {
        OnPropertyChanged(nameof(FlipUsesDefaults));
        OnPropertyChanged(nameof(FlipIsCustom));
        OnPropertyChanged(nameof(FlipFieldsVisible));
        OnPropertyChanged(nameof(FlipDefaultsSummary));
        OnPropertyChanged(nameof(FlipSummary));
        UseFlipDefaultsCommand.NotifyCanExecuteChanged();
        CustomizeFlipCommand.NotifyCanExecuteChanged();
    }

    // The settings changed: what follows the defaults follows them (compiled again, the document is not modified by it), and what is custom is left alone.
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        _post(() =>
        {
            RaiseFlipModeChanged();
            if (Definition is not null && _flipUsesDefaults)
            {
                Recompile(false);
            }
        });
    }

    /// <summary>Where the target is relative to the meridian, when the site is known: the countdown the session shows next to its target.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeridianInfo))]
    public partial string MeridianInfoText { get; private set; } = string.Empty;

    public bool HasMeridianInfo => MeridianInfoText.Length > 0;

    /// <summary>What the flips of the run are doing, one for each mount; empty while there is no run or no flip.</summary>
    public ObservableCollection<MeridianFlipStatusViewModel> FlipStatuses { get; } = [];

    public bool HasFlipStatus => FlipStatuses.Count > 0;

    partial void OnFlipEnabledChanged(bool value) => FlipEdited();
    partial void OnFlipPauseBeforeTextChanged(string value) => FlipEdited();
    partial void OnFlipAfterTextChanged(string value) => FlipEdited();
    partial void OnFlipLatestTextChanged(string value) => FlipEdited();
    partial void OnFlipStopGuidingChanged(bool value) => FlipEdited();
    partial void OnFlipRecenterChanged(bool value) => FlipEdited();
    partial void OnFlipRotationChanged(bool value) => FlipEdited();
    partial void OnFlipAutofocusChanged(bool value) => FlipEdited();
    partial void OnFlipRestartGuidingChanged(bool value) => FlipEdited();
    partial void OnFlipDitherChanged(bool value) => FlipEdited();
    partial void OnFlipPauseAfterTextChanged(string value) => FlipEdited();
    partial void OnFlipAttemptsTextChanged(string value) => FlipEdited();
    partial void OnFlipPauseOnFailureChanged(bool value) => FlipEdited();
    partial void OnFlipToleranceTextChanged(string value) => FlipEdited();
    partial void OnFlipCenterAttemptsTextChanged(string value) => FlipEdited();

    private void ReadFlipFields()
    {
        OnPropertyChanged(nameof(FlipUsesDefaults));
        OnPropertyChanged(nameof(FlipIsCustom));
        OnPropertyChanged(nameof(FlipDefaultsSummary));
        FlipEnabled = _flip.Enabled;
        FlipPauseBeforeText = _flip.PauseBeforeMeridianMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        FlipAfterText = _flip.FlipAfterMeridianMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        FlipLatestText = _flip.LatestAllowedFlipMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        FlipStopGuiding = _flip.StopGuidingBeforeFlip;
        FlipRecenter = _flip.RecenterAfterFlip;
        FlipRotation = _flip.VerifyRotationAfterFlip;
        FlipAutofocus = _flip.AutofocusAfterFlip;
        FlipRestartGuiding = _flip.RestartGuidingAfterFlip;
        FlipDither = _flip.DitherAfterFlip;
        FlipPauseAfterText = _flip.PauseAfterFlipMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        FlipAttemptsText = _flip.MaxFlipAttempts.ToString(CultureInfo.InvariantCulture);
        FlipPauseOnFailure = _flip.FailureBehavior == MeridianFlipFailureBehavior.PauseSession;
        FlipToleranceText = _flip.CenteringToleranceArcseconds.ToString("0.##", CultureInfo.InvariantCulture);
        FlipCenterAttemptsText = _flip.MaxCenteringAttempts.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(FlipSummary));
    }

    private void FlipEdited()
    {
        OnPropertyChanged(nameof(FlipSummary));
        if (_loading)
        {
            return;
        }

        if (_flipUsesDefaults)
        {
            return; // the fields are not shown; what is in them is not the workflow's
        }

        var problems = new List<string>();
        _flip = _flip with
        {
            Enabled = FlipEnabled,
            PauseBeforeMeridianMinutes = Number(FlipPauseBeforeText, "The pause before the meridian", "a number of minutes", problems, _flip.PauseBeforeMeridianMinutes),
            FlipAfterMeridianMinutes = Number(FlipAfterText, "The flip after the meridian", "a number of minutes", problems, _flip.FlipAfterMeridianMinutes),
            LatestAllowedFlipMinutes = Number(FlipLatestText, "The latest allowed flip", "a number of minutes", problems, _flip.LatestAllowedFlipMinutes),
            StopGuidingBeforeFlip = FlipStopGuiding,
            RecenterAfterFlip = FlipRecenter,
            VerifyRotationAfterFlip = FlipRotation,
            AutofocusAfterFlip = FlipAutofocus,
            RestartGuidingAfterFlip = FlipRestartGuiding,
            DitherAfterFlip = FlipDither,
            PauseAfterFlipMinutes = Number(FlipPauseAfterText, "The pause after the flip", "a number of minutes", problems, _flip.PauseAfterFlipMinutes),
            MaxFlipAttempts = Whole(FlipAttemptsText, "The flip attempts", problems, _flip.MaxFlipAttempts),
            FailureBehavior = FlipPauseOnFailure ? MeridianFlipFailureBehavior.PauseSession : MeridianFlipFailureBehavior.AbortSession,
            CenteringToleranceArcseconds = Number(FlipToleranceText, "The centering tolerance", "a number of arcseconds", problems, _flip.CenteringToleranceArcseconds),
            MaxCenteringAttempts = Whole(FlipCenterAttemptsText, "The centering attempts", problems, _flip.MaxCenteringAttempts),
        };
        _flipProblems = problems;
        RefreshMeridian();
        Recompile(true);
    }

    /// <summary>Reads the sky again for the countdown of the target; called about once a second while the page is shown.</summary>
    public void RefreshMeridian()
    {
        if (!FlipEnabled || Definition is null)
        {
            MeridianInfoText = string.Empty;
            return;
        }

        var site = (_draft.SiteProvider ?? _site)?.Invoke();
        if (site is null)
        {
            MeridianInfoText = "Set the observing site (Settings) to follow the meridian.";
            return;
        }

        var minutes = MeridianFlipTiming.HourAngleHours(_target.RightAscensionHours, (_draft.Clock ?? TimeProvider.System).GetUtcNow().UtcDateTime, site.LongitudeDegrees) * 60;
        MeridianInfoText = MeridianFlipTiming.PhaseOf(_flip, minutes) switch
        {
            MeridianFlipPhase.Monitoring => $"Meridian: {MeridianFormat(-minutes - _flip.PauseBeforeMeridianMinutes)} until the hold · {MeridianFormat(-minutes)} until the crossing",
            MeridianFlipPhase.Approaching when minutes < 0 => $"Meridian: new exposures are held when they do not fit · {MeridianFormat(-minutes)} until the crossing",
            MeridianFlipPhase.Approaching => $"Meridian: crossed {MeridianFormat(minutes)} ago · the flip is due in {MeridianFormat(_flip.FlipAfterMeridianMinutes - minutes)}",
            MeridianFlipPhase.FlipDue => $"Meridian: crossed {MeridianFormat(minutes)} ago · the flip is due",
            _ => $"Meridian: crossed {MeridianFormat(minutes)} ago · the latest allowed flip has passed",
        };
    }

    private static string MeridianFormat(double minutes) => MeridianFlipGroup.FormatMinutes(minutes);

    // The flips of the sequence that is running: one status for each mount.
    private void OnFlipGroupsChanged(object? sender, EventArgs e)
    {
        FlipStatuses.Clear();
        foreach (var group in _draft.FlipGroups)
        {
            var name = _registry.TryGet(group.MountId, out var device) ? device!.Name : group.MountId.Value;
            FlipStatuses.Add(new MeridianFlipStatusViewModel(group, name));
        }

        OnPropertyChanged(nameof(HasFlipStatus));
    }

    private void RefreshFlipStatus()
    {
        foreach (var status in FlipStatuses)
        {
            status.Refresh();
        }
    }

    // ---- loading

    // ---- conditions of the whole target: when imaging of it stops, whatever the blocks are doing

    /// <summary>When imaging of the target stops, for every block of it (any that is on): after the exposures that are running, and the Finish part follows.</summary>
    public ConditionTogglesViewModel TargetStop { get; }

    /// <summary>"Target stops: astronomical dawn or altitude &lt; 20°"; empty when nothing but the blocks end the imaging.</summary>
    public string TargetStopSummary => Definition is { } d && d.TargetStopAny.Count > 0 ? "Target stops: " + ConditionTogglesViewModel.SummarizeStop(d.TargetStopAny) : string.Empty;

    private IReadOnlyList<string> _targetStopProblems = [];

    private void TargetStopEdited()
    {
        if (_loading)
        {
            return;
        }

        _ = TargetStop.BuildStop();
        _targetStopProblems = TargetStop.Problems;
        Recompile(true);
    }

    /// <summary>Reads what the run says about its waits and stops into the rows; called about once a second while the page is shown, with <see cref="RefreshMeridian"/>.</summary>
    public void RefreshConditions()
    {
        if (Definition is null)
        {
            return;
        }

        var board = _draft.ConditionStatuses;
        foreach (var row in AllRows)
        {
            Sidera.Runtime.Sequencing.ConditionStatus? status = null;
            if (row.IsImaging)
            {
                // A block that still waits to start says why; once it runs, how it is doing.
                if (board.TryGet(WorkflowCompiler.Derive(row.Id, "start"), out var start) && start.Phase == Sidera.Runtime.Sequencing.ConditionPhase.Waiting)
                {
                    status = start;
                }
                else if (board.TryGet(WorkflowCompiler.Derive(row.Id, "repeat"), out var imaging))
                {
                    status = imaging;
                }
            }
            else if (row.Step is { Kind: WorkflowStepKind.Wait } && board.TryGet(WorkflowCompiler.Derive(row.Id, row.Section == WorkflowSection.Prepare ? "prepare" : "finish"), out var wait))
            {
                status = wait;
            }

            row.ConditionStatusText = status?.Text ?? string.Empty;
            row.ConditionStatusDetail = status?.Detail ?? string.Empty;
        }
    }

    // What the application's settings propose: used for what is created, never to change what exists.
    private MeridianFlipSettings ApplicationFlip => _settings?.MeridianFlip ?? new MeridianFlipSettings();

    private WorkflowDefinition.StartDefaults StartDefaults => new(_settings?.Autofocus ?? new Sidera.Desktop.Settings.AutofocusDefaults(), _settings?.Guiding ?? new Sidera.Desktop.Settings.GuidingDefaults());

    /// <summary>Starts a new session in the mode the settings choose: an empty workflow, or an empty Advanced sequence.</summary>
    public void StartNew()
    {
        _converted = null;
        if (_settings?.Sequencer.DefaultSessionMode == Sidera.Desktop.Settings.SessionMode.Advanced)
        {
            Load(null);
            return;
        }

        Load(WorkflowDefinition.NewEmpty(StartDefaults));
    }

    /// <summary>A starting point from the rigs there are, with a target of your own; the rows are what the first setup can do.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void StartFromTemplate()
    {
        var template = WorkflowDefinition.Template(_rigs?.GetAll().ToList() ?? [], _defaults, StartDefaults);
        Load(template with { Target = Definition?.Target ?? WorkflowTarget.Default }, modified: true);
    }

    public void Load(WorkflowDefinition? definition) => Load(definition, modified: false);

    private void Load(WorkflowDefinition? definition, bool modified)
    {
        _loading = true;
        try
        {
            PrepareRows.Clear();
            ImagingRows.Clear();
            FinishRows.Clear();
            SelectedRow = null;
            if (definition is null)
            {
                Definition = null;
                _draft.ExternalProblems = [];
                Problems = [];
                _draft.Revalidate();
                RaiseModeChanged();
                return;
            }

            _target = definition.Target;
            _dither = definition.Dither;
            _policies = [.. definition.AutofocusPolicies];
            _flip = definition.FlipSettings;
            _flipUsesDefaults = definition.MeridianFlipUsesDefaults;
            _flipProblems = [];
            TargetStop.Load(definition.TargetStopAny);
            _targetStopProblems = [];
            foreach (var step in definition.Prepare)
            {
                PrepareRows.Add(new WorkflowRowViewModel(this, WorkflowSection.Prepare, step, null));
            }

            foreach (var block in definition.Imaging)
            {
                ImagingRows.Add(new WorkflowRowViewModel(this, WorkflowSection.Imaging, null, block));
            }

            foreach (var step in definition.Finish)
            {
                FinishRows.Add(new WorkflowRowViewModel(this, WorkflowSection.Finish, step, null));
            }

            Definition = definition;
            ReadSetups();
            ReadTargetFields();
            ReadDitherFields();
            ReadFlipFields();
        }
        finally
        {
            _loading = false;
        }

        Renumber();
        SelectedRow = AllRows.FirstOrDefault();
        MarkSelection();
        ReadPolicyFields();
        Recompile(modified);
        RaiseModeChanged();
    }

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(CanReturnToWorkflow));
        OnPropertyChanged(nameof(WhyNotWorkflowText));
        SwitchToWorkflowCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasUnreadableFields));
        StartFromTemplateCommand.NotifyCanExecuteChanged();
        NotifyCommands();
    }

    // ---- the setups

    private IReadOnlyList<Rig> Rigs => _rigs?.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

    private Rig? RigOf(RigId? id) => id is { } rigId && _rigs is not null && _rigs.TryGet(rigId, out var rig) ? rig : null;

    // The setup a row works with: the one it names, else (for "Auto") the only setup there is.
    private Rig? ResolvedSetup(RigId? named) => named is not null ? RigOf(named) : Rigs.Count == 1 ? Rigs[0] : null;

    private string DescribeSetup(Rig rig)
    {
        var camera = _registry.TryGet(rig.CameraId, out var device) ? device!.Name : "no camera";
        var optics = rig.Optics is { } o ? $" · {o.FocalLengthMm:0.#} mm" : string.Empty;
        return camera + optics;
    }

    /// <summary>Reads the rigs again (one came, went or changed) and gives every row its choices.</summary>
    public void RefreshSetups()
    {
        if (Definition is null)
        {
            return;
        }

        var selected = SelectedRow?.Id;
        _loading = true;
        try
        {
            ReadSetups();
        }
        finally
        {
            _loading = false;
        }

        SelectedRow = AllRows.FirstOrDefault(r => r.Id == selected) ?? SelectedRow;
        Recompile(false);
    }

    private void ReadSetups()
    {
        var rigs = Rigs;
        SetupChoices.Clear();
        PointingChoices.Clear();
        CountedChoices.Clear();
        var auto = new SetupChoice(null, "Auto", rigs.Count == 1 ? $"the only setup: {rigs[0].Name}" : "the setups of the workflow");
        PointingChoices.Add(auto with { Name = "Auto", Detail = "the first setup of the mount" });
        CountedChoices.Add(auto with { Detail = "the first setup that is imaged" });
        foreach (var rig in rigs)
        {
            var choice = new SetupChoice(rig.Id, rig.Name, DescribeSetup(rig));
            SetupChoices.Add(choice);
            PointingChoices.Add(choice);
            CountedChoices.Add(choice);
        }

        foreach (var row in AllRows)
        {
            var rowChoices = row.IsImaging ? SetupChoices.ToList() : [auto, .. SetupChoices];
            var current = row.Step?.Setup ?? row.Block?.Setup;
            var selected = rowChoices.FirstOrDefault(c => c.Id == current) ?? (row.IsImaging ? (current is null ? rowChoices.FirstOrDefault() : null) : auto);
            row.SetChoices(rowChoices, selected);
            if (row.IsImaging)
            {
                var rig = ResolvedSetup(row.Block!.Setup);
                row.SetFilters(FiltersOf(rig), row.Block.FilterSlot);
            }
        }

        SelectedPointing = PointingChoices.FirstOrDefault(c => c.Id == _target.PointingSetup) ?? PointingChoices[0];
        SelectedCounted = CountedChoices.FirstOrDefault(c => c.Id == _dither.CountedSetup) ?? CountedChoices[0];
    }

    private IEnumerable<FilterChoice> FiltersOf(Rig? rig)
    {
        yield return new FilterChoice(null, "No filter change");
        if (rig?.FilterWheelId is { } id && _registry.TryGet(id, out var device) && device is IFilterWheel wheel)
        {
            foreach (var slot in wheel.Slots)
            {
                yield return new FilterChoice(slot.Index, slot.Name);
            }
        }
    }

    private string FilterName(Rig? rig, int? slot)
    {
        if (slot is not { } index)
        {
            return "—";
        }

        return rig?.FilterWheelId is { } id && _registry.TryGet(id, out var device) && device is IFilterWheel wheel && index >= 0 && index < wheel.Slots.Count
            ? wheel.Slots[index].Name
            : string.Create(CultureInfo.InvariantCulture, $"slot {index}");
    }

    // ---- fields of the target and the policies

    private void ReadTargetFields()
    {
        TargetNameText = _target.Name;
        TargetRaText = _target.RightAscensionHours.ToString("0.####", CultureInfo.InvariantCulture);
        TargetDecText = _target.DeclinationDegrees.ToString("0.####", CultureInfo.InvariantCulture);
        TargetRotationText = _target.DesiredRotationDegrees?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private void ReadDitherFields()
    {
        DitherEnabled = _dither.Enabled;
        DitherEveryText = _dither.EveryNFrames.ToString(CultureInfo.InvariantCulture);
        DitherAmplitudeText = _dither.AmplitudePixels.ToString("0.##", CultureInfo.InvariantCulture);
        DitherThresholdText = _dither.SettleThresholdPixels.ToString("0.##", CultureInfo.InvariantCulture);
        DitherStableText = _dither.SettleStableSeconds.ToString("0.##", CultureInfo.InvariantCulture);
        DitherTimeoutText = _dither.SettleTimeoutSeconds.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private Rig? PolicySetupOf(WorkflowRowViewModel row) => row.Block is { } block ? ResolvedSetup(block.Setup) : null;

    private void ReadPolicyFields()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            if (SelectedRow is { IsImaging: true } row && PolicySetupOf(row) is { } rig)
            {
                var policy = (Definition ?? WorkflowDefinition.Empty with { AutofocusPolicies = _policies }).AutofocusOf(rig.Id);
                policy = _policies.FirstOrDefault(p => p.Setup == rig.Id) ?? policy;
                PolicyEnabled = policy.Enabled;
                PolicyAtStart = policy.AtStart;
                PolicyIntervalText = policy.IntervalMinutes.ToString("0.##", CultureInfo.InvariantCulture);
                PolicyAfterFilterChange = policy.AfterFilterChange;
                PolicyExposureText = policy.Settings.ExposureSeconds.ToString("0.##", CultureInfo.InvariantCulture);
                PolicyStepText = policy.Settings.StepSize.ToString(CultureInfo.InvariantCulture);
                PolicySamplesText = policy.Settings.SampleCount.ToString(CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _loading = wasLoading;
        }

        OnPropertyChanged(nameof(HasPolicy));
        OnPropertyChanged(nameof(PolicySetupName));
    }

    partial void OnTargetNameTextChanged(string value) => TargetEdited();
    partial void OnTargetRaTextChanged(string value) => TargetEdited();
    partial void OnTargetDecTextChanged(string value) => TargetEdited();
    partial void OnTargetRotationTextChanged(string value) => TargetEdited();
    partial void OnSelectedPointingChanged(SetupChoice? value) => TargetEdited();

    private List<string> _targetProblems = [];

    private void TargetEdited()
    {
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        var ra = Number(TargetRaText, "The target's right ascension", "a number of hours", problems, _target.RightAscensionHours);
        var dec = Number(TargetDecText, "The target's declination", "a number of degrees", problems, _target.DeclinationDegrees);
        double? rotation = _target.DesiredRotationDegrees;
        if (string.IsNullOrWhiteSpace(TargetRotationText))
        {
            rotation = null;
        }
        else
        {
            rotation = Number(TargetRotationText, "The target's rotation", "a number of degrees", problems, rotation ?? 0);
        }

        if (problems.Count == 0 && (ra is < 0 or >= 24 || dec is < -90 or > 90))
        {
            problems.Add("The target's coordinates are out of range: right ascension 0 to 24 hours, declination -90 to 90 degrees.");
        }

        _targetProblems = problems;
        _target = new WorkflowTarget(string.IsNullOrWhiteSpace(TargetNameText) ? "Target" : TargetNameText.Trim(), ra, dec, rotation, SelectedPointing?.Id);
        Recompile(true);
    }

    partial void OnDitherEnabledChanged(bool value) => DitherEdited();
    partial void OnDitherEveryTextChanged(string value) => DitherEdited();
    partial void OnSelectedCountedChanged(SetupChoice? value) => DitherEdited();
    partial void OnDitherAmplitudeTextChanged(string value) => DitherEdited();
    partial void OnDitherThresholdTextChanged(string value) => DitherEdited();
    partial void OnDitherStableTextChanged(string value) => DitherEdited();
    partial void OnDitherTimeoutTextChanged(string value) => DitherEdited();

    private List<string> _ditherProblems = [];

    private void DitherEdited()
    {
        OnPropertyChanged(nameof(DitherSummary));
        if (_loading)
        {
            return;
        }

        var problems = new List<string>();
        var every = Whole(DitherEveryText, "Dither every N frames", problems, _dither.EveryNFrames);
        _ditherProblems = problems;
        _dither = new WorkflowDither(
            DitherEnabled, every, SelectedCounted?.Id,
            Number(DitherAmplitudeText, "The dither amplitude", "a number of pixels", problems, _dither.AmplitudePixels),
            Number(DitherThresholdText, "The settle threshold", "a number of pixels", problems, _dither.SettleThresholdPixels),
            Number(DitherStableText, "The settle time", "a number of seconds", problems, _dither.SettleStableSeconds),
            Number(DitherTimeoutText, "The settle timeout", "a number of seconds", problems, _dither.SettleTimeoutSeconds));
        Recompile(true);
    }

    partial void OnPolicyEnabledChanged(bool value) => PolicyEdited();
    partial void OnPolicyAtStartChanged(bool value) => PolicyEdited();
    partial void OnPolicyIntervalTextChanged(string value) => PolicyEdited();
    partial void OnPolicyAfterFilterChangeChanged(bool value) => PolicyEdited();
    partial void OnPolicyExposureTextChanged(string value) => PolicyEdited();
    partial void OnPolicyStepTextChanged(string value) => PolicyEdited();
    partial void OnPolicySamplesTextChanged(string value) => PolicyEdited();

    private List<string> _policyProblems = [];

    private void PolicyEdited()
    {
        if (_loading || SelectedRow is not { IsImaging: true } row || PolicySetupOf(row) is not { } rig)
        {
            return;
        }

        var problems = new List<string>();
        var current = _policies.FirstOrDefault(p => p.Setup == rig.Id) ?? new SetupAutofocus(rig.Id, false, false, 0, false, AutofocusSettings.Default);
        var updated = current with
        {
            Enabled = PolicyEnabled,
            AtStart = PolicyAtStart,
            AfterFilterChange = PolicyAfterFilterChange,
            IntervalMinutes = Math.Max(0, Number(PolicyIntervalText, "The autofocus interval", "a number of minutes", problems, current.IntervalMinutes)),
            Settings = new AutofocusSettings(
                Number(PolicyExposureText, "The autofocus exposure", "a number of seconds", problems, current.Settings.ExposureSeconds),
                Whole(PolicyStepText, "The autofocus step size", problems, current.Settings.StepSize),
                Whole(PolicySamplesText, "The autofocus samples", problems, current.Settings.SampleCount)),
        };
        _policyProblems = problems;
        _policies = [.. _policies.Where(p => p.Setup != rig.Id), updated];
        Recompile(true);
    }

    // ---- selection

    /// <summary>Selects a row; its fields are shown in the inspector.</summary>
    [RelayCommand]
    public void SelectRow(WorkflowRowViewModel? row)
    {
        SelectedRow = row;
        MarkSelection();
        ReadPolicyFields();
        NotifyCommands();
    }

    private void MarkSelection()
    {
        foreach (var row in AllRows)
        {
            row.IsSelected = ReferenceEquals(row, SelectedRow);
        }
    }

    // ---- adding, removing and moving rows

    private ObservableCollection<WorkflowRowViewModel> RowsOf(WorkflowSection section) => section switch
    {
        WorkflowSection.Prepare => PrepareRows,
        WorkflowSection.Imaging => ImagingRows,
        _ => FinishRows,
    };

    private WorkflowStep NewStep(WorkflowStepKind kind)
    {
        var af = StartDefaults.Autofocus;
        return new(
            Guid.NewGuid(), kind, null, true, _defaults.DelaySeconds, 60, 5, 5,
            kind == WorkflowStepKind.Autofocus ? new AutofocusSettings(af.ExposureSeconds, af.StepSize, af.SampleCount) : null);
    }

    private WorkflowRowViewModel Add(WorkflowSection section, WorkflowStep? step, ImagingBlock? block)
    {
        if (Definition is null)
        {
            Load(WorkflowDefinition.NewEmpty(StartDefaults), modified: true);
        }

        var row = new WorkflowRowViewModel(this, section, step, block);
        var rows = RowsOf(section);
        var after = SelectedRow is { } selected && selected.Section == section ? rows.IndexOf(selected) + 1 : rows.Count;
        rows.Insert(Math.Min(after, rows.Count), row);
        _loading = true;
        try
        {
            ReadSetups();
        }
        finally
        {
            _loading = false;
        }

        SelectRow(row);
        Renumber();
        Recompile(true);
        RaiseModeChanged();
        return row;
    }

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddSlewAndCenter() => Add(WorkflowSection.Prepare, NewStep(WorkflowStepKind.SlewAndCenter), null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddAutofocusStep() => Add(WorkflowSection.Prepare, NewStep(WorkflowStepKind.Autofocus), null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddStartGuiding() => Add(WorkflowSection.Prepare, NewStep(WorkflowStepKind.StartGuiding), null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddWaitToPrepare() => Add(WorkflowSection.Prepare, NewStep(WorkflowStepKind.Wait), null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddStopGuiding() => Add(WorkflowSection.Finish, NewStep(WorkflowStepKind.StopGuiding), null);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddWaitToFinish() => Add(WorkflowSection.Finish, NewStep(WorkflowStepKind.Wait), null);

    /// <summary>Adds an imaging block for a setup that is not imaged yet (else the first one), with the exposure a new exposure starts with.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddImagingBlock()
    {
        var used = ImagingRows.Select(r => r.Block!.Setup).ToHashSet();
        var rig = Rigs.FirstOrDefault(r => !used.Contains(r.Id)) ?? Rigs.FirstOrDefault();

        // A setup that comes into the workflow focuses by itself when the settings propose it (and it can); a setup that has a policy keeps it.
        if (rig?.FocuserId is not null && StartDefaults.Autofocus.PolicyEnabled && _policies.All(p => p.Setup != rig.Id))
        {
            _policies.Add(WorkflowDefinition.AutofocusPolicyOf(rig.Id, StartDefaults.Autofocus));
        }

        Add(WorkflowSection.Imaging, null, new ImagingBlock(Guid.NewGuid(), rig?.Id, null, _defaults.ExposureSeconds, 10));
    }

    private bool CanRemove => IsEditable && SelectedRow is not null;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var rows = RowsOf(row.Section);
        var index = rows.IndexOf(row);
        rows.Remove(row);
        var next = rows.Count > 0 ? rows[Math.Min(index, rows.Count - 1)] : AllRows.FirstOrDefault();
        SelectRow(next);
        Renumber();
        Recompile(true);
        RaiseModeChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void DuplicateSelected()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        Add(row.Section, row.Step is { } step ? step with { Id = Guid.NewGuid() } : null, row.Block is { } block ? block with { Id = Guid.NewGuid() } : null);
    }

    public bool CanMoveUp => IsEditable && SelectedRow is { } row && RowsOf(row.Section).IndexOf(row) > 0;

    public bool CanMoveDown => IsEditable && SelectedRow is { } row && RowsOf(row.Section) is var rows && rows.IndexOf(row) is var i && i >= 0 && i < rows.Count - 1;

    /// <summary>Moves the selected row up within its section. A row never changes section: a block cannot become a Finish step, so there is no way to drag it there.</summary>
    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(1);

    private void Move(int by)
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        var rows = RowsOf(row.Section);
        var from = rows.IndexOf(row);
        var to = from + by;
        if (from < 0 || to < 0 || to >= rows.Count)
        {
            return;
        }

        rows.Move(from, to);
        Renumber();
        Recompile(true);
        NotifyCommands();
    }

    /// <summary>Switches the row on or off; an off row stays in the table and is left out of the sequence.</summary>
    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void ToggleEnabled()
    {
        if (SelectedRow is { } row)
        {
            row.Enabled = !row.Enabled;
        }
    }

    private void Renumber()
    {
        OnPropertyChanged(nameof(HasPrepareRows));
        OnPropertyChanged(nameof(HasImagingRows));
        OnPropertyChanged(nameof(HasFinishRows));
        foreach (var rows in new[] { PrepareRows, ImagingRows, FinishRows })
        {
            for (var i = 0; i < rows.Count; i++)
            {
                rows[i].Number = i + 1;
            }
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsRunning));
        foreach (var command in new IRelayCommand[]
        {
            AddSlewAndCenterCommand, AddAutofocusStepCommand, AddStartGuidingCommand, AddWaitToPrepareCommand, AddStopGuidingCommand, AddWaitToFinishCommand, AddImagingBlockCommand,
            RemoveSelectedCommand, DuplicateSelectedCommand, MoveUpCommand, MoveDownCommand, ToggleEnabledCommand, StartFromTemplateCommand, ConvertToAdvancedCommand, SwitchToWorkflowCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    // ---- converting to Advanced

    /// <summary>A conversion was asked for once and waits for the second click: it cannot be undone.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingAdvanced { get; private set; }

    /// <summary>
    /// Leaves the workflow: the steps it compiled to stay as they are, in the Advanced editor, and the workflow is no longer kept. Asked twice, because the steps are not turned back into a
    /// workflow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void ConvertToAdvanced()
    {
        if (Definition is null)
        {
            return;
        }

        if (!IsConfirmingAdvanced)
        {
            IsConfirmingAdvanced = true;
            return;
        }

        // The steps stay exactly as the workflow compiled them: no action is lost. What was the workflow is remembered, so that going back is possible for as long as the steps are untouched.
        var workflow = Definition;
        IsConfirmingAdvanced = false;
        Load(null, modified: false);
        _draft.ExternalProblems = [];
        _draft.Revalidate();
        _converted = (workflow, Fingerprint(_draft.Snapshot()));
        RaiseModified();
        RaiseModeChanged();
    }

    /// <summary>What the user is told before the explicit tree is opened.</summary>
    public string AdvancedWarningText =>
        "Converting to Advanced exposes the explicit action tree. Workflow policies will no longer be editable through the high-level workflow model.";

    /// <summary>
    /// Going back to a workflow is only possible where it is exact: the sequence is empty, or it is still the very steps that a workflow compiled to (nothing of the tree was changed since it was
    /// opened). A tree is never read back into a workflow by guessing what its steps mean.
    /// </summary>
    public bool CanReturnToWorkflow =>
        Definition is null && IsEditable && (_draft.IsEmpty || (_converted is { } converted && Fingerprint(_draft.Snapshot()) == converted.Fingerprint));

    /// <summary>Why the sequence cannot become a workflow; empty when it can (or already is one).</summary>
    public string WhyNotWorkflowText => Definition is not null || CanReturnToWorkflow ? string.Empty : "This sequence cannot be represented as a Workflow.";

    /// <summary>Back to the workflow: a new empty one for an empty sequence, or the workflow that the steps still are.</summary>
    [RelayCommand(CanExecute = nameof(CanReturnToWorkflow))]
    private void SwitchToWorkflow()
    {
        if (Definition is not null)
        {
            return;
        }

        var workflow = _draft.IsEmpty || _converted is null ? WorkflowDefinition.NewEmpty(StartDefaults) : _converted.Value.Workflow;
        _converted = null;
        Load(workflow, modified: true);
        RaiseModeChanged();
    }

    // The steps as the document would hold them: two sequences with the same fingerprint are the same sequence.
    private static string Fingerprint(IReadOnlyList<SequenceStepDraft> steps)
    {
        return Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(steps));
    }

    [RelayCommand]
    private void CancelConvertToAdvanced() => IsConfirmingAdvanced = false;

    // ---- the editor changed a row

    internal void RowEdited(WorkflowRowViewModel row)
    {
        if (_loading)
        {
            return;
        }

        // A block that got another setup has the filters of that setup, and its policy is another one.
        if (row.IsImaging)
        {
            var wasLoading = _loading;
            _loading = true;
            try
            {
                row.SetFilters(FiltersOf(ResolvedSetup(row.Block!.Setup)), row.Block.FilterSlot);
            }
            finally
            {
                _loading = wasLoading;
            }

            if (ReferenceEquals(row, SelectedRow))
            {
                ReadPolicyFields();
            }
        }

        Recompile(true);
    }

    // ---- compiling

    private WorkflowDefinition BuildDefinition() => new(
        _target,
        PrepareRows.Select(r => r.Step!).ToList(),
        ImagingRows.Select(r => r.Block!).ToList(),
        FinishRows.Select(r => r.Step!).ToList(),
        _dither,
        _policies.Where(p => ImagingRows.Any(r => ResolvedSetup(r.Block!.Setup)?.Id == p.Setup)).ToList(),
        _flipUsesDefaults || _flip == new MeridianFlipSettings() ? null : _flip,
        _flipUsesDefaults,
        TargetStopConditions());

    private IReadOnlyList<Sidera.Core.Conditions.WorkflowCondition>? TargetStopConditions()
    {
        var conditions = TargetStop.BuildStop();
        return conditions.Count > 0 ? conditions : null;
    }

    private string LabelOf(Guid id)
    {
        foreach (var rows in new[] { PrepareRows, ImagingRows, FinishRows })
        {
            if (rows.FirstOrDefault(r => r.Id == id) is { } row)
            {
                return $"{(row.IsImaging ? "Imaging block" : row.Title)} {row.Number}";
            }
        }

        return "Workflow";
    }

    /// <summary>Compiles the workflow into the draft of the sequence, finds what is wrong with it and tells the rows. The document is modified when <paramref name="modified"/>.</summary>
    private void Recompile(bool modified)
    {
        if (_loading)
        {
            return;
        }

        var definition = BuildDefinition();
        Definition = definition;
        var compilation = WorkflowCompiler.Compile(definition, _rigs, _defaults, ApplicationFlip);

        var parse = AllRows.SelectMany(r => r.ParseProblems.Select(p => $"{LabelOf(r.Id)}: {p}")).Concat(_targetProblems).Concat(_ditherProblems).Concat(_policyProblems).Concat(_flipProblems).Concat(_targetStopProblems).ToList();
        _unreadable = _targetProblems.Count > 0 || _ditherProblems.Count > 0 || _policyProblems.Count > 0 || _flipProblems.Count > 0;
        var compile = compilation.Problems.Select(p => p.ElementId is { } id ? $"{LabelOf(id)}: {p.Message}" : p.Message).ToList();
        _draft.ExternalProblems = [.. parse, .. compile];
        _draft.ReplaceSteps(compilation.Steps);

        // The problems of the sequence that the compiled steps have, said at the row they came from.
        var perRow = AllRows.ToDictionary(r => r.Id, r => new List<string>(r.ParseProblems));
        var shown = new List<string>(parse);
        foreach (var problem in compilation.Problems)
        {
            if (problem.ElementId is { } id && perRow.TryGetValue(id, out var list))
            {
                list.Add(problem.Message);
            }

            shown.Add(problem.ElementId is { } known ? $"{LabelOf(known)}: {problem.Message}" : problem.Message);
        }

        foreach (var draftRow in _draft.Rows.Where(r => r.HasProblems))
        {
            if (compilation.Origins.TryGetValue(draftRow.Id, out var origin) && perRow.TryGetValue(origin, out var list))
            {
                foreach (var message in draftRow.Problems.Where(m => m != "A step inside has a problem."))
                {
                    if (!list.Contains(message))
                    {
                        list.Add(message);
                        shown.Add($"{LabelOf(origin)}: {message}");
                    }
                }
            }
        }

        foreach (var row in AllRows)
        {
            row.ProblemText = string.Join(" ", perRow[row.Id].Distinct());
        }

        Problems = shown.Distinct().ToList();
        UpdateLabels(definition, compilation);
        OnPropertyChanged(nameof(HasUnreadableFields));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanRunText));
        OnPropertyChanged(nameof(TargetStopSummary));
        if (modified)
        {
            RaiseModified();
        }
    }

    private void UpdateLabels(WorkflowDefinition definition, WorkflowCompilation compilation)
    {
        var imaged = ImagingRows.Where(r => r.Block!.Enabled).Select(r => ResolvedSetup(r.Block!.Setup)).OfType<Rig>().DistinctBy(r => r.Id).ToList();
        var counted = definition.Dither.Enabled
            ? (definition.Dither.CountedSetup is { } named ? imaged.FirstOrDefault(r => r.Id == named) : imaged.FirstOrDefault())
            : null;

        foreach (var row in PrepareRows.Concat(FinishRows))
        {
            var step = row.Step!;
            var rig = step.Setup is null ? null : RigOf(step.Setup);
            row.SetupLabel = rig?.Name ?? (step.Setup is not null ? "missing" : step.Kind is WorkflowStepKind.Wait ? string.Empty : AutoLabel(imaged));
            var scope = rig is not null ? [rig] : imaged.Count > 0 ? imaged : Rigs.Count == 1 ? Rigs.ToList() : new List<Rig>();
            row.Summary = step.Kind switch
            {
                WorkflowStepKind.SlewAndCenter => string.Create(CultureInfo.InvariantCulture, $"{definition.Target.Name} · {step.ToleranceArcseconds:0.##}\" · up to {step.MaxAttempts} attempts")
                    + (definition.Target.DesiredRotationDegrees is { } rotation ? string.Create(CultureInfo.InvariantCulture, $" · rotation {rotation:0.#}°") : string.Empty),
                WorkflowStepKind.Autofocus => string.Create(CultureInfo.InvariantCulture, $"Once · {(step.Autofocus ?? AutofocusSettings.Default).ExposureSeconds:0.##} s × {(step.Autofocus ?? AutofocusSettings.Default).SampleCount} samples"),
                WorkflowStepKind.StartGuiding => GuiderNames(scope) is { Length: > 0 } guiders ? $"{guiders} · settles automatically" : "no guider",
                WorkflowStepKind.StopGuiding => GuiderNames(scope),
                _ when step.WaitMode == WorkflowWaitMode.UntilTime => "Until " + ConditionTogglesViewModel.SummarizeStart(step.UntilAll),
                _ when step.WaitMode == WorkflowWaitMode.UntilCondition => "Until " + ConditionTogglesViewModel.SummarizeStart(step.UntilAll),
                _ => string.Create(CultureInfo.InvariantCulture, $"{step.Seconds:0.##} s"),
            };
        }

        foreach (var row in ImagingRows)
        {
            var block = row.Block!;
            var rig = ResolvedSetup(block.Setup);
            row.SetupLabel = rig?.Name ?? "Choose a setup";
            row.FilterLabel = FilterName(rig, block.FilterSlot);
            row.ExposureLabel = string.Create(CultureInfo.InvariantCulture, $"{block.ExposureSeconds:0.##} s");
            row.FramesLabel = block.Frames.ToString(CultureInfo.InvariantCulture);
            row.Summary = string.Create(CultureInfo.InvariantCulture, $"{(block.FilterSlot is null ? string.Empty : FilterName(rig, block.FilterSlot) + " · ")}{block.ExposureSeconds:0.##} s × {block.Frames}");
            row.StartText = block.StartAll.Count > 0 ? "Start: " + ConditionTogglesViewModel.SummarizeStart(block.StartAll) : string.Empty;
            row.StopText = block.StopAny.Count > 0 ? "Stop: " + ConditionTogglesViewModel.SummarizeStop(block.StopAny, block.Frames) : string.Empty;
            row.DitherLabel = DitherLabel(definition, rig, counted, imaged);
            var policy = rig is null ? null : definition.AutofocusPolicies.FirstOrDefault(p => p.Setup == rig.Id);
            row.AutofocusLabel = policy is { Enabled: true } p2 ? AutofocusWhen(p2) : "Off";
        }
    }

    private static string AutoLabel(IReadOnlyList<Rig> imaged) => imaged.Count switch
    {
        0 => "Auto",
        1 => "Auto · " + imaged[0].Name,
        _ => "Auto · all",
    };

    private string GuiderNames(IReadOnlyList<Rig> scope) => string.Join(
        ", ",
        scope.Select(r => r.GuiderId).OfType<DeviceId>().Distinct().Select(id => _registry.TryGet(id, out var device) ? device!.Name : id.Value));

    private static string AutofocusWhen(SetupAutofocus policy)
    {
        var parts = new List<string>();
        if (policy.AtStart)
        {
            parts.Add("At start");
        }

        if (policy.IntervalMinutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"every {policy.IntervalMinutes:0.##} min"));
        }

        if (policy.AfterFilterChange)
        {
            parts.Add("after filter change");
        }

        return parts.Count == 0 ? "No trigger" : string.Join(" · ", parts);
    }

    // "Every 2" on the setup that is counted, "Coordinated" on the others that share its mount, "—" where it does not apply.
    private static string DitherLabel(WorkflowDefinition definition, Rig? rig, Rig? counted, IReadOnlyList<Rig> imaged)
    {
        if (!definition.Dither.Enabled || rig is null || counted is null)
        {
            return "Off";
        }

        if (rig.Id == counted.Id)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Every {definition.Dither.EveryNFrames}");
        }

        return counted.MountId is not null && rig.MountId == counted.MountId ? "Coordinated" : "—";
    }

    // ---- the framing hands over a target

    /// <summary>
    /// "Add to Session" of the framing: in a workflow the target becomes the workflow's target (and the setup of the framing its pointing setup); in an empty session a workflow is started for
    /// it. A session of explicit steps is left to the framing, which adds its steps as it always did (<c>null</c>). No sync step is ever added.
    /// </summary>
    internal string? AddTarget(WorkflowTargetRequest request)
    {
        if (!_draft.IsEditable)
        {
            return null;
        }

        if (Definition is null)
        {
            if (!_draft.IsEmpty)
            {
                return null;
            }

            Load(WorkflowDefinition.Template(_rigs?.GetAll().ToList() ?? [], _defaults, StartDefaults), modified: true);
        }

        _loading = true;
        try
        {
            _target = new WorkflowTarget(request.Name, request.RightAscensionHours, request.DeclinationDegrees, request.DesiredRotationDegrees, request.Setup);
            ReadTargetFields();
            SelectedPointing = PointingChoices.FirstOrDefault(c => c.Id == request.Setup) ?? PointingChoices[0];
        }
        finally
        {
            _loading = false;
        }

        // The workflow centers the target: a Slew & Center is there for it, and a setup of the framing is imaged when nothing is.
        var added = false;
        if (!PrepareRows.Any(r => r.Step is { Kind: WorkflowStepKind.SlewAndCenter }))
        {
            PrepareRows.Insert(0, new WorkflowRowViewModel(this, WorkflowSection.Prepare, NewStep(WorkflowStepKind.SlewAndCenter), null));
            added = true;
        }

        if (ImagingRows.Count == 0 && request.Setup is { } setup)
        {
            ImagingRows.Add(new WorkflowRowViewModel(this, WorkflowSection.Imaging, null, new ImagingBlock(Guid.NewGuid(), setup, null, _defaults.ExposureSeconds, 10)));
            added = true;
        }

        if (added)
        {
            _loading = true;
            try
            {
                ReadSetups();
            }
            finally
            {
                _loading = false;
            }
        }

        Renumber();
        Recompile(true);
        RaiseModeChanged();
        return $"Set the target of the workflow to {request.Name}.";
    }

    // ---- following the draft and the run

    private bool _ownModification;

    private void RaiseModified()
    {
        _ownModification = true;
        try
        {
            _draft.MarkModified();
        }
        finally
        {
            _ownModification = false;
        }
    }

    // Something other than this editor changed the steps while the session is a workflow (a step pasted, a step added by code): the steps are no longer what the workflow compiles to, so the
    // session is a sequence of explicit steps from here on, and nothing is lost: the steps are kept as they are.
    // The steps of an Advanced sequence changed: whether it can still be a workflow is read again.
    private void OnDraftChanged(object? sender, EventArgs e)
    {
        if (Definition is null)
        {
            OnPropertyChanged(nameof(CanReturnToWorkflow));
            OnPropertyChanged(nameof(WhyNotWorkflowText));
            SwitchToWorkflowCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnDraftModified(object? sender, EventArgs e)
    {
        if (Definition is not null && !_ownModification)
        {
            Load(null, modified: false);
            RaiseModeChanged();
        }
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SequenceDraftViewModel.IsEditable))
        {
            NotifyCommands();
        }
    }

    private void OnExecutionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExecutionOverviewViewModel.Lanes) && _execution is not null)
        {
            foreach (var lane in _wiredLanes)
            {
                lane.PropertyChanged -= OnLaneChanged;
            }

            _wiredLanes.Clear();
            foreach (var lane in _execution.Lanes)
            {
                lane.PropertyChanged += OnLaneChanged;
                _wiredLanes.Add(lane);
            }
        }

        RefreshProgress();
    }

    private void OnLaneChanged(object? sender, PropertyChangedEventArgs e) => RefreshProgress();

    /// <summary>Shows what each setup is doing on its imaging rows: its frames, what it is at, and an autofocus it is running.</summary>
    public void RefreshProgress()
    {
        if (_execution is null)
        {
            return;
        }

        SharedActivityText = _execution.IsRunning ? _execution.SharedActivity ?? string.Empty : string.Empty;
        foreach (var row in ImagingRows)
        {
            var rig = ResolvedSetup(row.Block!.Setup);
            var lane = rig is null || !_execution.IsRunning ? null : _execution.Lanes.FirstOrDefault(l => l.RigId == rig.Id);
            row.ProgressText = lane?.FrameText ?? string.Empty;
            row.ProgressFraction = lane?.FrameProgress ?? 0;
            row.ActivityText = lane is null ? string.Empty : lane.HasFocus ? lane.FocusText : lane.CurrentStep;
        }
    }

    // ---- reading numbers

    private static double Number(string? text, string label, string expected, List<string> problems, double fallback)
    {
        var trimmed = text?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            && double.IsFinite(value))
        {
            return value;
        }

        problems.Add($"{label} must be {expected}.");
        return fallback;
    }

    private static int Whole(string? text, string label, List<string> problems, int fallback)
    {
        var trimmed = text?.Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) || int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        problems.Add($"{label} must be a whole number.");
        return fallback;
    }
}

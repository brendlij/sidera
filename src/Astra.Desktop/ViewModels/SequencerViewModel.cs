using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Focusing;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Sequencing;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

public enum NodeStatus
{
    Pending,
    Active,
    Done,

    /// <summary>A Rig Track that ended the run with a failure.</summary>
    Failed
}

/// <summary>One line of the sequence definition, with where the running sequence is relative to it.</summary>
public sealed partial class SequenceNodeViewModel(SequenceNode node) : ObservableObject
{
    private const double IndentPerLevel = 20;

    public SequenceNode Node { get; } = node;
    public string Title => Node.Title;
    public string Detail => Node.Detail;
    public string? SubText => Node.SubText;
    public bool HasSubText => Node.SubText is not null;
    public bool IsProblem => Node.IsProblem;

    /// <summary>The step of the editor draft this row shows, when the sequence comes from one.</summary>
    public Guid? DraftId => Node.DraftId;

    public bool HasNumber => !string.IsNullOrEmpty(Node.NumberLabel);
    public string NumberText => Node.NumberLabel ?? string.Empty;
    public double IndentWidth => Node.Depth * IndentPerLevel;
    public SequenceNodeKind Kind => Node.Kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailedNode))]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial NodeStatus Status { get; set; }

    /// <summary>What a step the definition does not describe is doing right now, for example "Settle guiding ≤ 0.5 px for 1 s".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? ActiveNote { get; set; }

    public bool HasNote => ActiveNote is not null;

    /// <summary>Which repetition a running Repeat is in, for example "iteration 3 / 5"; <c>null</c> otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIteration))]
    public partial string? IterationText { get; set; }

    /// <summary>
    /// For a running Repeat: the repetition it is in (from 1) and how many there are. The repetitions before it have
    /// ended, so <c>Iteration - 1</c> frames of an imaging repeat are done. <c>null</c> when it does not apply.
    /// </summary>
    [ObservableProperty]
    public partial int? Iteration { get; set; }

    [ObservableProperty]
    public partial int? IterationCount { get; set; }

    public bool HasIteration => IterationText is not null;
    public bool IsActive => Status == NodeStatus.Active;
    public bool IsDone => Status == NodeStatus.Done;
    public bool IsFailedNode => Status == NodeStatus.Failed;

    /// <summary>The node holds other nodes: a Repeat, a Rig Track, a Multi-Rig block, a group.</summary>
    public bool IsContainer => Node.Kind != SequenceNodeKind.Step;

    public string Glyph => Status switch
    {
        NodeStatus.Done => "✓",
        NodeStatus.Active => "●",
        NodeStatus.Failed => "✕",
        _ => "○",
    };
}

/// <summary>A branch of the running sequence that is active right now: where it is and what it does.</summary>
/// <param name="BranchName">The branch under a parallel step, or empty outside one.</param>
/// <param name="Title">The innermost running step.</param>
/// <param name="Context">The containers around it, outermost first.</param>
/// <param name="Camera">The camera of a running exposure, whose progress the branch shows; otherwise <c>null</c>.</param>
/// <param name="IsWaiting">The branch waits because of a pause; <paramref name="Title"/> is the step it will start next.</param>
public sealed record ActiveBranchViewModel(
    string BranchName,
    string Title,
    string Context,
    CameraViewModel? Camera,
    bool IsWaiting = false
)
{
    /// <summary>What the branch shows as its current line.</summary>
    public string DisplayTitle => IsWaiting ? $"Waiting to resume · next: {Title}" : Title;

    public bool HasBranchName => BranchName.Length > 0;
    public bool HasContext => Context.Length > 0;
    public bool HasProgress => Camera is not null;
}

/// <summary>
/// Runs and shows the sequence: its definition as a workflow with what is done, running and pending, every branch
/// that is active right now, and any failure. Everything shown comes from the <see cref="SequenceRunner"/>'s
/// positions and notifications; nothing is simulated by the view model.
/// </summary>
public sealed partial class SequencerViewModel : ViewModelBase, IDisposable
{
    private readonly SequenceRunner _runner;
    private readonly Sequence? _fixedSequence;
    private string _sequenceName;
    private IReadOnlyList<DraftRow> _shownDraft = [];
    private readonly Action<Action> _postToUi;
    private readonly SessionActivity _activity;
    private readonly ImagingViewModel _imaging;
    private readonly IReadOnlyList<CameraViewModel> _cameras;
    private readonly Func<string?>? _readiness;
    private IReadOnlyList<SequenceNode> _roots = [];
    private HashSet<Guid> _failedTracks = [];
    private readonly HashSet<SequenceExecutionPosition> _completed = new();
    private readonly Dictionary<SequenceNode, int> _latestIteration = new();
    private CancellationTokenSource? _cts;
    private readonly AstraRuntimeHost _host;
    private readonly IDisposable _autofocusSubscription;
    private readonly Dictionary<RigId, AutofocusStatusViewModel> _autofocus = new();

    /// <summary>A sequencer for one fixed sequence definition, without editable parameters.</summary>
    /// <param name="readiness">Returns why the sequence cannot start right now, or <c>null</c> if it can.</param>
    public SequencerViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        IReadOnlyList<CameraViewModel> cameras,
        Sequence sequence,
        Func<string?>? readiness = null
    ) : this(host, postToUi, activity, imaging, cameras, sequence, null, readiness)
    {
    }

    /// <summary>
    /// A sequencer whose sequence is built from <paramref name="draft"/> every time it runs. The definition shown
    /// follows the draft while nothing runs, and is the snapshot that was built for the run while one is in progress.
    /// </summary>
    public SequencerViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        IReadOnlyList<CameraViewModel> cameras,
        SequenceDraftViewModel draft,
        Func<string?>? readiness = null
    ) : this(host, postToUi, activity, imaging, cameras, null, draft, readiness)
    {
    }

    private SequencerViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        IReadOnlyList<CameraViewModel> cameras,
        Sequence? fixedSequence,
        SequenceDraftViewModel? draft,
        Func<string?>? readiness
    )
    {
        _runner = new SequenceRunner(
            host.ResourceManager, host.SafePointCoordinator, host.LoggerFactory.CreateLogger<SequenceRunner>());
        _host = host;
        _autofocusSubscription = host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            postToUi(() => ApplyAutofocus(e));
            return Task.CompletedTask;
        });
        _fixedSequence = fixedSequence;
        _sequenceName = fixedSequence?.Name ?? SequenceDraftBuilder.SequenceName;
        Draft = draft;
        _postToUi = postToUi;
        _activity = activity;
        _imaging = imaging;
        _cameras = cameras;
        _readiness = readiness;

        Definition = [];
        if (fixedSequence is not null)
        {
            ShowFixed(fixedSequence);
        }
        else if (draft is not null)
        {
            ShowDraft();
        }

        _runner.Changed += OnRunnerChanged;
        _runner.StepCompleted += OnStepCompleted;
        if (draft is not null)
        {
            draft.Changed += OnDraftChanged;
        }

        RefreshExecution();
        RefreshReadiness();
    }

    /// <summary>The sequence being edited, or <c>null</c> for a sequencer with a fixed sequence.</summary>
    public SequenceDraftViewModel? Draft { get; }

    public string SequenceName => $"{_sequenceName} sequence";

    /// <summary>
    /// The sequence in display order: while nothing runs the draft, one row per step; while one runs the snapshot
    /// that was built for it, with where it is. After a run it stays until the draft changes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial IReadOnlyList<SequenceNodeViewModel> Definition { get; private set; }

    /// <summary>The sequence has no steps.</summary>
    public bool IsEmpty => Definition.Count == 0;

    /// <summary>
    /// <see cref="Definition"/> is the snapshot of a run (with what is done and running), not the draft: true from the
    /// start of a run until the draft changes.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsRun { get; private set; }

    // The row of a draft step, as shown while nothing runs.
    private sealed record DraftRow(
        Guid Id, string Label, string Title, string Summary, string? Problem, SequenceStepKind Kind, int Depth);

    private void ShowRoots(IReadOnlyList<SequenceNode> roots, string name)
    {
        _failedTracks = [];
        _sequenceName = name;
        _roots = roots;
        _completed.Clear();
        _latestIteration.Clear();
        Definition = SequenceNodeBuilder.Flatten(_roots).Select(node => new SequenceNodeViewModel(node)).ToList();
        OnPropertyChanged(nameof(SequenceName));
    }

    // A sequence that was given as a whole.
    private void ShowFixed(Sequence sequence) => ShowRoots(SequenceNodeBuilder.Build(sequence), sequence.Name);

    private List<DraftRow> ReadDraftRows() =>
        Draft!.Rows.Select(step => new DraftRow(
            step.Id, step.NumberLabel, step.Title, step.Summary, step.FirstProblem, step.Kind, step.Depth)).ToList();

    // The draft as rows. These have no runtime steps: nothing is built until the sequence runs.
    private void ShowDraft()
    {
        _shownDraft = ReadDraftRows();
        ShowsRun = false;

        // Rows only: what is inside a container is listed after it, indented, without a tree behind them.
        ShowRoots(
            _shownDraft.Select(row => new SequenceNode(
                null, KindOf(row.Kind), row.Title,
                row.Problem is null ? row.Summary : string.Empty, row.Problem, row.Depth, row.Id,
                row.Depth == 0 ? $"{row.Label}." : row.Label, row.Problem is not null)).ToList(),
            SequenceDraftBuilder.SequenceName);
    }

    private static SequenceNodeKind KindOf(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.Repeat => SequenceNodeKind.Repeat,
        SequenceStepKind.MultiRig => SequenceNodeKind.Parallel,
        SequenceStepKind.RigTrack => SequenceNodeKind.Group,
        _ => SequenceNodeKind.Step,
    };

    // The snapshot a run was built from. Its labels are those of the built draft, whatever the draft says later.
    private void ShowSnapshot(BuiltSequence built) =>
        ShowRoots(
            built.Steps.Select((step, index) => SnapshotNode(step, [index], 0)).ToList(),
            built.Sequence.Name);

    // A built step as nodes that mirror the runtime tree, so that running positions can be followed through it:
    // a Repeat is the runtime RepeatStep with the group the builder put around its steps (the group is not listed);
    // a Multi-Rig block is the ParallelStep with a node for each Rig Track, which runs like a group; the steps the
    // builder generated for orchestration (safe points, a dither) are nodes that are not listed, at the index at
    // which they run, so that every running position finds its node.
    private static SequenceNode SnapshotNode(BuiltStep step, int[] path, int depth)
    {
        var label = SequenceDraftBuilder.Label(path);
        var numberLabel = depth == 0 ? $"{label}." : label;
        var description = step.Description;

        // The children of a container: listed ones are numbered among themselves, generated ones are not numbered.
        void AddChildren(SequenceNode parent, IReadOnlyList<BuiltStep> children, int childDepth)
        {
            var listed = 0;
            foreach (var child in children)
            {
                parent.Children.Add(SnapshotNode(child, child.IsGenerated ? path : [.. path, listed++], childDepth));
            }
        }

        if (step.IsGenerated)
        {
            var hidden = new SequenceNode(
                step.Step, step.Children is null ? SequenceNodeKind.Step : SequenceNodeKind.Group, description.Title,
                string.Empty, null, depth, isHidden: true, autofocusOrigin: step.AutofocusOrigin);
            foreach (var child in step.Children ?? [])
            {
                hidden.Children.Add(SnapshotNode(child, path, depth));
            }

            return hidden;
        }

        switch (step)
        {
            case { Step: RepeatStep { Child: SequenceGroup body } repeat, Children: { } children }:
            {
                var node = new SequenceNode(
                    repeat, SequenceNodeKind.Repeat, description.Title, description.Summary, null, depth, step.DraftId, numberLabel);
                var group = new SequenceNode(body, SequenceNodeKind.Group, body.Name, string.Empty, null, depth + 1, isHidden: true);
                AddChildren(group, children, depth + 1);
                node.Children.Add(group);
                return node;
            }
            case { Step: ParallelStep parallel, Children: { } tracks }:
            {
                var node = new SequenceNode(
                    parallel, SequenceNodeKind.Parallel, description.Title, description.Summary, null, depth, step.DraftId, numberLabel);
                AddChildren(node, tracks, depth + 1);
                return node;
            }
            case { Step: RigTrackStep track, Children: { } steps }:
            {
                var node = new SequenceNode(
                    track, SequenceNodeKind.Group, description.Title, description.Summary, null, depth, step.DraftId, numberLabel);
                AddChildren(node, steps, depth + 1);
                return node;
            }
            default:
                return new SequenceNode(
                    step.Step, SequenceNodeKind.Step, description.Title, description.Summary, null, depth, step.DraftId, numberLabel);
        }
    }

    // The draft changed. While a sequence runs, the definition shown is the snapshot that was built for that run.
    private void OnDraftChanged(object? sender, EventArgs e)
    {
        if (!IsRunning && Draft is not null && !ReadDraftRows().SequenceEqual(_shownDraft))
        {
            ShowDraft();
        }

        // What the sequence needs may have changed with the draft: another step, another rig.
        if (!IsRunning)
        {
            ReadinessHint = _readiness?.Invoke();
        }

        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateKind))]
    [NotifyPropertyChangedFor(nameof(IsRunningState))]
    [NotifyPropertyChangedFor(nameof(IsPausing))]
    [NotifyPropertyChangedFor(nameof(IsPaused))]
    [NotifyPropertyChangedFor(nameof(IsPauseState))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(HasReadinessHint))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyPropertyChangedFor(nameof(CanPause))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial SequenceState State { get; private set; }

    /// <summary>When the last run started; <c>null</c> before the first one.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? RunStartedAt { get; private set; }

    /// <summary>When the last run ended; <c>null</c> while it is in progress and before the first one.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? RunEndedAt { get; private set; }

    /// <summary>How long the last run has gone on, or went on (a pause counts); <c>null</c> before the first run.</summary>
    public TimeSpan? Elapsed => RunStartedAt is { } started ? (RunEndedAt ?? DateTimeOffset.Now) - started : null;

    /// <summary>A run is in progress, including while it is pausing or paused.</summary>
    public bool IsRunning => State is SequenceState.Running or SequenceState.Pausing or SequenceState.Paused;

    // What the buttons may do. Every one of them is derived from the state alone, and every change of the state
    // re-evaluates all four commands, so no command can be left showing an earlier state.
    public bool CanRun => !IsRunning && ReadinessHint is null && (Draft?.IsValid ?? true);
    public bool CanPause => State == SequenceState.Running;
    public bool CanResume => State == SequenceState.Paused;
    public bool CanCancel => IsRunning;

    public bool IsCompleted => State == SequenceState.Completed;
    public bool IsCancelled => State == SequenceState.Cancelled;
    public bool IsFailed => State == SequenceState.Failed;

    public bool IsRunningState => State == SequenceState.Running;
    public bool IsPausing => State == SequenceState.Pausing;
    public bool IsPaused => State == SequenceState.Paused;

    /// <summary>The run is pausing or paused.</summary>
    public bool IsPauseState => IsPausing || IsPaused;

    public string StateText => State == SequenceState.Pausing ? "Pausing…" : State.ToString();

    /// <summary>How the state is shown: active while it runs, a warning when paused or cancelled, ok when completed, an error when it failed.</summary>
    public StatusKind StateKind => State switch
    {
        SequenceState.Running => StatusKind.Active,
        SequenceState.Pausing or SequenceState.Paused or SequenceState.Cancelled => StatusKind.Warning,
        SequenceState.Completed => StatusKind.Ok,
        SequenceState.Failed => StatusKind.Error,
        _ => StatusKind.Neutral,
    };

    /// <summary>Why the sequence cannot start now (for example "Connect Main Camera first."); <c>null</c> when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadinessHint))]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string? ReadinessHint { get; private set; }

    public bool HasReadinessHint => ReadinessHint is not null && !IsRunning;

    /// <summary>The containers around the running step and the step itself, outermost first; the last position once it ended.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SequenceStatusLine> StatusLines { get; private set; } = [];

    /// <summary><see cref="StatusLines"/> on one line.</summary>
    [ObservableProperty]
    public partial string StepText { get; private set; } = string.Empty;

    /// <summary>Every branch running right now, several at once with a parallel step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveBranches))]
    public partial IReadOnlyList<ActiveBranchViewModel> ActiveBranches { get; private set; } = [];

    public bool HasActiveBranches => ActiveBranches.Count > 0;

    /// <summary>
    /// What the whole session is doing that no single track does: a dither of the shared mount that is waiting for the
    /// tracks to be at a safe point, being made, or settling. Read from the running positions; <c>null</c> otherwise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedActivity))]
    public partial string? SharedActivity { get; private set; }

    public bool HasSharedActivity => SharedActivity is not null;

    /// <summary>
    /// What the autofocus of each rig is doing now, or did in the last run, in the order of the rig ids. Only what the
    /// runs report; cleared when a new run starts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutofocus))]
    public partial IReadOnlyList<AutofocusStatusViewModel> AutofocusStatuses { get; private set; } = [];

    public bool HasAutofocus => AutofocusStatuses.Count > 0;

    // Progress arrives on any thread and is applied on the UI thread, in the order it was published.
    private void ApplyAutofocus(AutofocusProgressChanged e)
    {
        if (!_autofocus.TryGetValue(e.RigId, out var status))
        {
            if (e.Progress.Phase == AutofocusPhase.Stopped)
            {
                return; // stopped before it said anything: there is nothing to show
            }

            var name = _host.RigRegistry.TryGet(e.RigId, out var rig) && rig is not null ? rig.Name : e.RigId.Value;
            _autofocus[e.RigId] = status = new AutofocusStatusViewModel(e.RigId, name);
            AutofocusStatuses = _autofocus.Values.OrderBy(s => s.RigId.Value, StringComparer.Ordinal).ToList();
        }

        // A new run of this rig's autofocus says why it runs: the step that is running is a node of the snapshot, and it
        // either is an autofocus the user wrote or one the policy of the track generated.
        if (e.Progress is { Phase: AutofocusPhase.Measuring, Attempt: 1, SampleIndex: 0 })
        {
            status.SetOrigin(OriginOfRunningAutofocus(e.RigId));
        }

        status.Apply(e.Progress);
    }

    private string? OriginOfRunningAutofocus(RigId rig)
    {
        foreach (var position in _runner.ActivePositions)
        {
            if (Resolve(position).Node is { Step: AutofocusAction action } node && action.RigId == rig)
            {
                return node.AutofocusOrigin is { } origin
                    ? AutofocusStatusViewModel.AutomaticOrigin(origin)
                    : AutofocusStatusViewModel.ManualOrigin;
            }
        }

        return null;
    }

    private void ClearAutofocus()
    {
        _autofocus.Clear();
        AutofocusStatuses = [];
    }

    /// <summary>
    /// The position is shown as lines of its own only while no branch shows it: every active branch already carries
    /// the containers around its step, so showing both would say the same thing twice.
    /// </summary>
    public bool ShowStatusLines => IsRunning && !HasActiveBranches;

    /// <summary>Re-evaluates whether the sequence can start; call when the equipment changed.</summary>
    public void RefreshReadiness()
    {
        // Also notices a device of the draft that is no longer available.
        if (!IsRunning)
        {
            Draft?.RefreshDevices();
        }

        ReadinessHint = _readiness?.Invoke();
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        ClearError();

        // A fresh sequence from the draft as it is now. Nothing of an earlier run is reused, and later edits cannot
        // reach it: the run executes this sequence and shows this snapshot, and the editors are locked from the start.
        Sequence sequence;
        if (_fixedSequence is not null)
        {
            sequence = _fixedSequence;
            ShowFixed(sequence);
        }
        else
        {
            BuiltSequence built;
            try
            {
                built = Draft!.Build();
            }
            catch (SequenceConfigurationException ex)
            {
                ReportError(ex.Problems.Count == 1 ? ex.Problems[0] : $"Check the sequence: {ex.Problems[0]} (+{ex.Problems.Count - 1} more)");
                return;
            }

            sequence = built.Sequence;
            ShowSnapshot(built);
        }

        ClearAutofocus();
        ShowsRun = true;
        var cts = new CancellationTokenSource();
        _cts = cts;
        RunStartedAt = DateTimeOffset.Now;
        RunEndedAt = null;

        try
        {
            // Starts synchronously and flips the runner to Running before the first await.
            var run = _runner.RunAsync(sequence, cts.Token);
            _activity.IsSequenceRunning = true;
            RefreshExecution();
            await run;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Cancelled by the user; the runner state is Cancelled.
        }
        catch (Exception ex)
        {
            // A failure inside a Rig Track says which track it was.
            _failedTracks = FailedTracks(ex).ToHashSet();

            // The log has the stack trace; the execution id is what finds this run in it.
            ReportError($"{UserFacingError.Describe(ex)} See the log, execution {_runner.ExecutionTag}.", ex);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            _activity.IsSequenceRunning = false;
            RunEndedAt = DateTimeOffset.Now;
            RefreshExecution();

            RefreshReadiness();
        }
    }

    private static IEnumerable<Guid> FailedTracks(Exception exception) => exception switch
    {
        RigTrackFailedException track => [track.TrackId],
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(FailedTracks),
        _ => [],
    };

    // Pausing is cooperative: running steps finish, nothing new starts. Resume is only offered once the run is
    // paused; the runner would also accept it while still pausing, to withdraw the request.
    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _runner.RequestPause();
        RefreshExecution();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _runner.Resume();
        RefreshExecution();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The sequence just ended.
        }
    }

    // Raised on whichever thread changed the runner, never while it holds a lock.
    private void OnRunnerChanged(object? sender, EventArgs e) => _postToUi(RefreshExecution);

    private void OnStepCompleted(object? sender, SequenceStepCompletedEventArgs e)
    {
        _postToUi(() =>
        {
            _completed.Add(e.Position);
            if (e.Result.Payload is CameraFrame frame)
            {
                _imaging.Publish(frame, $"{SequenceName} · {e.StepName}");
            }

            RefreshExecution();
        });
    }

    private void RefreshExecution()
    {
        State = _runner.State;
        if (Draft is not null)
        {
            Draft.IsEditable = !IsRunning;
        }

        var active = _runner.ActivePositions;
        var current = _runner.CurrentPosition;

        foreach (var position in active.Append(current).OfType<SequenceExecutionPosition>())
        {
            foreach (var (repeat, iteration) in Resolve(position).Repeats)
            {
                _latestIteration[repeat] = Math.Max(_latestIteration.GetValueOrDefault(repeat, -1), iteration);
            }
        }

        var activeNotes = new Dictionary<SequenceNode, string?>();
        foreach (var position in active)
        {
            var resolved = Resolve(position);
            if (resolved.Node is { } node)
            {
                var note = resolved.Exact ? null : resolved.InternalName;
                if (!activeNotes.TryGetValue(node, out var existing) || (existing is null && note is not null))
                {
                    activeNotes[node] = note;
                }
            }
        }

        var done = new HashSet<SequenceNode>();
        foreach (var position in _completed)
        {
            var resolved = Resolve(position);
            if (resolved is { Exact: true, Node: { } node } && resolved.Repeats.All(r => r.Iteration >= _latestIteration.GetValueOrDefault(r.Repeat, -1)))
            {
                done.Add(node);
            }
        }

        foreach (var vm in Definition)
        {
            vm.Status = vm.Node.DraftId is { } draftId && _failedTracks.Contains(draftId) ? NodeStatus.Failed
                : activeNotes.ContainsKey(vm.Node) ? NodeStatus.Active
                : done.Contains(vm.Node) ? NodeStatus.Done
                : NodeStatus.Pending;
            vm.ActiveNote = activeNotes.GetValueOrDefault(vm.Node);

            // Only the repetition that was started last is known; earlier ones are not shown.
            vm.IterationText = vm is { Status: NodeStatus.Active, Node.Step: RepeatStep repeat }
                && _latestIteration.TryGetValue(vm.Node, out var iteration)
                    ? $"iteration {iteration + 1} / {repeat.Count}"
                    : null;
            if (vm is { Status: NodeStatus.Active, Node.Step: RepeatStep running }
                && _latestIteration.TryGetValue(vm.Node, out var latest))
            {
                vm.Iteration = latest + 1;
                vm.IterationCount = running.Count;
            }
            else
            {
                vm.Iteration = null;
                vm.IterationCount = null;
            }
        }

        var lines = SequenceStatusLine.From(current);
        var text = string.Join(" › ", lines.Select(line => line.Text));
        if (text != StepText)
        {
            StatusLines = lines;
            StepText = text;
        }

        var dither = ReadDither(active);
        SharedActivity = dither?.Text;
        var branches = BuildBranches(active, dither is not null);
        if (!branches.SequenceEqual(ActiveBranches))
        {
            ActiveBranches = branches;
        }

        OnPropertyChanged(nameof(ShowStatusLines));
        ExecutionRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread each time the execution state was read again.</summary>
    public event EventHandler? ExecutionRefreshed;

    // How far the dither that is running now has got, and who is still to arrive: from the positions alone. A dither is
    // pending from the moment its step starts until its command runs; the tracks that have not reached a safe point
    // are those whose innermost running step is something else than a safe point.
    private DitherState? ReadDither(IReadOnlyCollection<SequenceExecutionPosition> active)
    {
        DitherState? state = null;
        foreach (var position in active)
        {
            var resolved = Resolve(position);
            if (resolved.Node?.Step is not DitherAction dither)
            {
                continue;
            }

            var stage = resolved.Exact ? 0 : resolved.InternalName?.StartsWith("Settle", StringComparison.Ordinal) == true ? 2 : 1;
            if (state is not null && state.Stage >= stage)
            {
                continue;
            }

            var text = stage switch
            {
                1 => string.Create(CultureInfo.InvariantCulture, $"Dithering · {dither.AmplitudePixels:0.##} px"),
                2 when dither.SettleOptions is { } settle => string.Create(
                    CultureInfo.InvariantCulture,
                    $"Settling · ≤ {settle.MaximumErrorPixels:0.##} px for {settle.StableDuration.TotalSeconds:0.##} s"),
                2 => "Settling",
                _ => "Dither pending",
            };

            if (stage == 0 && resolved.BranchName is { } requester)
            {
                // Tracks whose innermost step is not a safe point still have to get there.
                var busy = active
                    .Where(p => !active.Any(other => Equals(other.Parent, p)))
                    .Select(Resolve)
                    .Where(r => r.BranchName is not null && r.BranchName != requester && r.Node?.Step is not SafePointStep)
                    .Select(r => r.BranchName)
                    .Distinct()
                    .Count();
                if (busy > 0)
                {
                    text += $" · waiting for {busy} {(busy == 1 ? "rig" : "rigs")}";
                }
            }

            state = new DitherState(stage, text);
        }

        return state;
    }

    private sealed record DitherState(int Stage, string Text);

    private List<ActiveBranchViewModel> BuildBranches(IReadOnlyCollection<SequenceExecutionPosition> active, bool ditherPending)
    {
        var branches = new List<ActiveBranchViewModel>();
        var waiting = _runner.PausedPositions;

        // A branch that waits to start its next step because of a pause is shown as waiting, not as the container
        // around that step still "running".
        foreach (var leaf in active.Where(p =>
                     !active.Any(other => Equals(other.Parent, p)) && !waiting.Any(w => Equals(w.Parent, p))))
        {
            var resolved = Resolve(leaf);
            var lines = SequenceStatusLine.From(leaf);
            var context = string.Join(" › ", lines.Take(lines.Count - 1).Select(line => line.Text));
            var camera = resolved is { Exact: true, Node.Step: CameraExposureAction exposure }
                ? _cameras.FirstOrDefault(c => c.CameraId == exposure.CameraId)
                : null;
            // A track at a safe point while a dither is pending is waiting for it, not "at a safe point"; the track that
            // asked is waiting for the others for as long as its dither has not started.
            var title = resolved is { Exact: true, Node.Step: DitherAction }
                        || ditherPending && resolved is { Exact: true, Node.Step: SafePointStep }
                ? "Waiting for coordinated dither"
                : leaf.StepName;
            branches.Add(new ActiveBranchViewModel(resolved.BranchName ?? string.Empty, title, context, camera));
        }

        foreach (var next in waiting)
        {
            var lines = SequenceStatusLine.From(next);
            var context = string.Join(" › ", lines.Take(lines.Count - 1).Select(line => line.Text));
            branches.Add(new ActiveBranchViewModel(
                Resolve(next).BranchName ?? string.Empty, next.StepName, context, null, IsWaiting: true));
        }

        return branches;
    }

    // Maps a running position onto the definition by walking the same indexes the runner used.
    private Resolution Resolve(SequenceExecutionPosition position)
    {
        var chain = new List<SequenceExecutionPosition>();
        for (var p = position; p is not null; p = p.Parent)
        {
            chain.Insert(0, p);
        }

        if (chain[0].Index < 0 || chain[0].Index >= _roots.Count)
        {
            return new Resolution(null, false, null, [], null);
        }

        var node = _roots[chain[0].Index];
        var repeats = new List<(SequenceNode Repeat, int Iteration)>();
        string? branch = null;

        for (var i = 1; i < chain.Count; i++)
        {
            if (node.Kind == SequenceNodeKind.Repeat)
            {
                repeats.Add((node, chain[i].Index));
            }

            if (node.Kind == SequenceNodeKind.Parallel)
            {
                branch ??= chain[i].StepName;
            }

            var child = node.ChildAt(chain[i].Index);
            if (child is null)
            {
                // Below this node the runner executes steps the definition does not list (a dither's own steps).
                return new Resolution(node, false, chain[^1].StepName, repeats, branch);
            }

            node = child;
        }

        return new Resolution(node, true, null, repeats, branch);
    }

    private sealed record Resolution(
        SequenceNode? Node,
        bool Exact,
        string? InternalName,
        List<(SequenceNode Repeat, int Iteration)> Repeats,
        string? BranchName
    );

    public void Dispose()
    {
        Cancel();
        _autofocusSubscription.Dispose();
        _runner.Changed -= OnRunnerChanged;
        _runner.StepCompleted -= OnStepCompleted;
        if (Draft is not null)
        {
            Draft.Changed -= OnDraftChanged;
        }
    }
}

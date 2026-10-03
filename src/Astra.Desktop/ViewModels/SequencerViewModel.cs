using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

public enum NodeStatus
{
    Pending,
    Active,
    Done
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
    public double IndentWidth => Node.Depth * IndentPerLevel;
    public SequenceNodeKind Kind => Node.Kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(Glyph))]
    public partial NodeStatus Status { get; set; }

    /// <summary>What a step the definition does not describe is doing right now, for example "Settle guiding ≤ 0.5 px for 1 s".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string? ActiveNote { get; set; }

    public bool HasNote => ActiveNote is not null;
    public bool IsActive => Status == NodeStatus.Active;
    public bool IsDone => Status == NodeStatus.Done;

    public string Glyph => Status switch
    {
        NodeStatus.Done => "✓",
        NodeStatus.Active => "●",
        _ => "○",
    };
}

/// <summary>A branch of the running sequence that is active right now: where it is and what it does.</summary>
/// <param name="BranchName">The branch under a parallel step, or empty outside one.</param>
/// <param name="Title">The innermost running step.</param>
/// <param name="Context">The containers around it, outermost first.</param>
/// <param name="Camera">The camera of a running exposure, whose progress the branch shows; otherwise <c>null</c>.</param>
public sealed record ActiveBranchViewModel(string BranchName, string Title, string Context, CameraViewModel? Camera)
{
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
    private readonly Sequence _sequence;
    private readonly Action<Action> _postToUi;
    private readonly SessionActivity _activity;
    private readonly ImagingViewModel _imaging;
    private readonly IReadOnlyList<CameraViewModel> _cameras;
    private readonly Func<string?>? _readiness;
    private readonly IReadOnlyList<SequenceNode> _roots;
    private readonly HashSet<SequenceExecutionPosition> _completed = new();
    private readonly Dictionary<SequenceNode, int> _latestIteration = new();
    private CancellationTokenSource? _cts;

    /// <param name="readiness">Returns why the sequence cannot start right now, or <c>null</c> if it can.</param>
    public SequencerViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        IReadOnlyList<CameraViewModel> cameras,
        Sequence sequence,
        Func<string?>? readiness = null
    )
    {
        _runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        _sequence = sequence;
        _postToUi = postToUi;
        _activity = activity;
        _imaging = imaging;
        _cameras = cameras;
        _readiness = readiness;

        _roots = SequenceNodeBuilder.Build(sequence);
        Definition = SequenceNodeBuilder.Flatten(_roots).Select(node => new SequenceNodeViewModel(node)).ToList();

        _runner.Changed += OnRunnerChanged;
        _runner.StepCompleted += OnStepCompleted;
        RefreshExecution();
        RefreshReadiness();
    }

    public string SequenceName => $"{_sequence.Name} sequence";

    /// <summary>The definition, flattened in display order.</summary>
    public IReadOnlyList<SequenceNodeViewModel> Definition { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsCancelled))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial SequenceState State { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(HasReadinessHint))]
    public partial bool IsRunning { get; private set; }

    public bool IsCompleted => State == SequenceState.Completed;
    public bool IsCancelled => State == SequenceState.Cancelled;
    public bool IsFailed => State == SequenceState.Failed;

    public string StateText => State.ToString();

    /// <summary>Why the sequence cannot start now (for example "Connect Main Camera first."); <c>null</c> when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadinessHint))]
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

    /// <summary>Re-evaluates whether the sequence can start; call when the equipment changed.</summary>
    public void RefreshReadiness()
    {
        ReadinessHint = _readiness?.Invoke();
        RunCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        ClearError();
        _completed.Clear();
        _latestIteration.Clear();

        var cts = new CancellationTokenSource();
        _cts = cts;

        try
        {
            // Starts synchronously and flips the runner to Running before the first await.
            var run = _runner.RunAsync(_sequence, cts.Token);
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
            ReportError(ex);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            _activity.IsSequenceRunning = false;
            RefreshExecution();
            RefreshReadiness();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
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

    private bool CanRun() => !IsRunning && ReadinessHint is null;

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
        IsRunning = _runner.IsRunning;

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
            vm.Status = activeNotes.ContainsKey(vm.Node) ? NodeStatus.Active : done.Contains(vm.Node) ? NodeStatus.Done : NodeStatus.Pending;
            vm.ActiveNote = activeNotes.GetValueOrDefault(vm.Node);
        }

        var lines = SequenceStatusLine.From(current);
        var text = string.Join(" › ", lines.Select(line => line.Text));
        if (text != StepText)
        {
            StatusLines = lines;
            StepText = text;
        }

        var branches = BuildBranches(active);
        if (!branches.SequenceEqual(ActiveBranches))
        {
            ActiveBranches = branches;
        }

        ExecutionRefreshed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on the UI thread each time the execution state was read again.</summary>
    public event EventHandler? ExecutionRefreshed;

    private List<ActiveBranchViewModel> BuildBranches(IReadOnlyCollection<SequenceExecutionPosition> active)
    {
        var branches = new List<ActiveBranchViewModel>();
        foreach (var leaf in active.Where(p => !active.Any(other => Equals(other.Parent, p))))
        {
            var resolved = Resolve(leaf);
            var lines = SequenceStatusLine.From(leaf);
            var context = string.Join(" › ", lines.Take(lines.Count - 1).Select(line => line.Text));
            var camera = resolved is { Exact: true, Node.Step: CameraExposureAction exposure }
                ? _cameras.FirstOrDefault(c => c.CameraId == exposure.CameraId)
                : null;
            branches.Add(new ActiveBranchViewModel(resolved.BranchName ?? string.Empty, leaf.StepName, context, camera));
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
        _runner.Changed -= OnRunnerChanged;
        _runner.StepCompleted -= OnStepCompleted;
    }
}

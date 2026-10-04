using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// One Rig Track of a running Multi-Rig block, as the dashboard and the session page show it: the rig, what the track is
/// doing, how far its frames are and, when its autofocus has something to say, that. Everything is read from the
/// running sequence and from what the runs report; nothing is estimated.
/// </summary>
public sealed partial class TrackLaneViewModel : ObservableObject
{
    private bool _wasActive;

    public TrackLaneViewModel(string name, RigId? rigId, IReadOnlyList<string> description, RigViewModel? rig = null)
    {
        Name = name;
        RigId = rigId;
        Description = description;
        Rig = rig;
    }

    /// <summary>The rig of the track, for the focus position and the filter its devices report; <c>null</c> when the track names none.</summary>
    public RigViewModel? Rig { get; }

    /// <summary>The rig's name, for example "Main Rig".</summary>
    public string Name { get; }

    public RigId? RigId { get; }

    /// <summary>What the track says about itself: its camera, and the autofocus policy when it has one.</summary>
    public IReadOnlyList<string> Description { get; }

    public string DescriptionText => string.Join(" · ", Description);

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Waiting";

    [ObservableProperty]
    public partial StatusKind StatusKind { get; private set; }

    /// <summary>The innermost step the track is running ("Exposure 300 s"); empty while it is not running anything.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentStep))]
    public partial string CurrentStep { get; private set; } = string.Empty;

    public bool HasCurrentStep => CurrentStep.Length > 0;

    /// <summary>The camera whose exposure the track shows: its progress is read from the camera itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExposure))]
    public partial CameraViewModel? Camera { get; private set; }

    public bool HasExposure => Camera is not null;

    /// <summary>"Frame 18 / 40" while a repeat of the track runs, "40 / 40 frames" once it is done; empty without a repeat.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFrames))]
    public partial string FrameText { get; private set; } = string.Empty;

    /// <summary>The frames that have ended, as a fraction of those the repeat will make: from 0 to 1.</summary>
    [ObservableProperty]
    public partial double FrameProgress { get; private set; }

    public bool HasFrames => FrameText.Length > 0;

    /// <summary>What the autofocus of this rig reports: "Sample 4 / 7 · HFR 2.11 px"; empty when it has not run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus))]
    public partial string FocusText { get; private set; } = string.Empty;

    /// <summary>The autofocus of the rig is running now.</summary>
    [ObservableProperty]
    public partial bool IsFocusing { get; private set; }

    public bool HasFocus => FocusText.Length > 0;

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    public partial bool IsDone { get; private set; }

    /// <summary>The track has not started, or has no state to show: its bar is quiet.</summary>
    [ObservableProperty]
    public partial bool IsWaiting { get; private set; } = true;

    internal void Apply(
        NodeStatus status, bool paused, bool ended, ActiveBranchViewModel? branch, int? iteration, int? count, bool repeatDone,
        AutofocusStatusViewModel? focus)
    {
        _wasActive |= status == NodeStatus.Active;
        (StatusText, StatusKind) = status switch
        {
            NodeStatus.Failed => ("Failed", StatusKind.Error),
            NodeStatus.Done => ("Done", StatusKind.Ok),
            NodeStatus.Active when paused => ("Paused", StatusKind.Warning),
            NodeStatus.Active => ("Running", StatusKind.Active),

            // The run is over and this track did not finish: it was cut short, or never got to start.
            _ when ended && _wasActive => ("Stopped", StatusKind.Warning),
            _ when ended => ("Not started", StatusKind.Neutral),
            _ => ("Waiting", StatusKind.Neutral),
        };
        IsActive = status == NodeStatus.Active;
        IsFailed = status == NodeStatus.Failed;
        IsDone = status == NodeStatus.Done;
        IsWaiting = status == NodeStatus.Pending;

        CurrentStep = branch is null ? string.Empty : branch.IsWaiting ? "Waiting to resume" : branch.Title;
        Camera = branch?.Camera;

        if (iteration is { } current && count is { } total)
        {
            FrameText = string.Create(CultureInfo.InvariantCulture, $"Frame {current} / {total}");
            FrameProgress = total == 0 ? 0 : (double)(current - 1) / total;
        }
        else if (count is { } all && repeatDone)
        {
            FrameText = string.Create(CultureInfo.InvariantCulture, $"{all} / {all} frames");
            FrameProgress = 1;
        }
        else
        {
            FrameText = string.Empty;
            FrameProgress = 0;
        }

        FocusText = focus?.Summary ?? string.Empty;
        IsFocusing = focus is { IsActive: true };
    }
}

/// <summary>
/// What the running sequence is doing, in the words of the product: for a Multi-Rig block a lane for each Rig Track, for
/// any other sequence the step that is running. It reads the <see cref="SequencerViewModel"/> and owns no state of
/// its own, so it can be shown on the dashboard and on the session page at once without ever disagreeing.
/// </summary>
public sealed partial class ExecutionOverviewViewModel : ViewModelBase, IDisposable
{
    private readonly SequencerViewModel _sequencer;
    private IReadOnlyList<SequenceNodeViewModel>? _built;
    private IReadOnlyList<(TrackLaneViewModel Lane, SequenceNodeViewModel Node, IReadOnlyList<SequenceNodeViewModel> Descendants)> _tracks = [];

    private readonly IReadOnlyList<RigViewModel> _rigs;

    /// <param name="rigs">The rigs of the equipment, so that a lane can show the focus position and the filter of its rig.</param>
    public ExecutionOverviewViewModel(SequencerViewModel sequencer, IReadOnlyList<RigViewModel>? rigs = null)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        _sequencer = sequencer;
        _rigs = rigs ?? [];
        _sequencer.ExecutionRefreshed += OnRefreshed;
        _sequencer.PropertyChanged += OnSequencerChanged;
        Refresh();
    }

    /// <summary>The Rig Tracks of the Multi-Rig block of the sequence, in order; empty when it has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLanes))]
    [NotifyPropertyChangedFor(nameof(HasRunningLanes))]
    public partial IReadOnlyList<TrackLaneViewModel> Lanes { get; private set; } = [];

    /// <summary>The sequence has a Multi-Rig block: its tracks are shown as lanes.</summary>
    public bool HasLanes => Lanes.Count > 0;

    /// <summary>The lanes are shown on the session page: a sequence with a Multi-Rig block runs.</summary>
    public bool HasRunningLanes => HasLanes && _sequencer.IsRunning;

    /// <summary>What a sequence without lanes is doing: the step that runs. Empty when nothing runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    public partial string ActivityTitle { get; private set; } = string.Empty;

    /// <summary>The containers around the running step ("Repeat × 40 · 3 / 40").</summary>
    [ObservableProperty]
    public partial string ActivityContext { get; private set; } = string.Empty;

    /// <summary>The camera of the step that runs, when it is an exposure: its progress is read from the camera.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivityCamera))]
    public partial CameraViewModel? ActivityCamera { get; private set; }

    public bool HasActivity => ActivityTitle.Length > 0;
    public bool HasActivityCamera => ActivityCamera is not null;

    /// <summary>"Step 3 / 6" for the top-level step that runs; empty with a single step.</summary>
    [ObservableProperty]
    public partial string StepCounterText { get; private set; } = string.Empty;

    /// <summary>What the whole session is doing that no track does (a dither of the shared mount); <c>null</c> otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedActivity))]
    public partial string? SharedActivity { get; private set; }

    public bool HasSharedActivity => SharedActivity is not null;

    /// <summary>A sequence is running (also while it is pausing or paused).</summary>
    public bool IsRunning => _sequencer.IsRunning;

    private void OnRefreshed(object? sender, EventArgs e) => Refresh();

    private void OnSequencerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SequencerViewModel.AutofocusStatuses) or nameof(SequencerViewModel.Definition))
        {
            Refresh();
        }
    }

    /// <summary>Reads the running sequence again; the sequencer calls it on every change.</summary>
    public void Refresh()
    {
        var definition = _sequencer.Definition;
        if (!ReferenceEquals(definition, _built))
        {
            _built = definition;
            _tracks = FindTracks(definition);
            Lanes = _tracks.Select(track => track.Lane).ToList();
        }

        var branches = _sequencer.ActiveBranches;
        var paused = _sequencer.IsPaused;
        var ended = _sequencer.State is SequenceState.Completed or SequenceState.Failed or SequenceState.Cancelled;
        foreach (var (lane, node, descendants) in _tracks)
        {
            var branch = branches.FirstOrDefault(b => string.Equals(b.BranchName, node.Title, StringComparison.Ordinal));
            var repeat = descendants.FirstOrDefault(d => d.Kind == SequenceNodeKind.Repeat);
            var focus = lane.RigId is { } rig ? _sequencer.AutofocusStatuses.FirstOrDefault(s => s.RigId == rig) : null;
            lane.Apply(
                node.Status, paused, ended, branch,
                repeat?.Iteration, (repeat?.Node.Step as RepeatStep)?.Count, repeat is { IsDone: true }, focus);
        }

        var first = branches.Count > 0 ? branches[0] : null;
        if (_tracks.Count == 0 && first is not null)
        {
            ActivityTitle = first.DisplayTitle;
            ActivityContext = System.Text.RegularExpressions.Regex.Replace(first.Context, @"^Step \d+ / \d+( › )?", string.Empty);
            ActivityCamera = first.Camera;
        }
        else
        {
            ActivityTitle = _tracks.Count == 0 && _sequencer.IsRunning ? _sequencer.StepText : string.Empty;
            ActivityContext = string.Empty;
            ActivityCamera = null;
        }

        StepCounterText = _sequencer.IsRunning
            ? _sequencer.StatusLines.FirstOrDefault(line => line.IsContainer && line.Text.StartsWith("Step ", StringComparison.Ordinal))?.Text ?? string.Empty
            : string.Empty;
        SharedActivity = _sequencer.SharedActivity;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(HasRunningLanes));
    }

    // The tracks of the first Multi-Rig block: the nodes one level below it, and what each of them holds.
    private IReadOnlyList<(TrackLaneViewModel, SequenceNodeViewModel, IReadOnlyList<SequenceNodeViewModel>)> FindTracks(
        IReadOnlyList<SequenceNodeViewModel> definition)
    {
        var tracks = new List<(TrackLaneViewModel, SequenceNodeViewModel, IReadOnlyList<SequenceNodeViewModel>)>();
        var block = definition.ToList().FindIndex(node => node.Kind == SequenceNodeKind.Parallel);
        if (block < 0)
        {
            return tracks;
        }

        var depth = definition[block].Node.Depth;
        for (var i = block + 1; i < definition.Count && definition[i].Node.Depth > depth; i++)
        {
            if (definition[i].Node.Depth != depth + 1 || definition[i].Kind != SequenceNodeKind.Group)
            {
                continue;
            }

            var node = definition[i];
            var descendants = new List<SequenceNodeViewModel>();
            for (var j = i + 1; j < definition.Count && definition[j].Node.Depth > node.Node.Depth; j++)
            {
                descendants.Add(definition[j]);
            }

            var description = node.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rigId = (node.Node.Step as Astra.Desktop.RigTrackStep)?.RigId;
            var rig = rigId is { } id ? _rigs.FirstOrDefault(r => r.Id == id) : null;
            tracks.Add((new TrackLaneViewModel(node.Title, rigId, description, rig), node, descendants));
        }

        return tracks;
    }

    public void Dispose()
    {
        _sequencer.ExecutionRefreshed -= OnRefreshed;
        _sequencer.PropertyChanged -= OnSequencerChanged;
    }
}

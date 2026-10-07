using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Sessions;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.ViewModels;

public sealed partial class SessionEditorViewModel
{
    private readonly List<TrackLaneViewModel> _wiredLanes = [];

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

    private void OnExecutionRefreshed(object? sender, EventArgs e) => RefreshProgress();

    /// <summary>What the session does that no sequence does (a dither of the shared mount that waits for the sequences to be at a safe point); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedActivity))]
    public partial string SharedActivityText { get; private set; } = string.Empty;

    public bool HasSharedActivity => SharedActivityText.Length > 0;

    /// <summary>
    /// Shows where the run is, element by element: the block a setup is at shows its frame, what it is at and an autofocus it is running; a block that is done says so; the blocks that follow it on the
    /// same setup are queued (the blocks of one setup run one after another; only different setups run at the same time); a target says whether it is pending, running, completed or failed.
    /// </summary>
    public void RefreshProgress()
    {
        if (_execution is null || Session is not { } session)
        {
            return;
        }

        SharedActivityText = _execution.IsRunning ? _execution.SharedActivity ?? string.Empty : string.Empty;

        // What the sequence made each block and each target of: the nodes of the running sequence, by the element they came from.
        var repeatOf = new Dictionary<Guid, SequenceNodeViewModel>();
        var nodesOf = new Dictionary<Guid, List<SequenceNodeViewModel>>();
        foreach (var node in _execution.Nodes)
        {
            if (node.DraftId is not { } draft || !_origins.TryGetValue(draft, out var element))
            {
                continue;
            }

            if (node.Kind == SequenceNodeKind.Repeat && !repeatOf.ContainsKey(element))
            {
                repeatOf[element] = node;
            }

            if (SessionEdits.TargetOf(session, element) is { } target)
            {
                if (!nodesOf.TryGetValue(target.Id, out var list))
                {
                    nodesOf[target.Id] = list = [];
                }

                list.Add(node);
            }
        }

        var running = _execution.IsRunning;
        var cards = Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks).ToDictionary(b => b.Id);
        foreach (var target in session.Targets)
        {
            foreach (var lane in target.Lanes)
            {
                ShowLane(lane, repeatOf, cards, running);
            }
        }

        foreach (var card in Targets)
        {
            var nodes = nodesOf.GetValueOrDefault(card.Id) ?? [];
            (card.StatusText, card.StatusKind) = TargetStatus(card.IsOn, nodes, running);
        }
    }

    private void ShowLane(SetupLane lane, Dictionary<Guid, SequenceNodeViewModel> repeatOf, Dictionary<Guid, BlockCardViewModel> cards, bool running)
    {
        var rig = ResolvedSetup(lane.Setup);
        var track = rig is null || !running ? null : _execution!.Lanes.FirstOrDefault(l => l.RigId == rig.Id);

        // The block a setup is at: the first of its blocks whose frames are not all made.
        Guid? current = null;
        foreach (var block in lane.Blocks.Where(b => b.Enabled))
        {
            if (!(repeatOf.TryGetValue(block.Id, out var repeat) && repeat.Status == NodeStatus.Done))
            {
                current = block.Id;
                break;
            }
        }

        foreach (var block in lane.Blocks)
        {
            if (!cards.TryGetValue(block.Id, out var card))
            {
                continue;
            }

            if (track is null || !block.Enabled || !repeatOf.TryGetValue(block.Id, out var node))
            {
                card.ProgressText = string.Empty;
                card.ProgressFraction = 0;
                card.ActivityText = string.Empty;
                continue;
            }

            var count = block.Repeat.Count;
            if (node.Status == NodeStatus.Done)
            {
                card.ProgressText = count is { } done ? string.Create(CultureInfo.InvariantCulture, $"{done} / {done} frames") : "Done";
                card.ProgressFraction = 1;
                card.ActivityText = string.Empty;
            }
            else if (node.Status == NodeStatus.Failed)
            {
                card.ProgressText = "Failed";
                card.ProgressFraction = 0;
                card.ActivityText = string.Empty;
            }
            else if (current == block.Id)
            {
                var frame = node.Status == NodeStatus.Active ? node.Iteration : null;
                card.ProgressText = frame is { } made
                    ? (count is { } total ? string.Create(CultureInfo.InvariantCulture, $"Frame {made} / {total}") : string.Create(CultureInfo.InvariantCulture, $"Frame {made}"))
                    : "Starting";
                card.ProgressFraction = frame is { } started && count is { } all && all > 0 ? (double)(started - 1) / all : 0;
                card.ActivityText = track.HasFocus ? track.FocusText : track.CurrentStep;
            }
            else
            {
                card.ProgressText = "Queued";
                card.ProgressFraction = 0;
                card.ActivityText = string.Empty;
            }
        }
    }

    private (string Text, StatusKind Kind) TargetStatus(bool enabled, List<SequenceNodeViewModel> nodes, bool running)
    {
        if (!enabled)
        {
            return (running || _execution?.HasEnded == true ? "Skipped" : string.Empty, StatusKind.Neutral);
        }

        if (!running && _execution?.HasEnded != true)
        {
            return (string.Empty, StatusKind.Neutral);
        }

        if (nodes.Any(n => n.Status == NodeStatus.Failed))
        {
            return ("Failed", StatusKind.Error);
        }

        if (nodes.Any(n => n.Status == NodeStatus.Active))
        {
            return ("Running", StatusKind.Active);
        }

        if (nodes.Count > 0 && nodes.All(n => n.Status == NodeStatus.Done))
        {
            return ("Completed", StatusKind.Ok);
        }

        return running ? ("Pending", StatusKind.Neutral) : ("Not run", StatusKind.Warning);
    }

    /// <summary>Reads what the run says about its waits and stops into the cards; called about once a second while the page is shown, with <see cref="RefreshMeridian"/>.</summary>
    public void RefreshConditions()
    {
        if (Session is not { } session)
        {
            return;
        }

        var board = _draft.ConditionStatuses;
        (string Text, string Detail) StatusOf(Guid action)
        {
            foreach (var purpose in new[] { "x", "before", "body" })
            {
                if (board.TryGet(SessionCompiler.Derive(action, purpose), out var status))
                {
                    return (status.Text, status.Detail);
                }
            }

            return (string.Empty, string.Empty);
        }

        foreach (var list in new[] { StartList, EndList }.Concat(Targets.Select(t => t.Preparation)))
        {
            foreach (var row in list.Rows.Where(r => r.Kind == SessionActionKind.WaitUntil))
            {
                (row.StatusText, row.StatusDetail) = StatusOf(row.Id);
            }
        }

        foreach (var block in session.Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks))
        {
            var card = Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks).FirstOrDefault(b => b.Id == block.Id);
            if (card is null)
            {
                continue;
            }

            // A block that still waits to start says why; once it runs, how it is doing.
            var waiting = block.Actions.Where(a => a is WaitUntilAction).Select(a => StatusOf(a.Id)).FirstOrDefault(t => t.Text.Length > 0);
            if (waiting.Text is { Length: > 0 } && board.TryGet(SessionCompiler.Derive(block.Actions.First(a => a is WaitUntilAction).Id, "before"), out var gate) && gate.Phase == Sidera.Runtime.Sequencing.ConditionPhase.Waiting)
            {
                (card.StatusText, card.StatusDetail) = waiting;
            }
            else if (board.TryGet(SessionCompiler.Derive(block.Id, "repeat"), out var imaging))
            {
                (card.StatusText, card.StatusDetail) = (imaging.Text, imaging.Detail);
            }
            else
            {
                (card.StatusText, card.StatusDetail) = (string.Empty, string.Empty);
            }
        }
    }

    // ---- the meridian flip: where the target is relative to the meridian, and what the flips of the run are doing

    /// <summary>What the flips of the run are doing, one for each mount; empty while there is no run or no flip.</summary>
    public ObservableCollection<MeridianFlipStatusViewModel> FlipStatuses { get; } = [];

    public bool HasFlipStatus => FlipStatuses.Count > 0;

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

    /// <summary>"Meridian: 12 min until the hold · 17 min until the crossing" for the first target that is imaged, when the site is known; empty when the flip is off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeridianInfo))]
    public partial string MeridianInfoText { get; private set; } = string.Empty;

    public bool HasMeridianInfo => MeridianInfoText.Length > 0;

    /// <summary>Reads the sky again for the countdown of the target; called about once a second while the page is shown.</summary>
    public void RefreshMeridian()
    {
        var flip = Session?.Automation.Flip ?? ApplicationFlip;
        var target = Session?.Targets.FirstOrDefault(t => t.Enabled && t.Lanes.Any(l => l.Blocks.Any(b => b.Enabled)));
        if (!flip.Enabled || target is null)
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

        var minutes = MeridianFlipTiming.HourAngleHours(target.RightAscensionHours, (_draft.Clock ?? TimeProvider.System).GetUtcNow().UtcDateTime, site.LongitudeDegrees) * 60;
        MeridianInfoText = MeridianFlipTiming.PhaseOf(flip, minutes) switch
        {
            MeridianFlipPhase.Monitoring => $"Meridian: {MeridianFormat(-minutes - flip.PauseBeforeMeridianMinutes)} until the hold · {MeridianFormat(-minutes)} until the crossing",
            MeridianFlipPhase.Approaching when minutes < 0 => $"Meridian: new exposures are held when they do not fit · {MeridianFormat(-minutes)} until the crossing",
            MeridianFlipPhase.Approaching => $"Meridian: crossed {MeridianFormat(minutes)} ago · the flip is due in {MeridianFormat(flip.FlipAfterMeridianMinutes - minutes)}",
            MeridianFlipPhase.FlipDue => $"Meridian: crossed {MeridianFormat(minutes)} ago · the flip is due",
            _ => $"Meridian: crossed {MeridianFormat(minutes)} ago · the latest allowed flip has passed",
        };
    }

    private static string MeridianFormat(double minutes) => MeridianFlipGroup.FormatMinutes(minutes);

    // ---- the tree of steps

    /// <summary>The tree was asked for once and waits for the second click: the session is not made again from the steps.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingTree { get; private set; }

    /// <summary>What the user is told before the tree of steps is opened.</summary>
    public string TreeWarningText =>
        "The tree shows every step the session runs, one by one, and lets you change them. The session cannot be made again from them: its blocks, automation and limits will no longer be editable as such.";

    /// <summary>
    /// Opens the tree: the steps that the session compiled to stay exactly as they are, and the session is no longer kept. Asked twice, because the steps are not turned back into a session. What was the
    /// session is remembered, so that going back is possible for as long as the steps are untouched.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void ShowTree()
    {
        if (Session is null)
        {
            return;
        }

        if (!IsConfirmingTree)
        {
            IsConfirmingTree = true;
            return;
        }

        var session = Session;
        IsConfirmingTree = false;
        Load(null, modified: false);
        _draft.ExternalProblems = [];
        _draft.Revalidate();
        _converted = (session, Fingerprint(_draft.Snapshot()));
        RaiseModified();
        RaiseModeChanged();
    }

    [RelayCommand]
    private void CancelShowTree() => IsConfirmingTree = false;

    /// <summary>
    /// Going back to the blocks is only possible where it is exact: the sequence is empty, or it is still the very steps that a session compiled to (nothing of the tree was changed since it was
    /// opened). A tree is never read back into a session by guessing what its steps mean.
    /// </summary>
    public bool CanReturnToStructured =>
        Session is null && IsEditable && (_draft.IsEmpty || (_converted is { } converted && Fingerprint(_draft.Snapshot()) == converted.Fingerprint));

    /// <summary>Why the sequence cannot be shown as blocks; empty when it can (or already is).</summary>
    public string WhyNotStructuredText => Session is not null || CanReturnToStructured ? string.Empty : "This sequence cannot be shown as blocks: its steps are not those of a session.";

    /// <summary>Back to the blocks: a new empty session for an empty sequence, or the session that the steps still are.</summary>
    [RelayCommand(CanExecute = nameof(CanReturnToStructured))]
    private void SwitchToStructured()
    {
        if (Session is not null)
        {
            return;
        }

        var session = _draft.IsEmpty || _converted is null ? SessionDefinition.Empty : _converted.Value.Session;
        _converted = null;
        Load(session, modified: true);
        RaiseModeChanged();
    }

    // The steps as the document would hold them: two sequences with the same fingerprint are the same sequence.
    private static string Fingerprint(IReadOnlyList<SequenceStepDraft> steps) =>
        Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(steps));

    public bool HasUnreadableFields => Drawer is IUnreadableFields { HasUnreadableFields: true };
}

/// <summary>An editor with fields that are being typed in: when one of them does not read as a number the session cannot be saved faithfully.</summary>
public interface IUnreadableFields
{
    bool HasUnreadableFields { get; }
}

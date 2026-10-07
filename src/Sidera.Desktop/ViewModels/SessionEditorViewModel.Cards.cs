using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>What is open in the drawer: nothing, the meridian flip, a target, a block, or an action (<see cref="Id"/> is its id).</summary>
public readonly record struct SessionSelection(SessionSelectionKind Kind, Guid Id);

public sealed partial class SessionEditorViewModel
{
    private SessionSelection _selection = new(SessionSelectionKind.None, Guid.Empty);
    private readonly Dictionary<Guid, Guid> _shownLane = [];

    /// <summary>The start of the session: actions that run once before the first target.</summary>
    public ActionListViewModel StartList { get; }

    /// <summary>The end of the session: actions that run once after the last target.</summary>
    public ActionListViewModel EndList { get; }

    public ActionLibraryViewModel Library { get; }

    public ObservableCollection<TargetCardViewModel> Targets { get; } = [];

    /// <summary>What the session does by itself (the meridian flip), as a card.</summary>
    [ObservableProperty]
    public partial AutomationCardViewModel? Automation { get; private set; }

    /// <summary>What is open next to the sequence: the editor of the selected element; <c>null</c> when nothing is selected (then there is no panel).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDrawer))]
    public partial object? Drawer { get; private set; }

    public bool HasDrawer => Drawer is not null;

    public SessionSelection Selection => _selection;

    // ---- making the cards

    private void RebuildCards()
    {
        if (Session is not { } session)
        {
            return;
        }

        FillList(StartList, session.Start, null);
        FillList(EndList, session.End, null);

        Targets.Clear();
        var enabled = session.Targets;
        for (var i = 0; i < enabled.Count; i++)
        {
            Targets.Add(MakeTarget(session, enabled[i], i + 1, enabled.Count));
        }

        Automation = new AutomationCardViewModel(this, FlipLine(session.Automation), (session.Automation.Flip ?? ApplicationFlip).Enabled) { IsSelected = _selection.Kind == SessionSelectionKind.Flip };
        OnPropertyChanged(nameof(HasFlipStatus));
    }

    private void FillList(ActionListViewModel list, IReadOnlyList<SessionAction> actions, Func<int, string?>? filterName, bool markRepeat = false)
    {
        list.Rows.Clear();
        var firstExposure = markRepeat ? actions.ToList().FindIndex(a => a is ExposureAction) : -1;
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            list.Rows.Add(new ActionRowViewModel(this, action, i, actions.Count, ActionSummary.Of(action, filterName), i == firstExposure)
            {
                IsSelected = _selection is { Kind: SessionSelectionKind.Action } selected && selected.Id == action.Id,
                ProblemText = ProblemTextOf(action.Id),
            });
        }

        list.Changed();
    }

    private TargetCardViewModel MakeTarget(SessionDefinition session, SessionTarget target, int number, int count)
    {
        var preparation = new ActionListViewModel(this, ActionOwner.PreparationOf(target.Id), "SHARED PREPARATION", "Done once for every sequence of the target: slew and center, start guiding.");
        FillList(preparation, target.Preparation, null);
        var card = new TargetCardViewModel(this, target, number, count, preparation)
        {
            IsSelected = _selection is { Kind: SessionSelectionKind.Target } selected && selected.Id == target.Id,
            ShowLaneTabs = IsMultiSetup || target.Lanes.Count >= 2,
            ParallelText = ParallelNamesOf(target) is { Length: > 0 } names ? "Parallel imaging · " + names : string.Empty,
            SharedText = SharedNamesOf(target) is { Length: > 0 } shared ? "Shared: " + shared : string.Empty,
            LimitsText = target.Limits.Count > 0 ? "Limits · " + string.Join(", ", target.Limits.Select(BlockSummary.Brief)) : "No limits besides the blocks",
            ProblemText = ProblemTextOf(target.Id),
        };

        var shown = _shownLane.TryGetValue(target.Id, out var id) && target.Lanes.Any(l => l.Id == id) ? id : target.Lanes.FirstOrDefault()?.Id;
        foreach (var lane in target.Lanes)
        {
            card.Lanes.Add(MakeLane(session, target, lane, lane.Id == shown));
        }

        card.SelectedLane = card.Lanes.FirstOrDefault(l => l.IsSelected);
        return card;
    }

    private LaneViewModel MakeLane(SessionDefinition session, SessionTarget target, SetupLane lane, bool shown)
    {
        var rig = ResolvedSetup(lane.Setup);
        var name = rig?.Name ?? (lane.Setup is null ? "Choose a setup" : "Not available");
        var vm = new LaneViewModel(this, target.Id, lane, IsMultiSetup || target.Lanes.Count >= 2 ? name : "Imaging", target.Lanes.Count > 1)
        {
            IsSelected = shown,
            ProblemText = ProblemTextOf(lane.Id),
            NeedsSetupChoice = (IsMultiSetup || lane.Setup is not null) && rig is null,
        };

        if (vm.NeedsSetupChoice)
        {
            var taken = target.Lanes.Where(l => l.Id != lane.Id).Select(l => ResolvedSetup(l.Setup)?.Id).OfType<RigId>().ToHashSet();
            foreach (var choice in SetupChoices().Where(c => ResolvedSetup(c.Id) is { } r && !taken.Contains(r.Id)))
            {
                vm.SetupChoices.Add(choice);
            }
        }

        var filterName = FilterNameOf(lane.Setup);
        for (var i = 0; i < lane.Blocks.Count; i++)
        {
            var block = lane.Blocks[i];
            vm.Blocks.Add(new BlockCardViewModel(this, block, i + 1, lane.Blocks.Count, BlockSummary.Title(block, i + 1, filterName), BlockSummary.Line(block))
            {
                IsSelected = IsBlockSelected(block),
                ProblemText = ProblemTextOf(block.Id),
            });
        }

        vm.Changed();
        return vm;
    }

    private bool IsBlockSelected(SequenceBlock block) =>
        _selection.Kind == SessionSelectionKind.Block && _selection.Id == block.Id
        || _selection.Kind == SessionSelectionKind.Action && block.Actions.Any(a => a.Id == _selection.Id);

    /// <summary>"Enabled · Hold −5m · Flip +2m · Recenter · Autofocus · Guiding", or "Off"; with the application's settings it says so.</summary>
    internal string FlipLine(SessionAutomation automation)
    {
        var settings = automation.Flip ?? ApplicationFlip;
        if (!settings.Enabled)
        {
            return automation.UsesDefaultFlip ? "Off · follows Settings → Meridian Flip" : "Off";
        }

        var parts = new List<string>
        {
            "Enabled",
            string.Create(CultureInfo.InvariantCulture, $"Hold −{settings.PauseBeforeMeridianMinutes:0.#}m"),
            string.Create(CultureInfo.InvariantCulture, $"Flip +{settings.FlipAfterMeridianMinutes:0.#}m"),
        };
        if (settings.RecenterAfterFlip)
        {
            parts.Add("Recenter");
        }

        if (settings.VerifyRotationAfterFlip)
        {
            parts.Add("Rotation");
        }

        if (settings.AutofocusAfterFlip)
        {
            parts.Add("Autofocus");
        }

        if (settings.RestartGuidingAfterFlip)
        {
            parts.Add("Guiding");
        }

        if (settings.DitherAfterFlip)
        {
            parts.Add("Dither");
        }

        return string.Join(" · ", parts);
    }

    // ---- selecting

    [RelayCommand]
    public void ClearSelection() => Select(SessionSelectionKind.None, Guid.Empty);

    public void SelectTarget(Guid id) => Select(SessionSelectionKind.Target, id);

    public void SelectBlock(Guid id) => Select(SessionSelectionKind.Block, id);

    public void SelectAction(Guid id) => Select(SessionSelectionKind.Action, id);

    public void SelectFlip() => Select(SessionSelectionKind.Flip, Guid.Empty);

    /// <summary>Shows the sequence of another setup under a target. It does not change the imaging setup of the application: the two are separate on purpose.</summary>
    public void SelectLane(Guid target, Guid lane)
    {
        _shownLane[target] = lane;
        if (Session is { } s && _selection.Kind is SessionSelectionKind.Block or SessionSelectionKind.Action
            && SessionEdits.LaneOf(s, _selection.Id) is { } selectedLane && selectedLane.Id != lane)
        {
            _selection = new SessionSelection(SessionSelectionKind.None, Guid.Empty);
            RebuildDrawer();
        }

        RebuildCards();
    }

    internal void Select(SessionSelectionKind kind, Guid id)
    {
        _selection = new SessionSelection(kind, id);
        RebuildCards();
        RebuildDrawer();
    }

    private void RebuildDrawer()
    {
        if (Session is not { } session)
        {
            Drawer = null;
            return;
        }

        switch (_selection.Kind)
        {
            case SessionSelectionKind.Flip:
                Drawer = new FlipEditorViewModel(this, session.Automation);
                break;
            case SessionSelectionKind.Target when SessionEdits.FindTarget(session, _selection.Id) is { } target:
                Drawer = new TargetDrawerViewModel(this, target);
                break;
            case SessionSelectionKind.Block when SessionEdits.FindBlock(session, _selection.Id) is { } block:
                Drawer = new BlockDrawerViewModel(this, block, SessionEdits.LaneOf(session, block.Id)?.Setup);
                break;
            case SessionSelectionKind.Action when SessionEdits.FindAction(session, _selection.Id) is { } action:
                Drawer = new ActionEditorViewModel(this, action, SessionEdits.OwnerOf(session, action.Id) ?? ActionOwner.Start, SessionEdits.LaneOf(session, action.Id)?.Setup);
                break;
            default:
                _selection = new SessionSelection(SessionSelectionKind.None, Guid.Empty);
                Drawer = null;
                break;
        }
    }

    /// <summary>The actions of a block as lines, for the drawer.</summary>
    internal IEnumerable<ActionRowViewModel> RowsFor(SequenceBlock block, Func<int, string?> filterName)
    {
        var firstExposure = block.Actions.ToList().FindIndex(a => a is ExposureAction);
        for (var i = 0; i < block.Actions.Count; i++)
        {
            var action = block.Actions[i];
            yield return new ActionRowViewModel(this, action, i, block.Actions.Count, ActionSummary.Of(action, filterName), i == firstExposure)
            {
                IsSelected = _selection is { Kind: SessionSelectionKind.Action } selected && selected.Id == action.Id,
                ProblemText = ProblemTextOf(action.Id),
            };
        }
    }

    internal void EditAutomation(SessionAutomation automation, bool rebuildDrawer = false) => Edit(s => s with { Automation = automation }, rebuildDrawer);

    // ---- targets

    /// <summary>Adds a target with what its setup needs to image it: the preparation, one sequence and one block.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddTarget()
    {
        if (Session is null)
        {
            if (!_draft.IsEmpty)
            {
                return;
            }

            Load(SessionDefinition.Empty, modified: true);
        }

        var target = NewTarget($"Target {Session!.Targets.Count + 1}", SequenceDraftDefaults.TargetRightAscensionHours, SequenceDraftDefaults.TargetDeclinationDegrees, null, null);
        AddTargetCore(target);
        Select(SessionSelectionKind.Target, target.Id);
    }

    private SessionTarget NewTarget(string name, double ra, double dec, double? rotation, RigId? framingSetup)
    {
        var usable = UsableRigs;
        ImagingBindingId? laneSetup = null;
        if (usable.Count >= 2)
        {
            // The setup the target was framed with; else the one the application works with now; else the first.
            var rig = framingSetup is { } id ? ResolvedSetup(id) : CurrentSetup?.Invoke() is { } current ? ResolvedSetup(current) : null;
            laneSetup = PathOf(rig ?? usable[0]);
        }

        var lanes = SessionTemplates.NewTarget(name, ra, dec, rotation, usable, laneSetup, _defaults.ExposureSeconds, AutofocusDefaults, GuidingDefaults);
        return lanes;
    }

    private void AddTargetCore(SessionTarget target)
    {
        var first = Session!.Targets.Count == 0 && Session.End.Count == 0;
        Edit(s => SessionEdits.AddTarget(s, target) with { End = first ? [.. SessionTemplates.DefaultEnd(UsableRigs, GuidingDefaults)] : s.End });
    }

    /// <summary>Framing hands over a target: it is added after the targets there are, with the preparation that centers it.</summary>
    internal string? AddTarget(SessionTargetRequest request)
    {
        if (!_draft.IsEditable)
        {
            return null;
        }

        if (Session is null)
        {
            if (!_draft.IsEmpty)
            {
                return null;
            }

            Load(SessionDefinition.Empty, modified: true);
        }

        var target = NewTarget(request.Name, request.RightAscensionHours, request.DeclinationDegrees, request.DesiredRotationDegrees, request.Setup);
        AddTargetCore(target);
        Select(SessionSelectionKind.Target, target.Id);
        return $"Added {request.Name} to the session.";
    }

    internal void RemoveTarget(Guid id)
    {
        Edit(s => SessionEdits.RemoveTarget(s, id));
        if (Session is { } session && _selection.Kind != SessionSelectionKind.None && !Exists(_selection.Id) && _selection.Kind != SessionSelectionKind.Flip)
        {
            Select(SessionSelectionKind.None, Guid.Empty);
        }
    }

    internal void DuplicateTarget(Guid id)
    {
        var copy = Guid.NewGuid();
        Edit(s => SessionEdits.DuplicateTarget(s, id, copy));
        Select(SessionSelectionKind.Target, copy);
    }

    internal void MoveTarget(Guid id, int delta) => Edit(s => SessionEdits.MoveTarget(s, id, delta));

    internal void SetTargetEnabled(Guid id, bool on) => Edit(s => SessionEdits.ReplaceTarget(s, id, t => t with { Enabled = on }));

    internal void EditTarget(Guid id, Func<SessionTarget, SessionTarget> change, bool rebuildDrawer = false) => Edit(s => SessionEdits.ReplaceTarget(s, id, change), rebuildDrawer);

    // ---- sequences (one for each imaging setup of a target)

    internal void AddLane(Guid targetId)
    {
        if (Session is not { } session || SessionEdits.FindTarget(session, targetId) is not { } target)
        {
            return;
        }

        if (target.Lanes.Any(l => l.Setup is null) && IsMultiSetup)
        {
            SetupNotice = "Choose the imaging setup of the first sequence before adding another.";
            return;
        }

        var taken = target.Lanes.Select(l => ResolvedSetup(l.Setup)?.Id).OfType<RigId>().ToHashSet();
        var free = UsableRigs.FirstOrDefault(r => !taken.Contains(r.Id));
        if (free is null)
        {
            SetupNotice = "No additional imaging setup is available.";
            return;
        }

        SetupNotice = string.Empty;
        var lane = new SetupLane(Guid.NewGuid(), PathOf(free), [SessionTemplates.NewBlock(free, _defaults.ExposureSeconds, AutofocusDefaults, GuidingDefaults)]);
        _shownLane[targetId] = lane.Id;
        Edit(s => SessionEdits.AddLane(s, targetId, lane));
    }

    internal void RemoveLane(Guid id)
    {
        Edit(s => SessionEdits.RemoveLane(s, id));
        if (_selection.Kind is SessionSelectionKind.Block or SessionSelectionKind.Action && !Exists(_selection.Id))
        {
            Select(SessionSelectionKind.None, Guid.Empty);
        }
    }

    internal void SetLaneSetup(Guid lane, ImagingBindingId setup) => Edit(s => SessionEdits.ReplaceLane(s, lane, l => l with { Setup = setup }));

    // ---- blocks

    internal void AddBlock(Guid laneId)
    {
        if (Session is not { } session || SessionEdits.TargetOf(session, laneId) is null)
        {
            return;
        }

        var lane = SessionEdits.FindLane(session, laneId);
        var block = SessionTemplates.NewBlock(ResolvedSetup(lane?.Setup), _defaults.ExposureSeconds, AutofocusDefaults, GuidingDefaults);
        Edit(s => SessionEdits.AddBlock(s, laneId, block));
        Select(SessionSelectionKind.Block, block.Id);
    }

    internal void AddBlockToTarget(Guid targetId)
    {
        if (Session is not { } session || SessionEdits.FindTarget(session, targetId) is not { } target)
        {
            return;
        }

        if (target.Lanes.Count > 0)
        {
            AddBlock(target.Lanes[0].Id);
            return;
        }

        var setup = IsMultiSetup ? PathOf(UsableRigs[0]) : (ImagingBindingId?)null;
        var lane = new SetupLane(Guid.NewGuid(), setup, []);
        Edit(s => SessionEdits.AddLane(s, targetId, lane));
        AddBlock(lane.Id);
    }

    internal void RemoveBlock(Guid id)
    {
        Edit(s => SessionEdits.RemoveBlock(s, id));
        if (_selection.Kind is SessionSelectionKind.Block or SessionSelectionKind.Action && !Exists(_selection.Id))
        {
            Select(SessionSelectionKind.None, Guid.Empty);
        }
    }

    internal void DuplicateBlock(Guid id)
    {
        var copy = Guid.NewGuid();
        Edit(s => SessionEdits.DuplicateBlock(s, id, copy));
        Select(SessionSelectionKind.Block, copy);
    }

    internal void MoveBlock(Guid id, int delta) => Edit(s => SessionEdits.MoveBlock(s, id, delta));

    internal void SetBlockEnabled(Guid id, bool on) => Edit(s => SessionEdits.ReplaceBlock(s, id, b => b with { Enabled = on }));

    internal void EditBlock(Guid id, Func<SequenceBlock, SequenceBlock> change, bool rebuildDrawer = false) => Edit(s => SessionEdits.ReplaceBlock(s, id, change), rebuildDrawer);

    // ---- actions

    internal void SetActionEnabled(Guid id, bool on) => Edit(s => SessionEdits.ReplaceAction(s, id, a => a with { Enabled = on }));

    internal void RemoveAction(Guid id)
    {
        var owner = Session is { } session ? SessionEdits.OwnerOf(session, id) : null;
        var wasSelected = _selection.Kind == SessionSelectionKind.Action && _selection.Id == id;
        Edit(s => SessionEdits.RemoveAction(s, id));
        if (wasSelected)
        {
            if (owner is { Place: ActionPlace.Block, Owner: { } block })
            {
                Select(SessionSelectionKind.Block, block);
            }
            else
            {
                Select(SessionSelectionKind.None, Guid.Empty);
            }
        }
    }

    internal void MoveAction(Guid id, int delta) => Edit(s => SessionEdits.MoveAction(s, id, delta));

    internal void EditAction(Guid id, Func<SessionAction, SessionAction> change, bool rebuildDrawer = false) => Edit(s => SessionEdits.ReplaceAction(s, id, change), rebuildDrawer);

    /// <summary>Adds an action of the library to a list; the new action is opened to be set.</summary>
    internal void AddAction(ActionOwner owner, SessionActionKind kind)
    {
        var action = NewAction(kind);
        int? index = null;

        // Actions that prepare the block (a filter, a focus, a wait) go before the first exposure: they run once, when the block starts.
        if (owner.Place == ActionPlace.Block && Session is { } session && kind is SessionActionKind.SetFilter or SessionActionKind.WaitUntil or SessionActionKind.Autofocus or SessionActionKind.MoveFocuser)
        {
            var existing = SessionEdits.ActionsOf(session, owner).ToList();
            var first = existing.FindIndex(a => a is ExposureAction);
            index = first >= 0 ? first : null;
        }

        Edit(s => SessionEdits.AddAction(s, owner, action, index));
        Select(SessionSelectionKind.Action, action.Id);
    }

    private SessionAction NewAction(SessionActionKind kind)
    {
        var id = Guid.NewGuid();
        var focus = AutofocusDefaults;
        var guiding = GuidingDefaults;
        return kind switch
        {
            SessionActionKind.Exposure => new ExposureAction(id, _defaults.ExposureSeconds),
            SessionActionKind.Wait => new WaitAction(id, _defaults.DelaySeconds),
            SessionActionKind.Autofocus => new AutofocusAction(id, new FocusSettings(focus.ExposureSeconds, focus.StepSize, focus.SampleCount)),
            SessionActionKind.MoveFocuser => new MoveFocuserAction(id, _defaults.FocuserPosition),
            SessionActionKind.DitherNow => new DitherNowAction(id, new DitherSettings(guiding.DitherAmplitudePixels, guiding.SettleThresholdPixels, guiding.SettleStableSeconds, guiding.SettleTimeoutSeconds)),
            _ => ActionCatalog.Of(kind).Create(id),
        };
    }
}

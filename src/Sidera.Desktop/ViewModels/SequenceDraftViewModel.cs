using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Focusing;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Microsoft.Extensions.Logging;
using Sidera.Runtime.Rigs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The sequence the user is editing: an ordered list of steps. A Repeat holds steps of its own; a Multi-Rig block holds
/// Rig Tracks, which hold exposures, delays and Repeats of those; nothing nests deeper than that. Steps can be added,
/// removed and moved among their siblings, and the one step whose parameters are shown is the selected one, wherever
/// it is. The session's shared equipment (the mount and the guider) is part of the draft.
/// It is only a draft. Nothing in it is executed: every run builds a fresh runtime sequence from a snapshot of it
/// (<see cref="Build"/>), and while a sequence runs <see cref="IsEditable"/> is false and every command that
/// changes the list is unavailable.
/// <para>
/// After every change all steps are read again and validated: each step shows its own problems, a container also says
/// that a step inside has one, and <see cref="ValidationErrors"/> lists all of them. <see cref="IsValid"/> is only
/// what the editor says; <see cref="Build"/> validates again on its own.
/// </para>
/// </summary>
public sealed partial class SequenceDraftViewModel : ViewModelBase
{
    private readonly DeviceRegistry _registry;
    private readonly RigRegistry? _rigs;
    private readonly SequenceDraftDefaults _defaults;
    private readonly ISequenceStepClipboard _clipboard;
    private readonly IFocusMetricProvider? _focusMetrics;
    private readonly IEventPublisher? _events;
    private readonly ILoggerFactory? _loggers;
    private readonly IAcquisitionDefaultsSource? _acquisitionDefaults;
    private readonly Sidera.Runtime.Astrometry.PlateSolveService? _plateSolving;
    private readonly Func<Sidera.Core.Astrometry.PlateSolveDefaults>? _solveDefaults;
    private HashSet<Guid> _unreadable = [];
    private bool _rebuilding;

    public SequenceDraftViewModel(
        DeviceRegistry registry,
        SequenceDraftDefaults defaults,
        IEnumerable<SequenceStepDraft>? initialSteps = null,
        ISequenceStepClipboard? clipboard = null,
        RigRegistry? rigs = null,
        SharedEquipmentDraft? shared = null,
        IFocusMetricProvider? focusMetrics = null,
        IEventPublisher? events = null,
        ILoggerFactory? loggers = null,
        IAcquisitionDefaultsSource? acquisitionDefaults = null,
        Sidera.Runtime.Astrometry.PlateSolveService? plateSolving = null,
        Func<Sidera.Core.Astrometry.PlateSolveDefaults>? solveDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(defaults);
        _acquisitionDefaults = acquisitionDefaults;
        _plateSolving = plateSolving; _solveDefaults = solveDefaults;
        _registry = registry;
        _rigs = rigs;
        _focusMetrics = focusMetrics;
        _events = events;
        _loggers = loggers;
        _defaults = defaults;
        _clipboard = clipboard ?? new SequenceStepClipboard();
        _clipboard.Changed += (_, _) => NotifyCommands();

        SharedMount = new DevicePickerViewModel(registry, device => device is IMount, shared?.MountId);
        SharedGuider = new DevicePickerViewModel(registry, device => device is IGuider, shared?.GuiderId);
        SharedMount.Changed += OnSharedChanged;
        SharedGuider.Changed += OnSharedChanged;

        Steps = [];
        Rows = [];
        foreach (var draft in initialSteps ?? [])
        {
            var step = CreateViewModel(draft);
            Attach(step);
            Steps.Add(step);
        }

        Steps.CollectionChanged += (_, _) => OnStepsChanged();
        RebuildRows();
        SelectedStep = Steps.FirstOrDefault();
        Revalidate();
    }

    /// <summary>The steps of the sequence itself, in order; containers hold the steps inside them.</summary>
    public ObservableCollection<StepDraftViewModel> Steps { get; }

    /// <summary>All steps as they are listed: each step of the sequence, followed by everything inside it.</summary>
    public ObservableCollection<StepDraftViewModel> Rows { get; }

    /// <summary>The copied step, kept for the session: it outlives New and Open, so steps can be copied between sequences.</summary>
    public ISequenceStepClipboard Clipboard => _clipboard;

    /// <summary>The mount the whole session shares.</summary>
    public DevicePickerViewModel SharedMount { get; }

    /// <summary>The guider the whole session shares.</summary>
    public DevicePickerViewModel SharedGuider { get; }

    /// <summary>The shared equipment as selected now.</summary>
    public SharedEquipmentDraft SharedEquipment => new(SharedMount.SelectedId, SharedGuider.SelectedId);

    /// <summary>The shared equipment a new sequence starts with: what the defaults name.</summary>
    public SharedEquipmentDraft DefaultSharedEquipment => new(_defaults.MountId, _defaults.GuiderId);

    /// <summary>What is wrong with the shared equipment: a device that is not there, or of the wrong kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSharedProblems))]
    public partial IReadOnlyList<string> SharedProblems { get; private set; } = [];

    public bool HasSharedProblems => SharedProblems.Count > 0;

    /// <summary>The step whose parameters are shown: any step, also one inside a container.</summary>
    [ObservableProperty]
    public partial StepDraftViewModel? SelectedStep { get; set; }

    /// <summary>False while a sequence runs (also while it pauses): the list and the parameters cannot be changed.</summary>
    [ObservableProperty]
    public partial bool IsEditable { get; set; } = true;

    public bool IsEmpty => Steps.Count == 0;

    /// <summary>What is wrong with the draft right now, as sentences; empty when it can be built.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationErrors))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial IReadOnlyList<string> ValidationErrors { get; private set; } = [];

    public bool HasValidationErrors => ValidationErrors.Count > 0;
    public bool IsValid => ValidationErrors.Count == 0;

    /// <summary>Raised after every re-evaluation of the draft.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised when the user (or code acting for them) changed the draft: a step added, removed or moved, a parameter,
    /// a device or a rig edited, the shared equipment changed. Not raised when the draft is only read again
    /// (equipment came or went), nor by <see cref="ReplaceSteps"/>, nor by anything about running the sequence.
    /// </summary>
    public event EventHandler? Modified;

    /// <summary>Some field holds text that is not a number. The draft can still be shown, but not saved faithfully.</summary>
    public bool HasUnreadableFields { get; private set; }

    /// <summary>
    /// Replaces the whole sequence, for example with a document that was opened, and the shared equipment with it.
    /// All new step view models are made before anything of the current sequence is touched.
    /// </summary>
    public void Replace(IEnumerable<SequenceStepDraft> steps, SharedEquipmentDraft? shared)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var created = steps.Select(CreateViewModel).ToList();
        SharedMount.Reset(shared?.MountId);
        SharedGuider.Reset(shared?.GuiderId);
        Install(created);
    }

    /// <summary>Replaces the steps and keeps the shared equipment as it is.</summary>
    public void ReplaceSteps(IEnumerable<SequenceStepDraft> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Install(steps.Select(CreateViewModel).ToList());
    }

    private void Install(List<StepDraftViewModel> created)
    {
        foreach (var old in Steps)
        {
            Detach(old);
        }

        Steps.Clear();
        foreach (var step in created)
        {
            Attach(step);
            Steps.Add(step);
        }

        SelectedStep = null;
        RebuildRows();
        SelectedStep = Steps.FirstOrDefault();
        Revalidate();
    }

    /// <summary>The steps as values: the draft that would be built, with unreadable fields replaced by stand-ins.</summary>
    public IReadOnlyList<SequenceStepDraft> Snapshot() => ReadAll(new Dictionary<Guid, IReadOnlyList<string>>());

    /// <summary>The devices the sequence needs for running: those its steps name, and the cameras of the rigs of its tracks.</summary>
    public IReadOnlyCollection<DeviceId> RequiredDeviceIds() =>
        SequenceDraftBuilder.RequiredDeviceIds(Snapshot(), Context);

    private SequenceDraftContext Context => new(_rigs, SharedEquipment, _focusMetrics, _events, _loggers, _acquisitionDefaults, _plateSolving, _solveDefaults);

    // New steps use the shared equipment of the session wherever they have a mount or a guider.
    private SequenceDraftDefaults EffectiveDefaults => _defaults with
    {
        MountId = SharedMount.SelectedId ?? _defaults.MountId,
        GuiderId = SharedGuider.SelectedId ?? _defaults.GuiderId,
        AutofocusRigId = _defaults.AutofocusRigId ?? FirstRigWithFocuser(),
    };

    // A new top-level autofocus starts with a rig it can focus: the first (by id) that has a focuser.
    private RigId? FirstRigWithFocuser() =>
        (_rigs?.GetAll() ?? []).Where(rig => rig.FocuserId is not null).OrderBy(rig => rig.Id.Value, StringComparer.Ordinal)
            .Select(rig => (RigId?)rig.Id).FirstOrDefault();

    /// <summary>Builds a new sequence from the current draft, validating it again.</summary>
    /// <exception cref="SequenceConfigurationException">The draft is not valid.</exception>
    public BuiltSequence Build()
    {
        Revalidate();
        if (!IsValid)
        {
            throw new SequenceConfigurationException(ValidationErrors);
        }

        return SequenceDraftBuilder.Build(_registry, Snapshot(), Context);
    }

    /// <summary>Reads all fields again and validates. Also catches a device that disappeared since the last time.</summary>
    public void Revalidate()
    {
        // The rigs a block can be triggered by are those of its tracks, which may have just changed.
        foreach (var block in Rows.OfType<MultiRigStepDraftViewModel>())
        {
            block.TriggerRig.Refresh();
        }

        // The names of the slots of a wheel come from a device, or from the rig of a track, which may have just changed.
        foreach (var choice in Rows.SelectMany(step => step.FilterChoices))
        {
            choice.Refresh();
        }

        var parseErrors = new Dictionary<Guid, IReadOnlyList<string>>();
        var drafts = ReadAll(parseErrors);
        HasUnreadableFields = parseErrors.Count > 0;
        _unreadable = [.. parseErrors.Keys];
        var context = Context;
        var validation = SequenceDraftBuilder.Validate(_registry, drafts, context);

        var sentences = new List<string>(validation.SequenceProblems);
        sentences.AddRange(validation.SharedProblems ?? []);
        if (!(validation.SharedProblems ?? []).SequenceEqual(SharedProblems))
        {
            SharedProblems = validation.SharedProblems ?? [];
        }

        List<string> Problems(StepDraftViewModel step) =>
            parseErrors.GetValueOrDefault(step.Id, []).Concat(validation.ProblemsOf(step.Id)).ToList();

        // Shows one step and everything inside it; says whether anything in it has a problem.
        bool Present(StepDraftViewModel step, SequenceStepDraft draft, int[] path, int number, Rig? rig)
        {
            var own = Problems(step);
            step.Number = number;
            step.NumberLabel = SequenceDraftBuilder.Label(path);
            sentences.AddRange(own.Select(p => $"Step {step.NumberLabel} ({SequenceDraftBuilder.TitleOf(step.Kind)}): {p}"));

            // The rig of a track is the rig of everything in it: its focuser and its filter wheel.
            var inner = draft is RigTrackDraft trackDraft && trackDraft.RigId is { } trackRigId
                        && _rigs is not null && _rigs.TryGet(trackRigId, out var trackRig)
                ? trackRig
                : rig;

            var inside = false;
            if (step is ContainerStepDraftViewModel container)
            {
                var drafted = ChildDrafts(draft);
                for (var j = 0; j < container.Children.Count; j++)
                {
                    inside |= Present(container.Children[j], drafted[j], [.. path, j], j + 1, draft is MultiRigStepDraft ? null : inner);
                }
            }

            // A container shows that something inside it needs attention; the sentences name the step itself.
            IReadOnlyList<string> shown = inside ? [.. own, "A step inside has a problem."] : own;
            step.Show(
                draft is RigTrackDraft track
                    ? SequenceDraftBuilder.DescribeTrack(_registry, track, context)
                    : SequenceDraftBuilder.Describe(_registry, draft, context, rig),
                shown);
            return own.Count > 0 || inside;
        }

        for (var i = 0; i < Steps.Count; i++)
        {
            Present(Steps[i], drafts[i], [i], i + 1, null);
        }

        if (!sentences.SequenceEqual(ValidationErrors))
        {
            ValidationErrors = sentences;
        }

        NotifyCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the device and rig registries again (equipment may have come or gone), then validates.</summary>
    public void RefreshDevices()
    {
        foreach (var picker in Rows.SelectMany(step => step.Pickers).Append(SharedMount).Append(SharedGuider))
        {
            picker.Refresh();
        }

        foreach (var picker in Rows.SelectMany(step => step.RigPickers))
        {
            picker.Refresh();
        }

        Revalidate();
    }

    /// <summary>Adds a step to the end of the sequence itself, wherever the selection is.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddStep(SequenceStepKind kind)
    {
        if (kind is SequenceStepKind.RigExposure or SequenceStepKind.RigTrack
            or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus)
        {
            return; // these only exist inside a Multi-Rig block
        }

        InsertAt(null, Steps.Count, EffectiveDefaults.Create(kind));
    }

    /// <summary>
    /// Adds a step to the end of the selected Repeat, or of the Repeat the selected step is in. Inside a Rig Track an
    /// exposure is the exposure of the rig.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddChild))]
    private void AddChild(SequenceStepKind kind)
    {
        if (!CanAddChild(kind))
        {
            return;
        }

        var repeat = ChildTarget!;
        InsertAt(repeat, repeat.Children.Count, NewTrackLeaf(repeat.IsInTrack ? repeat.Parent as RigTrackDraftViewModel : null, kind));
    }

    /// <summary>Adds a Rig Track to the selected Multi-Rig block, or to the one the selected step is in.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTrack))]
    private void AddTrack()
    {
        if (!CanAddTrack)
        {
            return;
        }

        var multiRig = MultiRigTarget!;
        var used = multiRig.Children.OfType<RigTrackDraftViewModel>().Select(track => track.Rig.SelectedId).ToHashSet();
        var rig = (_rigs?.GetAll() ?? [])
            .OrderBy(r => r.Id.Value, StringComparer.Ordinal)
            .Select(r => (RigId?)r.Id)
            .FirstOrDefault(id => !used.Contains(id));

        InsertAt(multiRig, multiRig.Children.Count, new RigTrackDraft(NewId(), rig, []));
    }

    /// <summary>Adds an exposure, a delay or a Repeat to the end of the selected Rig Track, or of the one the selected step is in.</summary>
    [RelayCommand(CanExecute = nameof(CanAddTrackStep))]
    private void AddTrackStep(SequenceStepKind kind)
    {
        if (!CanAddTrackStep(kind))
        {
            return;
        }

        var track = TrackTarget!;
        SequenceStepDraft step = kind == SequenceStepKind.Repeat
            ? new RepeatStepDraft(NewId(), _defaults.RepeatCount, [])
            : NewTrackLeaf(track, kind);
        InsertAt(track, track.Children.Count, step);
    }

    /// <summary>
    /// Puts a copy of the selected step, with new ids, right after it, in the same list: a step of the sequence after
    /// that step, a step inside a Repeat after that step inside the Repeat, a Repeat or a Multi-Rig block with
    /// everything inside it after it. The copy is selected. It is not checked for sense: a second Start Guiding is
    /// added, and the validation says what is wrong with it. A Rig Track is not duplicated on its own: two tracks of
    /// one rig cannot run.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDuplicate))]
    private void DuplicateStep()
    {
        if (!CanDuplicate)
        {
            return;
        }

        var source = SelectedStep!;
        var clone = SequenceStepDraftCloner.CloneWithNewIds(ReadStep(source, []), AllIds());
        InsertAfter(source, clone);
    }

    /// <summary>Keeps a snapshot of the selected step, with everything inside it, on the clipboard. The sequence is not changed.</summary>
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyStep()
    {
        if (CanCopy)
        {
            _clipboard.Copy(ReadStep(SelectedStep!, []));
        }
    }

    /// <summary>
    /// Pastes a copy of the clipboard, with new ids, and selects it. Where it goes: with nothing selected at the end of
    /// the sequence; with a Rig Track selected at the end of the track; with any other step selected right after it in
    /// its own list, so a Repeat or a Multi-Rig block is never entered. It goes only where such a step may be: a copied
    /// Repeat cannot go inside a Repeat, an exposure with a camera not into a track, and so on. Then pasting is not
    /// available (nothing is put on another level instead).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteStep()
    {
        if (!CanPaste)
        {
            return;
        }

        var target = FindPasteTarget()!;
        InsertAt(target.Parent, target.Index, _clipboard.CreateClone(AllIds()));
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveStep()
    {
        var step = SelectedStep!;
        var siblings = SiblingsOf(step);
        var index = siblings.IndexOf(step);
        Detach(step);
        siblings.RemoveAt(index);
        RebuildRows();

        // The next step in the same list, else the one before; after the last step of a container, the container.
        SelectedStep = siblings.Count > 0 ? siblings[Math.Min(index, siblings.Count - 1)] : step.Parent;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveStepUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveStepDown() => MoveSelected(1);

    public bool CanAdd => IsEditable;

    /// <summary>The Repeat that a new child would go into: the selected one, or the one the selected step is in.</summary>
    public RepeatStepDraftViewModel? ChildTarget => SelectedStep as RepeatStepDraftViewModel ?? SelectedStep?.Parent as RepeatStepDraftViewModel;

    /// <summary>The Multi-Rig block of the selection: the selected one, or the one the selected step is in.</summary>
    public MultiRigStepDraftViewModel? MultiRigTarget => Ancestors(SelectedStep).OfType<MultiRigStepDraftViewModel>().FirstOrDefault();

    /// <summary>The Rig Track of the selection: the selected one, or the one the selected step is in.</summary>
    public RigTrackDraftViewModel? TrackTarget => Ancestors(SelectedStep).OfType<RigTrackDraftViewModel>().FirstOrDefault();

    /// <summary>The selection is a Multi-Rig block or something inside one: tracks and track steps can be added.</summary>
    public bool IsMultiRigContext => MultiRigTarget is not null;

    /// <summary>Steps can be added inside a Repeat right now.</summary>
    public bool CanAddChildHere => IsEditable && ChildTarget is not null;

    public bool CanAddChild(SequenceStepKind kind) =>
        CanAddChildHere
        && (ChildTarget!.IsInTrack
            ? kind is SequenceStepKind.Exposure or SequenceStepKind.Delay or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter
                or SequenceStepKind.Autofocus
            : kind is SequenceStepKind.Exposure or SequenceStepKind.Delay or SequenceStepKind.Slew
                or SequenceStepKind.StartGuiding or SequenceStepKind.StopGuiding or SequenceStepKind.Dither
                or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter or SequenceStepKind.Autofocus);

    public bool CanAddTrack => IsEditable && MultiRigTarget is not null;

    public bool CanAddTrackStep(SequenceStepKind kind) =>
        IsEditable && TrackTarget is not null
        && kind is SequenceStepKind.Exposure or SequenceStepKind.Delay or SequenceStepKind.Repeat
            or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter or SequenceStepKind.Autofocus;

    public bool CanRemove => IsEditable && SelectedStep is not null;
    public bool CanMoveUp => IsEditable && SelectedStep is not null && SiblingsOf(SelectedStep).IndexOf(SelectedStep) > 0;

    public bool CanMoveDown => IsEditable && SelectedStep is not null
        && SiblingsOf(SelectedStep) is var siblings && siblings.IndexOf(SelectedStep) is var i && i >= 0 && i < siblings.Count - 1;

    // A step whose fields do not all read as numbers cannot be copied faithfully; it has to be fixed first.
    public bool CanDuplicate => IsEditable && SelectedStep is { } step && step is not RigTrackDraftViewModel && IsReadable(step);
    public bool CanCopy => CanDuplicate;

    public bool CanPaste => IsEditable && _clipboard.HasContent && FindPasteTarget() is not null;

    partial void OnIsEditableChanged(bool value)
    {
        if (!value)
        {
            EndDrag();
        }

        NotifyCommands();
    }

    partial void OnSelectedStepChanged(StepDraftViewModel? value) => NotifyCommands();

    private void OnSharedChanged(object? sender, EventArgs e)
    {
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    // Steps move among their siblings only: never out of a container, and never into one.
    private void MoveSelected(int offset)
    {
        var selected = SelectedStep!;
        var siblings = SiblingsOf(selected);
        var from = siblings.IndexOf(selected);
        siblings.Move(from, from + offset);
        RebuildRows();
        SelectedStep = selected;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private ObservableCollection<StepDraftViewModel> SiblingsOf(StepDraftViewModel step) => step.Parent?.Children ?? Steps;

    // Drag and drop. The view finds the row under the pointer and asks; the draft judges, moves, selects and says it was
    // modified. A step moves within its own list only (the sequence, a Repeat, a Rig Track, a Multi-Rig block): to a place
    // that would break the structure it is refused, and to another list it is not offered.

    /// <summary>The step that is being dragged now, or <c>null</c>.</summary>
    public StepDraftViewModel? DraggedStep { get; private set; }

    /// <summary>
    /// Whether the step could be put into the list of <paramref name="targetParentId"/> (<c>null</c> for the sequence) at
    /// <paramref name="targetIndex"/>, counted among the siblings as they are now, before the step is taken out.
    /// </summary>
    public bool CanMoveStep(Guid sourceId, Guid? targetParentId, int targetIndex) =>
        JudgeMove(sourceId, targetParentId, targetIndex).IsMove;

    /// <summary>Why the step cannot be put there, in a sentence; <c>null</c> when it can, and also when it is there already.</summary>
    public string? WhyNotMoveStep(Guid sourceId, Guid? targetParentId, int targetIndex) =>
        JudgeMove(sourceId, targetParentId, targetIndex).Reason;

    /// <summary>
    /// Moves the step to the place and selects it; the draft is then modified. Nothing happens, and nothing is reported,
    /// when <see cref="CanMoveStep"/> says no.
    /// </summary>
    /// <returns>Whether the step was moved.</returns>
    public bool MoveStep(Guid sourceId, Guid? targetParentId, int targetIndex)
    {
        if (!JudgeMove(sourceId, targetParentId, targetIndex).IsMove)
        {
            return false;
        }

        var step = Rows.First(row => row.Id == sourceId);
        var siblings = SiblingsOf(step);
        var from = siblings.IndexOf(step);
        siblings.Move(from, targetIndex > from ? targetIndex - 1 : targetIndex);
        RebuildRows();
        SelectedStep = step;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// The place a dragged step would go to when it is dropped before or after a row: next to that row, in the list it is
    /// in. After a row that holds steps means after all of them. Pure: nothing is changed, nothing is marked.
    /// </summary>
    public StepDropPlan PlanDrop(Guid sourceId, Guid overRowId, DropPlacement placement)
    {
        var over = Rows.FirstOrDefault(row => row.Id == overRowId);
        if (over is null)
        {
            return new StepDropPlan(StepDropOutcome.Rejected, null, 0, null, null, false, "That row is not in the sequence.");
        }

        var index = SiblingsOf(over).IndexOf(over) + (placement == DropPlacement.After ? 1 : 0);
        var judged = JudgeMove(sourceId, over.Parent?.Id, index);
        var before = placement == DropPlacement.Before;
        return judged with { Over = over, Marker = before ? over : LastRowOf(over), MarkerBefore = before };
    }

    /// <summary>Starts dragging a step: it is selected and marked as the one being moved. Not while the sequence runs.</summary>
    public bool BeginDrag(Guid sourceId)
    {
        EndDrag();
        if (!IsEditable || Rows.FirstOrDefault(row => row.Id == sourceId) is not { } step)
        {
            return false;
        }

        SelectedStep = step;
        DraggedStep = step;
        step.IsDragSource = true;
        return true;
    }

    /// <summary>Marks the place of a plan (the insertion line, or the refusal) and clears the marks of the plan before.</summary>
    public void ShowDrop(StepDropPlan? plan)
    {
        ClearDropMarks();
        if (plan is null)
        {
            return;
        }

        if (plan.IsMove && plan.Marker is { } marker && plan.Over is { } over)
        {
            marker.DropIndentWidth = over.IndentWidth;
            if (plan.MarkerBefore)
            {
                marker.ShowsDropBefore = true;
            }
            else
            {
                marker.ShowsDropAfter = true;
            }
        }
        else if (plan.Outcome == StepDropOutcome.Rejected && plan.Over is { } refused)
        {
            refused.IsDropRejected = true;
        }
    }

    /// <summary>Ends the drag, whether it ended in a drop or not: all marks go.</summary>
    public void EndDrag()
    {
        ClearDropMarks();
        if (DraggedStep is { } step)
        {
            step.IsDragSource = false;
            DraggedStep = null;
        }
    }

    /// <summary>Drops the dragged step before or after a row: moves it if that is allowed, and ends the drag.</summary>
    /// <returns>Whether the step was moved.</returns>
    public bool Drop(Guid sourceId, Guid overRowId, DropPlacement placement)
    {
        var plan = PlanDrop(sourceId, overRowId, placement);
        EndDrag();
        return plan.IsMove && MoveStep(sourceId, plan.ParentId, plan.Index);
    }

    private void ClearDropMarks()
    {
        foreach (var row in Rows)
        {
            row.ShowsDropBefore = false;
            row.ShowsDropAfter = false;
            row.IsDropRejected = false;
        }
    }

    private static StepDraftViewModel LastRowOf(StepDraftViewModel row) =>
        row is ContainerStepDraftViewModel { Children.Count: > 0 } container ? LastRowOf(container.Children[^1]) : row;

    // The judgement of a move: the structure first (what the step may be next to), then that it stays in its list, then
    // whether it would change anything.
    private StepDropPlan JudgeMove(Guid sourceId, Guid? parentId, int index)
    {
        StepDropPlan Refuse(string reason) => new(StepDropOutcome.Rejected, parentId, index, null, null, false, reason);

        if (!IsEditable)
        {
            return Refuse("The sequence cannot be changed while it runs.");
        }

        if (Rows.FirstOrDefault(row => row.Id == sourceId) is not { } step)
        {
            return Refuse("That step is not in the sequence.");
        }

        ContainerStepDraftViewModel? parent = null;
        if (parentId is { } id)
        {
            parent = Rows.FirstOrDefault(row => row.Id == id) as ContainerStepDraftViewModel;
            if (parent is null)
            {
                return Refuse("Steps can only be put into a Repeat, a Rig Track or a Multi-Rig block.");
            }

            if (Ancestors(parent).Contains(step))
            {
                return Refuse("A step cannot be put into itself.");
            }
        }

        if (!Accepts(parent, step.Kind))
        {
            return Refuse(WhyNotAccepted(step, parent));
        }

        if (parent != step.Parent)
        {
            return Refuse("Steps are reordered within their own list.");
        }

        var siblings = SiblingsOf(step);
        if (index < 0 || index > siblings.Count)
        {
            return Refuse("That place is outside the list.");
        }

        var from = siblings.IndexOf(step);
        return index == from || index == from + 1
            ? new StepDropPlan(StepDropOutcome.Unchanged, parentId, index, null, null, false, null)
            : new StepDropPlan(StepDropOutcome.Move, parentId, index, null, null, false, null);
    }

    private static string WhyNotAccepted(StepDraftViewModel step, ContainerStepDraftViewModel? parent)
    {
        var rigLocal = step.Kind is SequenceStepKind.RigExposure or SequenceStepKind.RigMoveFocuser
            or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus;
        var inTrack = parent is RigTrackDraftViewModel or RepeatStepDraftViewModel { IsInTrack: true };
        return step switch
        {
            RepeatStepDraftViewModel when parent is RepeatStepDraftViewModel => "A Repeat cannot be put into another Repeat.",
            MultiRigStepDraftViewModel => "A Multi-Rig block belongs at the top level of the sequence.",
            RigTrackDraftViewModel => "A Rig Track belongs in a Multi-Rig block.",
            _ when rigLocal => "A rig step only exists inside a Rig Track.",
            _ when parent is MultiRigStepDraftViewModel => "A Multi-Rig block holds Rig Tracks only.",
            _ when inTrack => $"{step.Title} is a step of the session and cannot be part of a Rig Track.",
            _ => $"{step.Title} cannot be put there.",
        };
    }

    // The selected step and then the containers around it, innermost first.
    private static IEnumerable<StepDraftViewModel> Ancestors(StepDraftViewModel? step)
    {
        for (var current = step; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }

    // A step may be put into a list only if it may be a step of that list.
    private static bool Accepts(ContainerStepDraftViewModel? parent, SequenceStepKind kind) => parent switch
    {
        null => kind is not (SequenceStepKind.RigExposure or SequenceStepKind.RigTrack
            or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus),
        RepeatStepDraftViewModel { IsInTrack: true } => kind is SequenceStepKind.RigExposure or SequenceStepKind.Delay
            or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus,
        RepeatStepDraftViewModel => kind is SequenceStepKind.Exposure or SequenceStepKind.Delay or SequenceStepKind.Slew
            or SequenceStepKind.StartGuiding or SequenceStepKind.StopGuiding or SequenceStepKind.Dither
            or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter or SequenceStepKind.Autofocus,
        RigTrackDraftViewModel => kind is SequenceStepKind.RigExposure or SequenceStepKind.Delay or SequenceStepKind.Repeat
            or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus,
        MultiRigStepDraftViewModel => kind is SequenceStepKind.RigTrack,
        _ => false,
    };

    private sealed record PasteTarget(ContainerStepDraftViewModel? Parent, int Index);

    // A copied Repeat also brings its steps: they must be steps that a Repeat at that place may hold.
    private PasteTarget? FindPasteTarget()
    {
        var kind = _clipboard.ContentKind!.Value;
        var selected = SelectedStep;
        PasteTarget target;
        if (selected is null)
        {
            target = new PasteTarget(null, Steps.Count);
        }
        else if (selected is RigTrackDraftViewModel track)
        {
            target = new PasteTarget(track, track.Children.Count);
        }
        else
        {
            target = new PasteTarget(selected.Parent, SiblingsOf(selected).IndexOf(selected) + 1);
        }

        if (!Accepts(target.Parent, kind))
        {
            return null;
        }

        if (kind == SequenceStepKind.Repeat)
        {
            var insideTrack = target.Parent is RigTrackDraftViewModel;
            if (!_clipboard.ContentChildKinds.All(child => insideTrack
                    ? child is SequenceStepKind.RigExposure or SequenceStepKind.Delay
                        or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus
                    : child is not (SequenceStepKind.RigExposure or SequenceStepKind.RigMoveFocuser
                        or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus)))
            {
                return null;
            }
        }

        return target;
    }

    // In a track the steps that use equipment are the rig's: the same menu entry makes the step of the rig, and a new
    // focuser move starts at where the focuser of that rig stands (not where some other focuser does).
    private LeafStepDraft NewTrackLeaf(RigTrackDraftViewModel? track, SequenceStepKind kind) => track is not null
        ? kind switch
        {
            SequenceStepKind.Exposure => new RigExposureStepDraft(NewId(), _defaults.ExposureSeconds),
            SequenceStepKind.MoveFocuser => new RigMoveFocuserStepDraft(NewId(), RigFocuserPosition(track)),
            SequenceStepKind.ChangeFilter => EffectiveDefaults.CreateLeaf(SequenceStepKind.RigChangeFilter),
            SequenceStepKind.Autofocus => EffectiveDefaults.CreateLeaf(SequenceStepKind.RigAutofocus),
            _ => EffectiveDefaults.CreateLeaf(kind),
        }
        : EffectiveDefaults.CreateLeaf(kind);

    private int RigFocuserPosition(RigTrackDraftViewModel track) =>
        track.Rig.SelectedId is { } rigId && _rigs is not null && _rigs.TryGet(rigId, out var rig) && rig?.FocuserId is { } focuserId
        && _registry.TryGet(focuserId, out var device) && device is IFocuser focuser
            ? focuser.Position
            : _defaults.FocuserPosition;

    // The listing is rebuilt after every change of structure. The list control clears its selection while the rows
    // are replaced; the selected step is put back afterwards.
    private void RebuildRows()
    {
        var keep = SelectedStep;
        _rebuilding = true;
        try
        {
            Rows.Clear();

            void Add(StepDraftViewModel step)
            {
                Rows.Add(step);
                if (step is ContainerStepDraftViewModel container)
                {
                    foreach (var child in container.Children)
                    {
                        Add(child);
                    }
                }
            }

            foreach (var step in Steps)
            {
                Add(step);
            }
        }
        finally
        {
            _rebuilding = false;
        }

        SelectedStep = keep is not null && Rows.Contains(keep) ? keep : null;
        NotifyCommands();
    }

    private void OnStepsChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        if (_rebuilding)
        {
            return;
        }

        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanAddChildHere));
        OnPropertyChanged(nameof(CanAddTrack));
        OnPropertyChanged(nameof(IsMultiRigContext));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanDuplicate));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        AddStepCommand.NotifyCanExecuteChanged();
        AddChildCommand.NotifyCanExecuteChanged();
        AddTrackCommand.NotifyCanExecuteChanged();
        AddTrackStepCommand.NotifyCanExecuteChanged();
        RemoveStepCommand.NotifyCanExecuteChanged();
        DuplicateStepCommand.NotifyCanExecuteChanged();
        CopyStepCommand.NotifyCanExecuteChanged();
        PasteStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    // Reads every step, everything inside it included, each field with its own problems.
    private List<SequenceStepDraft> ReadAll(Dictionary<Guid, IReadOnlyList<string>> parseErrors) =>
        Steps.Select(step => ReadStep(step, parseErrors)).ToList();

    // Reads one step with everything inside it, as a draft that shares nothing with the view models.
    private static SequenceStepDraft ReadStep(StepDraftViewModel step, Dictionary<Guid, IReadOnlyList<string>> parseErrors)
    {
        var errors = new List<string>();
        SequenceStepDraft draft;
        switch (step)
        {
            case RepeatStepDraftViewModel repeat:
                draft = new RepeatStepDraft(
                    repeat.Id,
                    repeat.ReadCount(errors),
                    repeat.Children.Select(child => (LeafStepDraft)ReadStep(child, parseErrors)).ToList());
                break;
            case RigTrackDraftViewModel track:
                draft = new RigTrackDraft(
                    track.Id, track.Rig.SelectedId, track.Children.Select(child => ReadStep(child, parseErrors)).ToList(),
                    track.ReadPolicy(errors));
                break;
            case MultiRigStepDraftViewModel multiRig:
                draft = new MultiRigStepDraft(
                    multiRig.Id,
                    multiRig.Children.Select(track => (RigTrackDraft)ReadStep(track, parseErrors)).ToList(),
                    multiRig.ReadPolicy(errors));
                break;
            default:
                draft = step.Read(errors);
                break;
        }

        if (errors.Count > 0)
        {
            parseErrors[step.Id] = errors;
        }

        return draft;
    }

    private static IReadOnlyList<SequenceStepDraft> ChildDrafts(SequenceStepDraft draft) => draft switch
    {
        RepeatStepDraft repeat => repeat.Children,
        RigTrackDraft track => track.Steps,
        MultiRigStepDraft multiRig => multiRig.Tracks,
        _ => [],
    };

    private bool IsReadable(StepDraftViewModel step) =>
        !_unreadable.Contains(step.Id)
        && (step is not ContainerStepDraftViewModel container || container.Children.All(IsReadable));

    // The ids of every step of the sequence, steps inside containers and tracks included.
    private HashSet<Guid> AllIds() => [.. Rows.Select(row => row.Id)];

    private Guid NewId()
    {
        var taken = AllIds();
        Guid id;
        do
        {
            id = Guid.NewGuid();
        }
        while (taken.Contains(id));

        return id;
    }

    // Puts a new step into a container (or the sequence itself) at an index; selects it and reports the change.
    private void InsertAt(ContainerStepDraftViewModel? parent, int index, SequenceStepDraft draft)
    {
        var step = CreateViewModel(draft);
        step.Parent = parent;
        Attach(step);
        (parent?.Children ?? Steps).Insert(index, step);
        RebuildRows();
        SelectedStep = step;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private void InsertAfter(StepDraftViewModel anchor, SequenceStepDraft draft) =>
        InsertAt(anchor.Parent, SiblingsOf(anchor).IndexOf(anchor) + 1, draft);

    private void Attach(StepDraftViewModel step)
    {
        step.Edited += OnStepEdited;
        if (step is ContainerStepDraftViewModel container)
        {
            container.Children.ToList().ForEach(Attach);
        }
    }

    private void Detach(StepDraftViewModel step)
    {
        step.Edited -= OnStepEdited;
        if (step is ContainerStepDraftViewModel container)
        {
            container.Children.ToList().ForEach(Detach);
        }
    }

    private void OnStepEdited(object? sender, EventArgs e)
    {
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private StepDraftViewModel CreateViewModel(SequenceStepDraft draft) => draft switch
    {
        MultiRigStepDraft m => new MultiRigStepDraftViewModel(
            m, m.Tracks.Select(CreateViewModel), new RigPickerViewModel(_rigs, _registry, m.DitherPolicy?.TriggerRigId)),
        RigTrackDraft t => new RigTrackDraftViewModel(
            t, t.Steps.Select(CreateViewModel), new RigPickerViewModel(_rigs, _registry, t.RigId)),
        RepeatStepDraft r => new RepeatStepDraftViewModel(r, r.Children.Select(CreateLeafViewModel)),
        LeafStepDraft leaf => CreateLeafViewModel(leaf),
        _ => throw new ArgumentException($"Unsupported step '{draft.GetType().Name}'.", nameof(draft)),
    };

    private StepDraftViewModel CreateLeafViewModel(LeafStepDraft draft) => draft switch
    {
        ExposureStepDraft e => new ExposureStepDraftViewModel(_registry, e),
        RigExposureStepDraft e => RigExposureViewModel(e),
        DelayStepDraft d => new DelayStepDraftViewModel(d),
        SlewStepDraft s => new SlewStepDraftViewModel(_registry, s),
        StartGuidingStepDraft g => new StartGuidingStepDraftViewModel(_registry, g),
        StopGuidingStepDraft g => new StopGuidingStepDraftViewModel(_registry, g),
        DitherStepDraft d => new DitherStepDraftViewModel(_registry, d),
        MoveFocuserStepDraft f => new MoveFocuserStepDraftViewModel(_registry, f),
        ChangeFilterStepDraft c => new ChangeFilterStepDraftViewModel(_registry, c),
        RigMoveFocuserStepDraft f => new RigMoveFocuserStepDraftViewModel(f),
        RigChangeFilterStepDraft c => RigFilterViewModel(c),
        PlateSolveStepDraft p => new PlateSolveStepDraftViewModel(p, new RigPickerViewModel(_rigs, _registry, p.RigId)),
        AutofocusStepDraft a => new AutofocusStepDraftViewModel(a, new RigPickerViewModel(_rigs, _registry, a.RigId)),
        RigAutofocusStepDraft a => new RigAutofocusStepDraftViewModel(a),
        _ => throw new ArgumentException($"Unsupported step '{draft.GetType().Name}'.", nameof(draft)),
    };

    // The camera is the camera of the rig of the track the step is in, looked up whenever it is needed (the track can get another rig).
    private RigExposureStepDraftViewModel RigExposureViewModel(RigExposureStepDraft draft)
    {
        RigExposureStepDraftViewModel? step = null;
        step = new RigExposureStepDraftViewModel(draft, () => CameraOfRigTrack(step));
        return step;
    }

    private AcquisitionCameraContext CameraOfRigTrack(StepDraftViewModel? step)
    {
        var track = Ancestors(step).OfType<RigTrackDraftViewModel>().FirstOrDefault();
        return track?.Rig.SelectedId is { } rigId && _rigs is not null && _rigs.TryGet(rigId, out var rig) && rig is not null
            ? ExposureStepDraftViewModel.CameraContext(_registry, rig.CameraId)
            : AcquisitionCameraContext.None;
    }

    // The slots come from the filter wheel of the rig of the track the step is in, looked up whenever they are needed:
    // the step is created before it is put into its track, and the track can get another rig.
    private RigChangeFilterStepDraftViewModel RigFilterViewModel(RigChangeFilterStepDraft draft)
    {
        RigChangeFilterStepDraftViewModel? step = null;
        step = new RigChangeFilterStepDraftViewModel(draft, () => SlotsOfRigWheel(step));
        return step;
    }

    private IReadOnlyList<FilterSlot>? SlotsOfRigWheel(StepDraftViewModel? step)
    {
        var track = Ancestors(step).OfType<RigTrackDraftViewModel>().FirstOrDefault();
        return track?.Rig.SelectedId is { } rigId
               && _rigs is not null && _rigs.TryGet(rigId, out var rig) && rig?.FilterWheelId is { } wheelId
               && _registry.TryGet(wheelId, out var device) && device is IFilterWheel wheel
            ? wheel.Slots
            : null;
    }
}

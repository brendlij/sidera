using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Astra.Runtime.Devices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The sequence the user is editing: an ordered list of steps, of which a Repeat holds an ordered list of steps of
/// its own, one level deep and no deeper. Steps can be added, removed and moved among their siblings, and the one
/// step whose parameters are shown is the selected one, a step of the sequence or one inside a Repeat.
/// It is only a draft. Nothing in it is executed: every run builds a fresh runtime sequence from a snapshot of it
/// (<see cref="Build"/>), and while a sequence runs <see cref="IsEditable"/> is false and every command that
/// changes the list is unavailable.
/// <para>
/// After every change all steps are read again and validated: each step shows its own problems, a Repeat also says
/// that a step inside has one, and <see cref="ValidationErrors"/> lists all of them. <see cref="IsValid"/> is only
/// what the editor says; <see cref="Build"/> validates again on its own.
/// </para>
/// </summary>
public sealed partial class SequenceDraftViewModel : ViewModelBase
{
    private readonly DeviceRegistry _registry;
    private readonly SequenceDraftDefaults _defaults;
    private readonly ISequenceStepClipboard _clipboard;
    private HashSet<Guid> _unreadable = [];
    private bool _rebuilding;

    public SequenceDraftViewModel(
        DeviceRegistry registry,
        SequenceDraftDefaults defaults,
        IEnumerable<SequenceStepDraft>? initialSteps = null,
        ISequenceStepClipboard? clipboard = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(defaults);
        _registry = registry;
        _defaults = defaults;
        _clipboard = clipboard ?? new SequenceStepClipboard();
        _clipboard.Changed += (_, _) => NotifyCommands();

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

    /// <summary>The steps of the sequence itself, in order; a Repeat holds the steps inside it.</summary>
    public ObservableCollection<StepDraftViewModel> Steps { get; }

    /// <summary>The copied step, kept for the session: it outlives New and Open, so steps can be copied between sequences.</summary>
    public ISequenceStepClipboard Clipboard => _clipboard;

    /// <summary>All steps as they are listed: each step of the sequence, followed by the steps inside it if it is a Repeat.</summary>
    public ObservableCollection<StepDraftViewModel> Rows { get; }

    /// <summary>The step whose parameters are shown: a step of the sequence, or one inside a Repeat.</summary>
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
    /// Raised when the user (or code acting for them) changed the draft: a step added, removed or moved, a parameter
    /// or a device edited. Not raised when the draft is only read again (equipment came or went), nor by
    /// <see cref="ReplaceSteps"/>, nor by anything about running the sequence.
    /// </summary>
    public event EventHandler? Modified;

    /// <summary>Some field holds text that is not a number. The draft can still be shown, but not saved faithfully.</summary>
    public bool HasUnreadableFields { get; private set; }

    /// <summary>
    /// Replaces the whole sequence, for example with a document that was opened. All new step view models are made
    /// before anything of the current sequence is touched.
    /// </summary>
    public void ReplaceSteps(IEnumerable<SequenceStepDraft> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var created = steps.Select(CreateViewModel).ToList();

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

    /// <summary>Builds a new sequence from the current draft, validating it again.</summary>
    /// <exception cref="SequenceConfigurationException">The draft is not valid.</exception>
    public BuiltSequence Build()
    {
        Revalidate();
        if (!IsValid)
        {
            throw new SequenceConfigurationException(ValidationErrors);
        }

        return SequenceDraftBuilder.Build(_registry, Snapshot());
    }

    /// <summary>Reads all fields again and validates. Also catches a device that disappeared since the last time.</summary>
    public void Revalidate()
    {
        var parseErrors = new Dictionary<Guid, IReadOnlyList<string>>();
        var drafts = ReadAll(parseErrors);
        HasUnreadableFields = parseErrors.Count > 0;
        _unreadable = [.. parseErrors.Keys];
        var validation = SequenceDraftBuilder.Validate(_registry, drafts);

        var sentences = new List<string>(validation.SequenceProblems);

        List<string> Problems(StepDraftViewModel step) =>
            parseErrors.GetValueOrDefault(step.Id, []).Concat(validation.ProblemsOf(step.Id)).ToList();

        for (var i = 0; i < Steps.Count; i++)
        {
            var step = Steps[i];
            var problems = Problems(step);
            step.Number = i + 1;
            step.NumberLabel = SequenceDraftBuilder.Label(i);
            sentences.AddRange(problems.Select(p => $"Step {step.NumberLabel} ({TitleOf(step)}): {p}"));

            if (step is RepeatStepDraftViewModel repeat)
            {
                var repeatDraft = (RepeatStepDraft)drafts[i];
                var childProblems = false;
                for (var j = 0; j < repeat.Children.Count; j++)
                {
                    var child = repeat.Children[j];
                    var own = Problems(child);
                    child.Number = j + 1;
                    child.NumberLabel = SequenceDraftBuilder.Label(i, j);
                    child.Show(SequenceDraftBuilder.Describe(_registry, repeatDraft.Children[j]), own);
                    sentences.AddRange(own.Select(p => $"Step {child.NumberLabel} ({TitleOf(child)}): {p}"));
                    childProblems |= own.Count > 0;
                }

                // The Repeat shows that something inside it needs attention; the sentences name the step itself.
                IReadOnlyList<string> shown = childProblems ? [.. problems, "A step inside has a problem."] : problems;
                repeat.Show(SequenceDraftBuilder.Describe(_registry, repeatDraft), shown);
            }
            else
            {
                step.Show(SequenceDraftBuilder.Describe(_registry, drafts[i]), problems);
            }
        }

        if (!sentences.SequenceEqual(ValidationErrors))
        {
            ValidationErrors = sentences;
        }

        NotifyCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the device registry again (equipment may have come or gone), then validates.</summary>
    public void RefreshDevices()
    {
        foreach (var picker in Rows.SelectMany(step => step.Pickers))
        {
            picker.Refresh();
        }

        Revalidate();
    }

    /// <summary>Adds a step to the end of the sequence itself, also when a step inside a Repeat is selected.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddStep(SequenceStepKind kind)
    {
        var step = CreateViewModel(_defaults.Create(kind));
        Attach(step);
        Steps.Add(step);
        RebuildRows();
        SelectedStep = step;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a leaf step to the end of the selected Repeat, or of the Repeat the selected step is in.</summary>
    [RelayCommand(CanExecute = nameof(CanAddChild))]
    private void AddChild(SequenceStepKind kind)
    {
        var repeat = ChildTarget!;
        var child = CreateLeafViewModel(_defaults.CreateLeaf(kind));
        child.Parent = repeat;
        Attach(child);
        repeat.Children.Add(child);
        RebuildRows();
        SelectedStep = child;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts a copy of the selected step, with new ids, right after it, in the same list: a step of the sequence after
    /// that step, a step inside a Repeat after that step inside the Repeat, a Repeat with all its steps after the
    /// Repeat. The copy is selected. It is not checked for sense: a second Start Guiding is added, and the validation
    /// says what is wrong with it.
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

    /// <summary>Keeps a snapshot of the selected step, with its steps if it is a Repeat, on the clipboard. The sequence is not changed.</summary>
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
    /// the sequence; with a step of the sequence or a Repeat selected after it in the sequence; with a step inside a
    /// Repeat selected, a copied step goes after it inside the Repeat. A copied Repeat cannot go inside a Repeat, and
    /// then pasting is not available (nothing is put on another level instead).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPaste))]
    private void PasteStep()
    {
        if (!CanPaste)
        {
            return;
        }

        var clone = _clipboard.CreateClone(AllIds());
        var selected = SelectedStep;
        InsertAfter(selected is { IsChild: true } && clone is LeafStepDraft ? selected : selected?.Parent ?? selected, clone);
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

        // The next step in the same list, else the one before; after the last step of a Repeat, the Repeat.
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
    public RepeatStepDraftViewModel? ChildTarget => SelectedStep as RepeatStepDraftViewModel ?? SelectedStep?.Parent;

    /// <summary>Steps can be added inside a Repeat right now.</summary>
    public bool CanAddChildHere => IsEditable && ChildTarget is not null;

    public bool CanAddChild(SequenceStepKind kind) => CanAddChildHere && kind != SequenceStepKind.Repeat;

    public bool CanRemove => IsEditable && SelectedStep is not null;

    // A step whose fields do not all read as numbers cannot be copied faithfully; it has to be fixed first.
    public bool CanDuplicate => IsEditable && SelectedStep is { } step && IsReadable(step);
    public bool CanCopy => CanDuplicate;

    public bool CanPaste => IsEditable
        && _clipboard.HasContent
        && !(SelectedStep is { IsChild: true } && _clipboard.ContentKind == SequenceStepKind.Repeat);
    public bool CanMoveUp => IsEditable && SelectedStep is not null && SiblingsOf(SelectedStep).IndexOf(SelectedStep) > 0;

    public bool CanMoveDown => IsEditable && SelectedStep is not null
        && SiblingsOf(SelectedStep) is var siblings && siblings.IndexOf(SelectedStep) is var i && i >= 0 && i < siblings.Count - 1;

    partial void OnIsEditableChanged(bool value) => NotifyCommands();

    partial void OnSelectedStepChanged(StepDraftViewModel? value) => NotifyCommands();

    // Steps move among their siblings only: never out of a Repeat, and never into one.
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

    // The listing is rebuilt after every change of structure. The list control clears its selection while the rows
    // are replaced; the selected step is put back afterwards.
    private void RebuildRows()
    {
        var keep = SelectedStep;
        _rebuilding = true;
        try
        {
            Rows.Clear();
            foreach (var step in Steps)
            {
                Rows.Add(step);
                if (step is RepeatStepDraftViewModel repeat)
                {
                    foreach (var child in repeat.Children)
                    {
                        Rows.Add(child);
                    }
                }
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
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanDuplicate));
        OnPropertyChanged(nameof(CanCopy));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
        AddStepCommand.NotifyCanExecuteChanged();
        AddChildCommand.NotifyCanExecuteChanged();
        RemoveStepCommand.NotifyCanExecuteChanged();
        DuplicateStepCommand.NotifyCanExecuteChanged();
        CopyStepCommand.NotifyCanExecuteChanged();
        PasteStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
    }

    // Reads every step, the steps inside a Repeat with their own problems.
    private List<SequenceStepDraft> ReadAll(Dictionary<Guid, IReadOnlyList<string>> parseErrors) =>
        Steps.Select(step => ReadStep(step, parseErrors)).ToList();

    // Reads one step with the steps inside it, as a draft that shares nothing with the view models.
    private static SequenceStepDraft ReadStep(StepDraftViewModel step, Dictionary<Guid, IReadOnlyList<string>> parseErrors)
    {
        var errors = new List<string>();
        SequenceStepDraft draft;
        if (step is RepeatStepDraftViewModel repeat)
        {
            var children = new List<LeafStepDraft>(repeat.Children.Count);
            foreach (var child in repeat.Children)
            {
                var childErrors = new List<string>();
                children.Add((LeafStepDraft)child.Read(childErrors));
                if (childErrors.Count > 0)
                {
                    parseErrors[child.Id] = childErrors;
                }
            }

            draft = new RepeatStepDraft(repeat.Id, repeat.ReadCount(errors), children);
        }
        else
        {
            draft = step.Read(errors);
        }

        if (errors.Count > 0)
        {
            parseErrors[step.Id] = errors;
        }

        return draft;
    }

    private bool IsReadable(StepDraftViewModel step) =>
        !_unreadable.Contains(step.Id)
        && (step is not RepeatStepDraftViewModel repeat || repeat.Children.All(child => !_unreadable.Contains(child.Id)));

    // The ids of every step of the sequence, steps inside Repeats included.
    private HashSet<Guid> AllIds() => [.. Rows.Select(row => row.Id)];

    // Puts a new step right after its anchor in the anchor's own list (a step inside a Repeat stays inside it), or at the
    // end of the sequence without an anchor; selects it and reports the change.
    private void InsertAfter(StepDraftViewModel? anchor, SequenceStepDraft draft)
    {
        var siblings = anchor is null ? Steps : SiblingsOf(anchor);
        var index = anchor is null ? Steps.Count : siblings.IndexOf(anchor) + 1;

        StepDraftViewModel step;
        if (anchor?.Parent is { } repeat)
        {
            step = CreateLeafViewModel((LeafStepDraft)draft);
            step.Parent = repeat;
        }
        else
        {
            step = CreateViewModel(draft);
        }

        Attach(step);
        siblings.Insert(index, step);
        RebuildRows();
        SelectedStep = step;
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private void Attach(StepDraftViewModel step)
    {
        step.Edited += OnStepEdited;
        if (step is RepeatStepDraftViewModel repeat)
        {
            repeat.Children.ToList().ForEach(Attach);
        }
    }

    private void Detach(StepDraftViewModel step)
    {
        step.Edited -= OnStepEdited;
        if (step is RepeatStepDraftViewModel repeat)
        {
            repeat.Children.ToList().ForEach(Detach);
        }
    }

    private void OnStepEdited(object? sender, EventArgs e)
    {
        Revalidate();
        Modified?.Invoke(this, EventArgs.Empty);
    }

    private StepDraftViewModel CreateViewModel(SequenceStepDraft draft) => draft switch
    {
        RepeatStepDraft r => new RepeatStepDraftViewModel(r, r.Children.Select(CreateLeafViewModel)),
        LeafStepDraft leaf => CreateLeafViewModel(leaf),
        _ => throw new ArgumentException($"Unsupported step '{draft.GetType().Name}'.", nameof(draft)),
    };

    private StepDraftViewModel CreateLeafViewModel(LeafStepDraft draft) => draft switch
    {
        ExposureStepDraft e => new ExposureStepDraftViewModel(_registry, e),
        DelayStepDraft d => new DelayStepDraftViewModel(d),
        SlewStepDraft s => new SlewStepDraftViewModel(_registry, s),
        StartGuidingStepDraft g => new StartGuidingStepDraftViewModel(_registry, g),
        StopGuidingStepDraft g => new StopGuidingStepDraftViewModel(_registry, g),
        DitherStepDraft d => new DitherStepDraftViewModel(_registry, d),
        _ => throw new ArgumentException($"Unsupported step '{draft.GetType().Name}'.", nameof(draft)),
    };

    private static string TitleOf(StepDraftViewModel step) => SequenceDraftBuilder.TitleOf(step.Kind);
}

using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sidera.Desktop.Sessions;

namespace Sidera.Desktop.ViewModels;

/// <summary>One action of a list, as a line: its title, what it is set to, whether it is on, where it is in the run, what is wrong with it. Clicking it opens it in the drawer.</summary>
public sealed partial class ActionRowViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;
    private readonly bool _initialized;

    internal ActionRowViewModel(SessionEditorViewModel owner, SessionAction action, int index, int count, string summary, bool startsRepeat)
    {
        _owner = owner;
        Id = action.Id;
        Kind = action.Kind;
        Title = ActionSummary.Title(action);
        Summary = summary;
        Category = ActionCatalog.Of(action.Kind).Category;
        IsOn = action.Enabled;
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        StartsRepeat = startsRepeat;
        _initialized = true;
    }

    public Guid Id { get; }

    public SessionActionKind Kind { get; }

    public string Title { get; }

    /// <summary>What it is set to, in a few characters: "300 s", "Ha", "every 60 min".</summary>
    public string Summary { get; }

    public ActionCategory Category { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    /// <summary>This is the first exposure of a block: from here on the actions repeat; the ones above run once when the block starts.</summary>
    public bool StartsRepeat { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>What is wrong with the action, said where it is; empty when nothing is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; internal set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>What the run says about it while it waits ("Waiting for astronomical darkness"); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; internal set; } = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The numbers behind the status ("Sun altitude -4.2° now, -18° needed").</summary>
    [ObservableProperty]
    public partial string StatusDetail { get; internal set; } = string.Empty;

    partial void OnIsOnChanged(bool value)
    {
        if (_initialized)
        {
            _owner.SetActionEnabled(Id, value);
        }
    }

    [RelayCommand]
    private void Select() => _owner.SelectAction(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveAction(Id);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.MoveAction(Id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.MoveAction(Id, 1);

    private bool IsEditable => _owner.IsEditable;
}

/// <summary>A list of actions with its heading and the button that adds one (it opens the library): the start or the end of the session, or the preparation of a target.</summary>
public sealed partial class ActionListViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;

    internal ActionListViewModel(SessionEditorViewModel owner, ActionOwner place, string title, string hint)
    {
        _owner = owner;
        Place = place;
        Title = title;
        Hint = hint;
    }

    public ActionOwner Place { get; internal set; }

    public string Title { get; }

    /// <summary>What the list is for, shown while it is empty.</summary>
    public string Hint { get; }

    public ObservableCollection<ActionRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    internal void Changed() => OnPropertyChanged(nameof(HasRows));

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Add() => _owner.Library.Open(Place);

    private bool IsEditable => _owner.IsEditable;
}

/// <summary>A block as a card: its name, one line about how it repeats and what it does by itself, where it is in the run. Clicking it opens it in the drawer.</summary>
public sealed partial class BlockCardViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;
    private readonly bool _initialized;

    internal BlockCardViewModel(SessionEditorViewModel owner, SequenceBlock block, int number, int count, string title, string line)
    {
        _owner = owner;
        Id = block.Id;
        Number = number;
        Title = title;
        Line = line;
        IsOn = block.Enabled;
        CanMoveUp = number > 1;
        CanMoveDown = number < count;
        _initialized = true;
    }

    public Guid Id { get; }

    public int Number { get; }

    public string Title { get; }

    /// <summary>"300 s × 40 · Dither 3 · AF 60 m · stops: dawn, alt&lt;25°".</summary>
    public string Line { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; internal set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>Where the block is in the run: "Frame 6 / 40", "40 / 40 frames", "Queued"; empty when nothing runs.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial string ProgressText { get; internal set; } = string.Empty;

    public bool HasProgress => ProgressText.Length > 0;

    [ObservableProperty]
    public partial double ProgressFraction { get; internal set; }

    /// <summary>What the setup is at while this block is at work: "Exposure 300 s", "Autofocus · Sample 4 / 7".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    public partial string ActivityText { get; internal set; } = string.Empty;

    public bool HasActivity => ActivityText.Length > 0;

    /// <summary>Why the block waits ("Waiting for the target above 30°"); empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; internal set; } = string.Empty;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The numbers behind the status ("Sun altitude -4.2° now, -18° needed"; "Stop: astronomical dawn").</summary>
    [ObservableProperty]
    public partial string StatusDetail { get; internal set; } = string.Empty;

    partial void OnIsOnChanged(bool value)
    {
        if (_initialized)
        {
            _owner.SetBlockEnabled(Id, value);
        }
    }

    [RelayCommand]
    private void Select() => _owner.SelectBlock(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Duplicate() => _owner.DuplicateBlock(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveBlock(Id);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.MoveBlock(Id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.MoveBlock(Id, 1);

    private bool IsEditable => _owner.IsEditable;
}

/// <summary>The sequence of one imaging setup under a target: its blocks, one after another. With several setups it is a tab; with one it is just "Imaging".</summary>
public sealed partial class LaneViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;
    private readonly Guid _target;

    internal LaneViewModel(SessionEditorViewModel owner, Guid target, SetupLane lane, string setupName, bool canRemove)
    {
        _owner = owner;
        _target = target;
        Id = lane.Id;
        SetupName = setupName;
        CanRemove = canRemove;
    }

    public Guid Id { get; }

    /// <summary>The name of the imaging setup, as the tab shows it.</summary>
    public string SetupName { get; }

    public bool CanRemove { get; }

    public ObservableCollection<BlockCardViewModel> Blocks { get; } = [];

    public bool HasBlocks => Blocks.Count > 0;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; internal set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>With several setups a sequence must say which one it is for; until it does this shows the choices.</summary>
    [ObservableProperty]
    public partial bool NeedsSetupChoice { get; internal set; }

    public ObservableCollection<SetupChoice> SetupChoices { get; } = [];

    [ObservableProperty]
    public partial SetupChoice? SelectedSetup { get; set; }

    partial void OnSelectedSetupChanged(SetupChoice? value)
    {
        if (value is { Id: { } setup } && NeedsSetupChoice)
        {
            _owner.SetLaneSetup(Id, setup);
        }
    }

    internal void Changed() => OnPropertyChanged(nameof(HasBlocks));

    [RelayCommand]
    private void Select() => _owner.SelectLane(_target, Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddBlock() => _owner.AddBlock(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveLane(Id);

    private bool IsEditable => _owner.IsEditable;
}

/// <summary>A target as a card: where it points, what is prepared for it once, the sequences of its imaging setups, what ends it, and where it is in the run.</summary>
public sealed partial class TargetCardViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;
    private readonly bool _initialized;

    internal TargetCardViewModel(SessionEditorViewModel owner, SessionTarget target, int number, int count, ActionListViewModel preparation)
    {
        _owner = owner;
        Id = target.Id;
        Number = number;
        Name = target.Name;
        Coordinates = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"RA {SkyFormat.Ra(target.RightAscensionHours)} · Dec {SkyFormat.Dec(target.DeclinationDegrees)}{(target.RotationDegrees is { } r ? $" · Rotation {r:0.#}°" : string.Empty)}");
        IsOn = target.Enabled;
        CanMoveUp = number > 1;
        CanMoveDown = number < count;
        Preparation = preparation;
        _initialized = true;
    }

    public Guid Id { get; }

    public int Number { get; }

    public string Name { get; }

    /// <summary>"RA 05h 35m 17s · Dec −05° 23′ 28″ · Rotation 90°".</summary>
    public string Coordinates { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    /// <summary>What is prepared once for every sequence of the target: slew and center, start guiding.</summary>
    public ActionListViewModel Preparation { get; }

    public ObservableCollection<LaneViewModel> Lanes { get; } = [];

    [ObservableProperty]
    public partial LaneViewModel? SelectedLane { get; internal set; }

    /// <summary>Tabs for the setups are shown: there are several setups to image with.</summary>
    [ObservableProperty]
    public partial bool ShowLaneTabs { get; internal set; }

    [ObservableProperty]
    public partial bool IsOn { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>"Parallel imaging · Main 750mm + Wide 400mm"; empty with one sequence.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParallel))]
    public partial string ParallelText { get; internal set; } = string.Empty;

    public bool HasParallel => ParallelText.Length > 0;

    /// <summary>"Shared: AM3 · PHD2"; empty when the setups share nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShared))]
    public partial string SharedText { get; internal set; } = string.Empty;

    public bool HasShared => SharedText.Length > 0;

    /// <summary>"Limits · dawn, alt&lt;25°" or "No limits besides the blocks".</summary>
    [ObservableProperty]
    public partial string LimitsText { get; internal set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; internal set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>Where the target is in the run: Pending, Waiting, Running, Completed, Skipped, Failed.</summary>
    [ObservableProperty]
    public partial string StatusText { get; internal set; } = string.Empty;

    [ObservableProperty]
    public partial StatusKind StatusKind { get; internal set; }

    public bool HasStatus => StatusText.Length > 0;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    partial void OnIsOnChanged(bool value)
    {
        if (_initialized)
        {
            _owner.SetTargetEnabled(Id, value);
        }
    }

    [RelayCommand]
    private void Select() => _owner.SelectTarget(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Remove() => _owner.RemoveTarget(Id);

    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void Duplicate() => _owner.DuplicateTarget(Id);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => _owner.MoveTarget(Id, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => _owner.MoveTarget(Id, 1);

    /// <summary>Adds a sequence for another setup under this target (tabs, with several setups).</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddLane() => _owner.AddLane(Id);

    /// <summary>Adds an imaging block to the sequence that is shown.</summary>
    [RelayCommand(CanExecute = nameof(IsEditable))]
    private void AddBlock()
    {
        if (SelectedLane is { } lane)
        {
            _owner.AddBlock(lane.Id);
        }
        else
        {
            _owner.AddBlockToTarget(Id);
        }
    }

    private bool IsEditable => _owner.IsEditable;
}

/// <summary>What the session does by itself, as a card: today the meridian flip, in a line. Clicking it opens the flip in the drawer.</summary>
public sealed partial class AutomationCardViewModel : ObservableObject
{
    private readonly SessionEditorViewModel _owner;

    internal AutomationCardViewModel(SessionEditorViewModel owner, string flipLine, bool flipOn)
    {
        _owner = owner;
        FlipLine = flipLine;
        FlipOn = flipOn;
    }

    /// <summary>"Enabled · Hold −5m · Flip +2m · Recenter · Autofocus · Guiding", or "Off".</summary>
    public string FlipLine { get; }

    public bool FlipOn { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Edit() => _owner.SelectFlip();
}

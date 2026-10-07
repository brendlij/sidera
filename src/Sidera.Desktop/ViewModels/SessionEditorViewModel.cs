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
using Sidera.Desktop.Sessions;
using Sidera.Desktop.Workflows;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.ViewModels;

/// <summary>The session of the page: what it shows, what is saved with the steps, and what an opened file with a session shows.</summary>
public interface ISessionSource
{
    /// <summary>The session; <c>null</c> when the sequence is a tree of explicit steps.</summary>
    SessionDefinition? Session { get; }

    /// <summary>A field that is open for editing does not read as a number; the session cannot be saved faithfully.</summary>
    bool HasUnreadableFields { get; }

    /// <summary>Shows a session that was opened, or <c>null</c> for a sequence without one (a tree of steps). Not a modification.</summary>
    void Load(SessionDefinition? session);

    /// <summary>Shows the workflow of a file from before sessions, as the session it migrates to. Not a modification.</summary>
    void LoadLegacy(WorkflowDefinition workflow);

    /// <summary>Starts a new, empty session: what a new session is.</summary>
    void StartNew();
}

/// <summary>A target that framing hands to the session: where to point, with which setup, and the rotation it wants.</summary>
public sealed record SessionTargetRequest(string Name, double RightAscensionHours, double DeclinationDegrees, double? DesiredRotationDegrees, RigId? Setup);

/// <summary>What is open in the drawer next to the sequence.</summary>
public enum SessionSelectionKind
{
    None,
    Flip,
    Target,
    Block,
    Action,
}

/// <summary>
/// The editor of the session: a sequence that reads from the top to the bottom, with what the session does by itself, its start, its targets (each with its preparation and the sequences of its imaging
/// setups) and its end. It owns a <see cref="SessionDefinition"/>, a plain model, and every change is a function from a session to a session (<see cref="SessionEdits"/>); the cards, the drawer and the
/// compiled steps of the sequence are made from it each time, so there are not two models. Compiling is <see cref="SessionCompiler"/>: the runner, the resource coordination, the document and the tree
/// of steps all see the one sequence they have always seen. Opening the tree of steps drops the session and keeps the steps.
/// </summary>
public sealed partial class SessionEditorViewModel : ViewModelBase, ISessionSource, IDisposable
{
    private readonly SequenceDraftViewModel _draft;
    private readonly ISetupSource? _rigs;
    private readonly DeviceRegistry _registry;
    private readonly SequenceDraftDefaults _defaults;
    private readonly ExecutionOverviewViewModel? _execution;
    private readonly Sidera.Runtime.Events.EventBus? _events;
    private readonly Action<Action> _post;
    private readonly Func<Sidera.Core.Location.ObservingSite?>? _site;
    private readonly Sidera.Desktop.Settings.SiteService? _settings;
    private readonly Action? _openEquipment;
    private IDisposable? _flipSubscription;
    private (SessionDefinition Session, string Fingerprint)? _converted;
    private bool _ownModification;
    private bool _loading;

    public SessionEditorViewModel(
        SequenceDraftViewModel draft, ISetupSource? rigs, DeviceRegistry registry, SequenceDraftDefaults defaults, ExecutionOverviewViewModel? execution = null,
        Sidera.Runtime.Events.EventBus? events = null, Action<Action>? postToUi = null, Func<Sidera.Core.Location.ObservingSite?>? site = null,
        Sidera.Desktop.Settings.SiteService? settings = null, Action? openEquipment = null)
    {
        _settings = settings;
        _events = events;
        _post = postToUi ?? (action => action());
        _site = site;
        _openEquipment = openEquipment;
        _draft = draft;
        _rigs = rigs;
        _registry = registry;
        _defaults = defaults;
        _execution = execution;
        Library = new ActionLibraryViewModel(this);
        StartList = new ActionListViewModel(this, ActionOwner.Start, "START", "Before the first target: wait for darkness, unpark, cool the camera.");
        EndList = new ActionListViewModel(this, ActionOwner.End, "END", "After the last target: stop guiding, warm the camera, park.");
        if (_settings is not null)
        {
            _settings.Changed += OnSettingsChanged;
        }

        draft.FlipGroupsChanged += OnFlipGroupsChanged;
        _flipSubscription = events?.Subscribe<Sidera.Core.Mounts.MeridianFlipStateChanged>((_, _) =>
        {
            _post(RefreshFlipStatus);
            return System.Threading.Tasks.Task.CompletedTask;
        });
        _draft.TargetSink = AddTarget;
        _draft.PropertyChanged += OnDraftPropertyChanged;
        _draft.Modified += OnDraftModified;
        _draft.Changed += OnDraftChanged;
        if (_execution is not null)
        {
            _execution.PropertyChanged += OnExecutionChanged;
            _execution.Refreshed += OnExecutionRefreshed;
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
            _execution.Refreshed -= OnExecutionRefreshed;
        }

        foreach (var lane in _wiredLanes)
        {
            lane.PropertyChanged -= OnLaneChanged;
        }
    }

    // ---- the session

    /// <summary>The session being edited; <c>null</c> when the sequence is a tree of explicit steps.</summary>
    [ObservableProperty]
    public partial SessionDefinition? Session { get; private set; }

    /// <summary>The session is edited as blocks (as opposed to the tree of steps).</summary>
    public bool IsStructured => Session is not null;

    public bool IsTree => Session is null;

    /// <summary>The session has nothing in it yet: the page then shows how to begin.</summary>
    public bool IsEmpty => Session is { IsEmpty: true };

    public bool IsEditable => _draft.IsEditable;

    public bool IsRunning => !_draft.IsEditable;

    /// <summary>The application's meridian flip settings: what a session that follows the defaults uses.</summary>
    internal MeridianFlipSettings ApplicationFlip => _settings?.MeridianFlip ?? new MeridianFlipSettings();

    internal Sidera.Desktop.Settings.AutofocusDefaults AutofocusDefaults => _settings?.Autofocus ?? new Sidera.Desktop.Settings.AutofocusDefaults();

    internal Sidera.Desktop.Settings.GuidingDefaults GuidingDefaults => _settings?.Guiding ?? new Sidera.Desktop.Settings.GuidingDefaults();

    internal SequenceDraftDefaults Defaults => _defaults;

    /// <summary>Starts a new session in the mode the settings choose: an empty session, or an empty tree of steps.</summary>
    public void StartNew()
    {
        _converted = null;
        if (_settings?.Sequencer.DefaultSessionMode == Sidera.Desktop.Settings.SessionMode.Advanced)
        {
            Load(null);
            return;
        }

        Load(SessionDefinition.Empty);
    }

    public void Load(SessionDefinition? session) => Load(session, modified: false);

    public void LoadLegacy(WorkflowDefinition workflow) =>
        Load(WorkflowMigration.ToSession(workflow, _rigs, UsableIds), modified: false);

    private void Load(SessionDefinition? session, bool modified)
    {
        _loading = true;
        try
        {
            Session = session;
            _selection = new SessionSelection(SessionSelectionKind.None, Guid.Empty);
            Drawer = null;
            _problemsOf.Clear();
            if (session is null)
            {
                Targets.Clear();
                StartList.Rows.Clear();
                EndList.Rows.Clear();
                Automation = null;
                _draft.ExternalProblems = [];
                Problems = [];
                BannerProblems = [];
                _draft.Revalidate();
                RaiseModeChanged();
                return;
            }
        }
        finally
        {
            _loading = false;
        }

        Refresh(modified);
        RaiseModeChanged();
    }

    /// <summary>A new, empty session; an empty one is where the page starts.</summary>
    internal void Replace(SessionDefinition session, bool modified)
    {
        Load(session, modified);
    }

    // ---- changing it

    /// <summary>Changes the session by a function; the cards, the compiled steps and the problems follow. Nothing happens while a sequence runs.</summary>
    internal void Edit(Func<SessionDefinition, SessionDefinition> change, bool rebuildDrawer = true)
    {
        if (Session is not { } session || !IsEditable)
        {
            return;
        }

        Session = change(session);
        Refresh(modified: true, rebuildDrawer);
    }

    // Compiles the session into the steps of the sequence, reads the problems back, makes the cards, and (unless a field of the drawer is being typed in) the drawer.
    private void Refresh(bool modified, bool rebuildDrawer = true)
    {
        if (_loading || Session is not { } session)
        {
            return;
        }

        _lastUsable = UsableRigs.Select(r => r.Id).ToHashSet(); // the setups the cards are made with: a change of them is what RefreshAvailability looks for
        var compilation = SessionCompiler.Compile(session, _rigs, ApplicationFlip, UsableIds);
        _origins = compilation.Origins;
        _notes = compilation.Notes;
        _draft.ExternalProblems = compilation.Problems.Select(p => p.ElementId is { } id ? $"{LabelOf(id)}: {p.Message}" : p.Message).ToList();
        _draft.ReplaceSteps(compilation.Steps);

        // The problems of the sequence that the compiled steps have, said at the element they came from.
        _problemsOf.Clear();
        var banner = new List<string>();
        var shown = new List<string>();
        foreach (var problem in compilation.Problems)
        {
            if (problem.ElementId is { } id && Exists(id))
            {
                Put(id, problem.Message);
            }
            else
            {
                banner.Add(problem.Message);
            }

            shown.Add(problem.ElementId is { } known ? $"{LabelOf(known)}: {problem.Message}" : problem.Message);
        }

        foreach (var draftRow in _draft.Rows.Where(r => r.HasProblems))
        {
            if (compilation.Origins.TryGetValue(draftRow.Id, out var origin) && Exists(origin))
            {
                foreach (var message in draftRow.Problems.Where(m => m != "A step inside has a problem."))
                {
                    if (Put(origin, message))
                    {
                        shown.Add($"{LabelOf(origin)}: {message}");
                    }
                }
            }
        }

        Problems = shown.Distinct().ToList();
        BannerProblems = banner.Distinct().ToList();
        RebuildCards();
        UpdateSetupNeeded();
        if (rebuildDrawer)
        {
            RebuildDrawer();
        }

        RefreshMeridian();
        RefreshProgress();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanRunText));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        NotifyCommands();
        if (modified)
        {
            RaiseModified();
        }
    }

    private IReadOnlyList<string> _notes = [];

    /// <summary>What the compiler wants the user to know about how things were mapped (for example which setup the dither is counted on); not errors.</summary>
    public IReadOnlyList<string> Notes => _notes;

    public bool HasNotes => _notes.Count > 0;

    private IReadOnlyDictionary<Guid, Guid> _origins = new Dictionary<Guid, Guid>();

    private readonly Dictionary<Guid, List<string>> _problemsOf = [];

    private bool Put(Guid element, string message)
    {
        if (!_problemsOf.TryGetValue(element, out var list))
        {
            _problemsOf[element] = list = [];
        }

        if (list.Contains(message))
        {
            return false;
        }

        list.Add(message);
        return true;
    }

    /// <summary>What is wrong with an element of the session (an action, a block, a sequence, a target), as one text; empty when nothing is.</summary>
    internal string ProblemTextOf(Guid element) => _problemsOf.TryGetValue(element, out var list) ? string.Join(" ", list) : string.Empty;

    private bool Exists(Guid id) =>
        Session is { } s && (SessionEdits.FindTarget(s, id) is not null || SessionEdits.FindLane(s, id) is not null || SessionEdits.FindBlock(s, id) is not null || SessionEdits.FindAction(s, id) is not null);

    // ---- problems

    /// <summary>What is wrong with the session, in sentences about setups and actions; empty when it can run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(CanRunText))]
    public partial IReadOnlyList<string> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    /// <summary>The problems that no element shows: they are about the session, or about nothing in particular. The ones of an element are said at the element, once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBannerProblems))]
    public partial IReadOnlyList<string> BannerProblems { get; private set; } = [];

    public bool HasBannerProblems => BannerProblems.Count > 0;

    /// <summary>Why the session cannot run yet (how many things to fix; they are said where they are), or what it will do.</summary>
    public string CanRunText => HasProblems
        ? (Problems.Count == 1 ? "1 problem to fix." : string.Create(CultureInfo.InvariantCulture, $"{Problems.Count} problems to fix."))
        : IsEmpty ? "Add a target to begin."
        : !(Session?.Targets.Any(t => t.Enabled && t.Lanes.Any(l => l.Blocks.Any(b => b.Enabled))) ?? false) ? "Add an imaging block: the session images nothing yet."
        : "The session is complete.";

    private string LabelOf(Guid id)
    {
        if (Session is not { } s)
        {
            return string.Empty;
        }

        if (SessionEdits.FindAction(s, id) is { } action)
        {
            return ActionSummary.Title(action);
        }

        if (SessionEdits.FindBlock(s, id) is { } block)
        {
            var lane = SessionEdits.LaneOf(s, id);
            return BlockSummary.Title(block, (lane?.Blocks.ToList().IndexOf(block) ?? 0) + 1, FilterNameOf(lane?.Setup));
        }

        if (SessionEdits.FindLane(s, id) is { } found)
        {
            return ResolvedSetup(found.Setup)?.Name ?? "Imaging sequence";
        }

        return SessionEdits.FindTarget(s, id)?.Name ?? string.Empty;
    }

    // ---- keeping up with the draft and the run

    // Something other than this editor changed the steps while the session is structured (a step pasted, a step added by code): the steps are no longer what the session compiles to, so the sequence is a
    // tree of explicit steps from here on, and nothing is lost: the steps are kept as they are.
    private void OnDraftModified(object? sender, EventArgs e)
    {
        if (Session is not null && !_ownModification)
        {
            Load(null, modified: false);
        }
    }

    private void OnDraftChanged(object? sender, EventArgs e)
    {
        if (Session is null)
        {
            OnPropertyChanged(nameof(CanReturnToStructured));
            OnPropertyChanged(nameof(WhyNotStructuredText));
            SwitchToStructuredCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SequenceDraftViewModel.IsEditable))
        {
            NotifyCommands();
            RebuildCards();
        }
    }

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

    private void RaiseModeChanged()
    {
        OnPropertyChanged(nameof(IsStructured));
        OnPropertyChanged(nameof(IsTree));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasUnreadableFields));
        OnPropertyChanged(nameof(CanReturnToStructured));
        OnPropertyChanged(nameof(WhyNotStructuredText));
        OnPropertyChanged(nameof(CanRunText));
        SwitchToStructuredCommand.NotifyCanExecuteChanged();
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsEditable));
        OnPropertyChanged(nameof(IsRunning));
        foreach (var command in new IRelayCommand[]
        {
            AddTargetCommand, SwitchToStructuredCommand, ShowTreeCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _post(() =>
    {
        // What follows the defaults follows them (compiled again; the document is not modified by it), and what is custom is left alone.
        if (Session is { } session && session.Automation.UsesDefaultFlip)
        {
            Refresh(modified: false);
        }
    });
}

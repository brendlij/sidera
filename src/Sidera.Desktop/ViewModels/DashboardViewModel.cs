using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>One thing the whole session shares and what it is doing: the mount, the guider, a dither.</summary>
public sealed record SharedStatusItem(string Role, string Name, string StatusText, StatusKind Kind);

/// <summary>
/// The live overview: what Sidera is doing right now. It owns nothing; it reads the view models that hold the real state
/// (the sequencer, the execution overview, the equipment, the imaging page), so the overview can never disagree with the
/// pages behind it. It answers one question and shows neither the sequence tree nor any editor.
/// <list type="bullet">
/// <item>While a Multi-Rig sequence runs (and after it, until it is edited): a lane for each track.</item>
/// <item>While any other sequence runs: the step that is running, and the equipment.</item>
/// <item>Otherwise: the equipment, as rig cards when rigs exist and as camera cards when none do.</item>
/// </list>
/// </summary>
public sealed partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly SequencerViewModel _sequencer;
    private readonly EquipmentViewModel _equipment;
    private readonly SequenceDocumentViewModel _document;
    private readonly SharedEquipmentViewModel _shared;
    private readonly Action<Action> _postToUi;
    private readonly Action<AppPage> _navigate;
    private readonly ImagingSetupContext _context;
    private Timer? _clock;

    public DashboardViewModel(
        RuntimeStatusViewModel runtime,
        SequencerViewModel sequencer,
        ImagingViewModel imaging,
        EquipmentViewModel equipment,
        SequenceDocumentViewModel document,
        SharedEquipmentViewModel shared,
        ExecutionOverviewViewModel execution,
        ImagingSetupContext context,
        Action<Action>? postToUi = null,
        Action<AppPage>? navigate = null)
    {
        Runtime = runtime;
        _context = context;
        _sequencer = sequencer;
        Imaging = imaging;
        _equipment = equipment;
        _document = document;
        _shared = shared;
        Execution = execution;
        _postToUi = postToUi ?? (action => action());
        _navigate = navigate ?? (_ => { });

        _sequencer.PropertyChanged += OnChanged;
        _sequencer.ExecutionRefreshed += OnExecutionRefreshed;
        _document.PropertyChanged += OnChanged;
        _shared.PropertyChanged += OnChanged;
        Execution.PropertyChanged += OnChanged;
        foreach (var device in _equipment.Devices)
        {
            device.Refreshed += OnExecutionRefreshed;
        }

        _equipment.DeviceViewModelAdded += OnDeviceAdded;
        _equipment.DevicesChanged += OnDevicesChanged;
        _context.Changed += OnDevicesChanged;
        Units = BuildUnits();
        Refresh();
    }

    public RuntimeStatusViewModel Runtime { get; }
    public SequencerViewModel Sequencer => _sequencer;
    public ImagingViewModel Imaging { get; }
    public ExecutionOverviewViewModel Execution { get; }

    /// <summary>What the equipment is doing, as cards: the imaging setups that can image, otherwise the cameras.</summary>
    public IReadOnlyList<object> Units { get; private set; }

    private IReadOnlyList<object> BuildUnits() =>
        UnitsAreSetups ? _context.Options.Select(o => (object)_equipment.ViewOf(o.Setup)).ToList() : _equipment.Cameras.Cast<object>().ToList();

    // A device came or went: the cards are made again, and the new device is followed like the others.
    private void OnDeviceAdded(object? sender, DeviceViewModelBase device) => device.Refreshed += OnExecutionRefreshed;

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Units = BuildUnits();
        OnPropertyChanged(nameof(Units));
        OnPropertyChanged(nameof(UnitsAreSetups));
        OnPropertyChanged(nameof(UnitsTitle));
        OnPropertyChanged(nameof(HasUnits));
        OnPropertyChanged(nameof(ShowUnits));
    }

    /// <summary>Imaging setups are optional: with none made, the units are the cameras and nothing says "setup".</summary>
    public bool UnitsAreSetups => _equipment.HasRigs && _context.Options.Count > 0;

    public string UnitsTitle => !UnitsAreSetups ? "Cameras" : _context.Options.Count == 1 ? "Imaging Setup" : "Imaging Setups";

    /// <summary>What the lanes of a run are called: with one imaging setup nothing says that there could be several.</summary>
    public string LanesTitle => Execution.Lanes.Count >= 2 ? "Imaging Setups" : "Imaging Setup";

    /// <summary>The mount and the guider the setups share; with one setup they are the mount and the guider of the session.</summary>
    public string SharedTitle => _context.Options.Count >= 2 || Execution.Lanes.Count >= 2 ? "Shared" : "Mount and guider";

    public bool HasUnits => Units.Count > 0;

    /// <summary>The equipment cards are shown: not while the lanes of a Multi-Rig run already say what each rig does.</summary>
    public bool ShowUnits => HasUnits && !ShowLanes;

    /// <summary>What the idle panel says: nothing runs, or how the last run ended.</summary>
    public string IdleTitle => _sequencer.State switch
    {
        Sidera.Core.Sequencing.SequenceState.Completed => "The last run completed",
        Sidera.Core.Sequencing.SequenceState.Failed => "The last run failed",
        Sidera.Core.Sequencing.SequenceState.Cancelled => "The last run was cancelled",
        _ => "No sequence is running",
    };

    // The session

    /// <summary>The name of the session: the name of its sequence file, or "Untitled Session".</summary>
    public string Title => _document.FilePath is null
        ? "Untitled Session"
        : Path.GetFileNameWithoutExtension(_document.FilePath);

    /// <summary>The sequence has steps.</summary>
    public bool HasSession => !_sequencer.IsEmpty;

    /// <summary>"6 steps", for the session that is loaded and not running.</summary>
    public string SessionSummary => _sequencer.Draft is { } draft
        ? string.Create(CultureInfo.InvariantCulture, $"{draft.Rows.Count} {(draft.Rows.Count == 1 ? "step" : "steps")}")
        : string.Empty;

    public string StateText => _sequencer.State == Sidera.Core.Sequencing.SequenceState.Idle ? "Idle" : _sequencer.StateText;

    public StatusKind StateKind => _sequencer.StateKind;

    public bool IsRunning => _sequencer.IsRunning;

    /// <summary>How long the run has gone on (or went on), "1:42:07"; empty before the first run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasElapsed))]
    public partial string ElapsedText { get; private set; } = string.Empty;

    public bool HasElapsed => ElapsedText.Length > 0;

    /// <summary>The tracks of the Multi-Rig block are shown as lanes: the run (or its result) is that of a sequence that has them.</summary>
    public bool ShowLanes => Execution.HasLanes && _sequencer.ShowsRun;

    /// <summary>A sequence without lanes is running: its step is shown.</summary>
    public bool ShowActivity => _sequencer.IsRunning && !Execution.HasLanes;

    /// <summary>Nothing runs and nothing has run: the equipment says what Sidera could do.</summary>
    public bool IsIdle => !_sequencer.IsRunning && !ShowLanes;

    /// <summary>The error of the last run, shown where the run is.</summary>
    public string? RunError => _sequencer.ErrorMessage;

    public bool HasRunError => _sequencer.HasError;

    // The shared equipment

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShared))]
    public partial IReadOnlyList<SharedStatusItem> SharedItems { get; private set; } = [];

    public bool HasShared => SharedItems.Count > 0;

    /// <summary>The guider of the session when it measures: its graph and RMS are shown on the dashboard.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuider))]
    public partial GuiderViewModel? Guider { get; private set; }

    public bool HasGuider => Guider is not null;

    [RelayCommand]
    private void OpenSession() => _navigate(AppPage.Session);

    [RelayCommand]
    private void OpenImaging() => _navigate(AppPage.Imaging);

    [RelayCommand]
    private void OpenEquipment() => _navigate(AppPage.Equipment);

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void OnExecutionRefreshed(object? sender, EventArgs e) => Refresh();

    /// <summary>Reads everything again; the view models it reads from call it on every change.</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        RefreshElapsed();
        SharedItems = ReadShared();
        Guider = (_equipment.Guiders.FirstOrDefault(g => g.DeviceIdText == _shared.Guider.SelectedId?.Value) ?? _equipment.Guiders.FirstOrDefault()) is { HasMeasurements: true } measured
            ? measured
            : null;

        // The clock ticks only while a sequence runs.
        if (_sequencer.IsRunning && _clock is null)
        {
            _clock = new Timer(_ => _postToUi(RefreshElapsed), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else if (!_sequencer.IsRunning && _clock is not null)
        {
            _clock.Dispose();
            _clock = null;
        }
    }

    /// <summary>Reads the elapsed time of the run again; called every second while a run goes on.</summary>
    public void RefreshElapsed()
    {
        ElapsedText = _sequencer.Elapsed is { } elapsed ? Format(elapsed) : string.Empty;
    }

    private static string Format(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{elapsed.Minutes}:{elapsed.Seconds:00}");

    // The mount and the guider of the session (those the session page selected), and what the sequence does with them.
    private IReadOnlyList<SharedStatusItem> ReadShared()
    {
        var items = new List<SharedStatusItem>();

        var mount = _equipment.Mounts.FirstOrDefault(m => m.DeviceIdText == _shared.Mount.SelectedId?.Value)
            ?? _equipment.Mounts.FirstOrDefault();
        if (mount is not null)
        {
            items.Add(Describe("Mount", mount));
        }

        var guider = _equipment.Guiders.FirstOrDefault(g => g.DeviceIdText == _shared.Guider.SelectedId?.Value)
            ?? _equipment.Guiders.FirstOrDefault();
        if (guider is not null)
        {
            items.Add(Describe("Guider", guider));
        }

        if (Execution.SharedActivity is { } dither)
        {
            items.Add(new SharedStatusItem("Dither", dither, string.Empty, StatusKind.Active));
        }

        return items;
    }

    private static SharedStatusItem Describe(string role, DeviceViewModelBase device) => new(
        role, device.Name, device.IsConnected ? device.ActivityText : device.StatusText,
        device.IsConnected ? (device.IsBusy ? StatusKind.Active : StatusKind.Ok) : device.StatusKind);

    public void Dispose()
    {
        _clock?.Dispose();
        _clock = null;
        _sequencer.PropertyChanged -= OnChanged;
        _sequencer.ExecutionRefreshed -= OnExecutionRefreshed;
        _document.PropertyChanged -= OnChanged;
        _shared.PropertyChanged -= OnChanged;
        Execution.PropertyChanged -= OnChanged;
        _equipment.DeviceViewModelAdded -= OnDeviceAdded;
        _equipment.DevicesChanged -= OnDevicesChanged;
        foreach (var device in _equipment.Devices)
        {
            device.Refreshed -= OnExecutionRefreshed;
        }
    }
}

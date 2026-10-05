using System;
using System.Threading.Tasks;
using Sidera.Desktop.Documents;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Desktop.Diagnostics;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>The pages of the application. The first four are the work, the last two the care of the application.</summary>
public enum AppPage
{
    Dashboard,
    Session,
    Imaging,
    Framing,
    PlateSolve,
    Equipment,
    Diagnostics,
    Settings
}

/// <summary>
/// The application shell: it creates the page view models around one runtime host and switches between them.
/// All logic lives in the page view models; this one only composes and navigates.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <param name="store">Where sequence documents are read and written; the file store of the current format by default.</param>
    /// <param name="filePicker">How the user chooses sequence files; by default nothing can be chosen.</param>
    /// <param name="withDemoSequence">Whether a start with equipment gets the demo sequence; the application starts with an empty session.</param>
    /// <param name="equipmentManagement">What lets the user add, edit and remove devices; without it the equipment page only shows the devices of the host.</param>
    public MainViewModel(
        SideraRuntimeHost host,
        Action<Action> postToUi,
        DemoOptions? options = null,
        ISequenceDocumentStore? store = null,
        ISequenceFilePicker? filePicker = null,
        LogInfo? logInfo = null,
        IFolderOpener? folderOpener = null,
        IClipboardService? clipboard = null,
        EquipmentManagement? equipmentManagement = null,
        bool withDemoSequence = true,
        Sidera.Sky.ICelestialObjectCatalog? objectCatalog = null,
        System.Func<Sidera.Sky.SkySurveyDescriptor, Sidera.Sky.ISkySurveyProvider>? skyProviders = null)
    {
        options ??= new DemoOptions();
        var activity = new SessionActivity();

        Imaging = new ImagingViewModel(host.FrameAnalyzer, postToUi);
        Equipment = new EquipmentViewModel(host, postToUi, activity, Imaging, options.ManualExposure, equipmentManagement);
        Runtime = new RuntimeStatusViewModel(host, [DemoSetup.CoordinationGroup]);
        var defaults = SequenceDraftDefaults.From(options, host.DeviceRegistry);
        SequenceDraft = new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, host.DeviceRegistry.GetAll().Count == 0 || !withDemoSequence ? [] : defaults.InitialSteps(),
            rigs: host.RigRegistry, shared: new SharedEquipmentDraft(defaults.MountId, defaults.GuiderId),
            focusMetrics: host.FocusMetricProvider, events: host.EventBus,
            loggers: host.LoggerFactory, acquisitionDefaults: host.AcquisitionDefaults,
            plateSolving: host.PlateSolving, solveDefaults: () => (equipmentManagement?.Site?.PlateSolving ?? new Sidera.Desktop.Settings.PlateSolvingSettings()).Defaults());
        Diagnostics = new DiagnosticsViewModel(logInfo, folderOpener, clipboard, postToUi);
        Settings = new SettingsViewModel(logInfo, equipmentManagement?.Site);
        PlateSolve = new PlateSolveViewModel(host, Imaging, equipmentManagement?.Site, postToUi);
        Framing = new FramingViewModel(host, equipmentManagement?.Site, SequenceDraft, objectCatalog, skyProviders, postToUi);
        Sequencer = new SequencerViewModel(
            host, postToUi, activity, Imaging, Equipment.Cameras, SequenceDraft, CheckEquipmentOfSequence);
        SequenceDocument = new SequenceDocumentViewModel(
            SequenceDraft, store ?? SequenceDocumentStore.CreateDefault(), filePicker ?? new NoSequenceFilePicker());
        var shared = new SharedEquipmentViewModel(SequenceDraft, Equipment);
        Execution = new ExecutionOverviewViewModel(Sequencer, Equipment.Rigs);
        SessionPage = new SessionPageViewModel(SequenceDocument, SequenceDraft, Sequencer, shared, Execution);
        Dashboard = new DashboardViewModel(
            Runtime, Sequencer, Imaging, Equipment, SequenceDocument, shared, Execution, postToUi, page => SelectedPage = page);

        PrimaryNavigation =
        [
            Item(AppPage.Dashboard, "Dashboard", "IconDashboard"),
            Item(AppPage.Session, "Session", "IconSession"),
            Item(AppPage.Imaging, "Imaging", "IconImaging"),
            Item(AppPage.Framing, "Framing", "IconImaging"),
            Item(AppPage.PlateSolve, "Plate Solve", "IconImaging"),
            Item(AppPage.Equipment, "Equipment", "IconEquipment"),
        ];
        SecondaryNavigation =
        [
            Item(AppPage.Diagnostics, "Diagnostics", "IconDiagnostics"),
            Item(AppPage.Settings, "Settings", "IconSettings"),
        ];
        UpdateNavigation();

        // The runtime summary and the "can the sequence start" hint follow the equipment and the sequence.
        void OnDeviceRefreshed(object? sender, EventArgs e)
        {
            Runtime.Refresh();
            Sequencer.RefreshReadiness();
        }

        foreach (var device in Equipment.Devices)
        {
            device.Refreshed += OnDeviceRefreshed;
        }

        // Devices that are added while Sidera runs are followed too; the draft is told about devices that came or went.
        Equipment.DeviceViewModelAdded += (_, device) => device.Refreshed += OnDeviceRefreshed;
        Equipment.DevicesChanged += (_, _) => OnDeviceRefreshed(this, EventArgs.Empty);

        // The demo is a quick start: when it is added to an installation whose session is still empty and untouched, the
        // session gets the demo sequence that a first start with equipment always had.
        if (equipmentManagement is not null && withDemoSequence)
        {
            equipmentManagement.Service.Changed += (_, change) =>
            {
                if (change.Kind == Sidera.Desktop.Hardware.EquipmentChangeKind.RigsAdded
                    && SequenceDraft.IsEmpty && !SequenceDocument.IsDirty && SequenceDocument.FilePath is null && !Sequencer.IsRunning)
                {
                    SequenceDraft.ReplaceSteps(SequenceDraftDefaults.From(options, host.DeviceRegistry).InitialSteps());
                }
            };
        }

        Equipment.RemovalGuard = id => SequenceDraft.RequiredDeviceIds().Any(d => d.Value == id)
            ? "It is used by a step of the current sequence."
            : null;

        Sequencer.ExecutionRefreshed += (_, _) => Runtime.Refresh();
        Sequencer.RefreshReadiness();
    }

    public DashboardViewModel Dashboard { get; }
    public EquipmentViewModel Equipment { get; }
    public SequenceDraftViewModel SequenceDraft { get; }
    public SequenceDocumentViewModel SequenceDocument { get; }
    public SequencerViewModel Sequencer { get; }
    public SessionPageViewModel SessionPage { get; }
    public ExecutionOverviewViewModel Execution { get; }
    public ImagingViewModel Imaging { get; }
    public RuntimeStatusViewModel Runtime { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel Settings { get; }
    public PlateSolveViewModel PlateSolve { get; }
    public FramingViewModel Framing { get; }

    /// <summary>The sidebar entries of the work: dashboard, session, imaging, equipment.</summary>
    public IReadOnlyList<NavItemViewModel> PrimaryNavigation { get; }

    /// <summary>The sidebar entries below the separator: diagnostics and settings.</summary>
    public IReadOnlyList<NavItemViewModel> SecondaryNavigation { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(PageTitle))]
    public partial AppPage SelectedPage { get; private set; }

    public ViewModelBase CurrentPage => SelectedPage switch
    {
        AppPage.Session => SessionPage,
        AppPage.Imaging => Imaging,
        AppPage.Framing => Framing,
        AppPage.PlateSolve => PlateSolve,
        AppPage.Equipment => Equipment,
        AppPage.Diagnostics => Diagnostics,
        AppPage.Settings => Settings,
        _ => Dashboard,
    };

    /// <summary>The name of the current page, as the sidebar names it.</summary>
    public string PageTitle => SelectedPage == AppPage.PlateSolve ? "Plate Solve" : SelectedPage.ToString();

    partial void OnSelectedPageChanged(AppPage value)
    {
        if (value == AppPage.PlateSolve) PlateSolve.RefreshEquipment();
        if (value == AppPage.Framing) Framing.RefreshEquipment();
        UpdateNavigation();
    }

    private NavItemViewModel Item(AppPage page, string title, string iconKey) =>
        new(page, title, iconKey, new RelayCommand(() => SelectedPage = page));

    private void UpdateNavigation()
    {
        foreach (var item in PrimaryNavigation.Concat(SecondaryNavigation))
        {
            item.IsSelected = item.Page == SelectedPage;
        }
    }

    [RelayCommand]
    private void Navigate(AppPage page) => SelectedPage = page;

    // Every device a step of the sequence uses must be connected and not busy.
    private string? CheckEquipmentOfSequence()
    {
        var ids = SequenceDraft.RequiredDeviceIds().Select(id => id.Value).ToList();

        // A missing or unsuitable device is reported by the draft itself; this is only about the state of chosen equipment.
        var cameras = Equipment.Cameras.Where(c => ids.Contains(c.DeviceIdText)).ToArray();
        var mounts = Equipment.Mounts.Where(m => ids.Contains(m.DeviceIdText)).ToArray();
        var guiders = Equipment.Guiders.Where(g => ids.Contains(g.DeviceIdText)).ToArray();

        foreach (var device in cameras.Cast<DeviceViewModelBase>().Concat(mounts).Concat(guiders))
        {
            if (!device.IsConnected)
            {
                return $"Connect {device.Name} to run the sequence.";
            }
        }

        if (cameras.Any(c => c.ExposureState != CameraExposureState.Idle || c.IsManualExposureRunning))
        {
            return "Wait for the camera to finish its exposure.";
        }

        if (mounts.Any(m => m.IsSlewing || m.IsManualSlewRunning))
        {
            return "Wait for the mount to finish slewing.";
        }

        return guiders.All(g => g.GuidingState is GuidingState.Idle or GuidingState.Guiding)
            ? null
            : "Wait for the guider to finish its operation.";
    }

    // Without a window there is nothing to pick from: opening and saving as do nothing.
    private sealed class NoSequenceFilePicker : ISequenceFilePicker
    {
        public Task<string?> PickOpenPathAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    public void Dispose()
    {
        PlateSolve.Dispose();
        Framing.Dispose();
        Dashboard.Dispose();
        Execution.Dispose();
        Diagnostics.Dispose();
        Imaging.Dispose();
        Sequencer.Dispose();
        Equipment.Dispose();
    }
}

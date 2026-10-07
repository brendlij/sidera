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
        System.Func<Sidera.Sky.SkySurveyDescriptor, Sidera.Sky.ISkySurveyProvider>? skyProviders = null,
        bool startWithSettingsMode = false,
        Sidera.Desktop.Themes.IThemeApplier? themeApplier = null)
    {
        options ??= new DemoOptions();
        var activity = new SessionActivity();

        // The setups the application works with: the ones that were made, and the one that follows from a single camera. One of them is current; the pages follow it.
        Setups = new Sidera.Runtime.Rigs.ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);
        SetupContext = new ImagingSetupContext(Setups, host.DeviceRegistry);
        Imaging = new ImagingViewModel(host.FrameAnalyzer, postToUi);
        Equipment = new EquipmentViewModel(host, postToUi, activity, Imaging, options.ManualExposure, equipmentManagement);
        Equipment.AttachSetupContext(SetupContext);
        Runtime = new RuntimeStatusViewModel(host, [DemoSetup.CoordinationGroup]);
        var defaults = SequenceDraftDefaults.From(options, host.DeviceRegistry);

        // What the settings propose for what is created from now on: the measurements of an autofocus and the values of a dither. Nothing that exists is changed by it.
        if (equipmentManagement?.Site is { } proposed)
        {
            defaults = defaults with
            {
                AutofocusExposureSeconds = proposed.Autofocus.ExposureSeconds,
                AutofocusStepSize = proposed.Autofocus.StepSize,
                AutofocusSampleCount = proposed.Autofocus.SampleCount,
                DitherAmplitudePixels = proposed.Guiding.DitherAmplitudePixels,
                SettleThresholdPixels = proposed.Guiding.SettleThresholdPixels,
                SettleStableSeconds = proposed.Guiding.SettleStableSeconds,
                SettleTimeoutSeconds = proposed.Guiding.SettleTimeoutSeconds,
            };
        }

        SequenceDraft = new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, host.DeviceRegistry.GetAll().Count == 0 || !withDemoSequence ? [] : defaults.InitialSteps(),
            rigs: Setups, shared: SharedEquipmentDraft.FromRigs(Setups.GetAll(), defaults.MountId, defaults.GuiderId, host.RigRegistry.GetAll().Count == 0),
            focusMetrics: host.FocusMetricProvider, events: host.EventBus,
            loggers: host.LoggerFactory, acquisitionDefaults: host.AcquisitionDefaults,
            plateSolving: host.PlateSolving, solveDefaults: () => (equipmentManagement?.Site?.PlateSolving ?? new Sidera.Desktop.Settings.PlateSolvingSettings()).Defaults(),
            rotation: host.Rotation);
        Imaging.AutoStretch = equipmentManagement?.Site?.Imaging.AutoStretch ?? true;
        Imaging.FitOnCapture = equipmentManagement?.Site?.Imaging.FitOnCapture ?? true;
        Imaging.SaveDirectory = () => equipmentManagement?.Site?.Imaging.SaveDirectory;
        Imaging.Capture = new ImagingCaptureViewModel(host, Imaging, SetupContext, equipmentManagement?.Site?.Imaging.ManualExposureSeconds ?? 5);
        Imaging.Autofocus = new ManualAutofocusViewModel(host, defaults, SetupContext, postToUi);
        Imaging.Capture.CreateSetup = () => OpenEquipment(() => Equipment.BeginNewImagingSetup());
        Imaging.Autofocus.ConfigureFocuser = () => OpenEquipment(() => Equipment.ShowFocuserSetup());
        Imaging.ExportHost = host;
        Imaging.ExportSite = () => equipmentManagement?.Site?.Site;
        Diagnostics = new DiagnosticsViewModel(logInfo, folderOpener, clipboard, postToUi);
        Settings = new SettingsViewModel(logInfo, equipmentManagement?.Site, Safety, themeApplier);
        PlateSolve = new PlateSolveViewModel(host, Imaging, SetupContext, equipmentManagement?.Site, postToUi);
        Framing = new FramingViewModel(host, SetupContext, equipmentManagement?.Site, SequenceDraft, objectCatalog, skyProviders, postToUi);
        Sequencer = new SequencerViewModel(
            host, postToUi, activity, Imaging, Equipment.Cameras, SequenceDraft, CheckEquipmentOfSequence);
        SequenceDocument = new SequenceDocumentViewModel(
            SequenceDraft, store ?? SequenceDocumentStore.CreateDefault(), filePicker ?? new NoSequenceFilePicker());
        var shared = new SharedEquipmentViewModel(SequenceDraft, Equipment);
        Execution = new ExecutionOverviewViewModel(Sequencer, Equipment.Rigs);
        SequenceDraft.SiteProvider = () => equipmentManagement?.Site?.Site;
        SequenceDraft.AutofocusHoldsMount = () => equipmentManagement?.Site?.Autofocus.HoldMountStable ?? false;
        SessionEditor = new SessionEditorViewModel(
            SequenceDraft, Setups, host.DeviceRegistry, defaults, Execution, host.EventBus, postToUi, () => equipmentManagement?.Site?.Site, equipmentManagement?.Site,
            () => OpenEquipment(() => Equipment.BeginNewImagingSetup()));
        SessionEditor.CurrentSetup = () => SetupContext.Current?.Id;
        SessionEditor.OpenFraming = () => SelectedPage = AppPage.Framing;
        SessionEditor.ShowEquipment = () => OpenEquipment(() => Equipment.ShowSetupView());
        SequenceDraft.CurrentSetup = () => SetupContext.Current?.Id;

        // A new session opens in the mode the settings choose (blocks unless said otherwise), not as whatever the last editor left behind. A session that is opened from a file is its own.
        if (startWithSettingsMode && SequenceDraft.IsEmpty)
        {
            SessionEditor.StartNew();
        }
        SequenceDocument.SessionSource = SessionEditor;
        SessionPage = new SessionPageViewModel(SequenceDocument, SequenceDraft, Sequencer, shared, Execution, SessionEditor);
        PlateSolve.Safety = Safety;
        Framing.Safety = Safety;
        Sequencer.Safety = Safety;
        Dashboard = new DashboardViewModel(
            Runtime, Sequencer, Imaging, Equipment, SequenceDocument, shared, Execution, SetupContext, postToUi, page => SelectedPage = page);

        StatusBar = new StatusBarViewModel(Sequencer, Execution, postToUi);

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
            SetupContext.Refresh(); // a camera that was connected or disconnected can change which setups can image
            Runtime.Refresh();
            Sequencer.RefreshReadiness();
            Imaging.Capture?.RefreshCapabilities();
            Imaging.Autofocus?.Refresh();
        }

        foreach (var device in Equipment.Devices)
        {
            device.Refreshed += OnDeviceRefreshed;
        }

        // Devices that are added while Sidera runs are followed too; the draft is told about devices that came or went.
        Equipment.DeviceViewModelAdded += (_, device) => device.Refreshed += OnDeviceRefreshed;
        Equipment.DevicesChanged += (_, _) =>
        {
            Imaging.Capture?.Refresh();
            OnDeviceRefreshed(this, EventArgs.Empty);
        };

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

        // A rig that is added, renamed, changed or removed is followed by the pages that choose a rig: without this the Imaging page and the others would offer the rigs of the start.
        if (equipmentManagement is not null)
        {
            equipmentManagement.Service.Changed += (_, change) =>
            {
                if (change.Kind is Sidera.Desktop.Hardware.EquipmentChangeKind.RigsAdded or Sidera.Desktop.Hardware.EquipmentChangeKind.RigsChanged)
                {
                    SetupContext.Refresh();
                    Imaging.Capture?.Refresh();
                    OnDeviceRefreshed(this, EventArgs.Empty);
                    SessionEditor.RefreshSetups();
                    PlateSolve.RefreshEquipment();
                    Framing.RefreshEquipment();
                }
            };
        }

        Equipment.RemovalGuard = id => SequenceDraft.RequiredDeviceIds().Any(d => d.Value == id)
            ? "It is used by a step of the current sequence."
            : null;

        Sequencer.ExecutionRefreshed += (_, _) => Runtime.Refresh();
        Sequencer.RefreshReadiness();
    }

    /// <summary>The imaging setups of the sequencer: the configured ones and, for one camera, the implicit one.</summary>
    public Sidera.Runtime.Rigs.ImagingSetupCatalog Setups { get; }

    /// <summary>The imaging setup that Imaging, Autofocus, Framing, Plate Solve and the equipment view of a setup work with; chosen in the sidebar when there are several.</summary>
    public ImagingSetupContext SetupContext { get; }

    public DashboardViewModel Dashboard { get; }
    public EquipmentViewModel Equipment { get; }
    public SequenceDraftViewModel SequenceDraft { get; }
    public SequenceDocumentViewModel SequenceDocument { get; }
    public SequencerViewModel Sequencer { get; }
    public SessionPageViewModel SessionPage { get; }
    /// <summary>The editor of the session: its start, targets with their blocks, and end.</summary>
    public SessionEditorViewModel SessionEditor { get; }

    /// <summary>The question that comes before real equipment moves; answered by the user, never by an environment variable.</summary>
    public HardwareSafetyViewModel Safety { get; } = new();
    public ExecutionOverviewViewModel Execution { get; }

    /// <summary>The bar at the bottom of the window: what Sidera is doing now.</summary>
    public StatusBarViewModel StatusBar { get; }
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

    // The equipment page starts with the current imaging setup when it is opened from the sidebar.
    private NavItemViewModel Item(AppPage page, string title, string iconKey) =>
        new(page, title, iconKey, new RelayCommand(() =>
        {
            if (page == AppPage.Equipment)
            {
                Equipment.ShowSetupView();
            }

            SelectedPage = page;
        }));

    // Opens the equipment page the way another page asks for it (to make a setup, to give a setup a focuser), not as the sidebar does.
    private void OpenEquipment(Action arrange)
    {
        arrange();
        SelectedPage = AppPage.Equipment;
    }

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
        StatusBar.Dispose();
        SessionEditor.Dispose();
        Dashboard.Dispose();
        Execution.Dispose();
        Diagnostics.Dispose();
        Imaging.Dispose();
        Sequencer.Dispose();
        Equipment.Dispose();
    }
}

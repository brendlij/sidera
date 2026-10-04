using System;
using System.Threading.Tasks;
using Astra.Desktop.Documents;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Desktop.Diagnostics;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>The pages of the application. The first four are the work, the last two the care of the application.</summary>
public enum AppPage
{
    Dashboard,
    Session,
    Imaging,
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
    public MainViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        DemoOptions? options = null,
        ISequenceDocumentStore? store = null,
        ISequenceFilePicker? filePicker = null,
        LogInfo? logInfo = null,
        IFolderOpener? folderOpener = null,
        IClipboardService? clipboard = null)
    {
        options ??= new DemoOptions();
        var activity = new SessionActivity();

        Imaging = new ImagingViewModel(host.FrameAnalyzer, postToUi);
        Equipment = new EquipmentViewModel(host, postToUi, activity, Imaging, options.ManualExposure);
        Runtime = new RuntimeStatusViewModel(host, [DemoSetup.CoordinationGroup]);
        var defaults = SequenceDraftDefaults.From(options, host.DeviceRegistry);
        SequenceDraft = new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, defaults.InitialSteps(),
            rigs: host.RigRegistry, shared: new SharedEquipmentDraft(defaults.MountId, defaults.GuiderId),
            focusMetrics: host.FocusMetricProvider, events: host.EventBus,
            loggers: host.LoggerFactory);
        Diagnostics = new DiagnosticsViewModel(logInfo, folderOpener, clipboard, postToUi);
        Settings = new SettingsViewModel(logInfo);
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
            Item(AppPage.Equipment, "Equipment", "IconEquipment"),
        ];
        SecondaryNavigation =
        [
            Item(AppPage.Diagnostics, "Diagnostics", "IconDiagnostics"),
            Item(AppPage.Settings, "Settings", "IconSettings"),
        ];
        UpdateNavigation();

        // The runtime summary and the "can the sequence start" hint follow the equipment and the sequence.
        foreach (var device in Equipment.Devices)
        {
            device.Refreshed += (_, _) =>
            {
                Runtime.Refresh();
                Sequencer.RefreshReadiness();
            };
        }

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
        AppPage.Equipment => Equipment,
        AppPage.Diagnostics => Diagnostics,
        AppPage.Settings => Settings,
        _ => Dashboard,
    };

    /// <summary>The name of the current page, as the sidebar names it.</summary>
    public string PageTitle => SelectedPage.ToString();

    partial void OnSelectedPageChanged(AppPage value) => UpdateNavigation();

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
        Dashboard.Dispose();
        Execution.Dispose();
        Diagnostics.Dispose();
        Imaging.Dispose();
        Sequencer.Dispose();
        Equipment.Dispose();
    }
}

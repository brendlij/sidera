using System;
using System.Threading.Tasks;
using Astra.Desktop.Documents;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

public enum AppPage
{
    Dashboard,
    Equipment,
    Sequencer,
    Imaging
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
        ISequenceFilePicker? filePicker = null)
    {
        options ??= new DemoOptions();
        var activity = new SessionActivity();

        Imaging = new ImagingViewModel();
        Equipment = new EquipmentViewModel(host, postToUi, activity, Imaging, options.ManualExposure);
        Runtime = new RuntimeStatusViewModel(host, [DemoSetup.CoordinationGroup]);
        var defaults = SequenceDraftDefaults.From(options, host.DeviceRegistry);
        SequenceDraft = new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, defaults.InitialSteps(),
            rigs: host.RigRegistry, shared: new SharedEquipmentDraft(defaults.MountId, defaults.GuiderId));
        Sequencer = new SequencerViewModel(
            host, postToUi, activity, Imaging, Equipment.Cameras, SequenceDraft, CheckEquipmentOfSequence);
        SequenceDocument = new SequenceDocumentViewModel(
            SequenceDraft, store ?? SequenceDocumentStore.CreateDefault(), filePicker ?? new NoSequenceFilePicker());
        SequencerPage = new SequencerPageViewModel(
            SequenceDocument, SequenceDraft, Sequencer, new SharedEquipmentViewModel(SequenceDraft, Equipment));
        Dashboard = new DashboardViewModel(
            Runtime, Sequencer, Imaging,
            Equipment.Rigs.FirstOrDefault(),
            Equipment.Cameras.FirstOrDefault(c => c.CameraId == DemoSetup.MainCameraId) ?? Equipment.Cameras.FirstOrDefault(),
            Equipment.Mounts.FirstOrDefault(),
            Equipment.Guiders.FirstOrDefault());

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
    public SequencerPageViewModel SequencerPage { get; }
    public ImagingViewModel Imaging { get; }
    public RuntimeStatusViewModel Runtime { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage))]
    [NotifyPropertyChangedFor(nameof(IsDashboardSelected))]
    [NotifyPropertyChangedFor(nameof(IsEquipmentSelected))]
    [NotifyPropertyChangedFor(nameof(IsSequencerSelected))]
    [NotifyPropertyChangedFor(nameof(IsImagingSelected))]
    public partial AppPage SelectedPage { get; private set; }

    public ViewModelBase CurrentPage => SelectedPage switch
    {
        AppPage.Equipment => Equipment,
        AppPage.Sequencer => SequencerPage,
        AppPage.Imaging => Imaging,
        _ => Dashboard,
    };

    public bool IsDashboardSelected => SelectedPage == AppPage.Dashboard;
    public bool IsEquipmentSelected => SelectedPage == AppPage.Equipment;
    public bool IsSequencerSelected => SelectedPage == AppPage.Sequencer;
    public bool IsImagingSelected => SelectedPage == AppPage.Imaging;

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
        Sequencer.Dispose();
        Equipment.Dispose();
    }
}

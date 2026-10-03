using System;
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
    public MainViewModel(AstraRuntimeHost host, Action<Action> postToUi, DemoOptions? options = null)
    {
        options ??= new DemoOptions();
        var activity = new SessionActivity();

        Imaging = new ImagingViewModel();
        Equipment = new EquipmentViewModel(host, postToUi, activity, Imaging, options.ManualExposure);
        Runtime = new RuntimeStatusViewModel(host, [DemoSetup.CoordinationGroup]);
        Sequencer = new SequencerViewModel(
            host, postToUi, activity, Imaging, Equipment.Cameras,
            DemoSequenceFactory.Create(host.DeviceRegistry, options),
            CheckDemoEquipment);
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
    public SequencerViewModel Sequencer { get; }
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
        AppPage.Sequencer => Sequencer,
        AppPage.Imaging => Imaging,
        _ => Dashboard,
    };

    public bool IsDashboardSelected => SelectedPage == AppPage.Dashboard;
    public bool IsEquipmentSelected => SelectedPage == AppPage.Equipment;
    public bool IsSequencerSelected => SelectedPage == AppPage.Sequencer;
    public bool IsImagingSelected => SelectedPage == AppPage.Imaging;

    [RelayCommand]
    private void Navigate(AppPage page) => SelectedPage = page;

    // The demo sequence uses the camera, the mount and the guider; each must be connected and not busy.
    private string? CheckDemoEquipment()
    {
        var camera = Equipment.Cameras.FirstOrDefault(c => c.CameraId == DemoSetup.MainCameraId);
        var mount = Equipment.Mounts.FirstOrDefault(m => m.DeviceIdText == DemoSetup.MountId.Value);
        var guider = Equipment.Guiders.FirstOrDefault(g => g.DeviceIdText == DemoSetup.GuiderId.Value);

        if (camera is null || mount is null || guider is null)
        {
            return "The equipment of the demo sequence is not registered.";
        }

        foreach (var device in new DeviceViewModelBase[] { camera, mount, guider })
        {
            if (!device.IsConnected)
            {
                return $"Connect {device.Name} to run the sequence.";
            }
        }

        if (camera.ExposureState != CameraExposureState.Idle || camera.IsManualExposureRunning)
        {
            return "Wait for the camera to finish its exposure.";
        }

        if (mount.IsSlewing || mount.IsManualSlewRunning)
        {
            return "Wait for the mount to finish slewing.";
        }

        return guider.GuidingState is GuidingState.Idle or GuidingState.Guiding
            ? null
            : "Wait for the guider to finish its operation.";
    }

    public void Dispose()
    {
        Sequencer.Dispose();
        Equipment.Dispose();
    }
}

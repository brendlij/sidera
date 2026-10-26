using Sidera.Core.Devices;
using Sidera.Desktop.Notifications;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>A manual capture is off while a session runs, and the camera says when it is handing its image over.</summary>
public sealed class ManualCaptureLockTests : IAsyncLifetime
{
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }
    }

    private async Task<MainViewModel> CreateAsync()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.a"), "Camera A", 1);
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), withDemoSequence: false, equipmentManagement: null, startWithSettingsMode: true);
        _apps.Add((host, vm));
        await host.DeviceOperations.ConnectAsync(new DeviceId("camera.a"));
        vm.Imaging.Capture!.RefreshCapabilities();
        return vm;
    }

    [Fact]
    public async Task WhileASessionRuns_TheCaptureButtonIsOff_AndSaysWhy_AndComesBackWhenItEnds()
    {
        var vm = await CreateAsync();
        var capture = vm.Imaging.Capture!;
        Assert.True(capture.CaptureCommand.CanExecute(null));
        Assert.Equal(string.Empty, capture.DisabledText);

        var running = true;
        capture.SequenceRunning = () => running;
        capture.RefreshCapabilities();

        Assert.False(capture.CaptureCommand.CanExecute(null));
        Assert.Contains("A session is running", capture.DisabledText, StringComparison.Ordinal);

        running = false;
        capture.RefreshCapabilities();

        Assert.True(capture.CaptureCommand.CanExecute(null));
        Assert.Equal(string.Empty, capture.DisabledText);
    }

    [Fact]
    public async Task TheCameraSaysItIsDownloading_AndTheActivityShowsIt_UntilTheDownloadIsOver()
    {
        var vm = await CreateAsync();
        var camera = vm.Equipment.Cameras.Single();
        var host = _apps[0].Host;
        await host.EventBus.PublishAsync(new CameraExposureStateChanged(camera.CameraId, CameraExposureState.Idle, CameraExposureState.Exposing));

        await host.EventBus.PublishAsync(new CameraDownloadStateChanged(camera.CameraId, true));
        Assert.True(camera.IsDownloading);

        await host.EventBus.PublishAsync(new CameraDownloadStateChanged(camera.CameraId, false));
        Assert.False(camera.IsDownloading);
        Assert.DoesNotContain("Downloading", camera.ActivityText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadOfAnotherCamera_IsNotShownHere()
    {
        var vm = await CreateAsync();
        var camera = vm.Equipment.Cameras.Single();

        await _apps[0].Host.EventBus.PublishAsync(new CameraDownloadStateChanged(new DeviceId("camera.other"), true));

        Assert.False(camera.IsDownloading);
    }
}

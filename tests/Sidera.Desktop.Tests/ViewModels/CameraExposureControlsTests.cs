using Sidera.Ascom;
using Sidera.Ascom.Cameras;
using Sidera.Ascom.Tests;
using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Stop and Abort on the Camera page: only while a manual exposure runs, and only for what the camera says it can.</summary>
public sealed class CameraExposureControlsTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<(CameraViewModel Vm, FakeCameraDriver Driver, ImagingViewModel Imaging)> ConnectedCamera(Action<FakeCameraDriver> configure)
    {
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureCamera = d => { d.HoldExposure = true; configure(d); } };
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = new AscomCamera(new DeviceId("camera.ascom"), "ASCOM Camera", "ASCOM.Test.Camera", drivers, host.EventBus, null, FastTimings.Create());
        host.AddDevice(camera);
        var imaging = new ImagingViewModel();
        var vm = new CameraViewModel(camera, host, a => a(), new SessionActivity(), imaging, TimeSpan.FromSeconds(1));
        _disposables.Add(vm);
        await vm.ConnectCommand.ExecuteAsync(null);
        return (vm, drivers.Cameras[0], imaging);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task WhenIdle_StartIsEnabled_AndStopAndAbortAreNot()
    {
        var (vm, _, _) = await ConnectedCamera(d => d.CanStopExposure = true);

        Assert.True(vm.SupportsStop);
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.False(vm.StopExposureCommand.CanExecute(null));
        Assert.False(vm.CancelExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileExposing_StartIsDisabled_AndStopAndAbortAreEnabled_ForACameraThatCanStop()
    {
        var (vm, driver, _) = await ConnectedCamera(d => d.CanStopExposure = true);

        var run = vm.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => driver.Starts.Count == 1 && vm.IsExposing);

        Assert.False(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.StopExposureCommand.CanExecute(null));
        Assert.True(vm.CancelExposureCommand.CanExecute(null));
        Assert.Equal("Abort Exposure", vm.CancelButtonText);

        vm.CancelExposureCommand.Execute(null);
        await run;
        Assert.False(vm.StopExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_KeepsTheFrame_ShowsItOnTheImagingPage_AndSaysItWasStopped()
    {
        var (vm, driver, imaging) = await ConnectedCamera(d => d.CanStopExposure = true);
        var run = vm.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => driver.Starts.Count == 1 && vm.IsExposing);

        await vm.StopExposureCommand.ExecuteAsync(null);
        await run;

        Assert.NotNull(imaging.LatestFrame);
        Assert.StartsWith("Stopped after about", vm.LastFrameText);
        Assert.Equal(0, driver.Log.Calls.Count(c => c.Name == "AbortExposure"));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.False(vm.StopExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Abort_ProducesNoFrame()
    {
        var (vm, driver, imaging) = await ConnectedCamera(d => d.CanStopExposure = true);
        var run = vm.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => driver.Starts.Count == 1 && vm.IsExposing);

        vm.CancelExposureCommand.Execute(null);
        await run;

        Assert.Null(imaging.LatestFrame);
        Assert.Equal(1, driver.Log.Calls.Count(c => c.Name == "AbortExposure"));
        Assert.Equal(0, driver.Log.Calls.Count(c => c.Name == "StopExposure"));
    }

    [Fact]
    public async Task ACameraThatCannotStop_HasNoStop_EvenWhileExposing()
    {
        var (vm, driver, _) = await ConnectedCamera(d => d.CanStopExposure = false);

        var run = vm.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => driver.Starts.Count == 1 && vm.IsExposing);

        Assert.False(vm.SupportsStop);
        Assert.False(vm.StopExposureCommand.CanExecute(null));
        Assert.True(vm.CancelExposureCommand.CanExecute(null));
        vm.CancelExposureCommand.Execute(null);
        await run;
    }

    [Fact]
    public async Task ACameraThatCannotAbort_OffersCancelAndSaysSoInTheButton()
    {
        var (vm, _, _) = await ConnectedCamera(d => d.CanAbort = false);

        Assert.False(vm.SupportsAbort);
        Assert.Equal("Cancel Exposure", vm.CancelButtonText);
    }
}

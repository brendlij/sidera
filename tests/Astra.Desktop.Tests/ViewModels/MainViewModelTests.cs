using Astra.Core.Devices;
using Astra.Desktop.ViewModels;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Devices;

namespace Astra.Desktop.Tests.ViewModels;

public class MainViewModelTests
{
    private static (MainViewModel Vm, SimulatedCamera Camera) Create(
        TimeSpan? exposure = null,
        TimeSpan? sequenceExposure = null,
        bool registerCamera = true
    )
    {
        var host = new AstraRuntimeHost();
        var camera = registerCamera
            ? host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera")
            : new SimulatedCamera(new DeviceId("camera.main"), "Main Camera", host.EventBus);
        var vm = new MainViewModel(camera, host, action => action(), exposure, sequenceExposure);
        return (vm, camera);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(10);
        }
    }

    private static void AssertManualControlsAvailableForConnectedCamera(MainViewModel vm)
    {
        Assert.False(vm.IsSequenceRunning);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.RunSequenceCommand.CanExecute(null));
        Assert.False(vm.CancelSequenceCommand.CanExecute(null));
    }

    private static async Task<(MainViewModel Vm, SimulatedCamera Camera)> CreateConnected(
        TimeSpan? sequenceExposure = null,
        bool registerCamera = true
    )
    {
        var (vm, camera) = Create(sequenceExposure: sequenceExposure, registerCamera: registerCamera);
        await vm.ConnectCommand.ExecuteAsync(null);
        return (vm, camera);
    }

    [Fact]
    public async Task RunSequence_IsOnlyAvailableWhenCameraIsConnectedAndIdle()
    {
        var (vm, _) = Create();
        Assert.False(vm.RunSequenceCommand.CanExecute(null));

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.RunSequenceCommand.CanExecute(null));

        await vm.DisconnectCommand.ExecuteAsync(null);
        Assert.False(vm.RunSequenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task RunSequence_ExecutesThreeExposuresAndLeavesCameraConnected()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30));

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Completed, vm.SequenceState);
        Assert.Equal(3, vm.SequenceStepCount);
        Assert.Equal("Step 3 / 3", vm.SequenceStepText);
        Assert.Equal("Exposure 0.03s", vm.CurrentStepName);
        Assert.Null(vm.SequenceError);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }

    [Fact]
    public async Task Sequence_ReflectsStateAndStepWhileRunning_AndDisablesManualControls()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SequenceStepText == "Step 2 / 3");

        Assert.Equal(SequenceState.Running, vm.SequenceState);
        Assert.Equal("Exposure 0.4s", vm.CurrentStepName);
        Assert.True(vm.IsSequenceRunning);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
        Assert.False(vm.RunSequenceCommand.CanExecute(null));
        Assert.True(vm.CancelSequenceCommand.CanExecute(null));

        await run;
    }

    [Fact]
    public async Task Sequence_ShowsEveryStepInOrderStartingAtOne()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30));
        var shown = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SequenceStepText))
            {
                shown.Add(vm.SequenceStepText);
            }
        };

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Step 1 / 3", "Step 2 / 3", "Step 3 / 3" }, shown);
    }

    [Fact]
    public async Task CancelSequence_StopsRunAndRestoresControls()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromSeconds(10));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SequenceStepText == "Step 1 / 3" && camera.ExposureState == CameraExposureState.Exposing);
        vm.CancelSequenceCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Cancelled, vm.SequenceState);
        Assert.Null(vm.SequenceError);
        Assert.False(vm.IsSequenceRunning);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        // The cancelled sequence left the camera connected; normal camera-state rules apply again.
        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.RunSequenceCommand.CanExecute(null));
        Assert.False(vm.CancelSequenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedSequence_SurfacesErrorWithoutThrowing_AndRecovers()
    {
        // Connected manually, but not registered, so the sequence cannot find the camera.
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30), registerCamera: false);

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Failed, vm.SequenceState);
        Assert.Contains("is not registered", vm.SequenceError);
        Assert.Equal("Step 1 / 3", vm.SequenceStepText);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }


    [Fact]
    public void InitialState_OnlyConnectIsEnabled()
    {
        var (vm, _) = Create();

        Assert.Equal("Main Camera", vm.CameraName);
        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, vm.ExposureState);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_UpdatesStateAndEnablesCommands()
    {
        var (vm, _) = Create();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_ReturnsToDisconnected()
    {
        var (vm, _) = Create();
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_ShowsExposingWhileRunningThenIdle()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(300));
        await vm.ConnectCommand.ExecuteAsync(null);

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.Equal(CameraExposureState.Exposing, vm.ExposureState);
        Assert.False(vm.StartExposureCommand.CanExecute(null));

        await running;

        Assert.Equal(CameraExposureState.Idle, vm.ExposureState);
        Assert.True(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_IsDisabledWhileExposing()
    {
        var (vm, camera) = Create(TimeSpan.FromMilliseconds(300));
        await vm.ConnectCommand.ExecuteAsync(null);

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.False(vm.DisconnectCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        await running;

        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_UpdatesProgressProperties()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(700));
        await vm.ConnectCommand.ExecuteAsync(null);
        var progressSeen = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ExposureProgress))
            {
                progressSeen.Add(vm.ExposureProgress);
            }
        };

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.True(vm.IsExposing);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.ExposureDuration);

        await running;

        Assert.Contains(progressSeen, p => p > 0.0 && p < 1.0);
        Assert.Equal(1.0, vm.ExposureProgress);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.ExposureElapsed);
        Assert.False(vm.IsExposing);
    }

    [Fact]
    public async Task CompletedExposure_StoresFrame()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(50));
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Null(vm.LastFrame);

        await vm.StartExposureCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastFrame);
        Assert.Equal(800, vm.LastFrame!.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(50), vm.LastFrame.ExposureDuration);
    }

    [Fact]
    public async Task ConnectionChanges_AreMarshalledThroughUiDispatcher()
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var posted = new List<Action>();
        var vm = new MainViewModel(camera, host, posted.Add);

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.NotEmpty(posted);

        posted.ForEach(a => a());

        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
    }
}

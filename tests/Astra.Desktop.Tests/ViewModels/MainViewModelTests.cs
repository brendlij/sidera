using Astra.Core.Devices;
using Astra.Desktop.ViewModels;
using Astra.Runtime.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.State;

namespace Astra.Desktop.Tests.ViewModels;

public class MainViewModelTests
{
    private static (MainViewModel Vm, SimulatedCamera Camera) Create(TimeSpan? exposure = null)
    {
        var bus = new EventBus();
        var store = new StateStore(bus);
        var camera = new SimulatedCamera(new DeviceId("camera.main"), "Main Camera", bus);
        var vm = new MainViewModel(camera, bus, store, action => action(), exposure);
        return (vm, camera);
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
    public async Task ConnectionChanges_AreMarshalledThroughUiDispatcher()
    {
        var bus = new EventBus();
        var store = new StateStore(bus);
        var camera = new SimulatedCamera(new DeviceId("camera.main"), "Main Camera", bus);
        var posted = new List<Action>();
        var vm = new MainViewModel(camera, bus, store, posted.Add);

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.NotEmpty(posted);

        posted.ForEach(a => a());

        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
    }
}

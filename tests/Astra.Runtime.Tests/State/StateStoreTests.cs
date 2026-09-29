using Astra.Core.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.State;

namespace Astra.Runtime.Tests.State;

public class StateStoreTests
{
    private static readonly DeviceId Cam1 = new("cam-1");
    private static readonly DeviceId Cam2 = new("cam-2");

    private static Task Publish(EventBus bus, DeviceId id, DeviceConnectionState from, DeviceConnectionState to)
    {
        return bus.PublishAsync(new DeviceConnectionStateChanged(id, from, to));
    }

    [Fact]
    public async Task State_IsCreatedAfterConnectionStateEvent()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);

        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting);

        Assert.True(store.TryGet(Cam1, out var state));
        Assert.Equal(new DeviceState(Cam1, DeviceConnectionState.Connecting), state);
    }

    [Fact]
    public async Task NewerEvent_ReplacesPreviousState()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);

        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting);
        await Publish(bus, Cam1, DeviceConnectionState.Connecting, DeviceConnectionState.Connected);

        Assert.True(store.TryGet(Cam1, out var state));
        Assert.Equal(DeviceConnectionState.Connected, state!.ConnectionState);
        Assert.Single(store.GetAll());
    }

    [Fact]
    public async Task Devices_AreTrackedIndependently()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);

        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connected);
        await Publish(bus, Cam2, DeviceConnectionState.Disconnected, DeviceConnectionState.Faulted);

        Assert.True(store.TryGet(Cam1, out var state1));
        Assert.True(store.TryGet(Cam2, out var state2));
        Assert.Equal(DeviceConnectionState.Connected, state1!.ConnectionState);
        Assert.Equal(DeviceConnectionState.Faulted, state2!.ConnectionState);
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownDevice()
    {
        using var store = new StateStore(new EventBus());

        Assert.False(store.TryGet(Cam1, out var state));
        Assert.Null(state);
    }

    [Fact]
    public async Task GetAll_ReturnsCurrentStates()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);
        Assert.Empty(store.GetAll());

        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting);
        await Publish(bus, Cam1, DeviceConnectionState.Connecting, DeviceConnectionState.Connected);
        await Publish(bus, Cam2, DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting);

        Assert.Equivalent(
            new[]
            {
                new DeviceState(Cam1, DeviceConnectionState.Connected),
                new DeviceState(Cam2, DeviceConnectionState.Connecting),
            },
            store.GetAll());
    }

    [Fact]
    public async Task ExposureEvents_UpdateExposureStateAndKeepConnectionState()
    {
        var bus = new EventBus();
        using var store = new StateStore(bus);
        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connected);

        await bus.PublishAsync(new CameraExposureStateChanged(Cam1, CameraExposureState.Idle, CameraExposureState.Exposing));

        Assert.True(store.TryGet(Cam1, out var exposing));
        Assert.Equal(new DeviceState(Cam1, DeviceConnectionState.Connected, CameraExposureState.Exposing), exposing);

        await bus.PublishAsync(new CameraExposureStateChanged(Cam1, CameraExposureState.Exposing, CameraExposureState.Idle));
        await Publish(bus, Cam1, DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting);

        Assert.True(store.TryGet(Cam1, out var idle));
        Assert.Equal(new DeviceState(Cam1, DeviceConnectionState.Disconnecting, CameraExposureState.Idle), idle);
    }

    [Fact]
    public async Task Dispose_StopsUpdates()
    {
        var bus = new EventBus();
        var store = new StateStore(bus);
        store.Dispose();

        await Publish(bus, Cam1, DeviceConnectionState.Disconnected, DeviceConnectionState.Connected);

        Assert.False(store.TryGet(Cam1, out _));
    }
}

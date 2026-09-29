using Astra.Core.Devices;
using Astra.Runtime.Devices;
using Astra.Runtime.Events;

namespace Astra.Runtime.Tests.Devices;

public class SimulatedCameraTests
{
    private static (SimulatedCamera Camera, List<DeviceConnectionStateChanged> Events) Create()
    {
        var bus = new EventBus();
        var events = new List<DeviceConnectionStateChanged>();
        bus.Subscribe<DeviceConnectionStateChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });

        return (new SimulatedCamera(new DeviceId("cam-1"), events: bus), events);
    }

    [Fact]
    public async Task ConnectAndDisconnect_PublishStateChanges()
    {
        var (camera, events) = Create();

        await camera.ConnectAsync();
        await camera.DisconnectAsync();

        Assert.Equal(
            new[]
            {
                (DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting),
                (DeviceConnectionState.Connecting, DeviceConnectionState.Connected),
                (DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting),
                (DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            },
            events.Select(e => (e.PreviousState, e.NewState)));
        Assert.All(events, e => Assert.Equal(camera.Id, e.DeviceId));
    }

    [Fact]
    public async Task CancelledConnect_PublishesReturnToDisconnected()
    {
        var (camera, events) = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => camera.ConnectAsync(cts.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        var last = events[^1];
        Assert.Equal(DeviceConnectionState.Connecting, last.PreviousState);
        Assert.Equal(DeviceConnectionState.Disconnected, last.NewState);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotAffectConnectOrDisconnect()
    {
        var failures = new List<EventHandlerFailure>();
        var bus = new EventBus(failures.Add);
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => throw new InvalidOperationException("boom"));
        var camera = new SimulatedCamera(new DeviceId("cam-1"), events: bus);

        await camera.ConnectAsync();
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);

        await camera.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        Assert.Equal(4, failures.Count);
    }

    private static async Task<(SimulatedCamera Camera, List<CameraExposureStateChanged> Events)> CreateConnected()
    {
        var bus = new EventBus();
        var events = new List<CameraExposureStateChanged>();
        bus.Subscribe<CameraExposureStateChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });
        var camera = new SimulatedCamera(new DeviceId("cam-1"), events: bus);
        await camera.ConnectAsync();
        return (camera, events);
    }

    [Fact]
    public async Task Exposure_PublishesEventWhenStarting()
    {
        var (camera, events) = await CreateConnected();

        var exposure = camera.ExposeAsync(TimeSpan.FromMilliseconds(200));

        var started = Assert.Single(events);
        Assert.Equal(CameraExposureState.Idle, started.PreviousState);
        Assert.Equal(CameraExposureState.Exposing, started.NewState);
        Assert.Equal(camera.Id, started.DeviceId);
        await exposure;
    }

    [Fact]
    public async Task Exposure_PublishesEventWhenFinishing()
    {
        var (camera, events) = await CreateConnected();

        await camera.ExposeAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            new[]
            {
                (CameraExposureState.Idle, CameraExposureState.Exposing),
                (CameraExposureState.Exposing, CameraExposureState.Idle),
            },
            events.Select(e => (e.PreviousState, e.NewState)));
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }

    [Fact]
    public async Task CancelledExposure_ReturnsToIdleAndPublishes()
    {
        var (camera, events) = await CreateConnected();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            camera.ExposeAsync(TimeSpan.FromSeconds(10), cts.Token));

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureState.Exposing, events[^2].NewState);
        Assert.Equal(CameraExposureState.Idle, events[^1].NewState);
        Assert.Equal(CameraExposureState.Exposing, events[^1].PreviousState);
    }

    [Fact]
    public async Task Disconnect_WhileExposing_IsRejected()
    {
        var (camera, _) = await CreateConnected();
        var exposure = camera.ExposeAsync(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        await exposure;
        await camera.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task WithoutPublisher_ConnectStillWorks()
    {
        var camera = new SimulatedCamera(new DeviceId("cam-1"));

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }
}

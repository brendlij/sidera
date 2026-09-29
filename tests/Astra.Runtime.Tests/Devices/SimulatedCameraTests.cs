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

    [Fact]
    public async Task WithoutPublisher_ConnectStillWorks()
    {
        var camera = new SimulatedCamera(new DeviceId("cam-1"));

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }
}

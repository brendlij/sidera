using Astra.Core.Devices;

namespace Astra.Runtime.Tests;

public class AstraRuntimeHostTests
{
    private sealed class FakeDevice(string id, DeviceConnectionState state, bool failOnDisconnect = false) : IDevice
    {
        public DeviceId Id { get; } = new(id);
        public string Name => "Fake";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState { get; private set; } = state;
        public int DisconnectCalls { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCalls++;
            if (failOnDisconnect)
            {
                throw new InvalidOperationException("cannot disconnect");
            }

            ConnectionState = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Start_SucceedsOnceAndRejectsSecondStart()
    {
        await using var host = new AstraRuntimeHost();

        host.Start();

        Assert.Throws<InvalidOperationException>(host.Start);
    }

    [Fact]
    public async Task AddDevice_MakesDeviceAvailableThroughRegistry()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");

        Assert.True(host.DeviceRegistry.TryGet(new DeviceId("camera.main"), out var device));
        Assert.Same(camera, device);
        Assert.Throws<InvalidOperationException>(() =>
            host.AddDevice(new FakeDevice("camera.main", DeviceConnectionState.Disconnected)));
    }

    [Fact]
    public async Task SimulatedCamera_PublishesIntoHostStateStore()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");

        await camera.ConnectAsync();

        Assert.True(host.StateStore.TryGet(camera.Id, out var state));
        Assert.Equal(DeviceConnectionState.Connected, state!.ConnectionState);
    }

    [Fact]
    public async Task Stop_DisconnectsConnectedCamera()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.Start();
        await camera.ConnectAsync();

        await host.StopAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task Stop_DoesNotTouchAlreadyDisconnectedDevices()
    {
        await using var host = new AstraRuntimeHost();
        var idle = new FakeDevice("idle", DeviceConnectionState.Disconnected);
        var connected = new FakeDevice("connected", DeviceConnectionState.Connected);
        host.AddDevice(idle);
        host.AddDevice(connected);
        host.Start();

        await host.StopAsync();

        Assert.Equal(0, idle.DisconnectCalls);
        Assert.Equal(1, connected.DisconnectCalls);
    }

    [Fact]
    public async Task Stop_ContinuesAfterFailingDeviceAndReportsFailure()
    {
        await using var host = new AstraRuntimeHost();
        var failing = new FakeDevice("failing", DeviceConnectionState.Connected, failOnDisconnect: true);
        var healthy = new FakeDevice("healthy", DeviceConnectionState.Connected);
        host.AddDevice(failing);
        host.AddDevice(healthy);
        host.Start();

        var error = await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync());

        Assert.Single(error.InnerExceptions);
        Assert.Equal(1, failing.DisconnectCalls);
        Assert.Equal(1, healthy.DisconnectCalls);
        Assert.Equal(DeviceConnectionState.Disconnected, healthy.ConnectionState);
    }

    [Fact]
    public async Task Stop_IsNoOpWhenNotStartedOrAlreadyStopped()
    {
        await using var host = new AstraRuntimeHost();
        var device = new FakeDevice("connected", DeviceConnectionState.Connected);
        host.AddDevice(device);

        await host.StopAsync();
        Assert.Equal(0, device.DisconnectCalls);

        host.Start();
        await host.StopAsync();
        await host.StopAsync();
        Assert.Equal(1, device.DisconnectCalls);
    }

    [Fact]
    public async Task Dispose_StopsHostReleasesSubscriptionsAndIsIdempotent()
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.Start();
        await camera.ConnectAsync();

        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);

        // The store no longer listens: a later event does not change it.
        var before = host.StateStore.GetAll();
        await host.EventBus.PublishAsync(new DeviceConnectionStateChanged(
            new DeviceId("other"), DeviceConnectionState.Disconnected, DeviceConnectionState.Connected));
        Assert.Equal(before.Count, host.StateStore.GetAll().Count);
        Assert.False(host.StateStore.TryGet(new DeviceId("other"), out _));

        Assert.Throws<ObjectDisposedException>(() =>
            host.AddDevice(new FakeDevice("late", DeviceConnectionState.Disconnected)));
    }
}

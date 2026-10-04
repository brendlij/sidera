using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests;

public class SideraRuntimeHostTests
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
        await using var host = new SideraRuntimeHost();

        host.Start();

        Assert.Throws<InvalidOperationException>(host.Start);
    }

    [Fact]
    public async Task AddDevice_MakesDeviceAvailableThroughRegistry()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");

        Assert.True(host.DeviceRegistry.TryGet(new DeviceId("camera.main"), out var device));
        Assert.Same(camera, device);
        Assert.Throws<InvalidOperationException>(() =>
            host.AddDevice(new FakeDevice("camera.main", DeviceConnectionState.Disconnected)));
    }

    private static Rig MakeRig(string rigId, string cameraId) => new(
        new RigId(rigId),
        rigId,
        new DeviceId(cameraId),
        new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176));

    [Fact]
    public async Task Host_ExposesRigRegistry()
    {
        await using var host = new SideraRuntimeHost();

        Assert.NotNull(host.RigRegistry);
        Assert.Empty(host.RigRegistry.GetAll());
    }

    [Fact]
    public async Task AddRig_MakesRigRetrievable_AndValidatesAgainstHostDevices()
    {
        await using var host = new SideraRuntimeHost();

        Assert.Throws<InvalidOperationException>(() => host.AddRig(MakeRig("rig.main", "camera.main")));

        host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.AddRig(MakeRig("rig.main", "camera.main"));

        Assert.True(host.RigRegistry.TryGet(new RigId("rig.main"), out var rig));
        Assert.Equal(new DeviceId("camera.main"), rig!.CameraId);
    }

    [Fact]
    public async Task Host_CanHoldMultipleRigs()
    {
        await using var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.AddSimulatedCamera(new DeviceId("camera.wide"), "Wide Camera");

        host.AddRig(MakeRig("rig.main", "camera.main"));
        host.AddRig(MakeRig("rig.wide", "camera.wide"));

        Assert.Equal(2, host.RigRegistry.GetAll().Count);
    }

    [Fact]
    public async Task AddRig_AfterDispose_IsRejected()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        await host.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => host.AddRig(MakeRig("rig.main", "camera.main")));
    }

    [Fact]
    public async Task SimulatedCamera_PublishesIntoHostStateStore()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");

        await camera.ConnectAsync();

        Assert.True(host.StateStore.TryGet(camera.Id, out var state));
        Assert.Equal(DeviceConnectionState.Connected, state!.ConnectionState);
    }

    [Fact]
    public async Task Stop_DisconnectsConnectedCamera()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        host.Start();
        await camera.ConnectAsync();

        await host.StopAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task Stop_DoesNotTouchAlreadyDisconnectedDevices()
    {
        await using var host = new SideraRuntimeHost();
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
        await using var host = new SideraRuntimeHost();
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
        await using var host = new SideraRuntimeHost();
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
        var host = new SideraRuntimeHost();
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

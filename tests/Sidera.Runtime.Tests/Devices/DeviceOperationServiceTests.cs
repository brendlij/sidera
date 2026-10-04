using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Devices;

public class DeviceOperationServiceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);

    private sealed class Harness
    {
        public Harness()
        {
            Registry = new DeviceRegistry();
            Resources = new ResourceManager();
            Operations = new DeviceOperationService(Registry, Resources);
        }

        public DeviceRegistry Registry { get; }
        public ResourceManager Resources { get; }
        public DeviceOperationService Operations { get; }

        public FakeCamera AddCamera(string id, bool block = false)
        {
            var camera = new FakeCamera(id) { Block = block };
            Registry.Register(camera);
            return camera;
        }

        public SequenceRunner Runner() => new(Resources);

        public Sequence ExposureSequence(string cameraId) =>
            new("s", [new CameraExposureAction(Registry, new DeviceId(cameraId), Short)]);
    }

    private sealed class NotACamera : IDevice
    {
        public DeviceId Id { get; } = new("focuser.main");
        public string Name => "Focuser";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task WaitForWaiters(ResourceManager manager, int count)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (manager.WaitingCount != count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} waiting requests, have {manager.WaitingCount}.");
            await Task.Delay(5);
        }
    }

    private static ResourceId Resource(string id) => ResourceId.ForDevice(new DeviceId(id));

    // Basic behavior

    [Fact]
    public async Task Connect_ResolvesDeviceAndConnectsIt()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main");

        await rig.Operations.ConnectAsync(new DeviceId("camera.main"));

        Assert.Equal(1, camera.ConnectCalls);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    [Fact]
    public async Task Disconnect_ResolvesDeviceAndDisconnectsIt()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main");
        await rig.Operations.ConnectAsync(camera.Id);

        await rig.Operations.DisconnectAsync(camera.Id);

        Assert.Equal(1, camera.DisconnectCalls);
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    [Fact]
    public async Task Expose_ReturnsFrame_AndReleasesResource()
    {
        var rig = new Harness();
        rig.AddCamera("camera.main");

        var frame = await rig.Operations.ExposeAsync(new DeviceId("camera.main"), TimeSpan.FromSeconds(3));

        Assert.Equal(TimeSpan.FromSeconds(3), frame.ExposureDuration);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    [Fact]
    public async Task UnknownDevice_FailsClearly_ForEveryOperation()
    {
        var rig = new Harness();
        var id = new DeviceId("nope");

        var connect = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.ConnectAsync(id));
        var disconnect = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.DisconnectAsync(id));
        var expose = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.ExposeAsync(id, Short));

        Assert.Contains("'nope' is not registered", connect.Message);
        Assert.Contains("'nope' is not registered", disconnect.Message);
        Assert.Contains("'nope' is not registered", expose.Message);
        Assert.False(rig.Resources.IsHeld(Resource("nope")));
    }

    [Fact]
    public async Task Expose_OnNonCamera_FailsClearly()
    {
        var rig = new Harness();
        rig.Registry.Register(new NotACamera());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Operations.ExposeAsync(new DeviceId("focuser.main"), Short));

        Assert.Contains("'focuser.main' is not a camera", error.Message);
        Assert.False(rig.Resources.IsHeld(Resource("focuser.main")));
    }

    [Fact]
    public async Task InvalidExposureRules_StillComeFromTheCamera()
    {
        var rig = new Harness();
        var camera = new SimulatedCamera(new DeviceId("camera.sim"));
        rig.Registry.Register(camera);

        // Not connected: the camera rejects it, the service adds no rules of its own.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.ExposeAsync(camera.Id, Short));

        await rig.Operations.ConnectAsync(camera.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Operations.ExposeAsync(camera.Id, TimeSpan.Zero));
        Assert.False(rig.Resources.IsHeld(Resource("camera.sim")));
    }

    [Fact]
    public async Task DeviceFailure_PropagatesOriginalException_AndResourceIsReleased()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main");
        var failure = new InvalidOperationException("device broke");
        camera.Failure = failure;

        var connect = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.ConnectAsync(camera.Id));
        var expose = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Operations.ExposeAsync(camera.Id, Short));

        Assert.Same(failure, connect);
        Assert.Same(failure, expose);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));

        // Later callers can still use the device.
        camera.Failure = null;
        await rig.Operations.ExposeAsync(camera.Id, Short);
        Assert.Equal(2, camera.ExposeCalls);
    }

    [Fact]
    public async Task CancellationWhileWaiting_PreventsTheOperation_AndLeavesQueueClean()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        var first = rig.Operations.ExposeAsync(camera.Id, Short);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        using var cts = new CancellationTokenSource();

        var second = rig.Operations.ExposeAsync(camera.Id, Short, cts.Token);
        await WaitForWaiters(rig.Resources, 1);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync(Bound));
        Assert.Equal(1, camera.ExposeCalls); // the cancelled call never started
        Assert.Equal(0, rig.Resources.WaitingCount);
        Assert.True(rig.Resources.IsHeld(Resource("camera.main"))); // still the first call's

        camera.GateOf("expose", 1).Release.SetResult();
        await first.WaitAsync(Bound);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    [Fact]
    public async Task CancellationDuringOperation_ReleasesTheResource()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        using var cts = new CancellationTokenSource();

        var exposure = rig.Operations.ExposeAsync(camera.Id, Short, cts.Token);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        Assert.True(rig.Resources.IsHeld(Resource("camera.main")));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure.WaitAsync(Bound));
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    // Manual path and sequence path share one ResourceManager

    [Fact]
    public async Task ManualExposure_BlocksSequenceExposureOnTheSameCamera_UntilItReleases()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        var runner = rig.Runner();

        var manual = rig.Operations.ExposeAsync(camera.Id, Short);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        var sequence = runner.RunAsync(rig.ExposureSequence("camera.main"));
        await WaitForWaiters(rig.Resources, 1);

        Assert.Equal(1, camera.ExposeCalls); // only the manual one has reached the device
        Assert.Equal(SequenceState.Running, runner.State);

        camera.GateOf("expose", 1).Release.SetResult();
        await manual.WaitAsync(Bound);
        await camera.GateOf("expose", 2).Started.Task.WaitAsync(Bound); // the sequence proceeds
        camera.GateOf("expose", 2).Release.SetResult();
        await sequence.WaitAsync(Bound);

        Assert.Equal(2, camera.ExposeCalls);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    [Fact]
    public async Task SequenceExposure_BlocksManualExposureOnTheSameCamera_UntilItReleases()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        var runner = rig.Runner();

        var sequence = runner.RunAsync(rig.ExposureSequence("camera.main"));
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        var manual = rig.Operations.ExposeAsync(camera.Id, Short);
        await WaitForWaiters(rig.Resources, 1);

        Assert.Equal(1, camera.ExposeCalls);
        Assert.False(manual.IsCompleted);

        camera.GateOf("expose", 1).Release.SetResult();
        await sequence.WaitAsync(Bound);
        await camera.GateOf("expose", 2).Started.Task.WaitAsync(Bound);
        camera.GateOf("expose", 2).Release.SetResult();
        var frame = await manual.WaitAsync(Bound);

        Assert.NotNull(frame);
        Assert.Equal(2, camera.ExposeCalls);
    }

    [Fact]
    public async Task ManualOnOneCamera_AndSequenceOnAnother_RunConcurrently()
    {
        var rig = new Harness();
        var main = rig.AddCamera("camera.main", block: true);
        var wide = rig.AddCamera("camera.wide", block: true);
        var runner = rig.Runner();

        var manual = rig.Operations.ExposeAsync(main.Id, Short);
        var sequence = runner.RunAsync(rig.ExposureSequence("camera.wide"));

        // Both reached their devices while the other was still running.
        await Task.WhenAll(
            main.GateOf("expose", 1).Started.Task,
            wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);
        Assert.Equal(0, rig.Resources.WaitingCount);

        main.GateOf("expose", 1).Release.SetResult();
        wide.GateOf("expose", 1).Release.SetResult();
        await Task.WhenAll(manual, sequence).WaitAsync(Bound);
    }

    [Fact]
    public async Task ManualDisconnect_WaitsForActiveSequenceExposure()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        var runner = rig.Runner();

        var sequence = runner.RunAsync(rig.ExposureSequence("camera.main"));
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        var disconnect = rig.Operations.DisconnectAsync(camera.Id);
        await WaitForWaiters(rig.Resources, 1);

        Assert.Equal(0, camera.DisconnectCalls);

        camera.GateOf("expose", 1).Release.SetResult();
        await sequence.WaitAsync(Bound);
        await camera.GateOf("disconnect", 1).Started.Task.WaitAsync(Bound);
        camera.GateOf("disconnect", 1).Release.SetResult();
        await disconnect.WaitAsync(Bound);

        Assert.Equal(1, camera.DisconnectCalls);
    }

    [Fact]
    public async Task ConnectAndDisconnect_ShareTheSameDeviceResource()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);

        var connect = rig.Operations.ConnectAsync(camera.Id);
        await camera.GateOf("connect", 1).Started.Task.WaitAsync(Bound);
        Assert.True(rig.Resources.IsHeld(Resource("camera.main")));

        var disconnect = rig.Operations.DisconnectAsync(camera.Id);
        await WaitForWaiters(rig.Resources, 1);
        Assert.Equal(0, camera.DisconnectCalls); // waits for connect to finish

        camera.GateOf("connect", 1).Release.SetResult();
        await connect.WaitAsync(Bound);
        await camera.GateOf("disconnect", 1).Started.Task.WaitAsync(Bound);
        camera.GateOf("disconnect", 1).Release.SetResult();
        await disconnect.WaitAsync(Bound);
    }

    [Fact]
    public async Task CancelledWaiter_LeavesTheQueueClean_AndTheNextOperationProceeds()
    {
        var rig = new Harness();
        var camera = rig.AddCamera("camera.main", block: true);
        var holder = rig.Operations.ExposeAsync(camera.Id, Short);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        using var cts = new CancellationTokenSource();

        var cancelled = rig.Operations.ConnectAsync(camera.Id, cts.Token);
        var next = rig.Operations.DisconnectAsync(camera.Id);
        await WaitForWaiters(rig.Resources, 2);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(1, rig.Resources.WaitingCount);

        camera.GateOf("expose", 1).Release.SetResult();
        await holder.WaitAsync(Bound);
        await camera.GateOf("disconnect", 1).Started.Task.WaitAsync(Bound);
        camera.GateOf("disconnect", 1).Release.SetResult();
        await next.WaitAsync(Bound);

        Assert.Equal(0, camera.ConnectCalls);
        Assert.Equal(0, rig.Resources.WaitingCount);
        Assert.False(rig.Resources.IsHeld(Resource("camera.main")));
    }

    // Host

    [Fact]
    public async Task Host_ExposesDeviceOperations()
    {
        await using var host = new SideraRuntimeHost();

        Assert.NotNull(host.DeviceOperations);
    }

    [Fact]
    public async Task HostDeviceOperationsAndSequenceRunner_CoordinateThroughTheHostResourceManager()
    {
        await using var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main") { Block = true };
        host.AddDevice(camera);
        var runner = new SequenceRunner(host.ResourceManager);

        var manual = host.DeviceOperations.ExposeAsync(camera.Id, Short);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        var sequence = runner.RunAsync(new Sequence("s", [new CameraExposureAction(host.DeviceRegistry, camera.Id, Short)]));
        await WaitForWaiters(host.ResourceManager, 1);
        Assert.Equal(1, camera.ExposeCalls);

        camera.GateOf("expose", 1).Release.SetResult();
        await manual.WaitAsync(Bound);
        await camera.GateOf("expose", 2).Started.Task.WaitAsync(Bound);
        camera.GateOf("expose", 2).Release.SetResult();
        await sequence.WaitAsync(Bound);

        Assert.Equal(2, camera.ExposeCalls);
    }
}

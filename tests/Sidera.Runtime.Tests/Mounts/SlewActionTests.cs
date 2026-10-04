using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Mounts;

public class SlewActionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);

    private static readonly CelestialCoordinates Target = new(5.5, 22.5);
    private static readonly ResourceId MountResource = ResourceId.ForDevice(new DeviceId("mount.eq6"));

    private sealed class NotAMount : IDevice
    {
        public DeviceId Id { get; } = new("focuser.main");
        public string Name => "Focuser";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static SlewAction Slew(SideraRuntimeHost host, string mountId = "mount.eq6") =>
        new(host.DeviceRegistry, new DeviceId(mountId), Target);

    private static Task<SequenceStepResult> Execute(SlewAction action, CancellationToken ct = default) =>
        action.ExecuteAsync(NoContext.Instance, ct);

    // The action itself

    [Fact]
    public async Task Action_DeclaresTheMountDeviceResource_AndANameThatShowsTheTarget()
    {
        await using var host = new SideraRuntimeHost();
        var action = Slew(host);

        Assert.Equal(new[] { new ResourceId("device:mount.eq6") }, action.RequiredResources);
        Assert.Equal("Slew to RA 5.5h Dec +22.5°", action.Name);
        Assert.Same(Target, action.Target);
    }

    [Fact]
    public async Task Action_RejectsUnknownMount()
    {
        await using var host = new SideraRuntimeHost();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Slew(host)));

        Assert.Contains("'mount.eq6' is not registered", error.Message);
    }

    [Fact]
    public async Task Action_RejectsNonMountDevice()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAMount());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Slew(host, "focuser.main")));

        Assert.Contains("'focuser.main' is not a mount", error.Message);
    }

    [Fact]
    public async Task Action_RejectsDisconnectedMount_AndDoesNotConnectIt()
    {
        await using var host = new SideraRuntimeHost();
        var mount = host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6", Quick);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Execute(Slew(host)));

        Assert.Contains("'mount.eq6' is not connected", error.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, mount.ConnectionState);
    }

    [Fact]
    public async Task Action_SlewsAndReturnsResultWithoutPayload()
    {
        await using var host = new SideraRuntimeHost();
        var mount = host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6", Quick);
        await mount.ConnectAsync();

        var result = await Execute(Slew(host));

        Assert.Null(result.Payload);
        Assert.Equal(Target, mount.Coordinates);
        Assert.Equal(MountMotionState.Tracking, mount.MotionState);
    }

    [Fact]
    public async Task Action_CancellationStopsTheSlew()
    {
        await using var host = new SideraRuntimeHost();
        var mount = host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6", TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(Slew(host), cts.Token));

        Assert.Equal(MountMotionState.Idle, mount.MotionState);
        Assert.Equal(SimulatedMount.DefaultCoordinates, mount.Coordinates);
    }

    [Fact]
    public async Task FailingSlew_ReleasesTheMountResource_ThroughTheRunner()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Failure = new InvalidOperationException("motor stalled") };
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Slew(host)])));

        Assert.Equal("motor stalled", error.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(MountResource));
    }

    [Fact]
    public async Task CancelledSlew_ReleasesTheMountResource_ThroughTheRunner()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [Slew(host)]), cts.Token);
        await mount.GateOf(1).Started.Task.WaitAsync(Bound);
        Assert.True(host.ResourceManager.IsHeld(MountResource));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(host.ResourceManager.IsHeld(MountResource));
    }

    // Shared mount and parallel branches

    [Fact]
    public async Task TwoSlewsOnTheSameMount_Serialize_TheSecondWaitsInTheResourceManager()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [Slew(host), Slew(host)]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await mount.GateOf(1).Started.Task.WaitAsync(Bound);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second slew waiting for the mount");
        Assert.Equal(1, mount.SlewCalls); // the second has not reached the mount

        mount.GateOf(1).Release.SetResult();
        await mount.GateOf(2).Started.Task.WaitAsync(Bound); // proceeds after the first released
        mount.GateOf(2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(2, mount.SlewCalls);
        Assert.False(host.ResourceManager.IsHeld(MountResource));
    }

    [Fact]
    public async Task SlewsOnTwoDifferentMounts_RunConcurrently()
    {
        await using var host = new SideraRuntimeHost();
        var first = new FakeMount("mount.eq6") { Block = true };
        var second = new FakeMount("mount.az") { Block = true };
        host.AddDevice(first);
        host.AddDevice(second);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [Slew(host, "mount.eq6"), Slew(host, "mount.az")]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await Task.WhenAll(first.GateOf(1).Started.Task, second.GateOf(1).Started.Task).WaitAsync(Bound);
        Assert.Equal(0, host.ResourceManager.WaitingCount);

        first.GateOf(1).Release.SetResult();
        second.GateOf(1).Release.SetResult();
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task ExposureAndSlew_CanCurrentlyRunConcurrently_BecauseTheyRequireDifferentResources()
    {
        // Expected for now: an exposure does not require the mount. Stopping a slew (or dither) from disturbing
        // running exposures is a safe-point problem between branches, not something this lock should solve.
        await using var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main") { Block = true };
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(camera);
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Quick),
            Slew(host),
        ]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await Task.WhenAll(camera.GateOf("expose", 1).Started.Task, mount.GateOf(1).Started.Task).WaitAsync(Bound);
        Assert.Equal(0, host.ResourceManager.WaitingCount);
        Assert.True(host.ResourceManager.IsHeld(ResourceId.ForDevice(camera.Id)));
        Assert.True(host.ResourceManager.IsHeld(MountResource));

        camera.GateOf("expose", 1).Release.SetResult();
        mount.GateOf(1).Release.SetResult();
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task ParallelTwoCamerasAndAMount_FollowTheResourceIds()
    {
        await using var host = new SideraRuntimeHost();
        var main = new FakeCamera("camera.main") { Block = true };
        var wide = new FakeCamera("camera.wide") { Block = true };
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(main);
        host.AddDevice(wide);
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, main.Id, Quick),
            new CameraExposureAction(host.DeviceRegistry, wide.Id, Quick),
            Slew(host),
            Slew(host), // same mount as the third branch: must wait for it
        ]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await Task.WhenAll(
            main.GateOf("expose", 1).Started.Task,
            wide.GateOf("expose", 1).Started.Task,
            mount.GateOf(1).Started.Task).WaitAsync(Bound);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "fourth branch waiting for the mount");
        Assert.Equal(1, mount.SlewCalls);

        main.GateOf("expose", 1).Release.SetResult();
        wide.GateOf("expose", 1).Release.SetResult();
        mount.GateOf(1).Release.SetResult();
        await mount.GateOf(2).Started.Task.WaitAsync(Bound);
        mount.GateOf(2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(2, mount.SlewCalls);
    }

    [Fact]
    public async Task FailureOnOneMountBranch_DoesNotLeaveTheResourceHeld()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Failure = new InvalidOperationException("motor stalled") };
        var camera = new FakeCamera("camera.main") { Block = true };
        host.AddDevice(mount);
        host.AddDevice(camera);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            Slew(host),
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Quick),
        ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new Sequence("s", [parallel])));

        Assert.False(host.ResourceManager.IsHeld(MountResource));
        Assert.False(host.ResourceManager.IsHeld(ResourceId.ForDevice(camera.Id)));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }

    [Fact]
    public async Task CancellationOfAMountBranch_DoesNotLeaveTheResourceHeld()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();
        var parallel = new ParallelStep("p", [Slew(host), Slew(host)]);

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await mount.GateOf(1).Started.Task.WaitAsync(Bound);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second slew waiting");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.False(host.ResourceManager.IsHeld(MountResource));
        Assert.Equal(0, host.ResourceManager.WaitingCount);
        Assert.Equal(1, mount.SlewCalls);
    }

    // Composition

    [Fact]
    public async Task Group_SlewThenDelay_Works()
    {
        await using var host = new SideraRuntimeHost();
        var mount = host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6", Quick);
        await mount.ConnectAsync();
        var runner = new SequenceRunner(host.ResourceManager);
        var completed = new List<string>();
        runner.StepCompleted += (_, e) => completed.Add(e.StepName);

        await runner.RunAsync(new Sequence("s", [
            new SequenceGroup("target", [Slew(host), new DelayAction(Quick)]),
        ]));

        Assert.Equal(new[] { "Slew to RA 5.5h Dec +22.5°", "Wait 0.02s", "target" }, completed);
        Assert.Equal(Target, mount.Coordinates);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task Repeat_Slew_RunsEveryIteration()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6");
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, Slew(host))]));

        Assert.Equal(3, mount.SlewCalls);
        Assert.All(mount.Targets, t => Assert.Equal(Target, t));
        Assert.False(host.ResourceManager.IsHeld(MountResource));
    }

    // Host and direct operations

    [Fact]
    public async Task DirectSlew_UsesTheSameResourceAsTheSequence()
    {
        await using var host = new SideraRuntimeHost();
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(mount);
        var runner = new SequenceRunner(host.ResourceManager);

        var manual = host.DeviceOperations.SlewToAsync(mount.Id, Target);
        await mount.GateOf(1).Started.Task.WaitAsync(Bound);
        var sequence = runner.RunAsync(new Sequence("s", [Slew(host)]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "sequence slew waiting for the manual one");
        Assert.Equal(1, mount.SlewCalls);

        mount.GateOf(1).Release.SetResult();
        await manual.WaitAsync(Bound);
        await mount.GateOf(2).Started.Task.WaitAsync(Bound);
        mount.GateOf(2).Release.SetResult();
        await sequence.WaitAsync(Bound);

        Assert.Equal(2, mount.SlewCalls);
        Assert.False(host.ResourceManager.IsHeld(MountResource));
    }

    [Fact]
    public async Task DirectSlew_FailsClearlyForUnknownOrNonMountDevices()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAMount());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.DeviceOperations.SlewToAsync(new DeviceId("nope"), Target));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.DeviceOperations.SlewToAsync(new DeviceId("focuser.main"), Target));

        Assert.Contains("'nope' is not registered", unknown.Message);
        Assert.Contains("'focuser.main' is not a mount", wrong.Message);
    }

    [Fact]
    public async Task OneHost_CanHoldTwoCamerasAndAMount()
    {
        await using var host = new SideraRuntimeHost();

        host.AddSimulatedCamera(new DeviceId("camera.main"), "Main");
        host.AddSimulatedCamera(new DeviceId("camera.wide"), "Wide");
        host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6");

        Assert.Equal(3, host.DeviceRegistry.GetAll().Count);
        Assert.Single(host.DeviceRegistry.GetAll().OfType<IMount>());
    }
}

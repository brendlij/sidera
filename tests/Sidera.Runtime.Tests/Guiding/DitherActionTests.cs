using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Guiding;

public partial class DitherActionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(10);
    private static readonly CoordinationGroupId Session = new("session.mount.eq6");

    private static readonly DeviceId GuiderId = new("guider.main");
    private static readonly DeviceId MountId = new("mount.eq6");
    private static readonly DeviceId MainId = new("camera.main");
    private static readonly DeviceId WideId = new("camera.wide");

    private static readonly ResourceId GuiderResource = ResourceId.ForDevice(GuiderId);
    private static readonly ResourceId MountResource = ResourceId.ForDevice(MountId);
    private static readonly ResourceId MainResource = ResourceId.ForDevice(MainId);
    private static readonly ResourceId WideResource = ResourceId.ForDevice(WideId);
    private static readonly ResourceId[] DitherResources = [GuiderResource, MountResource, MainResource, WideResource];

    private sealed class NotADevice(string id, DeviceType type) : IDevice
    {
        public DeviceId Id { get; } = new(id);
        public string Name => "Other";
        public DeviceType Type => type;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A guider that is guiding but cannot dither.</summary>
    private sealed class PlainGuider(string id) : IGuider
    {
        public DeviceId Id { get; } = new(id);
        public string Name => "Plain guider";
        public DeviceType Type => DeviceType.Guider;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public GuidingState GuidingState => GuidingState.Guiding;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class WorkStep(string name) : ISequenceStep
    {
        private int _executions;
        public string Name { get; } = name;
        public int Executions => _executions;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new SequenceStepResult());
        }
    }

    /// <summary>The fake rig: two connected cameras, a connected mount and a guiding guider, all held by the test.</summary>
    private sealed class Rig
    {
        public required SideraRuntimeHost Host { get; init; }
        public required FakeCamera Main { get; init; }
        public required FakeCamera Wide { get; init; }
        public required FakeMount Mount { get; init; }
        public required FakeGuider Guider { get; init; }
    }

    private static async Task<Rig> CreateRig(SideraRuntimeHost host)
    {
        var main = new FakeCamera(MainId.Value);
        var wide = new FakeCamera(WideId.Value);
        await main.ConnectAsync();
        await wide.ConnectAsync();
        main.Block = true;
        wide.Block = true;
        var mount = new FakeMount(MountId.Value);
        var guider = new FakeGuider(GuiderId.Value, guiding: true) { Block = true };
        host.AddDevice(main);
        host.AddDevice(wide);
        host.AddDevice(mount);
        host.AddDevice(guider);
        return new Rig { Host = host, Main = main, Wide = wide, Mount = mount, Guider = guider };
    }

    private static DitherAction Dither(SideraRuntimeHost host, double amplitude = 1.5, params DeviceId[] cameras) =>
        new(host.DeviceRegistry, GuiderId, MountId, cameras.Length == 0 ? [MainId, WideId] : cameras, amplitude);

    private static SequenceRunner Runner(SideraRuntimeHost host) =>
        new(host.ResourceManager, host.SafePointCoordinator);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static void AssertNothingLeftBehind(SideraRuntimeHost host, SequenceRunner runner)
    {
        var status = host.SafePointCoordinator.GetStatus(Session);
        Assert.Empty(status.Participants);
        Assert.Empty(status.AtSafePoint);
        Assert.False(status.RequestPending);
        Assert.Equal(0, host.ResourceManager.WaitingCount);
        Assert.All(DitherResources, r => Assert.False(host.ResourceManager.IsHeld(r)));
        Assert.Empty(runner.ActivePositions);
    }

    // Configuration

    [Fact]
    public async Task Constructor_CopiesAndDeduplicatesCameras_AndExposesItsConfiguration()
    {
        await using var host = new SideraRuntimeHost();
        var cameras = new List<DeviceId> { WideId, MainId, WideId };

        var action = new DitherAction(host.DeviceRegistry, GuiderId, MountId, cameras, 1.5);
        cameras.Add(new DeviceId("camera.later"));

        Assert.Equal(new[] { WideId, MainId }, action.CameraIds);
        Assert.Equal(GuiderId, action.GuiderId);
        Assert.Equal(MountId, action.MountId);
        Assert.Equal(1.5, action.AmplitudePixels);
        Assert.Equal("Dither 1.5 px", action.Name);
        Assert.False(action.CameraIds is IList<DeviceId> { IsReadOnly: false });
        // Resources are taken by the inner command, only once the group is safe; the action itself declares none.
        Assert.IsNotAssignableFrom<IResourceAwareSequenceStep>(action);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    public async Task Constructor_RejectsInvalidAmplitudes(double amplitude)
    {
        await using var host = new SideraRuntimeHost();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DitherAction(host.DeviceRegistry, GuiderId, MountId, [MainId], amplitude));
    }

    [Fact]
    public async Task Constructor_RejectsMissingArguments_AndAnEmptyCameraList()
    {
        await using var host = new SideraRuntimeHost();

        Assert.Throws<ArgumentNullException>(() => new DitherAction(null!, GuiderId, MountId, [MainId], 1));
        Assert.Throws<ArgumentNullException>(() => new DitherAction(host.DeviceRegistry, GuiderId, MountId, null!, 1));
        Assert.Throws<ArgumentException>(() => new DitherAction(host.DeviceRegistry, GuiderId, MountId, [], 1));
    }

    [Fact]
    public async Task Execution_HoldsTheDeduplicatedGuiderMountAndCameraResources_OnlyWhileTheDitherRuns()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var runner = Runner(host);
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completed)
            {
                completed.Add(e);
            }
        };

        var run = runner.RunAsync(new Sequence("s", [Dither(host, 2, MainId, WideId, MainId)]));
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);

        Assert.All(DitherResources, r => Assert.True(host.ResourceManager.IsHeld(r)));
        Assert.Equal(new[] { "Dither 2 px", "Dither command" }, runner.ActivePositions.Select(p => p.StepName).Order());
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(new[] { 2.0 }, rig.Guider.Amplitudes);
        Assert.Equal(new[] { "Dither command", "Dither 2 px" }, completed.Select(c => c.StepName));
        Assert.Equal(0, completed[0].StepIndex);
        Assert.Equal(1, completed[0].Position.Count);
        Assert.Same(completed[1].Position, completed[0].Position.Parent);
        Assert.All(completed, c => Assert.Null(c.Result.Payload));
        Assert.NotSame(completed[0].Result, completed[1].Result);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
        Assert.Equal(0, rig.Mount.SlewCalls);
        Assert.Equal(0, rig.Guider.StartCalls);
    }

    [Fact]
    public async Task Execution_WithTheSimulatedGuider_ReturnsToGuiding()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        host.DeviceRegistry.Unregister(GuiderId);
        var guider = host.AddSimulatedGuider(GuiderId, "Simulated", Quick, Quick, Quick);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        var states = new List<GuidingState>();
        host.EventBus.Subscribe<GuidingStateChanged>((e, _) =>
        {
            states.Add(e.NewState);
            return Task.CompletedTask;
        });

        await Runner(host).RunAsync(new Sequence("s", [Dither(host)]));

        Assert.Equal(new[] { GuidingState.Dithering, GuidingState.Guiding }, states);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        Assert.Equal(0, rig.Mount.SlewCalls);
    }

    // Lookup and preconditions

    public static TheoryData<string, string> LookupFailures => new()
    {
        { "missing guider", "'guider.main' is not registered" },
        { "missing mount", "'mount.eq6' is not registered" },
        { "missing camera", "'camera.wide' is not registered" },
        { "guider is not a guider", "'guider.main' is not a guider" },
        { "mount is not a mount", "'mount.eq6' is not a mount" },
        { "camera is not a camera", "'camera.wide' is not a camera" },
        { "guider cannot dither", "'guider.main' does not support dithering" },
    };

    [Theory]
    [MemberData(nameof(LookupFailures))]
    public async Task Execution_FailsClearly_ForMissingOrWrongDevices(string setup, string message)
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        switch (setup)
        {
            case "missing guider":
                host.DeviceRegistry.Unregister(GuiderId);
                break;
            case "missing mount":
                host.DeviceRegistry.Unregister(MountId);
                break;
            case "missing camera":
                host.DeviceRegistry.Unregister(WideId);
                break;
            case "guider is not a guider":
                host.DeviceRegistry.Unregister(GuiderId);
                host.AddDevice(new NotADevice(GuiderId.Value, DeviceType.Focuser));
                break;
            case "mount is not a mount":
                host.DeviceRegistry.Unregister(MountId);
                host.AddDevice(new NotADevice(MountId.Value, DeviceType.Focuser));
                break;
            case "camera is not a camera":
                host.DeviceRegistry.Unregister(WideId);
                host.AddDevice(new NotADevice(WideId.Value, DeviceType.Focuser));
                break;
            case "guider cannot dither":
                host.DeviceRegistry.Unregister(GuiderId);
                host.AddDevice(new PlainGuider(GuiderId.Value));
                break;
        }

        var runner = Runner(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Dither(host)])).WaitAsync(Bound));

        Assert.Contains(message, error.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(0, rig.Guider.DitherCalls);
        AssertNothingLeftBehind(host, runner);
    }

    public static TheoryData<string, string> PreconditionFailures => new()
    {
        { "mount disconnected", "Mount 'mount.eq6' is not connected" },
        { "camera disconnected", "Camera 'camera.wide' is not connected" },
        { "guider disconnected", "Guider 'guider.main' is not connected" },
        { "guider idle", "Guider 'guider.main' is not guiding" },
    };

    [Theory]
    [MemberData(nameof(PreconditionFailures))]
    public async Task Execution_RequiresConnectedEquipmentAndActiveGuiding_WithoutFixingAnythingItself(
        string setup,
        string message)
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        switch (setup)
        {
            case "mount disconnected":
                rig.Mount.ConnectionState = DeviceConnectionState.Disconnected;
                break;
            case "camera disconnected":
                rig.Wide.Block = false;
                await rig.Wide.DisconnectAsync();
                break;
            case "guider disconnected":
                rig.Guider.ConnectionState = DeviceConnectionState.Disconnected;
                break;
            case "guider idle":
                rig.Guider.GateOf("stop", 1).Release.SetResult();
                await rig.Guider.StopGuidingAsync();
                break;
        }

        var connectCalls = rig.Wide.ConnectCalls;
        var runner = Runner(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Dither(host)])).WaitAsync(Bound));

        Assert.Equal(message + ".", error.Message);
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.Equal(0, rig.Guider.ConnectCalls);
        Assert.Equal(0, rig.Guider.StartCalls);
        Assert.Equal(connectCalls, rig.Wide.ConnectCalls);
        Assert.Equal(0, rig.Mount.SlewCalls);
        AssertNothingLeftBehind(host, runner);
    }

    // The scenario: two cameras on one mount, the faster branch dithers

    [Fact]
    public async Task FastBranchDither_WaitsForTheSlowExposure_WithoutHoldingResources_ThenBlocksBothBranches()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var other = new FakeCamera("camera.other");
        await other.ConnectAsync();
        host.AddDevice(other);
        var coordinator = host.SafePointCoordinator;
        var resources = host.ResourceManager;
        var mainAfter = new WorkStep("main after");
        var wideAfter = new WorkStep("wide after");
        var parallel = new ParallelStep("rigs", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, TimeSpan.FromSeconds(3)),
                new SafePointStep(),
                mainAfter]),
            new SequenceGroup("wide", [
                new CameraExposureAction(host.DeviceRegistry, WideId, TimeSpan.FromSeconds(1)),
                Dither(host),
                wideAfter]),
        ], Session);
        var runner = Runner(host);

        var run = runner.RunAsync(new Sequence("night", [parallel]));
        await Task.WhenAll(
            rig.Main.GateOf("expose", 1).Started.Task,
            rig.Wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);

        // The wide exposure ends first and its branch asks for the dither while the main camera is still exposing.
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await WaitUntil(() => coordinator.GetStatus(Session).RequestPending, "dither request pending");
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.Empty(coordinator.GetStatus(Session).AtSafePoint);
        Assert.False(resources.IsHeld(GuiderResource));   // nothing of the dither is held or queued yet
        Assert.False(resources.IsHeld(MountResource));
        Assert.False(resources.IsHeld(WideResource));
        Assert.True(resources.IsHeld(MainResource));      // only by the running main exposure
        Assert.Equal(0, resources.WaitingCount);
        Assert.Equal(0, wideAfter.Executions);

        // The slow exposure finishes, the main branch reaches its safe point, and only then the dither starts.
        rig.Main.GateOf("expose", 1).Release.SetResult();
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        var status = coordinator.GetStatus(Session);
        Assert.True(status.OperationRunning);
        Assert.Single(status.AtSafePoint);
        Assert.All(DitherResources, r => Assert.True(resources.IsHeld(r)));
        Assert.Equal(0, mainAfter.Executions);
        Assert.Equal(0, wideAfter.Executions);

        // Manual users of the reserved devices wait for the dither; an unrelated camera does not.
        rig.Main.GateOf("expose", 2).Release.SetResult();
        rig.Guider.GateOf("stop", 1).Release.SetResult();
        var manualExposure = host.DeviceOperations.ExposeAsync(MainId, Quick);
        var manualSlew = host.DeviceOperations.SlewToAsync(MountId, new CelestialCoordinates(3, 4));
        var manualStop = host.DeviceOperations.StopGuidingAsync(GuiderId);
        var manualDisconnect = host.DeviceOperations.DisconnectAsync(GuiderId);
        await WaitUntil(() => resources.WaitingCount == 4, "manual operations waiting for the dither");
        await host.DeviceOperations.ExposeAsync(other.Id, Quick).WaitAsync(Bound);
        Assert.Equal(1, rig.Main.ExposeCalls);
        Assert.Equal(0, rig.Mount.SlewCalls);
        Assert.Equal(0, rig.Guider.StopCalls);
        Assert.Equal(0, rig.Guider.DisconnectCalls);

        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await Task.WhenAll(manualExposure, manualSlew, manualStop, manualDisconnect).WaitAsync(Bound);
        await run.WaitAsync(Bound);

        Assert.Equal(1, mainAfter.Executions);
        Assert.Equal(1, wideAfter.Executions);
        Assert.Equal(1, rig.Guider.DitherCalls);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }

    // Cancellation

    [Fact]
    public async Task CancellationWhileWaitingForSafePoints_NeverDithers_AndCleansUp()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var after = new WorkStep("after");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
                new SafePointStep(),
                after]),
            Dither(host),
        ], Session);
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await rig.Main.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "dither request pending");
        Assert.Equal(0, host.ResourceManager.WaitingCount);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.Equal(0, after.Executions);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task CancellationWhileWaitingForResources_NeverDithers_AndCleansUp()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var after = new WorkStep("after");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
                new SafePointStep(),
                after]),
            Dither(host),
        ], Session);
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();
        // A manual exposure outside the group keeps the wide camera busy.
        var manual = host.DeviceOperations.ExposeAsync(WideId, Quick);
        await rig.Wide.GateOf("expose", 1).Started.Task.WaitAsync(Bound);

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        rig.Main.GateOf("expose", 1).Release.SetResult();
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "dither waiting for the wide camera");
        Assert.Single(host.SafePointCoordinator.GetStatus(Session).AtSafePoint);
        Assert.False(host.ResourceManager.IsHeld(GuiderResource));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.Equal(0, after.Executions);
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await manual.WaitAsync(Bound);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task CancellationDuringTheDither_ReachesTheGuider_AndCleansUp()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var after = new WorkStep("after");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("main", [new SafePointStep(), after]),
            Dither(host),
        ], Session);
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();
        var completed = new List<string>();
        runner.StepCompleted += (_, e) => completed.Add(e.StepName);

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.DoesNotContain("Dither command", completed);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task CancellationDuringASimulatedDither_LeavesTheGuiderGuiding()
    {
        await using var host = new SideraRuntimeHost();
        await CreateRig(host);
        host.DeviceRegistry.Unregister(GuiderId);
        var guider = host.AddSimulatedGuider(GuiderId, "Simulated", Quick, Quick, TimeSpan.FromMinutes(1));
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        var dithering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.EventBus.Subscribe<GuidingStateChanged>((e, _) =>
        {
            if (e.NewState == GuidingState.Dithering)
            {
                dithering.TrySetResult();
            }

            return Task.CompletedTask;
        });
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [Dither(host)]), cts.Token);
        await dithering.Task.WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        AssertNothingLeftBehind(host, runner);
        await Runner(host).RunAsync(new Sequence("again", [new StopGuidingAction(host.DeviceRegistry, GuiderId)]));
        Assert.Equal(GuidingState.Idle, guider.GuidingState);
    }

    // Failure

    [Fact]
    public async Task DitherFailure_PropagatesTheOriginalException_WithoutReportingCompletion()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var failure = new InvalidOperationException("guider lost the star");
        rig.Guider.Failure = failure;
        var parallel = new ParallelStep("p", [
            new SequenceGroup("main", [new SafePointStep(), new WorkStep("after")]),
            Dither(host),
        ], Session);
        var runner = Runner(host);
        var completed = new List<string>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completed)
            {
                completed.Add(e.StepName);
            }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [parallel])).WaitAsync(Bound));

        Assert.Same(failure, error);
        Assert.Same(failure, runner.Failure);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.DoesNotContain("Dither command", completed);
        Assert.DoesNotContain("Dither 1.5 px", completed);
        AssertNothingLeftBehind(host, runner);
    }

    // Repetition and concurrency

    [Fact]
    public async Task RepeatedDithers_CoordinateEveryRound_WithoutLeakingState()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        rig.Main.Block = false;
        rig.Wide.Block = false;
        rig.Guider.Block = false;
        var parallel = new ParallelStep("rigs", [
            new RepeatStep(3, new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
                new SafePointStep()])),
            new RepeatStep(3, new SequenceGroup("wide", [
                new CameraExposureAction(host.DeviceRegistry, WideId, Quick),
                Dither(host)])),
        ], Session);
        var runner = Runner(host);

        await runner.RunAsync(new Sequence("s", [parallel])).WaitAsync(Bound);
        await runner.RunAsync(new Sequence("again", [parallel])).WaitAsync(Bound);

        Assert.Equal(6, rig.Guider.DitherCalls);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task SimultaneousDitherRequests_InOneGroup_RunOneAfterTheOther()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var parallel = new ParallelStep("p", [Dither(host, 1, MainId), Dither(host, 2, WideId)], Session);
        var runner = Runner(host);

        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        Assert.Equal(1, rig.Guider.DitherCalls);          // not coalesced, not overlapped
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("dither", 2).Started.Task.WaitAsync(Bound);
        rig.Guider.GateOf("dither", 2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(new[] { 1.0, 2.0 }, rig.Guider.Amplitudes.Order());
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task SimultaneousDithersWithoutAGroup_SerializeOnTheirResources()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var parallel = new ParallelStep("p", [Dither(host, 1, MainId), Dither(host, 2, WideId)]);
        var runner = Runner(host);

        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second dither waiting for guider and mount");
        Assert.Equal(1, rig.Guider.DitherCalls);
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("dither", 2).Started.Task.WaitAsync(Bound);
        rig.Guider.GateOf("dither", 2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(2, rig.Guider.DitherCalls);
        AssertNothingLeftBehind(host, runner);
    }

    // Outside a coordination group

    [Fact]
    public async Task OutsideAGroup_DitherRunsDirectly_ButStillWaitsForAReservedCamera()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var runner = Runner(host);
        var manual = host.DeviceOperations.ExposeAsync(WideId, Quick);
        await rig.Wide.GateOf("expose", 1).Started.Task.WaitAsync(Bound);

        var run = runner.RunAsync(new Sequence("s", [Dither(host)]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "dither waiting for the exposing camera");
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.False(host.SafePointCoordinator.GetStatus(Session).RequestPending);

        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await manual.WaitAsync(Bound);
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        Assert.True(host.ResourceManager.IsHeld(WideResource));
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }
}

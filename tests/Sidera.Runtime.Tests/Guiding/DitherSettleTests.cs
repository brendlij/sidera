using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Guiding;

// Dithering with a settle wait; shares the fake rig and helpers of the dither tests.
public partial class DitherActionTests
{
    private static readonly GuidingSettleOptions Settle = new(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    private const string SettleName = "Settle guiding ≤ 0.5 px for 1 s";

    private static DitherAction SettlingDither(SideraRuntimeHost host, double amplitude = 1.5) =>
        new(host.DeviceRegistry, GuiderId, MountId, [MainId, WideId], amplitude, Settle);

    /// <summary>A guider that is guiding and can dither, but cannot report settling.</summary>
    private sealed class DitherOnlyGuider(string id) : IDitherGuider
    {
        private int _ditherCalls;

        public DeviceId Id { get; } = new(id);
        public string Name => "Dither-only guider";
        public DeviceType Type => DeviceType.Guider;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public GuidingState GuidingState => GuidingState.Guiding;
        public int DitherCalls => _ditherCalls;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _ditherCalls);
            return Task.CompletedTask;
        }
    }

    // Releases the first exposure of the main branch only once the dither has asked for the safe point, so that
    // the branch stops there instead of passing it before the request exists.
    private static async Task ReleaseMainIntoItsSafePoint(SideraRuntimeHost host, FakeCamera main)
    {
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "dither request pending");
        main.GateOf("expose", 1).Release.SetResult();
    }

    private static List<string> RecordCompletions(SequenceRunner runner)
    {
        var completed = new List<string>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completed)
            {
                completed.Add(e.StepName);
            }
        };
        return completed;
    }

    private static string[] Snapshot(List<string> completed)
    {
        lock (completed)
        {
            return completed.ToArray();
        }
    }

    // Configuration

    [Fact]
    public async Task SettleOptions_AreOptional_AndWithoutThemTheDitherNeverSettles()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        rig.Guider.Block = false;

        Assert.Same(Settle, SettlingDither(host).SettleOptions);
        Assert.Null(Dither(host).SettleOptions);
        Assert.Equal("Dither 1.5 px", SettlingDither(host).Name);

        var runner = Runner(host);
        var completed = RecordCompletions(runner);
        await runner.RunAsync(new Sequence("s", [Dither(host)])).WaitAsync(Bound);

        Assert.Equal(1, rig.Guider.DitherCalls);
        Assert.Equal(0, rig.Guider.SettleCalls);
        Assert.Equal(new[] { "Dither command", "Dither 1.5 px" }, Snapshot(completed));
    }

    [Fact]
    public async Task RequestedSettling_OnAGuiderThatCannotSettle_FailsBeforeMoving()
    {
        await using var host = new SideraRuntimeHost();
        await CreateRig(host);
        host.DeviceRegistry.Unregister(GuiderId);
        var guider = new DitherOnlyGuider(GuiderId.Value);
        host.AddDevice(guider);
        var runner = Runner(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [SettlingDither(host)])).WaitAsync(Bound));

        Assert.Equal("Guider 'guider.main' does not support settling.", error.Message);
        Assert.Equal(0, guider.DitherCalls);
        Assert.Equal(SequenceState.Failed, runner.State);
        AssertNothingLeftBehind(host, runner);

        // Without settle options the same guider still dithers as before.
        await Runner(host).RunAsync(new Sequence("s", [Dither(host)])).WaitAsync(Bound);
        Assert.Equal(1, guider.DitherCalls);
    }

    // Coordination

    [Fact]
    public async Task Settling_KeepsTheBranchesAtTheirSafePointsAndTheLease_ThroughMovementAndSettling_ThenResumes()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var coordinator = host.SafePointCoordinator;
        var resources = host.ResourceManager;
        var mainAfter = new WorkStep("main after");
        var wideAfter = new WorkStep("wide after");
        var parallel = new ParallelStep("rigs", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
                new SafePointStep(),
                mainAfter]),
            new SequenceGroup("wide", [SettlingDither(host), wideAfter]),
        ], Session);
        var runner = Runner(host);
        var completed = RecordCompletions(runner);

        var run = runner.RunAsync(new Sequence("night", [parallel]));
        await ReleaseMainIntoItsSafePoint(host, rig.Main);

        // Movement: the main branch waits at its safe point, the dither holds everything.
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        Assert.Equal(0, rig.Guider.SettleCalls);
        Assert.All(DitherResources, r => Assert.True(resources.IsHeld(r)));
        Assert.Single(coordinator.GetStatus(Session).AtSafePoint);

        // Settling: still the same operation under the same lease, shown as its own step below the dither command.
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
        var status = coordinator.GetStatus(Session);
        Assert.True(status.OperationRunning);
        Assert.Single(status.AtSafePoint);
        Assert.All(DitherResources, r => Assert.True(resources.IsHeld(r)));
        Assert.Equal(0, resources.WaitingCount);
        var settling = Assert.Single(runner.ActivePositions, p => p.StepName == SettleName);
        Assert.Equal("Dither command", settling.Parent!.StepName);
        Assert.Equal((0, 1), (settling.Index, settling.Count));
        Assert.Equal(0, mainAfter.Executions);
        Assert.Equal(0, wideAfter.Executions);
        Assert.DoesNotContain(Snapshot(completed), name => name.StartsWith("Settle") || name.StartsWith("Dither"));
        Assert.Same(Settle, Assert.Single(rig.Guider.SettleOptions));

        rig.Guider.GateOf("settle", 1).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(1, mainAfter.Executions);
        Assert.Equal(1, wideAfter.Executions);
        var order = Snapshot(completed);
        Assert.Equal(
            new[] { SettleName, "Dither command", "Dither 1.5 px" },
            order.Where(name => name.StartsWith("Settle") || name.StartsWith("Dither")));
        Assert.True(Array.IndexOf(order, "Dither 1.5 px") < Array.IndexOf(order, "wide after"));
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }

    public static TheoryData<string> SettleFailures => new() { "timeout", "guiding lost", "cancelled" };

    [Theory]
    [MemberData(nameof(SettleFailures))]
    public async Task UnsuccessfulSettling_EndsTheRun_ReleasesEveryWaiterAndLease_AndALaterRunSucceeds(string failure)
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var expected = failure switch
        {
            "timeout" => new GuidingSettleTimeoutException("did not settle"),
            "guiding lost" => (Exception)new InvalidOperationException("stopped guiding while settling"),
            _ => null,
        };
        rig.Guider.SettleFailure = expected;
        // However the main branch continues after its safe point, its next exposure blocks until cancelled.
        var parallel = new ParallelStep("rigs", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
                new SafePointStep(),
                new CameraExposureAction(host.DeviceRegistry, MainId, Quick)]),
            new SequenceGroup("wide", [SettlingDither(host), new WorkStep("wide after")]),
        ], Session);
        var runner = Runner(host);
        var completed = RecordCompletions(runner);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("night", [parallel]), cts.Token);
        await ReleaseMainIntoItsSafePoint(host, rig.Main);
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);

        if (expected is null)
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
            Assert.Equal(SequenceState.Cancelled, runner.State);
        }
        else
        {
            rig.Guider.GateOf("settle", 1).Release.SetResult();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Bound));
            Assert.Same(expected, error);
            Assert.Same(expected, runner.Failure);
            Assert.Equal(SequenceState.Failed, runner.State);
        }

        var names = Snapshot(completed);
        Assert.DoesNotContain(SettleName, names);
        Assert.DoesNotContain("Dither command", names);
        Assert.DoesNotContain("Dither 1.5 px", names);
        Assert.DoesNotContain("wide after", names);
        AssertNothingLeftBehind(host, runner);

        // The next run starts from a clean slate.
        rig.Guider.SettleFailure = null;
        rig.Guider.Block = false;
        rig.Main.Block = false;
        await runner.RunAsync(new Sequence("again", [parallel])).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(2, rig.Guider.SettleCalls);
        AssertNothingLeftBehind(host, runner);
    }

    // The simulated scenario: two cameras on one mount, the wide branch dithers and waits for the simulated guider
    // to settle, driven through the real runner and safe-point coordinator with controlled time.

    private static async Task<(SimulatedGuider Guider, ManualClock Clock)> UseSimulatedGuider(SideraRuntimeHost host)
    {
        host.DeviceRegistry.Unregister(GuiderId);
        var clock = new ManualClock();
        var guider = new SimulatedGuider(GuiderId, clock, "Simulated", host.EventBus, Quick, Quick, Quick);
        host.AddDevice(guider);
        await guider.ConnectAsync();
        await guider.StartGuidingAsync();
        return (guider, clock);
    }

    private static ParallelStep ImagingWithSettlingDither(SideraRuntimeHost host) => new("rigs", [
        new SequenceGroup("main", [
            new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
            new SafePointStep(),
            new CameraExposureAction(host.DeviceRegistry, MainId, Quick)]),
        new SequenceGroup("wide", [
            new CameraExposureAction(host.DeviceRegistry, WideId, Quick),
            new DitherAction(host.DeviceRegistry, GuiderId, MountId, [MainId, WideId], 3, Settle),
            new CameraExposureAction(host.DeviceRegistry, WideId, Quick)]),
    ], Session);

    [Fact]
    public async Task SimulatedScenario_ExposuresResumeOnlyAfterTheSimulatedGuiderHasSettled()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (guider, clock) = await UseSimulatedGuider(host);
        var runner = Runner(host);
        var completed = RecordCompletions(runner);

        var run = runner.RunAsync(new Sequence("night", [ImagingWithSettlingDither(host)]));
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await ReleaseMainIntoItsSafePoint(host, rig.Main);

        // 0.3 + 3 · 0.5^(t / 250 ms) reaches 0.5 px after 1 s and must then stay there for 1 s: 20 samples.
        for (var sample = 0; sample < 20; sample++)
        {
            await clock.NextDelay();
            Assert.Contains(runner.ActivePositions, p => p.StepName == SettleName);
            Assert.DoesNotContain(SettleName, Snapshot(completed));
            Assert.Equal(1, rig.Main.ExposeCalls);
            Assert.Equal(1, rig.Wide.ExposeCalls);
            Assert.All(DitherResources, r => Assert.True(host.ResourceManager.IsHeld(r)));
            Assert.Equal(GuidingState.Guiding, guider.GuidingState);
            clock.Advance(SimulatedGuider.SampleInterval);
        }

        await Task.WhenAll(
            rig.Main.GateOf("expose", 2).Started.Task,
            rig.Wide.GateOf("expose", 2).Started.Task).WaitAsync(Bound);
        Assert.Contains(SettleName, Snapshot(completed));
        rig.Main.GateOf("expose", 2).Release.SetResult();
        rig.Wide.GateOf("expose", 2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        AssertNothingLeftBehind(host, runner);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("guiding lost")]
    public async Task SimulatedScenario_UnsuccessfulSettling_FailsTheRunCleanly_AndTheNextRunSucceeds(string failure)
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (guider, clock) = await UseSimulatedGuider(host);
        if (failure == "timeout")
        {
            guider.ObservationSource = now => new GuideErrorObservation(now, 2.0);
        }

        var runner = Runner(host);
        var run = runner.RunAsync(new Sequence("night", [ImagingWithSettlingDither(host)]));
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await ReleaseMainIntoItsSafePoint(host, rig.Main);

        if (failure == "timeout")
        {
            // Never below 0.5 px: the 10 s timeout ends the wait after 100 samples.
            for (var sample = 0; sample < 100; sample++)
            {
                await clock.StepAsync(SimulatedGuider.SampleInterval);
            }

            await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => run.WaitAsync(Bound));
            Assert.Equal(GuidingState.Guiding, guider.GuidingState);
        }
        else
        {
            // Guiding is lost while settling (a direct device call, not through the held resource).
            await clock.NextDelay();
            await guider.StopGuidingAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound));
            Assert.Contains("stopped guiding while settling", error.Message);
            Assert.Equal(GuidingState.Idle, guider.GuidingState);
            await guider.StartGuidingAsync();
        }

        Assert.Equal(SequenceState.Failed, runner.State);
        AssertNothingLeftBehind(host, runner);

        // A later run on the same equipment settles after the disturbance of its own dither.
        guider.ObservationSource = null;
        rig.Main.Block = false;
        rig.Wide.Block = false;
        var again = runner.RunAsync(new Sequence("again", [ImagingWithSettlingDither(host)]));
        for (var sample = 0; sample < 20; sample++)
        {
            await clock.StepAsync(SimulatedGuider.SampleInterval);
        }

        await again.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }
}

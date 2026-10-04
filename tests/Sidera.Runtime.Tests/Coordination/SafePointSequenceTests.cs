using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Coordination;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Coordination;

public class SafePointSequenceTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly CoordinationGroupId Session = new("session.mount.eq6");
    private static readonly CelestialCoordinates Target = new(1, 2);

    /// <summary>Runs, counts, and finishes at once.</summary>
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

    /// <summary>Blocks each execution until the test releases it (optionally by failing).</summary>
    private sealed class HoldStep(string name) : ISequenceStep
    {
        private readonly SemaphoreSlim _releases = new(0);
        public string Name { get; } = name;
        public Exception? FailWhenReleased { get; init; }

        public void Release() => _releases.Release();

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            await _releases.WaitAsync(cancellationToken);
            if (FailWhenReleased is not null)
            {
                throw FailWhenReleased;
            }

            return new SequenceStepResult();
        }
    }

    /// <summary>Asks for a coordinated operation, like a future dither step would.</summary>
    private sealed class CoordinatedStep(string name, Func<CancellationToken, Task> operation) : ISequenceStep
    {
        public string Name { get; } = name;

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            await context.ExecuteWhenSafeAsync(operation, cancellationToken);
            return new SequenceStepResult();
        }
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

    private static void AssertNothingLeftBehind(SideraRuntimeHost host)
    {
        var status = host.SafePointCoordinator.GetStatus(Session);
        Assert.Empty(status.Participants);
        Assert.Empty(status.AtSafePoint);
        Assert.False(status.RequestPending);
    }

    private static SequenceRunner Runner(SideraRuntimeHost host) =>
        new(host.ResourceManager, host.SafePointCoordinator);

    // Basic behavior

    [Fact]
    public async Task Host_OwnsOneSafePointCoordinator_SharedByItsRunners()
    {
        await using var host = new SideraRuntimeHost();

        Assert.NotNull(host.SafePointCoordinator);
        Assert.Same(host.SafePointCoordinator, host.SafePointCoordinator);
        Assert.Empty(host.SafePointCoordinator.GetStatus(Session).Participants);
    }

    [Fact]
    public async Task SafePoint_OutsideACoordinationGroup_DoesNothing()
    {
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("s", [new SafePointStep(), new ParallelStep("p", [new SafePointStep(), new SafePointStep()])]));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal("Safe Point", new SafePointStep().Name);
    }

    [Fact]
    public async Task SafePoint_ContinuesImmediately_WhenNoCoordinatedOperationIsPending()
    {
        await using var host = new SideraRuntimeHost();
        var after = new WorkStep("after");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("a", [new SafePointStep(), after]),
            new SequenceGroup("b", [new SafePointStep(), new WorkStep("b")]),
        ], Session);

        await Runner(host).RunAsync(new Sequence("s", [parallel]));

        Assert.Equal(1, after.Executions);
        AssertNothingLeftBehind(host);
    }

    [Fact]
    public async Task CoordinatedOperationWithoutAGroup_JustRuns()
    {
        var ran = 0;
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("s", [new CoordinatedStep("op", _ =>
        {
            ran++;
            return Task.CompletedTask;
        })]));

        Assert.Equal(1, ran);
    }

    // The scenario: two cameras, one shared mount

    [Fact]
    public async Task FastBranchRequestsMountOperation_WaitsForSlowBranchsSafePoint_ThenBothContinue()
    {
        await using var host = new SideraRuntimeHost();
        var main = new FakeCamera("camera.main") { Block = true };
        var wide = new FakeCamera("camera.wide") { Block = true };
        var mount = new FakeMount("mount.eq6") { Block = true };
        host.AddDevice(main);
        host.AddDevice(wide);
        host.AddDevice(mount);
        var mountResource = ResourceId.ForDevice(mount.Id);
        var mainAfter = new WorkStep("main after");
        var wideAfter = new WorkStep("wide after");
        var coordinatedSlew = new CoordinatedStep("coordinated slew", async ct =>
        {
            using (await host.ResourceManager.AcquireAsync([mountResource], ct))
            {
                await mount.SlewToAsync(Target, ct);
            }
        });
        var parallel = new ParallelStep("rigs", [
            new SequenceGroup("main", [
                new CameraExposureAction(host.DeviceRegistry, main.Id, TimeSpan.FromSeconds(3)),
                new SafePointStep(),
                mainAfter]),
            new SequenceGroup("wide", [
                new CameraExposureAction(host.DeviceRegistry, wide.Id, TimeSpan.FromSeconds(1)),
                coordinatedSlew,
                wideAfter]),
        ], Session);
        var runner = Runner(host);
        var coordinator = host.SafePointCoordinator;

        var run = runner.RunAsync(new Sequence("night", [parallel]));
        await Task.WhenAll(main.GateOf("expose", 1).Started.Task, wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);
        Assert.Equal(2, coordinator.GetStatus(Session).Participants.Count);

        // The wide branch finishes first and asks for the slew; the main branch is still exposing.
        wide.GateOf("expose", 1).Release.SetResult();
        await WaitUntil(() => coordinator.GetStatus(Session).RequestPending, "coordinated request pending");
        Assert.Equal(0, mount.SlewCalls);                       // not slewed immediately
        Assert.Empty(coordinator.GetStatus(Session).AtSafePoint);
        Assert.False(host.ResourceManager.IsHeld(mountResource)); // resource is only taken once everyone is safe
        Assert.Equal(0, wideAfter.Executions);

        // The slower exposure finishes, the main branch reaches its safe point, and only then the slew starts.
        main.GateOf("expose", 1).Release.SetResult();
        await mount.GateOf(1).Started.Task.WaitAsync(Bound);
        var status = coordinator.GetStatus(Session);
        Assert.True(status.OperationRunning);
        Assert.Single(status.AtSafePoint);
        Assert.True(host.ResourceManager.IsHeld(mountResource));
        Assert.Equal(0, mainAfter.Executions);                  // both branches stay put during the operation
        Assert.Equal(0, wideAfter.Executions);

        // Another user of the mount has to wait for the coordinated operation (ResourceManager at work).
        var otherUser = host.DeviceOperations.SlewToAsync(mount.Id, new CelestialCoordinates(3, 4));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "other mount user waiting");
        Assert.Equal(1, mount.SlewCalls);

        mount.GateOf(1).Release.SetResult();
        await WaitUntil(() => mainAfter.Executions == 1 && wideAfter.Executions == 1, "both branches continue");

        await mount.GateOf(2).Started.Task.WaitAsync(Bound);    // the waiting user gets the mount afterwards
        mount.GateOf(2).Release.SetResult();
        await otherUser.WaitAsync(Bound);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(mountResource));
        AssertNothingLeftBehind(host);
    }

    // Cancellation

    [Fact]
    public async Task UserCancellationWhileBranchWaitsAtSafePointDuringOperation_EndsCancelled_AndCleansUp()
    {
        await using var host = new SideraRuntimeHost();
        var hold = new HoldStep("exposure");
        var opStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawCancellation = false;
        var after = new WorkStep("after safe point");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("waiter", [hold, new SafePointStep(), after]),
            new CoordinatedStep("op", async ct =>
            {
                opStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    sawCancellation = true;
                    throw;
                }
            }),
        ], Session);
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "request pending");
        hold.Release(); // the waiter finishes its work and reaches the safe point while the request is pending
        await opStarted.Task.WaitAsync(Bound);
        Assert.Single(host.SafePointCoordinator.GetStatus(Session).AtSafePoint); // it sits there during the operation
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.True(sawCancellation);        // the operation received the cancellation
        Assert.Equal(0, after.Executions);   // the waiter did not continue past its safe point
        AssertNothingLeftBehind(host);
    }

    [Fact]
    public async Task UserCancellationWhileOperationWaitsForAnotherBranch_NeverRunsTheOperation()
    {
        await using var host = new SideraRuntimeHost();
        var slowBranch = new HoldStep("slow work");
        var ran = 0;
        var parallel = new ParallelStep("p", [
            new SequenceGroup("slow", [slowBranch, new SafePointStep()]),
            new CoordinatedStep("op", _ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            }),
        ], Session);
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "request pending");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(0, ran);
        Assert.Equal(SequenceState.Cancelled, runner.State);
        AssertNothingLeftBehind(host);
    }

    // Failure

    [Fact]
    public async Task OperationFailure_PropagatesOriginalException_AndLeavesNoBranchBlocked()
    {
        await using var host = new SideraRuntimeHost();
        var failure = new InvalidOperationException("coordinated operation broke");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("waiter", [new SafePointStep(), new WorkStep("after")]),
            new CoordinatedStep("op", _ => throw failure),
        ], Session);
        var runner = Runner(host);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [parallel])).WaitAsync(Bound));

        Assert.Same(failure, error);
        Assert.Same(failure, runner.Failure);
        Assert.Equal(SequenceState.Failed, runner.State);
        AssertNothingLeftBehind(host);
    }

    [Fact]
    public async Task BranchFailingBeforeItsSafePoint_DoesNotLeaveTheRequestWaitingForever_AndKeepsTheOriginalFailure()
    {
        await using var host = new SideraRuntimeHost();
        var failure = new InvalidOperationException("camera error");
        var failing = new HoldStep("exposure") { FailWhenReleased = failure };
        var ran = 0;
        var parallel = new ParallelStep("p", [
            new SequenceGroup("failing", [failing, new SafePointStep()]),
            new CoordinatedStep("op", _ =>
            {
                Interlocked.Increment(ref ran);
                return Task.CompletedTask;
            }),
        ], Session);
        var runner = Runner(host);

        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "request pending");
        failing.Release();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound));

        Assert.Same(failure, error);           // the original failure, not a coordination error or an aggregate
        Assert.Equal(0, ran);                  // the operation never ran
        Assert.Equal(SequenceState.Failed, runner.State);
        AssertNothingLeftBehind(host);
    }

    // Composition

    [Fact]
    public async Task Repeat_ContainingSafePoint_Works()
    {
        await using var host = new SideraRuntimeHost();
        var work = new WorkStep("work");
        var parallel = new ParallelStep("p", [
            new RepeatStep(3, new SequenceGroup("a", [work, new SafePointStep()])),
            new RepeatStep(3, new SequenceGroup("b", [new WorkStep("b"), new SafePointStep()])),
        ], Session);

        await Runner(host).RunAsync(new Sequence("s", [parallel]));

        Assert.Equal(3, work.Executions);
        AssertNothingLeftBehind(host);
    }

    [Fact]
    public async Task Group_ContainingSafePoint_Works_WhenARequestIsPending()
    {
        await using var host = new SideraRuntimeHost();
        var gate = new HoldStep("work");
        var after = new WorkStep("after");
        var opRuns = 0;
        var parallel = new ParallelStep("p", [
            new SequenceGroup("a", [gate, new SafePointStep(), after]),
            new CoordinatedStep("op", _ =>
            {
                Interlocked.Increment(ref opRuns);
                return Task.CompletedTask;
            }),
        ], Session);

        var run = Runner(host).RunAsync(new Sequence("s", [parallel]));
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "request pending");
        gate.Release(); // branch a reaches its safe point inside the group
        await run.WaitAsync(Bound);

        Assert.Equal(1, opRuns);
        Assert.Equal(1, after.Executions);
        AssertNothingLeftBehind(host);
    }

    [Fact]
    public async Task ParallelWithRepeatAndSafePoints_CoordinatesEveryRound_WithoutLeakingState()
    {
        await using var host = new SideraRuntimeHost();
        var coordinator = host.SafePointCoordinator;
        var mainWork = new HoldStep("main exposure");
        var atSafePointDuringOperation = new List<int>();
        var mainAfter = new WorkStep("main after");
        var wideWork = new WorkStep("wide exposure");
        var parallel = new ParallelStep("rigs", [
            new RepeatStep(3, new SequenceGroup("main", [mainWork, new SafePointStep(), mainAfter])),
            new RepeatStep(3, new SequenceGroup("wide", [
                wideWork,
                new CoordinatedStep("dither-like", _ =>
                {
                    atSafePointDuringOperation.Add(coordinator.GetStatus(Session).AtSafePoint.Count);
                    return Task.CompletedTask;
                })])),
        ], Session);

        var run = Runner(host).RunAsync(new Sequence("s", [parallel]));
        for (var round = 1; round <= 3; round++)
        {
            // Round n: the wide branch has done its n-th exposure (so round n-1 is over) and its request is pending.
            await WaitUntil(
                () => wideWork.Executions == round && coordinator.GetStatus(Session).RequestPending,
                $"request of round {round}");
            mainWork.Release(); // the main branch finishes its exposure and reaches the safe point
            await WaitUntil(() => atSafePointDuringOperation.Count == round, $"operation of round {round}");
        }

        await run.WaitAsync(Bound);

        Assert.Equal(new[] { 1, 1, 1 }, atSafePointDuringOperation); // the main branch was safe every time
        Assert.Equal(3, mainAfter.Executions);
        AssertNothingLeftBehind(host);
    }
}

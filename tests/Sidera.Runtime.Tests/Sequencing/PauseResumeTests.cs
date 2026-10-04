using Sidera.Core.Coordination;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Sequencing;

public class PauseResumeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);
    private static readonly CoordinationGroupId Group = new("session.pause");
    private static readonly ResourceId R1 = new("device:camera.main");
    private static readonly ResourceId R2 = new("device:camera.wide");

    /// <summary>A step that signals when it starts and ends when the test releases it, or fails when told to.</summary>
    private sealed class GateStep(string name, ResourceId? resource = null) : IResourceAwareSequenceStep
    {
        private int _executions;
        private int _running;

        public string Name { get; } = name;
        public IReadOnlyCollection<ResourceId> RequiredResources { get; } = resource is { } r ? [r] : [];
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public Exception? FailWhenReleased { get; init; }
        public int Executions => _executions;
        public int Running => _running;
        public bool SawCancellation { get; private set; }

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            Interlocked.Increment(ref _running);
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                if (FailWhenReleased is not null)
                {
                    throw FailWhenReleased;
                }

                return new SequenceStepResult();
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    /// <summary>Runs, counts, and finishes at once.</summary>
    private sealed class WorkStep(string name, ResourceId? resource = null) : IResourceAwareSequenceStep
    {
        private int _executions;
        public string Name { get; } = name;
        public IReadOnlyCollection<ResourceId> RequiredResources { get; } = resource is { } r ? [r] : [];
        public int Executions => _executions;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new SequenceStepResult());
        }
    }

    /// <summary>Waits for the test, then asks for a coordinated operation, like a step that decides late to dither.</summary>
    private sealed class LateRequestStep(Func<CancellationToken, Task> operation) : ISequenceStep
    {
        public string Name => "late request";
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource Go { get; } = NewSignal();

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Go.Task.WaitAsync(cancellationToken);
            await context.ExecuteWhenSafeAsync(operation, cancellationToken);
            return new SequenceStepResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static Task WaitForState(SequenceRunner runner, SequenceState state) =>
        WaitUntil(() => runner.State == state, $"state {state} (is {runner.State})");

    // Basics

    [Fact]
    public async Task Pause_LetsTheRunningStepFinish_ThenStopsBeforeTheNextOne_AndResumeContinues()
    {
        var first = new GateStep("first");
        var second = new WorkStep("second");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [first, second]));
        await first.Started.Task.WaitAsync(Bound);

        Assert.True(runner.RequestPause());
        Assert.Equal(SequenceState.Pausing, runner.State);
        Assert.True(runner.IsRunning);
        Assert.Equal(1, first.Running); // nothing was interrupted

        first.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(0, second.Executions);
        Assert.Equal("second", Assert.Single(runner.PausedPositions).StepName);
        Assert.DoesNotContain(runner.ActivePositions, p => p.StepName == "second");
        Assert.True(runner.IsRunning);

        Assert.True(runner.Resume());
        await run.WaitAsync(Bound);

        Assert.Equal(1, second.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Empty(runner.PausedPositions);
    }

    [Fact]
    public async Task RequestPause_IsIdempotent_AndDoesNothingWithoutARun()
    {
        var runner = new SequenceRunner();
        Assert.False(runner.RequestPause());
        Assert.Equal(SequenceState.Idle, runner.State);

        var step = new GateStep("a");
        var run = runner.RunAsync(new Sequence("s", [step, new WorkStep("b")]));
        await step.Started.Task.WaitAsync(Bound);

        Assert.True(runner.RequestPause());
        Assert.False(runner.RequestPause()); // already requested
        Assert.Equal(SequenceState.Pausing, runner.State);

        step.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);
        Assert.False(runner.RequestPause()); // already paused
        Assert.Equal(SequenceState.Paused, runner.State);

        runner.Resume();
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task Resume_WhenNotPaused_DoesNotCorruptTheRun()
    {
        var runner = new SequenceRunner();
        Assert.False(runner.Resume()); // idle

        var step = new GateStep("a");
        var after = new WorkStep("b");
        var run = runner.RunAsync(new Sequence("s", [step, after]));
        await step.Started.Task.WaitAsync(Bound);

        Assert.False(runner.Resume()); // running, nothing requested
        Assert.Equal(SequenceState.Running, runner.State);

        step.Release.SetResult();
        await run.WaitAsync(Bound);
        Assert.Equal(1, after.Executions);
        Assert.False(runner.Resume()); // completed
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task Resume_WhileStillPausing_WithdrawsTheRequest()
    {
        var step = new GateStep("a");
        var after = new WorkStep("b");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [step, after]));
        await step.Started.Task.WaitAsync(Bound);
        runner.RequestPause();

        Assert.True(runner.Resume());
        Assert.Equal(SequenceState.Running, runner.State);
        step.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(1, after.Executions); // it never paused
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task PauseAsync_CompletesWhenPaused_AndWithFalseWhenTheRunEndsFirst()
    {
        var step = new GateStep("a");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [step, new WorkStep("b")]));
        await step.Started.Task.WaitAsync(Bound);

        var paused = runner.PauseAsync();
        Assert.False(paused.IsCompleted);
        step.Release.SetResult();
        Assert.True(await paused.WaitAsync(Bound));
        Assert.Equal(SequenceState.Paused, runner.State);
        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.False(await runner.PauseAsync().WaitAsync(Bound)); // nothing running any more

        var last = new GateStep("only");
        var run2 = runner.RunAsync(new Sequence("s2", [last]));
        await last.Started.Task.WaitAsync(Bound);
        var pauseAtTheEnd = runner.PauseAsync();
        last.Release.SetResult();
        await run2.WaitAsync(Bound);
        Assert.False(await pauseAtTheEnd.WaitAsync(Bound)); // the run ended before there was a boundary
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task APauseRequestedDuringTheLastStep_DoesNotLeakIntoTheNextRun()
    {
        var last = new GateStep("only");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [last]));
        await last.Started.Task.WaitAsync(Bound);
        runner.RequestPause();
        last.Release.SetResult();
        await run.WaitAsync(Bound);

        var next = new WorkStep("next");
        await runner.RunAsync(new Sequence("again", [next, new WorkStep("more")])).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(1, next.Executions);
    }

    // Exposure

    [Fact]
    public async Task Pause_DuringAnExposure_LetsItFinish_AndDoesNotStartTheNextExposureUntilResumed()
    {
        await using var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main") { Block = true };
        host.AddDevice(camera);
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var frames = 0;
        runner.StepCompleted += (_, e) =>
        {
            if (e.Result.Payload is Sidera.Core.Devices.CameraFrame)
            {
                Interlocked.Increment(ref frames);
            }
        };
        var sequence = new Sequence("s", [
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Short),
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Short),
        ]);

        var run = runner.RunAsync(sequence);
        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        runner.RequestPause();

        Assert.Equal(SequenceState.Pausing, runner.State);
        Assert.Equal(0, frames);                 // the exposure is still going on
        Assert.True(host.ResourceManager.IsHeld(R1));

        camera.GateOf("expose", 1).Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(1, frames);                 // it completed and produced its frame
        Assert.Equal(1, camera.ExposeCalls);     // the next one has not started
        Assert.False(host.ResourceManager.IsHeld(R1));

        runner.Resume();
        await camera.GateOf("expose", 2).Started.Task.WaitAsync(Bound);
        camera.GateOf("expose", 2).Release.SetResult();
        await run.WaitAsync(Bound);
        Assert.Equal(2, frames);
    }

    // Containers

    [Fact]
    public async Task Pause_BetweenRepeatIterations_AndResumeRunsTheRemainingOnes()
    {
        var step = new GateStep("iteration");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [new RepeatStep(3, step)]));

        await WaitUntil(() => step.Executions == 1, "iteration 1");
        runner.RequestPause();
        step.Release.SetResult(); // the gate is shared; later iterations pass straight through it
        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(1, step.Executions);        // iteration 2 has not started

        var waiting = Assert.Single(runner.PausedPositions);
        Assert.Equal((1, 3), (waiting.Index, waiting.Count)); // it is iteration 2 of 3 that waits

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(3, step.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task Pause_BetweenGroupChildren_ResumesWithTheNextChild()
    {
        var a = new GateStep("a");
        var b = new WorkStep("b");
        var c = new WorkStep("c");
        var order = new List<string>();
        var runner = new SequenceRunner();
        runner.StepCompleted += (_, e) =>
        {
            lock (order)
            {
                order.Add(e.StepName);
            }
        };
        var run = runner.RunAsync(new Sequence("s", [new SequenceGroup("g", [a, b, c])]));
        await a.Started.Task.WaitAsync(Bound);
        runner.RequestPause();
        a.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal("b", Assert.Single(runner.PausedPositions).StepName);
        Assert.Equal(0, b.Executions);

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(new[] { "a", "b", "c", "g" }, order);
    }

    // Parallel

    [Fact]
    public async Task Pause_InAParallelStep_IsPausedOnlyWhenEveryBranchWaits_AndResumeReleasesAll()
    {
        var a1 = new GateStep("a1");
        var a2 = new WorkStep("a2");
        var b1 = new GateStep("b1");
        var b2 = new WorkStep("b2");
        var runner = new SequenceRunner();
        var parallel = new ParallelStep("p", [new SequenceGroup("A", [a1, a2]), new SequenceGroup("B", [b1, b2])]);
        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await Task.WhenAll(a1.Started.Task, b1.Started.Task).WaitAsync(Bound);

        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State);

        b1.Release.SetResult();                  // branch B reaches its boundary first
        await WaitUntil(() => runner.PausedPositions.Any(p => p.StepName == "b2"), "branch B waiting");
        Assert.Equal(SequenceState.Pausing, runner.State); // A still runs its step: not paused yet
        Assert.Equal(1, a1.Running);

        a1.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(new[] { "a2", "b2" }, runner.PausedPositions.Select(p => p.StepName).Order());
        Assert.Equal(0, a2.Executions + b2.Executions); // no branch starts new work while paused

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(1, a2.Executions);
        Assert.Equal(1, b2.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task ABranchThatEndsWhilePausing_NoLongerHoldsUpThePause()
    {
        var a = new GateStep("a");              // branch A has only this step
        var b1 = new GateStep("b1");
        var b2 = new WorkStep("b2");
        var runner = new SequenceRunner();
        var parallel = new ParallelStep("p", [a, new SequenceGroup("B", [b1, b2])]);
        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await Task.WhenAll(a.Started.Task, b1.Started.Task).WaitAsync(Bound);
        runner.RequestPause();

        a.Release.SetResult();                   // A ends: no boundary left for it
        await WaitUntil(() => a.Running == 0, "a finished");
        Assert.Equal(SequenceState.Pausing, runner.State); // B still runs

        b1.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(0, b2.Executions);

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(1, b2.Executions);
    }

    [Fact]
    public async Task ParallelBranches_AreNotSerialized_ByAPauseThatIsLaterResumed()
    {
        var a = new GateStep("a");
        var b = new GateStep("b");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);

        runner.RequestPause();
        runner.Resume();
        a.Release.SetResult();
        b.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
    }

    // ResourceManager

    [Fact]
    public async Task PausedBranches_HoldNoResourcesOfTheirNextSteps_AndUnrelatedUsersGetThem()
    {
        var manager = new ResourceManager();
        var first = new GateStep("first", R1);
        var second = new GateStep("second", R2);
        var runner = new SequenceRunner(manager);
        var run = runner.RunAsync(new Sequence("s", [first, second]));
        await first.Started.Task.WaitAsync(Bound);
        runner.RequestPause();
        first.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.False(manager.IsHeld(R1));
        Assert.False(manager.IsHeld(R2));        // the next step has not taken its resource
        Assert.Equal(0, manager.WaitingCount);

        // An unrelated user can take the resource the paused step will need, at once.
        using (var lease = await manager.AcquireAsync([R2]).WaitAsync(Bound))
        {
            Assert.True(manager.IsHeld(R2));
        }

        runner.Resume();
        await second.Started.Task.WaitAsync(Bound);
        Assert.True(manager.IsHeld(R2));
        second.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.False(manager.IsHeld(R1));
        Assert.False(manager.IsHeld(R2));
        Assert.Equal(0, manager.WaitingCount);
    }

    [Fact]
    public async Task APauseWhileAStepWaitsForItsResource_LetsThatStepRunAndPausesAfterwards()
    {
        var manager = new ResourceManager();
        var holder = await manager.AcquireAsync([R1]);
        var step = new GateStep("needs R1", R1);
        var next = new WorkStep("next");
        var runner = new SequenceRunner(manager);
        var run = runner.RunAsync(new Sequence("s", [step, next]));
        await WaitUntil(() => manager.WaitingCount == 1, "step waiting for the resource");

        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State); // it already passed its boundary

        holder.Dispose();
        await step.Started.Task.WaitAsync(Bound);
        step.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(0, next.Executions);
        runner.Resume();
        await run.WaitAsync(Bound);
    }

    // Cancellation

    [Fact]
    public async Task Cancel_WhilePausing_EndsTheRun_WithoutNeedingAResume()
    {
        var step = new GateStep("a", R1);
        var manager = new ResourceManager();
        var runner = new SequenceRunner(manager);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(new Sequence("s", [step, new WorkStep("b")]), cts.Token);
        await step.Started.Task.WaitAsync(Bound);
        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(manager.IsHeld(R1));
        Assert.Empty(runner.ActivePositions);
        Assert.Empty(runner.PausedPositions);
    }

    [Fact]
    public async Task Cancel_WhilePaused_UnblocksEveryWaitingBranch_AndLeavesNothingBehind()
    {
        var manager = new ResourceManager();
        var coordinator = new Sidera.Runtime.Coordination.SafePointCoordinator();
        var a1 = new GateStep("a1", R1);
        var b1 = new GateStep("b1", R2);
        var a2 = new WorkStep("a2", R1);
        var b2 = new WorkStep("b2", R2);
        var runner = new SequenceRunner(manager, coordinator);
        using var cts = new CancellationTokenSource();
        var parallel = new ParallelStep(
            "p", [new SequenceGroup("A", [a1, a2]), new SequenceGroup("B", [b1, b2])], Group);
        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);
        await Task.WhenAll(a1.Started.Task, b1.Started.Task).WaitAsync(Bound);
        runner.RequestPause();
        a1.Release.SetResult();
        b1.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(2, runner.PausedPositions.Count);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, a2.Executions + b2.Executions);
        Assert.False(manager.IsHeld(R1));
        Assert.False(manager.IsHeld(R2));
        var status = coordinator.GetStatus(Group);
        Assert.Empty(status.Participants);
        Assert.False(status.RequestPending);
        Assert.Empty(runner.PausedPositions);

        // A fresh run on the same runner is not paused.
        var fresh = new WorkStep("fresh");
        await runner.RunAsync(new Sequence("again", [fresh, new WorkStep("more")])).WaitAsync(Bound);
        Assert.Equal(1, fresh.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    // Failure

    [Fact]
    public async Task AnActionThatFailsWhilePausing_EndsTheRunAsFailed_NotPaused()
    {
        var failure = new InvalidOperationException("device failed");
        var step = new GateStep("a") { FailWhenReleased = failure };
        var next = new WorkStep("b");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [step, next]));
        await step.Started.Task.WaitAsync(Bound);
        runner.RequestPause();

        step.Release.SetResult();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound));
        Assert.Same(failure, error);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(0, next.Executions);
        Assert.Empty(runner.PausedPositions);
    }

    [Fact]
    public async Task ABranchFailing_WhileASiblingWaitsAtThePauseBoundary_UnblocksTheSibling()
    {
        var failure = new InvalidOperationException("branch A failed");
        var a = new GateStep("a") { FailWhenReleased = failure };
        var b1 = new GateStep("b1");
        var b2 = new WorkStep("b2");
        var runner = new SequenceRunner();
        var run = runner.RunAsync(new Sequence("s", [
            new ParallelStep("p", [a, new SequenceGroup("B", [b1, b2])]),
        ]));
        await Task.WhenAll(a.Started.Task, b1.Started.Task).WaitAsync(Bound);
        runner.RequestPause();
        b1.Release.SetResult();
        await WaitUntil(() => runner.PausedPositions.Any(p => p.StepName == "b2"), "branch B waiting");
        Assert.Equal(SequenceState.Pausing, runner.State);

        a.Release.SetResult(); // A fails while B already waits at the boundary

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound));
        Assert.Same(failure, error);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(0, b2.Executions);
        Assert.Empty(runner.PausedPositions);
        Assert.Empty(runner.ActivePositions);
    }

    // Coordination (without devices)

    [Fact]
    public async Task APendingCoordinatedOperation_IsNotStuckByAPause_ItsBranchesAreLetThroughToTheirSafePoint()
    {
        var coordinator = new Sidera.Runtime.Coordination.SafePointCoordinator();
        var manager = new ResourceManager();
        var operationRuns = 0;
        var lateRequest = new LateRequestStep(_ =>
        {
            Interlocked.Increment(ref operationRuns);
            return Task.CompletedTask;
        });
        var b1 = new GateStep("b1");
        var b2 = new WorkStep("b2");
        var a2 = new WorkStep("a2");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("A", [lateRequest, a2]),
            new SequenceGroup("B", [b1, new SafePointStep(), b2]),
        ], Group);
        var runner = new SequenceRunner(manager, coordinator);
        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await Task.WhenAll(lateRequest.Started.Task, b1.Started.Task).WaitAsync(Bound);

        runner.RequestPause();
        b1.Release.SetResult();                  // B stops at its boundary before the safe point
        await WaitUntil(() => runner.PausedPositions.Any(p => p.StepName == "Safe Point"), "B waiting before its safe point");
        Assert.Equal(SequenceState.Pausing, runner.State); // A is still inside its step

        lateRequest.Go.SetResult();              // only now a coordinated operation is requested

        // B is let through to the safe point so the operation can run; both branches stop afterwards.
        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(1, operationRuns);
        Assert.False(coordinator.GetStatus(Group).RequestPending);
        Assert.Equal(0, a2.Executions + b2.Executions);

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(1, operationRuns);
        Assert.Equal(1, a2.Executions);
        Assert.Equal(1, b2.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task APauseBeforeASafePoint_StopsTheBranchThereLikeBeforeAnyStep_AndNoRoundIsStarted()
    {
        var coordinator = new Sidera.Runtime.Coordination.SafePointCoordinator();
        var a = new GateStep("a");
        var b = new GateStep("b");
        var after = new WorkStep("after");
        var parallel = new ParallelStep("p", [
            new SequenceGroup("A", [a, new SafePointStep(), after]),
            new SequenceGroup("B", [b, new SafePointStep(), new WorkStep("b after")]),
        ], Group);
        var runner = new SequenceRunner(new ResourceManager(), coordinator);
        var run = runner.RunAsync(new Sequence("s", [parallel]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);

        runner.RequestPause();
        a.Release.SetResult();
        b.Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(new[] { "Safe Point", "Safe Point" }, runner.PausedPositions.Select(p => p.StepName));
        Assert.False(coordinator.GetStatus(Group).RequestPending);
        Assert.Equal(0, after.Executions);

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(1, after.Executions);
    }
}

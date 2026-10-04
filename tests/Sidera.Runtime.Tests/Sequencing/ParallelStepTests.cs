using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Sequencing;

public class ParallelStepTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(10);

    private static readonly ResourceId R1 = new("device:camera.main");
    private static readonly ResourceId R2 = new("device:camera.wide");

    /// <summary>
    /// A branch whose progress the test controls. It signals when it starts, and either blocks until released,
    /// finishes at once, or throws, and it records whether it saw cancellation and whether it is still running.
    /// </summary>
    private sealed class BranchStep(string name, ResourceId? resource = null) : IResourceAwareSequenceStep
    {
        private int _executions;
        private int _running;

        public string Name { get; } = name;
        public IReadOnlyCollection<ResourceId> RequiredResources { get; } = resource is { } r ? [r] : [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Blocks { get; init; } = true;

        /// <summary>Thrown as soon as <see cref="ThrowAfter"/> (if any) has completed.</summary>
        public Exception? Throw { get; init; }
        public Task? ThrowAfter { get; init; }

        /// <summary>Thrown when the test releases the step.</summary>
        public Exception? ThrowOnRelease { get; init; }

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
                if (Throw is not null)
                {
                    if (ThrowAfter is not null)
                    {
                        await ThrowAfter.WaitAsync(Bound);
                    }

                    throw Throw;
                }

                if (Blocks)
                {
                    await Release.Task.WaitAsync(cancellationToken);
                }

                if (ThrowOnRelease is not null)
                {
                    throw ThrowOnRelease;
                }

                return new SequenceStepResult(Name);
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

    private static List<SequenceStepCompletedEventArgs> Observe(SequenceRunner runner)
    {
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completed)
            {
                completed.Add(e);
            }
        };
        return completed;
    }

    private static string PathOf(SequenceExecutionPosition p)
    {
        var parts = new List<string>();
        for (var x = p; x is not null; x = x.Parent)
        {
            parts.Insert(0, $"{x.StepName} {x.Index + 1}/{x.Count}");
        }

        return string.Join(" > ", parts);
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

    // Model

    [Fact]
    public void Constructor_RejectsNullOrEmptyName()
    {
        var children = new ISequenceStep[] { new BranchStep("a"), new BranchStep("b") };

        Assert.ThrowsAny<ArgumentException>(() => new ParallelStep(null!, children));
        Assert.Throws<ArgumentException>(() => new ParallelStep("", children));
        Assert.Throws<ArgumentException>(() => new ParallelStep("   ", children));
    }

    [Fact]
    public void Constructor_RejectsFewerThanTwoChildren_AndNulls()
    {
        Assert.Throws<ArgumentNullException>(() => new ParallelStep("p", null!));
        Assert.Throws<ArgumentException>(() => new ParallelStep("p", []));
        Assert.Throws<ArgumentException>(() => new ParallelStep("p", [new BranchStep("a")]));
        Assert.Throws<ArgumentException>(() => new ParallelStep("p", [new BranchStep("a"), null!]));
    }

    [Fact]
    public void Properties_ExposeNameAndChildrenInOrder()
    {
        var a = new BranchStep("a");
        var b = new BranchStep("b");

        var parallel = new ParallelStep("p", [a, b]);

        Assert.Equal("p", parallel.Name);
        Assert.Equal(new ISequenceStep[] { a, b }, parallel.Children);
    }

    // Concurrency

    [Fact]
    public async Task TwoDelayActions_RunConcurrently()
    {
        var runner = new SequenceRunner();
        using var cts = new CancellationTokenSource();
        var parallel = new ParallelStep("p", [new DelayAction(TimeSpan.FromSeconds(30)), new DelayAction(TimeSpan.FromSeconds(30))]);

        var run = runner.RunAsync(new Sequence("s", [parallel]), cts.Token);

        // Both waits are in progress at the same moment.
        await WaitUntil(() => runner.ActivePositions.Count(p => p.StepName == "Wait 30s") == 2, "both waits active");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
    }

    [Fact]
    public async Task TwoExposuresOnDifferentCameras_RunConcurrently()
    {
        await using var host = new SideraRuntimeHost();
        var main = new FakeCamera("camera.main") { Block = true };
        var wide = new FakeCamera("camera.wide") { Block = true };
        host.AddDevice(main);
        host.AddDevice(wide);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, main.Id, Short),
            new CameraExposureAction(host.DeviceRegistry, wide.Id, Short),
        ]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await Task.WhenAll(
            main.GateOf("expose", 1).Started.Task,
            wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);
        Assert.Equal(0, host.ResourceManager.WaitingCount);

        main.GateOf("expose", 1).Release.SetResult();
        wide.GateOf("expose", 1).Release.SetResult();
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task TwoExposuresOnTheSameCamera_AreSerializedByTheResourceManager()
    {
        await using var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main") { Block = true };
        host.AddDevice(camera);
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Short),
            new CameraExposureAction(host.DeviceRegistry, camera.Id, Short),
        ]);

        var run = runner.RunAsync(new Sequence("s", [parallel]));

        await camera.GateOf("expose", 1).Started.Task.WaitAsync(Bound);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second branch waiting for the camera");
        Assert.Equal(1, camera.ExposeCalls); // the second branch has not reached the device

        camera.GateOf("expose", 1).Release.SetResult();
        await camera.GateOf("expose", 2).Started.Task.WaitAsync(Bound);
        camera.GateOf("expose", 2).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(2, camera.ExposeCalls);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    // Active positions and Changed

    [Fact]
    public async Task ActivePositions_ContainBothBranches_AndDisappearWhenTheyFinish()
    {
        var runner = new SequenceRunner();
        var a = new BranchStep("A");
        var b = new BranchStep("B");

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);

        var active = runner.ActivePositions;
        Assert.Equal(new[] { "A", "B", "p" }, active.Select(x => x.StepName).Order());
        var branches = active.Where(x => x.Parent is not null).OrderBy(x => x.Index).ToArray();
        Assert.Equal(new[] { 0, 1 }, branches.Select(x => x.Index));
        Assert.All(branches, x =>
        {
            Assert.Equal(2, x.Count);
            Assert.Equal("p", x.Parent!.StepName);
        });

        a.Release.SetResult();
        b.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Empty(runner.ActivePositions);
    }

    [Fact]
    public async Task Changed_FiresWhenBranchesStartAndFinish()
    {
        var runner = new SequenceRunner();
        var snapshots = new List<int>();
        runner.Changed += (_, _) =>
        {
            lock (snapshots)
            {
                snapshots.Add(runner.ActivePositions.Count);
            }
        };
        var a = new BranchStep("A");
        var b = new BranchStep("B");

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);
        int[] whileRunning;
        lock (snapshots)
        {
            whileRunning = snapshots.ToArray();
        }

        a.Release.SetResult();
        b.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Contains(3, whileRunning); // parallel step plus two branches were active together
        lock (snapshots)
        {
            Assert.Equal(0, snapshots[^1]);
            Assert.True(snapshots.Count > whileRunning.Length); // finishing branches fired Changed too
        }
    }

    // Results

    [Fact]
    public async Task StepCompleted_FollowsRealCompletionOrder_WhileAggregateKeepsDefinitionOrder()
    {
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var a = new BranchStep("A");
        var b = new BranchStep("B");
        var bDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.StepCompleted += (_, e) =>
        {
            if (e.StepName == "B")
            {
                bDone.TrySetResult();
            }
        };

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);

        b.Release.SetResult();          // B completes first even though A is first in the definition
        await bDone.Task.WaitAsync(Bound);
        a.Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(new[] { "B", "A", "p" }, completed.Select(c => c.StepName));
        var aggregate = Assert.IsAssignableFrom<IReadOnlyList<SequenceStepResult>>(completed[2].Result.Payload);
        Assert.Equal(new object?[] { "A", "B" }, aggregate.Select(r => r.Payload));
        Assert.Same(completed[1].Result, aggregate[0]);
        Assert.Same(completed[0].Result, aggregate[1]);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task FramesFromBothCameras_AreDistinctAndObservable()
    {
        await using var host = new SideraRuntimeHost();
        var main = new FakeCamera("camera.main");
        var wide = new FakeCamera("camera.wide");
        host.AddDevice(main);
        host.AddDevice(wide);
        var runner = new SequenceRunner(host.ResourceManager);
        var completed = Observe(runner);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, main.Id, TimeSpan.FromSeconds(3)),
            new CameraExposureAction(host.DeviceRegistry, wide.Id, TimeSpan.FromSeconds(1)),
        ]);

        await runner.RunAsync(new Sequence("s", [parallel]));

        var frames = completed.Select(c => c.Result.Payload).OfType<CameraFrame>().ToArray();
        Assert.Equal(2, frames.Length);
        Assert.NotSame(frames[0], frames[1]);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) },
            frames.Select(f => f.ExposureDuration).Order());
    }

    // Failure

    [Fact]
    public async Task FailureInOneBranch_CancelsSibling_AwaitsIt_AndPreservesTheOriginalFailure()
    {
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var b = new BranchStep("B");
        var failure = new InvalidOperationException("A failed");
        var a = new BranchStep("A") { Throw = failure, ThrowAfter = b.Started.Task };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])])));

        Assert.Same(failure, error);
        Assert.Same(failure, runner.Failure);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.True(b.SawCancellation);             // the sibling was cancelled...
        Assert.Equal(0, b.Running);                 // ...and had fully stopped before RunAsync returned
        Assert.Empty(completed);                    // no completion for A, B or the parallel step
        Assert.Empty(runner.ActivePositions);
    }

    [Fact]
    public async Task SiblingCancellation_IsNotReportedAsTheFailure()
    {
        var runner = new SequenceRunner();
        var b = new BranchStep("B");
        var a = new BranchStep("A") { Throw = new InvalidOperationException("real cause"), ThrowAfter = b.Started.Task };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [new ParallelStep("p", [b, a])]))); // failing branch is not first

        Assert.Equal("real cause", error.Message);
        Assert.IsNotType<OperationCanceledException>(runner.Failure);
    }

    [Fact]
    public async Task SeveralIndependentFailures_AreReportedTogetherInDefinitionOrder()
    {
        var runner = new SequenceRunner();
        var aStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new BranchStep("A") { Throw = new InvalidOperationException("A failed"), ThrowAfter = bStarted.Task };
        var b = new BranchStep("B") { Throw = new ArgumentException("B failed"), ThrowAfter = aStarted.Task };
        _ = a.Started.Task.ContinueWith(_ => aStarted.TrySetResult());
        _ = b.Started.Task.ContinueWith(_ => bStarted.TrySetResult());

        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])])));

        Assert.Equal(new[] { "A failed", "B failed" }, error.InnerExceptions.Select(e => e.Message));
        Assert.IsType<InvalidOperationException>(error.InnerExceptions[0]);
        Assert.IsType<ArgumentException>(error.InnerExceptions[1]);
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    // Cancellation

    [Fact]
    public async Task UserCancellation_CancelsAndAwaitsAllBranches_AndRunnerEndsCancelled()
    {
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        using var cts = new CancellationTokenSource();
        var a = new BranchStep("A");
        var b = new BranchStep("B");

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]), cts.Token);
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Null(runner.Failure);
        Assert.True(a.SawCancellation);
        Assert.True(b.SawCancellation);
        Assert.Equal(0, a.Running);
        Assert.Equal(0, b.Running);
        Assert.Empty(completed);
        Assert.Empty(runner.ActivePositions);
    }

    // Resources

    [Fact]
    public async Task BranchesWithDifferentResources_RunTogether_AndSameResourceWaits()
    {
        var manager = new ResourceManager();
        var runner = new SequenceRunner(manager);
        var a = new BranchStep("A", R1);
        var b = new BranchStep("B", R2);

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [a, b])]));
        await Task.WhenAll(a.Started.Task, b.Started.Task).WaitAsync(Bound);
        Assert.Equal(0, manager.WaitingCount);
        Assert.True(manager.IsHeld(R1));
        Assert.True(manager.IsHeld(R2));
        a.Release.SetResult();
        b.Release.SetResult();
        await run.WaitAsync(Bound);

        var same1 = new BranchStep("C", R1);
        var same2 = new BranchStep("D", R1);
        var run2 = runner.RunAsync(new Sequence("s2", [new ParallelStep("p2", [same1, same2])]));
        await same1.Started.Task.WaitAsync(Bound);
        await WaitUntil(() => manager.WaitingCount == 1, "second branch waits for the shared resource");
        Assert.Equal(0, same2.Executions);

        same1.Release.SetResult();
        await same2.Started.Task.WaitAsync(Bound);
        same2.Release.SetResult();
        await run2.WaitAsync(Bound);
        Assert.False(manager.IsHeld(R1));
    }

    [Fact]
    public async Task FailingBranchReleasesItsResource_AndCancelsTheWaitingSibling()
    {
        var manager = new ResourceManager();
        var runner = new SequenceRunner(manager);
        var holder = new BranchStep("holder", R1) { ThrowOnRelease = new InvalidOperationException("holder failed") };
        var waiting = new BranchStep("waiting", R1);

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [holder, waiting])]));
        await holder.Started.Task.WaitAsync(Bound);
        await WaitUntil(() => manager.WaitingCount == 1, "sibling waiting");
        holder.Release.SetResult(); // the holder now fails while holding the resource

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(Bound));

        Assert.Equal("holder failed", error.Message);
        // It may receive the resource just as the holder fails, but it must not complete or stay running.
        Assert.True(waiting.Executions == 0 || waiting.SawCancellation);
        Assert.Equal(0, waiting.Running);
        Assert.Equal(0, manager.WaitingCount);
        Assert.False(manager.IsHeld(R1));
    }

    [Fact]
    public async Task WaitingSibling_CanBeCancelledCleanlyByTheUser()
    {
        var manager = new ResourceManager();
        var runner = new SequenceRunner(manager);
        using var cts = new CancellationTokenSource();
        var holder = new BranchStep("holder", R1);
        var waiting = new BranchStep("waiting", R1);

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [holder, waiting])]), cts.Token);
        await holder.Started.Task.WaitAsync(Bound);
        await WaitUntil(() => manager.WaitingCount == 1, "sibling waiting");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, waiting.Executions);
        Assert.Equal(0, manager.WaitingCount);
        Assert.False(manager.IsHeld(R1));
    }

    // Composition

    [Fact]
    public async Task GroupContainingParallel_Works()
    {
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var before = new BranchStep("before") { Blocks = false };
        var a = new BranchStep("A") { Blocks = false };
        var b = new BranchStep("B") { Blocks = false };
        var group = new SequenceGroup("g", [before, new ParallelStep("p", [a, b])]);

        await runner.RunAsync(new Sequence("s", [group]));

        Assert.Equal(1, before.Executions);
        Assert.Equal(1, a.Executions);
        Assert.Equal(1, b.Executions);
        Assert.Equal("before", completed.First().StepName);
        Assert.Equal("g", completed.Last().StepName);
        Assert.Equal("g 1/1 > p 2/2 > A 1/2", PathOf(completed.Single(c => c.StepName == "A").Position));
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task RepeatContainingParallel_RunsBothBranchesEveryIteration()
    {
        var runner = new SequenceRunner();
        var a = new BranchStep("A") { Blocks = false };
        var b = new BranchStep("B") { Blocks = false };

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, new ParallelStep("p", [a, b]))]));

        Assert.Equal(3, a.Executions);
        Assert.Equal(3, b.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Empty(runner.ActivePositions);
    }

    [Fact]
    public async Task ParallelContainingGroup_Works()
    {
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var x = new BranchStep("X") { Blocks = false };
        var y = new BranchStep("Y") { Blocks = false };
        var z = new BranchStep("Z") { Blocks = false };

        await runner.RunAsync(new Sequence("s", [new ParallelStep("p", [new SequenceGroup("g", [x, y]), z])]));

        Assert.Equal("p 1/1 > g 1/2 > Y 2/2", PathOf(completed.Single(c => c.StepName == "Y").Position));
        Assert.Equal("p 1/1 > Z 2/2", PathOf(completed.Single(c => c.StepName == "Z").Position));
        // Inside the group X always precedes Y.
        var names = completed.Select(c => c.StepName).ToList();
        Assert.True(names.IndexOf("X") < names.IndexOf("Y"));
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    // Host

    [Fact]
    public async Task TwoRunnersAndAParallelStep_ShareTheHostResourceManagerSafely()
    {
        await using var host = new SideraRuntimeHost();
        var main = new FakeCamera("camera.main") { Block = true };
        var wide = new FakeCamera("camera.wide") { Block = true };
        host.AddDevice(main);
        host.AddDevice(wide);
        var runnerOne = new SequenceRunner(host.ResourceManager);
        var runnerTwo = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [
            new CameraExposureAction(host.DeviceRegistry, main.Id, Short),
            new CameraExposureAction(host.DeviceRegistry, wide.Id, Short),
        ]);

        var runOne = runnerOne.RunAsync(new Sequence("one", [parallel]));
        await Task.WhenAll(
            main.GateOf("expose", 1).Started.Task,
            wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);
        var runTwo = runnerTwo.RunAsync(new Sequence("two", [new CameraExposureAction(host.DeviceRegistry, main.Id, Short)]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second runner waits for camera.main");
        Assert.Equal(1, main.ExposeCalls);

        wide.GateOf("expose", 1).Release.SetResult();
        main.GateOf("expose", 1).Release.SetResult();
        await runOne.WaitAsync(Bound);
        await main.GateOf("expose", 2).Started.Task.WaitAsync(Bound);
        main.GateOf("expose", 2).Release.SetResult();
        await runTwo.WaitAsync(Bound);

        Assert.Equal(2, main.ExposeCalls);
        Assert.Equal(1, wide.ExposeCalls);
        Assert.False(host.ResourceManager.IsHeld(ResourceId.ForDevice(main.Id)));
    }
}

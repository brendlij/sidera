using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Resources;

public class ResourceSequencingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static readonly ResourceId CameraMain = ResourceId.ForDevice(new DeviceId("camera.main"));
    private static readonly ResourceId CameraWide = ResourceId.ForDevice(new DeviceId("camera.wide"));

    /// <summary>
    /// A step needing <c>resource</c> that signals when it starts and then blocks until released,
    /// so tests control exactly when it finishes without sleeping.
    /// </summary>
    private sealed class GateStep(string name, ResourceId? resource, Action? onExecute = null) : IResourceAwareSequenceStep
    {
        public string Name { get; } = name;
        public IReadOnlyCollection<ResourceId> RequiredResources { get; } = resource is { } r ? [r] : [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Blocks { get; init; } = true;
        public int Executions { get; private set; }
        public Exception? Throw { get; init; }

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Executions++;
            onExecute?.Invoke();
            Started.TrySetResult();

            if (Throw is not null)
            {
                throw Throw;
            }

            if (Blocks)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new SequenceStepResult();
        }
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

    // Declarations

    [Fact]
    public void CameraExposureAction_DeclaresItsCameraResource()
    {
        var action = new CameraExposureAction(new Sidera.Runtime.Devices.DeviceRegistry(), new DeviceId("camera.main"), TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { new ResourceId("device:camera.main") }, action.RequiredResources);
    }

    [Fact]
    public void ConnectAndDisconnectActions_DeclareTheirTargetDeviceResource()
    {
        var registry = new Sidera.Runtime.Devices.DeviceRegistry();
        var id = new DeviceId("focuser.main");

        Assert.Equal(new[] { new ResourceId("device:focuser.main") }, new ConnectDeviceAction(registry, id).RequiredResources);
        Assert.Equal(new[] { new ResourceId("device:focuser.main") }, new DisconnectDeviceAction(registry, id).RequiredResources);
    }

    [Fact]
    public void DelayAction_DeclaresNoResources_AndContainersDeclareNone()
    {
        var delay = new DelayAction(TimeSpan.FromSeconds(1));

        Assert.False(((ISequenceStep)delay) is IResourceAwareSequenceStep);
        Assert.False(((ISequenceStep)new RepeatStep(2, delay)) is IResourceAwareSequenceStep);
        Assert.False(((ISequenceStep)new SequenceGroup("g", [delay])) is IResourceAwareSequenceStep);
    }

    // Runners sharing a manager

    [Fact]
    public async Task RunnersUsingDifferentCameras_ExecuteConcurrently()
    {
        var manager = new ResourceManager();
        var main = new GateStep("main", CameraMain);
        var wide = new GateStep("wide", CameraWide);
        var runnerMain = new SequenceRunner(manager);
        var runnerWide = new SequenceRunner(manager);

        var runMain = runnerMain.RunAsync(new Sequence("a", [main]));
        var runWide = runnerWide.RunAsync(new Sequence("b", [wide]));

        // Both are inside their step at the same time, which only works if neither waited for the other.
        await Task.WhenAll(main.Started.Task, wide.Started.Task).WaitAsync(Bound);
        Assert.Equal(0, manager.WaitingCount);

        main.Release.SetResult();
        wide.Release.SetResult();
        await Task.WhenAll(runMain, runWide).WaitAsync(Bound);
    }

    [Fact]
    public async Task RunnersUsingTheSameCamera_DoNotExecuteTheStepConcurrently_AndSecondContinuesAfterRelease()
    {
        var manager = new ResourceManager();
        var first = new GateStep("first", CameraMain);
        var second = new GateStep("second", CameraMain) { Blocks = false };
        var runnerA = new SequenceRunner(manager);
        var runnerB = new SequenceRunner(manager);

        var runA = runnerA.RunAsync(new Sequence("a", [first]));
        await first.Started.Task.WaitAsync(Bound);
        var runB = runnerB.RunAsync(new Sequence("b", [second]));
        await WaitForWaiters(manager, 1);

        // B is waiting for the resource, not running the step.
        Assert.Equal(0, second.Executions);
        Assert.False(second.Started.Task.IsCompleted);
        Assert.Equal("second", runnerB.CurrentPosition!.StepName);
        Assert.True(runnerB.IsRunning);

        first.Release.SetResult();
        await runA.WaitAsync(Bound);
        await runB.WaitAsync(Bound);

        Assert.Equal(1, second.Executions);
        Assert.Equal(SequenceState.Completed, runnerB.State);
        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task CancellationWhileWaitingForResource_YieldsCancelled_AndDoesNotExecuteTheStep()
    {
        var manager = new ResourceManager();
        var holder = new GateStep("holder", CameraMain);
        var waiting = new GateStep("waiting", CameraMain);
        var runnerA = new SequenceRunner(manager);
        var runnerB = new SequenceRunner(manager);
        using var cts = new CancellationTokenSource();

        var runA = runnerA.RunAsync(new Sequence("a", [holder]));
        await holder.Started.Task.WaitAsync(Bound);
        var runB = runnerB.RunAsync(new Sequence("b", [waiting]), cts.Token);
        await WaitForWaiters(manager, 1);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runB.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runnerB.State);
        Assert.Equal(0, waiting.Executions);
        Assert.Equal(0, manager.WaitingCount);
        Assert.True(manager.IsHeld(CameraMain)); // still A's

        holder.Release.SetResult();
        await runA.WaitAsync(Bound);
    }

    [Fact]
    public async Task FailingStep_ReleasesItsResource()
    {
        var manager = new ResourceManager();
        var failing = new GateStep("fail", CameraMain) { Throw = new InvalidOperationException("boom") };
        var runner = new SequenceRunner(manager);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new Sequence("s", [failing])));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task CancelledStep_ReleasesItsResource()
    {
        var manager = new ResourceManager();
        var step = new GateStep("blocked", CameraMain);
        var runner = new SequenceRunner(manager);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [step]), cts.Token);
        await step.Started.Task.WaitAsync(Bound);
        Assert.True(manager.IsHeld(CameraMain));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(manager.IsHeld(CameraMain));
    }

    // Containers

    [Fact]
    public async Task Repeat_AcquiresAndReleasesTheChildResourcePerIteration()
    {
        var manager = new ResourceManager();
        var heldDuringStep = new List<bool>();
        var heldAfterEachChild = new List<bool>();
        var step = new GateStep("exposure", CameraMain, () => heldDuringStep.Add(manager.IsHeld(CameraMain))) { Blocks = false };
        var runner = new SequenceRunner(manager);
        runner.StepCompleted += (_, e) =>
        {
            if (e.Position.Parent is not null)
            {
                heldAfterEachChild.Add(manager.IsHeld(CameraMain));
            }
        };

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, step)]));

        Assert.Equal(new[] { true, true, true }, heldDuringStep);
        // Between iterations (and at the Repeat's own level) nothing is held.
        Assert.Equal(new[] { false, false, false }, heldAfterEachChild);
        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task Group_DoesNotHoldResourcesOfFutureChildren()
    {
        var manager = new ResourceManager();
        var seenDuringA = new List<bool>();
        var seenDuringWait = new List<bool>();
        var seenDuringB = new List<bool>();
        var a = new GateStep("a", CameraMain, () =>
        {
            seenDuringA.Add(manager.IsHeld(CameraMain));
            seenDuringA.Add(manager.IsHeld(CameraWide));
        }) { Blocks = false };
        var b = new GateStep("b", CameraWide, () =>
        {
            seenDuringB.Add(manager.IsHeld(CameraMain));
            seenDuringB.Add(manager.IsHeld(CameraWide));
        }) { Blocks = false };
        var wait = new GateStep("wait", null, () =>
        {
            seenDuringWait.Add(manager.IsHeld(CameraMain));
            seenDuringWait.Add(manager.IsHeld(CameraWide));
        }) { Blocks = false };
        var runner = new SequenceRunner(manager);

        await runner.RunAsync(new Sequence("s", [new SequenceGroup("block", [a, wait, b])]));

        Assert.Equal(new[] { true, false }, seenDuringA);      // only its own camera
        Assert.Equal(new[] { false, false }, seenDuringWait);  // a step without requirements holds nothing
        Assert.Equal(new[] { false, true }, seenDuringB);
    }

    [Fact]
    public async Task GroupOfTwoCameras_LetsAnotherRunnerUseTheFirstCameraWhileTheSecondIsBusy()
    {
        var manager = new ResourceManager();
        var a = new GateStep("a", CameraMain) { Blocks = false };
        var b = new GateStep("b", CameraWide);
        var other = new GateStep("other", CameraMain) { Blocks = false };
        var groupRunner = new SequenceRunner(manager);
        var otherRunner = new SequenceRunner(manager);

        var groupRun = groupRunner.RunAsync(new Sequence("s", [new SequenceGroup("g", [a, b])]));
        await b.Started.Task.WaitAsync(Bound); // a is done, b holds only the wide camera

        await otherRunner.RunAsync(new Sequence("o", [other])).WaitAsync(Bound);

        Assert.Equal(1, other.Executions);
        b.Release.SetResult();
        await groupRun.WaitAsync(Bound);
    }

    // Host

    [Fact]
    public async Task Host_ExposesOneSharedResourceManager()
    {
        await using var host = new SideraRuntimeHost();

        Assert.NotNull(host.ResourceManager);
        Assert.Same(host.ResourceManager, host.ResourceManager);
    }

    [Fact]
    public async Task RunnersOfOneHost_CoordinateThroughItsResourceManager()
    {
        await using var host = new SideraRuntimeHost();
        var first = new GateStep("first", CameraMain);
        var second = new GateStep("second", CameraMain) { Blocks = false };
        var runnerA = new SequenceRunner(host.ResourceManager);
        var runnerB = new SequenceRunner(host.ResourceManager);

        var runA = runnerA.RunAsync(new Sequence("a", [first]));
        await first.Started.Task.WaitAsync(Bound);
        var runB = runnerB.RunAsync(new Sequence("b", [second]));
        await WaitForWaiters(host.ResourceManager, 1);
        Assert.Equal(0, second.Executions);

        first.Release.SetResult();
        await Task.WhenAll(runA, runB).WaitAsync(Bound);

        Assert.Equal(1, second.Executions);
    }
}

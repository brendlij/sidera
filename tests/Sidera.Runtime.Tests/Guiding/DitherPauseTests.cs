using Sidera.Core.Guiding;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Guiding;

// Pausing around coordinated dithering; shares the fake rig and helpers of the dither tests.
public partial class DitherActionTests
{
    private static ParallelStep PausableImaging(SideraRuntimeHost host, WorkStep mainAfter, WorkStep wideAfter) => new("rigs", [
        new SequenceGroup("main", [
            new CameraExposureAction(host.DeviceRegistry, MainId, Quick),
            new SafePointStep(),
            mainAfter]),
        new SequenceGroup("wide", [
            new CameraExposureAction(host.DeviceRegistry, WideId, Quick),
            SettlingDither(host),
            wideAfter]),
    ], Session);

    private static Task WaitForState(SequenceRunner runner, SequenceState state) =>
        WaitUntil(() => runner.State == state, $"state {state} (is {runner.State})");

    private static async Task WaitForExposures(Rig rig) =>
        await Task.WhenAll(
            rig.Main.GateOf("expose", 1).Started.Task,
            rig.Wide.GateOf("expose", 1).Started.Task).WaitAsync(Bound);

    [Fact]
    public async Task Pause_BeforeTheSafePointAndTheDither_StopsBothBranchesBeforeAnyCoordinationStarts()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (mainAfter, wideAfter) = (new WorkStep("main after"), new WorkStep("wide after"));
        var runner = Runner(host);
        var run = runner.RunAsync(new Sequence("night", [PausableImaging(host, mainAfter, wideAfter)]));
        await WaitForExposures(rig);

        runner.RequestPause();
        rig.Main.GateOf("expose", 1).Release.SetResult();
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);

        Assert.Equal(new[] { "Dither 1.5 px", "Safe Point" }, runner.PausedPositions.Select(p => p.StepName).Order());
        Assert.Equal(0, rig.Guider.DitherCalls);
        Assert.False(host.SafePointCoordinator.GetStatus(Session).RequestPending);
        Assert.All(DitherResources, r => Assert.False(host.ResourceManager.IsHeld(r)));

        runner.Resume();
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
        rig.Guider.GateOf("settle", 1).Release.SetResult();
        await run.WaitAsync(Bound);

        Assert.Equal(1, mainAfter.Executions);
        Assert.Equal(1, wideAfter.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task Pause_WhileTheDitherIsPending_LetsTheRoundFinish_ThenStopsTheBranchesBeforeTheirNormalWork()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (mainAfter, wideAfter) = (new WorkStep("main after"), new WorkStep("wide after"));
        var runner = Runner(host);
        var run = runner.RunAsync(new Sequence("night", [PausableImaging(host, mainAfter, wideAfter)]));
        await WaitForExposures(rig);

        // The wide branch finishes first and asks for the dither; the main branch is still exposing.
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await WaitUntil(() => host.SafePointCoordinator.GetStatus(Session).RequestPending, "dither request pending");
        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State);

        // The main branch is not stopped at its boundary: the pending dither waits for its safe point.
        rig.Main.GateOf("expose", 1).Release.SetResult();
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        Assert.True(host.SafePointCoordinator.GetStatus(Session).OperationRunning);
        Assert.Equal(SequenceState.Pausing, runner.State);

        // The coordinated operation, including its settle wait, is not interrupted by the pause.
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
        Assert.Equal(SequenceState.Pausing, runner.State);
        Assert.Empty(runner.PausedPositions);
        rig.Guider.GateOf("settle", 1).Release.SetResult();

        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(0, mainAfter.Executions);
        Assert.Equal(0, wideAfter.Executions);
        Assert.Equal(1, rig.Guider.SettleCalls);
        Assert.False(host.SafePointCoordinator.GetStatus(Session).RequestPending);
        Assert.All(DitherResources, r => Assert.False(host.ResourceManager.IsHeld(r)));

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(1, mainAfter.Executions);
        Assert.Equal(1, wideAfter.Executions);
        Assert.Equal(1, rig.Guider.DitherCalls);
        Assert.Equal(SequenceState.Completed, runner.State);
        AssertNothingLeftBehind(host, runner);
    }

    [Theory]
    [InlineData("during the dither movement")]
    [InlineData("during the settle wait")]
    public async Task Pause_DuringDitherOrSettle_DoesNotInterruptThem_AndStopsAfterwards(string when)
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (mainAfter, wideAfter) = (new WorkStep("main after"), new WorkStep("wide after"));
        var runner = Runner(host);
        var run = runner.RunAsync(new Sequence("night", [PausableImaging(host, mainAfter, wideAfter)]));
        await WaitForExposures(rig);
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await ReleaseMainIntoItsSafePoint(host, rig.Main);
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);

        if (when.Contains("settle"))
        {
            rig.Guider.GateOf("dither", 1).Release.SetResult();
            await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
            runner.RequestPause();
        }
        else
        {
            runner.RequestPause();
            rig.Guider.GateOf("dither", 1).Release.SetResult();
            await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
        }

        // Still inside the operation: not paused, nothing skipped, the branches wait at their safe point.
        Assert.Equal(SequenceState.Pausing, runner.State);
        Assert.Single(host.SafePointCoordinator.GetStatus(Session).AtSafePoint);
        Assert.All(DitherResources, r => Assert.True(host.ResourceManager.IsHeld(r)));
        Assert.Equal(1, rig.Guider.SettleCalls);

        rig.Guider.GateOf("settle", 1).Release.SetResult();
        await WaitForState(runner, SequenceState.Paused);
        Assert.Equal(0, mainAfter.Executions + wideAfter.Executions);
        Assert.All(DitherResources, r => Assert.False(host.ResourceManager.IsHeld(r)));

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(2, mainAfter.Executions + wideAfter.Executions);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task Cancel_WhilePausingDuringADither_CancelsTheOperation_ReleasesEveryone_AndNoResumeIsNeeded()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var (mainAfter, wideAfter) = (new WorkStep("main after"), new WorkStep("wide after"));
        var runner = Runner(host);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(new Sequence("night", [PausableImaging(host, mainAfter, wideAfter)]), cts.Token);
        await WaitForExposures(rig);
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await ReleaseMainIntoItsSafePoint(host, rig.Main);
        await rig.Guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, mainAfter.Executions + wideAfter.Executions);
        AssertNothingLeftBehind(host, runner);
    }

    [Fact]
    public async Task SettleFailure_WhilePausing_EndsTheRunAsFailed_AndReleasesTheBranches()
    {
        await using var host = new SideraRuntimeHost();
        var rig = await CreateRig(host);
        var failure = new GuidingSettleTimeoutException("did not settle");
        rig.Guider.SettleFailure = failure;
        var (mainAfter, wideAfter) = (new WorkStep("main after"), new WorkStep("wide after"));
        var runner = Runner(host);
        var run = runner.RunAsync(new Sequence("night", [PausableImaging(host, mainAfter, wideAfter)]));
        await WaitForExposures(rig);
        rig.Wide.GateOf("expose", 1).Release.SetResult();
        await ReleaseMainIntoItsSafePoint(host, rig.Main);
        rig.Guider.GateOf("dither", 1).Release.SetResult();
        await rig.Guider.GateOf("settle", 1).Started.Task.WaitAsync(Bound);
        runner.RequestPause();

        rig.Guider.GateOf("settle", 1).Release.SetResult();

        var error = await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => run.WaitAsync(Bound));
        Assert.Same(failure, error);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(0, mainAfter.Executions + wideAfter.Executions);
        AssertNothingLeftBehind(host, runner);
    }
}

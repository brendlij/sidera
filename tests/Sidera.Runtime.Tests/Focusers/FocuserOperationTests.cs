using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Focusers;

/// <summary>The focuser in the runtime: host, state store, manual operations, the sequence action and its resource.</summary>
public class FocuserOperationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DeviceId FocuserId = new("focuser.main");
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly ResourceId FocuserResource = ResourceId.ForDevice(FocuserId);

    private sealed class NotAFocuser : IDevice
    {
        public DeviceId Id { get; } = new("camera.fake");
        public string Name => "Fake";
        public DeviceType Type => DeviceType.Camera;
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

    // A connected focuser at 10000 whose moves take about a quarter second per 1000 steps.
    private static async Task<(SideraRuntimeHost Host, SimulatedFocuser Focuser)> Create(
        int stepsPerSecond = 4000, int minimumMs = 20)
    {
        var host = new SideraRuntimeHost();
        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF", stepsPerSecond: stepsPerSecond, minimumMoveDuration: TimeSpan.FromMilliseconds(minimumMs));
        await focuser.ConnectAsync();
        return (host, focuser);
    }

    private static MoveFocuserAction Move(SideraRuntimeHost host, int target, string id = "focuser.main") =>
        new(host.DeviceRegistry, new DeviceId(id), target);

    // Host and state store

    [Fact]
    public async Task TheHost_RegistersASimulatedFocuser_OnItsEventBus()
    {
        await using var host = new SideraRuntimeHost();

        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF", startPosition: 12000);

        Assert.True(host.DeviceRegistry.TryGet(FocuserId, out var registered));
        Assert.Same(focuser, registered);
        Assert.Equal(12000, focuser.Position);
        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
    }

    [Fact]
    public async Task TheStateStore_FollowsConnectionMotionAndPosition()
    {
        var (host, focuser) = await Create();
        await using var scope = host;
        Assert.True(host.StateStore.TryGet(FocuserId, out var connected));
        Assert.Equal(DeviceConnectionState.Connected, connected!.ConnectionState);
        Assert.Null(connected.FocuserPosition); // nothing observed yet
        Assert.Null(connected.FocuserMotionState);

        var move = focuser.MoveToAsync(10800);
        await WaitUntil(() => host.StateStore.TryGet(FocuserId, out var s) && s!.FocuserMotionState == FocuserMotionState.Moving, "moving state");
        Assert.True(host.StateStore.TryGet(FocuserId, out var moving));
        Assert.Equal(10000, moving!.FocuserPosition); // where it started
        await move;

        Assert.True(host.StateStore.TryGet(FocuserId, out var arrived));
        Assert.Equal((FocuserMotionState.Idle, 10800), (arrived!.FocuserMotionState, arrived.FocuserPosition));
        Assert.Equal(DeviceConnectionState.Connected, arrived.ConnectionState);
        Assert.Null(arrived.FilterSlot); // fields of other kinds of devices stay empty
        Assert.Null(arrived.Coordinates);
    }

    [Fact]
    public async Task TheStateStore_ShowsAnIdleFocuserAtItsOldPosition_AfterACancelledMove()
    {
        var (host, focuser) = await Create(stepsPerSecond: 100);
        await using var scope = host;
        using var cts = new CancellationTokenSource();

        var move = focuser.MoveToAsync(40000, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.True(host.StateStore.TryGet(FocuserId, out var state));
        Assert.Equal((FocuserMotionState.Idle, 10000), (state!.FocuserMotionState, state.FocuserPosition));
    }

    // Manual operations

    [Fact]
    public async Task ManualMove_MovesTheFocuser_ThroughTheDeviceOperationService()
    {
        var (host, focuser) = await Create();
        await using var scope = host;

        await host.DeviceOperations.MoveFocuserToAsync(FocuserId, 11000);

        Assert.Equal(11000, focuser.Position);
        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
    }

    [Fact]
    public async Task ManualMove_HoldsTheFocuserResource_WhileItRuns_AndNothingElse()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;
        var camera = host.AddSimulatedCamera(CameraId, "Camera");

        var move = host.DeviceOperations.MoveFocuserToAsync(FocuserId, 11000);
        await WaitUntil(() => host.ResourceManager.IsHeld(FocuserResource), "focuser resource");

        Assert.False(host.ResourceManager.IsHeld(ResourceId.ForDevice(CameraId))); // a move needs no camera
        await move;
        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task ManualMove_IsNotBlockedByAnExposureOnTheCamera()
    {
        var (host, focuser) = await Create();
        await using var scope = host;
        var camera = host.AddSimulatedCamera(CameraId, "Camera");
        await camera.ConnectAsync();

        var exposure = host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromSeconds(1));
        await WaitUntil(() => host.ResourceManager.IsHeld(ResourceId.ForDevice(CameraId)), "exposure running");
        await host.DeviceOperations.MoveFocuserToAsync(FocuserId, 10300).WaitAsync(TimeSpan.FromMilliseconds(900));

        Assert.Equal(10300, focuser.Position);
        Assert.Equal(CameraExposureState.Exposing, camera.ExposureState); // still going
        await exposure;
    }

    [Fact]
    public async Task TwoManualMovesOnOneFocuser_Serialize_TheSecondWaitsForTheResource()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;

        var first = host.DeviceOperations.MoveFocuserToAsync(FocuserId, 11000);
        await WaitUntil(() => host.ResourceManager.IsHeld(FocuserResource), "first move");
        var second = host.DeviceOperations.MoveFocuserToAsync(FocuserId, 9000);

        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second waiting");
        await Task.WhenAll(first, second).WaitAsync(Bound); // neither of them fails with "already moving"

        Assert.Equal(9000, focuser.Position);
    }

    [Fact]
    public async Task ManualMove_RejectsAnUnknownDevice_AndOneThatIsNoFocuser()
    {
        var (host, _) = await Create();
        await using var _host = host;
        host.AddDevice(new NotAFocuser());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.MoveFocuserToAsync(new DeviceId("focuser.none"), 1));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.MoveFocuserToAsync(new DeviceId("camera.fake"), 1));

        Assert.Contains("'focuser.none' is not registered", unknown.Message);
        Assert.Contains("'camera.fake' is not a focuser", wrong.Message);
    }

    [Fact]
    public async Task ManualMove_ToAnInvalidPosition_FailsAndReleasesTheResource()
    {
        var (host, focuser) = await Create();
        await using var scope = host;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.DeviceOperations.MoveFocuserToAsync(FocuserId, 99999));

        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
        Assert.Equal(10000, focuser.Position);
    }

    [Fact]
    public async Task ManualMove_CancelledWhileMoving_ReleasesTheResource_AndTheNextMoveSucceeds()
    {
        var (host, focuser) = await Create(stepsPerSecond: 100);
        await using var scope = host;
        using var cts = new CancellationTokenSource();

        var move = host.DeviceOperations.MoveFocuserToAsync(FocuserId, 40000, cts.Token);
        await WaitUntil(() => focuser.MotionState == FocuserMotionState.Moving, "moving");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        await host.DeviceOperations.MoveFocuserToAsync(FocuserId, 10010);
        Assert.Equal(10010, focuser.Position);
    }

    [Fact]
    public async Task ADisconnectWaits_ForAManualMove_BecauseBothNeedTheSameResource()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;

        var move = host.DeviceOperations.MoveFocuserToAsync(FocuserId, 11000);
        await WaitUntil(() => host.ResourceManager.IsHeld(FocuserResource), "move");
        var disconnect = host.DeviceOperations.DisconnectAsync(FocuserId);
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "disconnect waiting");

        await Task.WhenAll(move, disconnect).WaitAsync(Bound);

        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
        Assert.Equal(11000, focuser.Position);
    }

    // The action

    [Fact]
    public async Task Action_DeclaresOnlyTheFocuserResource_AndANameThatShowsTheTarget()
    {
        await using var host = new SideraRuntimeHost();
        var action = Move(host, 18350);

        Assert.Equal([FocuserResource], action.RequiredResources);
        Assert.Equal("Move focuser to 18350", action.Name);
        Assert.Equal((FocuserId, 18350), (action.FocuserId, action.Target));
    }

    [Fact]
    public async Task Action_MovesAndReturnsTheFinalPositionAsPayload()
    {
        var (host, focuser) = await Create();
        await using var scope = host;

        var result = await Move(host, 18350).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Equal(18350, Assert.IsType<int>(result.Payload));
        Assert.Equal(18350, focuser.Position);
    }

    [Fact]
    public async Task Action_KeepsNoResultState_ExecutionsAreIndependent()
    {
        var (host, focuser) = await Create();
        await using var scope = host;
        var action = Move(host, 12000);

        var first = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);
        await focuser.MoveToAsync(10000);
        var second = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(12000, second.Payload);
    }

    [Fact]
    public async Task Action_RejectsUnknownFocuser_NonFocuser_AndDisconnectedFocuser()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAFocuser());
        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF");

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => Move(host, 1, "focuser.none").ExecuteAsync(NoContext.Instance, default));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => Move(host, 1, "camera.fake").ExecuteAsync(NoContext.Instance, default));
        var offline = await Assert.ThrowsAsync<InvalidOperationException>(() => Move(host, 12000).ExecuteAsync(NoContext.Instance, default));

        Assert.Contains("'focuser.none' is not registered", unknown.Message);
        Assert.Contains("'camera.fake' is not a focuser", wrong.Message);
        Assert.Contains("'focuser.main' is not connected", offline.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState); // never connects it
    }

    [Fact]
    public async Task Action_ToAnInvalidPosition_Fails()
    {
        var (host, _) = await Create();
        await using var _host = host;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Move(host, -5).ExecuteAsync(NoContext.Instance, default));
    }

    [Fact]
    public async Task Action_CancelledWhileMoving_StopsTheMove_AndLeavesNoMovingState()
    {
        var (host, focuser) = await Create(stepsPerSecond: 100);
        await using var scope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Move(host, 40000).ExecuteAsync(NoContext.Instance, cts.Token));

        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(10000, focuser.Position);
    }

    // Through the runner

    [Fact]
    public async Task Runner_HoldsTheFocuserResourceWhileTheActionRuns_AndReleasesItAfterwards()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);

        var run = runner.RunAsync(new Sequence("s", [Move(host, 11000)]));
        await WaitUntil(() => host.ResourceManager.IsHeld(FocuserResource), "held");
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
        Assert.Equal(11000, focuser.Position);
    }

    [Fact]
    public async Task Runner_CancelDuringAMove_EndsCancelled_ReleasesTheResource_AndASubsequentOperationSucceeds()
    {
        var (host, focuser) = await Create(stepsPerSecond: 100);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [Move(host, 40000)]), cts.Token);
        await WaitUntil(() => focuser.MotionState == FocuserMotionState.Moving, "moving");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        await host.DeviceOperations.MoveFocuserToAsync(FocuserId, 10020);
        Assert.Equal(10020, focuser.Position);
    }

    [Fact]
    public async Task Runner_AFailingMove_FailsTheRun_AndReleasesTheResource()
    {
        var (host, _) = await Create();
        await using var _host = host;
        var runner = new SequenceRunner(host.ResourceManager);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(new Sequence("s", [Move(host, 99999)])));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(FocuserResource));
    }

    [Fact]
    public async Task Runner_PauseDuringAMove_LetsTheMoveFinish_ThenPausesBeforeTheNextStep()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);
        var sequence = new Sequence("s", [Move(host, 11000), Move(host, 12000)]);

        var run = runner.RunAsync(sequence);
        await WaitUntil(() => focuser.MotionState == FocuserMotionState.Moving, "first move");
        runner.RequestPause();
        await WaitUntil(() => runner.State == SequenceState.Paused, "paused");

        Assert.Equal(11000, focuser.Position); // the move that was running was not interrupted
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        await Task.Delay(150);
        Assert.Equal(11000, focuser.Position); // and the next step did not start

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(12000, focuser.Position);
    }

    [Fact]
    public async Task Runner_TwoMovesOnTheSameFocuserInParallel_RunOneAfterTheOther_WithoutFailing()
    {
        var (host, focuser) = await Create(stepsPerSecond: 2000);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [Move(host, 11000), Move(host, 12000)]);
        var completions = new List<(DateTime At, string Name)>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completions) { completions.Add((DateTime.UtcNow, e.Position.StepName)); }
        };

        await runner.RunAsync(new Sequence("s", [parallel])).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        var moves = completions.Where(c => c.Name.StartsWith("Move focuser", StringComparison.Ordinal)).OrderBy(c => c.At).ToList();
        Assert.Equal(2, moves.Count);
        Assert.True(moves[1].At - moves[0].At >= TimeSpan.FromMilliseconds(200), "the moves overlapped");
        Assert.Contains(focuser.Position, new[] { 11000, 12000 });
    }

    [Fact]
    public async Task Runner_MovesOnTwoDifferentFocusers_RunAtTheSameTime()
    {
        var (host, _) = await Create(stepsPerSecond: 1000);
        await using var _host = host;
        var wide = host.AddSimulatedFocuser(new DeviceId("focuser.wide"), "Wide EAF", stepsPerSecond: 1000, minimumMoveDuration: TimeSpan.FromMilliseconds(20));
        await wide.ConnectAsync();
        var runner = new SequenceRunner(host.ResourceManager);
        var parallel = new ParallelStep("p", [Move(host, 10500), Move(host, 10500, "focuser.wide")]);

        var started = DateTime.UtcNow;
        await runner.RunAsync(new Sequence("s", [parallel])).WaitAsync(Bound);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(900), "the moves were serialized");
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }
}

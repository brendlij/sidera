using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.FilterWheels;

/// <summary>The filter wheel in the runtime: host, state store, manual operations, the sequence action and its resource.</summary>
public class FilterWheelOperationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DeviceId WheelId = new("filterwheel.main");
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly ResourceId WheelResource = ResourceId.ForDevice(WheelId);

    private static readonly FilterSlot[] Seven =
    [
        new(0, "L"), new(1, "R"), new(2, "G"), new(3, "B"), new(4, "Ha"), new(5, "OIII"), new(6, "SII"),
    ];

    private sealed class NotAWheel : IDevice
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

    private static async Task<(SideraRuntimeHost Host, SimulatedFilterWheel Wheel)> Create(int moveMs = 300)
    {
        var host = new SideraRuntimeHost();
        var wheel = host.AddSimulatedFilterWheel(WheelId, "EFW", Seven, moveDuration: TimeSpan.FromMilliseconds(moveMs));
        await wheel.ConnectAsync();
        return (host, wheel);
    }

    private static ChangeFilterAction Change(SideraRuntimeHost host, int slot, string id = "filterwheel.main") =>
        new(host.DeviceRegistry, new DeviceId(id), slot);

    // Host and state store

    [Fact]
    public async Task TheHost_RegistersASimulatedFilterWheel_OnItsEventBus()
    {
        await using var host = new SideraRuntimeHost();

        var wheel = host.AddSimulatedFilterWheel(WheelId, "EFW", Seven, startSlotIndex: 1);

        Assert.True(host.DeviceRegistry.TryGet(WheelId, out var registered));
        Assert.Same(wheel, registered);
        Assert.Equal(new FilterSlot(1, "R"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task TheStateStore_FollowsConnectionMotionAndSlot()
    {
        var (host, wheel) = await Create();
        await using var scope = host;
        Assert.True(host.StateStore.TryGet(WheelId, out var connected));
        Assert.Null(connected!.FilterSlot);

        var move = wheel.MoveToSlotAsync(4);
        await WaitUntil(() => host.StateStore.TryGet(WheelId, out var s) && s!.FilterWheelMotionState == FilterWheelMotionState.Moving, "moving");
        Assert.True(host.StateStore.TryGet(WheelId, out var moving));
        Assert.Equal(new FilterSlot(0, "L"), moving!.FilterSlot);
        await move;

        Assert.True(host.StateStore.TryGet(WheelId, out var arrived));
        Assert.Equal((FilterWheelMotionState.Idle, new FilterSlot(4, "Ha")), (arrived!.FilterWheelMotionState, arrived.FilterSlot));
        Assert.Null(arrived.FocuserPosition);
    }

    [Fact]
    public async Task TheStateStore_ShowsTheOldSlot_AfterACancelledMove()
    {
        var (host, wheel) = await Create(moveMs: 30000);
        await using var scope = host;
        using var cts = new CancellationTokenSource();

        var move = wheel.MoveToSlotAsync(4, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.True(host.StateStore.TryGet(WheelId, out var state));
        Assert.Equal((FilterWheelMotionState.Idle, new FilterSlot(0, "L")), (state!.FilterWheelMotionState, state.FilterSlot));
    }

    // Manual operations

    [Fact]
    public async Task ManualMove_TurnsTheWheel_ThroughTheDeviceOperationService()
    {
        var (host, wheel) = await Create();
        await using var scope = host;

        await host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 4);

        Assert.Equal(new FilterSlot(4, "Ha"), wheel.CurrentSlot);
        Assert.False(host.ResourceManager.IsHeld(WheelResource));
    }

    [Fact]
    public async Task ManualMove_HoldsTheWheelResourceOnly_AndIsNotBlockedByAnExposure()
    {
        var (host, wheel) = await Create(moveMs: 200);
        await using var scope = host;
        var camera = host.AddSimulatedCamera(CameraId, "Camera");
        await camera.ConnectAsync();

        var exposure = host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromSeconds(1));
        await WaitUntil(() => host.ResourceManager.IsHeld(ResourceId.ForDevice(CameraId)), "exposure");
        var move = host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 2);
        await WaitUntil(() => host.ResourceManager.IsHeld(WheelResource), "wheel resource");
        await move.WaitAsync(TimeSpan.FromMilliseconds(900));

        Assert.Equal(new FilterSlot(2, "G"), wheel.CurrentSlot);
        Assert.Equal(CameraExposureState.Exposing, camera.ExposureState);
        await exposure;
    }

    [Fact]
    public async Task TwoManualMovesOnOneWheel_Serialize_TheSecondWaitsForTheResource()
    {
        var (host, wheel) = await Create(moveMs: 250);
        await using var scope = host;

        var first = host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 3);
        await WaitUntil(() => host.ResourceManager.IsHeld(WheelResource), "first move");
        var second = host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 5);

        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "second waiting");
        await Task.WhenAll(first, second).WaitAsync(Bound); // neither fails with "already moving"

        Assert.Equal(new FilterSlot(5, "OIII"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task ManualMove_RejectsAnUnknownDevice_OneThatIsNoWheel_AndASlotThatDoesNotExist()
    {
        var (host, wheel) = await Create();
        await using var scope = host;
        host.AddDevice(new NotAWheel());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.MoveFilterWheelToAsync(new DeviceId("filterwheel.none"), 1));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => host.DeviceOperations.MoveFilterWheelToAsync(new DeviceId("camera.fake"), 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 7));

        Assert.Contains("'filterwheel.none' is not registered", unknown.Message);
        Assert.Contains("'camera.fake' is not a filter wheel", wrong.Message);
        Assert.False(host.ResourceManager.IsHeld(WheelResource));
        Assert.Equal(new FilterSlot(0, "L"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task ManualMove_CancelledWhileMoving_ReleasesTheResource_AndTheNextMoveSucceeds()
    {
        var (host, wheel) = await Create(moveMs: 30000);
        await using var scope = host;
        using var cts = new CancellationTokenSource();

        var move = host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 4, cts.Token);
        await WaitUntil(() => wheel.MotionState == FilterWheelMotionState.Moving, "moving");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.False(host.ResourceManager.IsHeld(WheelResource));
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);

        var (other, again) = await Create(moveMs: 20);
        await using var otherScope = other;
        await again.MoveToSlotAsync(4);
        Assert.Equal(new FilterSlot(4, "Ha"), again.CurrentSlot);
    }

    // The action

    [Fact]
    public async Task Action_DeclaresOnlyTheWheelResource_AndANameThatShowsTheSlot()
    {
        await using var host = new SideraRuntimeHost();
        var action = Change(host, 4);

        Assert.Equal([WheelResource], action.RequiredResources);
        Assert.Equal("Change filter to slot 4", action.Name);
        Assert.Equal((WheelId, 4), (action.FilterWheelId, action.SlotIndex));
    }

    [Fact]
    public void Action_RejectsANegativeSlot()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeFilterAction(new DeviceRegistry(), WheelId, -1));
    }

    [Fact]
    public async Task Action_TurnsTheWheel_AndReturnsTheCurrentSlotAsPayload()
    {
        var (host, wheel) = await Create(moveMs: 20);
        await using var scope = host;

        var result = await Change(host, 4).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Equal(new FilterSlot(4, "Ha"), Assert.IsType<FilterSlot>(result.Payload));
        Assert.Equal(new FilterSlot(4, "Ha"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task Action_RejectsUnknownWheel_NonWheel_DisconnectedWheel_AndMissingSlot()
    {
        await using var host = new SideraRuntimeHost();
        host.AddDevice(new NotAWheel());
        var wheel = host.AddSimulatedFilterWheel(WheelId, "EFW", Seven, moveDuration: TimeSpan.FromMilliseconds(20));

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() => Change(host, 1, "filterwheel.none").ExecuteAsync(NoContext.Instance, default));
        var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => Change(host, 1, "camera.fake").ExecuteAsync(NoContext.Instance, default));
        var offline = await Assert.ThrowsAsync<InvalidOperationException>(() => Change(host, 1).ExecuteAsync(NoContext.Instance, default));
        await wheel.ConnectAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Change(host, 9).ExecuteAsync(NoContext.Instance, default));

        Assert.Contains("'filterwheel.none' is not registered", unknown.Message);
        Assert.Contains("'camera.fake' is not a filter wheel", wrong.Message);
        Assert.Contains("'filterwheel.main' is not connected", offline.Message);
    }

    [Fact]
    public async Task Action_CancelledWhileMoving_LeavesNoMovingState()
    {
        var (host, wheel) = await Create(moveMs: 30000);
        await using var scope = host;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Change(host, 4).ExecuteAsync(NoContext.Instance, cts.Token));

        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        Assert.Equal(new FilterSlot(0, "L"), wheel.CurrentSlot);
    }

    // Through the runner

    [Fact]
    public async Task Runner_HoldsTheWheelResourceWhileTheActionRuns_AndReleasesItAfterwards()
    {
        var (host, wheel) = await Create(moveMs: 250);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);

        var run = runner.RunAsync(new Sequence("s", [Change(host, 4)]));
        await WaitUntil(() => host.ResourceManager.IsHeld(WheelResource), "held");
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(WheelResource));
        Assert.Equal(new FilterSlot(4, "Ha"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task Runner_CancelDuringAChange_EndsCancelled_ReleasesTheResource_AndASubsequentOperationSucceeds()
    {
        var (host, wheel) = await Create(moveMs: 30000);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [Change(host, 4)]), cts.Token);
        await WaitUntil(() => wheel.MotionState == FilterWheelMotionState.Moving, "moving");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(host.ResourceManager.IsHeld(WheelResource));
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        await host.DeviceOperations.MoveFilterWheelToAsync(WheelId, 0); // already there: completes at once
    }

    [Fact]
    public async Task Runner_AFailingChange_FailsTheRun_AndReleasesTheResource()
    {
        var (host, _) = await Create(moveMs: 20);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(new Sequence("s", [Change(host, 9)])));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.False(host.ResourceManager.IsHeld(WheelResource));
    }

    [Fact]
    public async Task Runner_PauseDuringAChange_LetsTheChangeFinish_ThenPausesBeforeTheNextStep()
    {
        var (host, wheel) = await Create(moveMs: 300);
        await using var scope = host;
        var runner = new SequenceRunner(host.ResourceManager);

        var run = runner.RunAsync(new Sequence("s", [Change(host, 3), Change(host, 5)]));
        await WaitUntil(() => wheel.MotionState == FilterWheelMotionState.Moving, "first change");
        runner.RequestPause();
        await WaitUntil(() => runner.State == SequenceState.Paused, "paused");

        Assert.Equal(new FilterSlot(3, "B"), wheel.CurrentSlot);
        await Task.Delay(150);
        Assert.Equal(new FilterSlot(3, "B"), wheel.CurrentSlot); // the next step did not start

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(new FilterSlot(5, "OIII"), wheel.CurrentSlot);
    }

    [Fact]
    public async Task Runner_ChangesOnTwoDifferentWheels_RunAtTheSameTime_OnTheSameWheel_OneAfterTheOther()
    {
        var (host, _) = await Create(moveMs: 300);
        await using var scope = host;
        var other = host.AddSimulatedFilterWheel(new DeviceId("filterwheel.narrow"), "Narrow EFW", Seven, moveDuration: TimeSpan.FromMilliseconds(300));
        await other.ConnectAsync();
        var runner = new SequenceRunner(host.ResourceManager);

        var started = DateTime.UtcNow;
        await runner.RunAsync(new Sequence("s", [new ParallelStep("p", [Change(host, 4), Change(host, 4, "filterwheel.narrow")])])).WaitAsync(Bound);
        var different = DateTime.UtcNow - started;

        started = DateTime.UtcNow;
        await runner.RunAsync(new Sequence("s", [new ParallelStep("p", [Change(host, 1), Change(host, 2)])])).WaitAsync(Bound);
        var same = DateTime.UtcNow - started;

        Assert.True(different < TimeSpan.FromMilliseconds(550), $"different wheels took {different}");
        Assert.True(same >= TimeSpan.FromMilliseconds(550), $"one wheel took {same}");
    }
}

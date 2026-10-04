using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Focusers;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;

namespace Sidera.Runtime.Tests.Focusers;

public class SimulatedFocuserTests
{
    private static readonly DeviceId FocuserId = new("focuser.main");
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    // Every focuser event, in the order it was published.
    private sealed class Recorder
    {
        private readonly List<ISideraEvent> _events = [];

        public Recorder(EventBus bus)
        {
            bus.Subscribe<FocuserMotionStateChanged>(Record);
            bus.Subscribe<FocuserPositionChanged>(Record);
        }

        public IReadOnlyList<ISideraEvent> Events
        {
            get { lock (_events) { return _events.ToArray(); } }
        }

        private Task Record(ISideraEvent e, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add(e);
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<SimulatedFocuser> Connected(
        EventBus? bus = null,
        int start = 10000,
        int min = 0,
        int max = 50000,
        int stepsPerSecond = 100000,
        TimeSpan? minimum = null)
    {
        var focuser = new SimulatedFocuser(FocuserId, "EAF", bus, start, min, max, stepsPerSecond, minimum ?? Quick);
        await focuser.ConnectAsync();
        return focuser;
    }

    // Construction

    [Fact]
    public void NewFocuser_StartsDisconnected_IdleAtItsStartPosition()
    {
        var focuser = new SimulatedFocuser(FocuserId, "EAF", startPosition: 12345, minPosition: 100, maxPosition: 60000);

        Assert.Equal(DeviceType.Focuser, focuser.Type);
        Assert.Equal("EAF", focuser.Name);
        Assert.Equal(FocuserId, focuser.Id);
        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal((12345, 100, 60000), (focuser.Position, focuser.MinPosition, focuser.MaxPosition));
    }

    [Fact]
    public void TheDefaults_AreDocumented()
    {
        var focuser = new SimulatedFocuser(FocuserId);

        Assert.Equal((SimulatedFocuser.DefaultStartPosition, SimulatedFocuser.DefaultMinPosition, SimulatedFocuser.DefaultMaxPosition),
            (focuser.Position, focuser.MinPosition, focuser.MaxPosition));
    }

    [Theory]
    [InlineData(5, 10, 100)]   // start below the range
    [InlineData(500, 0, 100)]  // start above the range
    public void AStartPositionOutsideTheRange_IsRejected(int start, int min, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SimulatedFocuser(FocuserId, startPosition: start, minPosition: min, maxPosition: max));
    }

    [Fact]
    public void AnEmptyStepRate_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedFocuser(FocuserId, stepsPerSecond: 0));
    }

    [Fact]
    public void AnInvertedRange_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedFocuser(FocuserId, startPosition: 5, minPosition: 10, maxPosition: 0));
    }

    // Connection

    [Fact]
    public async Task ConnectAndDisconnect_ChangeTheConnectionState_AndPublishIt()
    {
        var bus = new EventBus();
        var states = new List<DeviceConnectionState>();
        bus.Subscribe<DeviceConnectionStateChanged>((e, _) =>
        {
            lock (states) { states.Add(e.NewState); }
            return Task.CompletedTask;
        });
        var focuser = new SimulatedFocuser(FocuserId, events: bus);

        await focuser.ConnectAsync();
        Assert.Equal(DeviceConnectionState.Connected, focuser.ConnectionState);
        await focuser.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
        Assert.Equal(
            [DeviceConnectionState.Connecting, DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected],
            states);
    }

    [Fact]
    public async Task ConnectingTwice_AndDisconnectingWhileDisconnected_DoNothing()
    {
        var focuser = new SimulatedFocuser(FocuserId);

        await focuser.DisconnectAsync();
        await focuser.ConnectAsync();
        await focuser.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, focuser.ConnectionState);
    }

    [Fact]
    public async Task ACancelledConnect_LeavesTheFocuserDisconnected()
    {
        var focuser = new SimulatedFocuser(FocuserId);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => focuser.ConnectAsync(cts.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
    }

    // Moving

    [Fact]
    public async Task MoveTo_ArrivesAtTheTarget_AndEndsIdle()
    {
        var focuser = await Connected();

        await focuser.MoveToAsync(18350);

        Assert.Equal(18350, focuser.Position);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task MoveTo_PublishesMotionPositionAndMotion_InThatOrder()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var focuser = await Connected(bus);

        await focuser.MoveToAsync(12000);

        Assert.Equal(
            new ISideraEvent[]
            {
                new FocuserMotionStateChanged(FocuserId, FocuserMotionState.Idle, FocuserMotionState.Moving, 10000),
                new FocuserPositionChanged(FocuserId, 10000, 12000),
                new FocuserMotionStateChanged(FocuserId, FocuserMotionState.Moving, FocuserMotionState.Idle, 12000),
            },
            recorder.Events);
    }

    [Fact]
    public async Task TheMotionStateIsMoving_WhileTheMoveRuns()
    {
        var focuser = await Connected(minimum: TimeSpan.FromMilliseconds(400));

        var move = focuser.MoveToAsync(11000);

        Assert.Equal(FocuserMotionState.Moving, focuser.MotionState);
        Assert.Equal(10000, focuser.Position); // not there yet
        await move.WaitAsync(Bound);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task AMoveToTheCurrentPosition_CompletesAtOnce_AndPublishesNothing()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var focuser = await Connected(bus, minimum: TimeSpan.FromSeconds(30));

        await focuser.MoveToAsync(10000).WaitAsync(Bound);

        Assert.Empty(recorder.Events);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public void TheMoveDuration_GrowsWithTheDistance_ButIsNeverShorterThanTheMinimum()
    {
        var focuser = new SimulatedFocuser(FocuserId, stepsPerSecond: 1000, minimumMoveDuration: TimeSpan.FromMilliseconds(100));

        Assert.Equal(TimeSpan.FromMilliseconds(100), focuser.MoveDuration(10, 11));
        Assert.Equal(TimeSpan.FromSeconds(2), focuser.MoveDuration(0, 2000));
        Assert.Equal(TimeSpan.FromSeconds(2), focuser.MoveDuration(2000, 0));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50001)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public async Task ATargetOutsideTheRange_IsRejected_WithoutMovingOrPublishing(int target)
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var focuser = await Connected(bus);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => focuser.MoveToAsync(target));

        Assert.Equal(10000, focuser.Position);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Empty(recorder.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    public async Task TheLimitsOfTheRange_AreValidTargets(int target)
    {
        var focuser = await Connected();

        await focuser.MoveToAsync(target);

        Assert.Equal(target, focuser.Position);
    }

    [Fact]
    public async Task MoveTo_RequiresAConnectedFocuser()
    {
        var focuser = new SimulatedFocuser(FocuserId, minimumMoveDuration: Quick);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => focuser.MoveToAsync(12000));

        Assert.Equal("Focuser is not connected.", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task AMoveWhileMoving_IsRejected_AndTheRunningMoveIsUnaffected()
    {
        var focuser = await Connected(minimum: TimeSpan.FromMilliseconds(300));
        var first = focuser.MoveToAsync(12000);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => focuser.MoveToAsync(15000));
        await first.WaitAsync(Bound);

        Assert.Equal("The focuser is already moving.", error.Message);
        Assert.Equal(12000, focuser.Position);
    }

    // Cancellation and failure

    [Fact]
    public async Task ACancelledMove_StopsWhereItStarted_PublishesNoPosition_AndLeavesNoStaleMovingState()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var focuser = await Connected(bus, minimum: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var move = focuser.MoveToAsync(20000, cts.Token);
        Assert.Equal(FocuserMotionState.Moving, focuser.MotionState);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(10000, focuser.Position);
        Assert.Equal(
            new ISideraEvent[]
            {
                new FocuserMotionStateChanged(FocuserId, FocuserMotionState.Idle, FocuserMotionState.Moving, 10000),
                new FocuserMotionStateChanged(FocuserId, FocuserMotionState.Moving, FocuserMotionState.Idle, 10000),
            },
            recorder.Events);
    }

    [Fact]
    public async Task AMoveWithAnAlreadyCancelledToken_EndsIdle()
    {
        var focuser = await Connected();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => focuser.MoveToAsync(20000, cts.Token));

        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(10000, focuser.Position);
    }

    [Fact]
    public async Task AfterACancelledMove_TheNextMoveOnTheSameFocuserSucceeds()
    {
        // 1000 steps per second: 30000 steps take 30 s, 100 steps take 100 ms.
        var focuser = await Connected(stepsPerSecond: 1000);
        using var cts = new CancellationTokenSource();
        var cancelled = focuser.MoveToAsync(40000, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        await focuser.MoveToAsync(10100).WaitAsync(Bound);

        Assert.Equal(10100, focuser.Position);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task Disconnecting_WhileMoving_IsRejected_ConsistentWithTheOtherSimulators_AndTheMoveFinishes()
    {
        var focuser = await Connected(minimum: TimeSpan.FromMilliseconds(300));
        var move = focuser.MoveToAsync(12000);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => focuser.DisconnectAsync());
        await move.WaitAsync(Bound);

        Assert.Equal("Cannot disconnect while the focuser is moving.", error.Message);
        Assert.Equal(DeviceConnectionState.Connected, focuser.ConnectionState);
        Assert.Equal(12000, focuser.Position);

        await focuser.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
        Assert.Equal(12000, focuser.Position); // the position survives a disconnect
    }

    [Fact]
    public async Task TwoFocusers_MoveIndependently_AtTheSameTime()
    {
        var a = await Connected(minimum: TimeSpan.FromMilliseconds(300));
        var b = new SimulatedFocuser(new DeviceId("focuser.wide"), minimumMoveDuration: TimeSpan.FromMilliseconds(300));
        await b.ConnectAsync();

        var started = DateTime.UtcNow;
        await Task.WhenAll(a.MoveToAsync(12000), b.MoveToAsync(9000));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(550));
        Assert.Equal((12000, 9000), (a.Position, b.Position));
    }

    [Fact]
    public async Task TheSameMovesPublishTheSameEvents_EveryTime()
    {
        var runs = new List<IReadOnlyList<ISideraEvent>>();
        for (var i = 0; i < 3; i++)
        {
            var bus = new EventBus();
            var recorder = new Recorder(bus);
            var focuser = await Connected(bus);
            await focuser.MoveToAsync(12000);
            await focuser.MoveToAsync(8000);
            runs.Add(recorder.Events);
        }

        Assert.Equal(6, runs[0].Count);
        Assert.All(runs.Skip(1), run => Assert.Equal(runs[0], run));
    }
}

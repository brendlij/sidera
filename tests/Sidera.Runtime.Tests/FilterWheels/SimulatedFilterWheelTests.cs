using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;

namespace Sidera.Runtime.Tests.FilterWheels;

public class SimulatedFilterWheelTests
{
    private static readonly DeviceId WheelId = new("filterwheel.main");
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static readonly FilterSlot[] Seven =
    [
        new(0, "L"), new(1, "R"), new(2, "G"), new(3, "B"), new(4, "Ha"), new(5, "OIII"), new(6, "SII"),
    ];

    private sealed class Recorder
    {
        private readonly List<ISideraEvent> _events = [];

        public Recorder(EventBus bus)
        {
            bus.Subscribe<FilterWheelMotionStateChanged>(Record);
            bus.Subscribe<FilterWheelSlotChanged>(Record);
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

    private static async Task<SimulatedFilterWheel> Connected(EventBus? bus = null, TimeSpan? move = null, int start = 0)
    {
        var wheel = new SimulatedFilterWheel(WheelId, Seven, "EFW", bus, start, move ?? Quick);
        await wheel.ConnectAsync();
        return wheel;
    }

    // Slots

    [Fact]
    public void ASlot_HasAnIndexAndAName_AndValueEquality()
    {
        var slot = new FilterSlot(4, "  Ha ");

        Assert.Equal((4, "Ha"), (slot.Index, slot.Name));
        Assert.Equal(new FilterSlot(4, "Ha"), slot);
        Assert.NotEqual(new FilterSlot(4, "OIII"), slot);
    }

    [Theory]
    [InlineData(-1, "L")]
    [InlineData(0, "")]
    [InlineData(0, "   ")]
    public void AnInvalidSlot_IsRejected(int index, string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FilterSlot(index, name));
    }

    [Fact]
    public void TheNamesAreArbitrary_AndNeedNotBeUnique()
    {
        var wheel = new SimulatedFilterWheel(WheelId, [new FilterSlot(0, "Clear"), new FilterSlot(1, "Clear"), new FilterSlot(2, "Dual-band 7nm")]);

        Assert.Equal(["Clear", "Clear", "Dual-band 7nm"], wheel.Slots.Select(s => s.Name));
    }

    // Construction

    [Fact]
    public void NewWheel_StartsDisconnected_IdleAtItsStartSlot()
    {
        var wheel = new SimulatedFilterWheel(WheelId, Seven, "EFW", startSlotIndex: 2);

        Assert.Equal(DeviceType.FilterWheel, wheel.Type);
        Assert.Equal("EFW", wheel.Name);
        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        Assert.Equal(new FilterSlot(2, "G"), wheel.CurrentSlot);
        Assert.Equal(Seven, wheel.Slots);
    }

    [Fact]
    public void NothingAboutTheNamesIsBuiltIn()
    {
        var wheel = new SimulatedFilterWheel(WheelId, [new FilterSlot(0, "Foo")]);

        Assert.Equal(new FilterSlot(0, "Foo"), Assert.Single(wheel.Slots));
    }

    [Fact]
    public void NoSlots_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new SimulatedFilterWheel(WheelId, []));
    }

    [Fact]
    public void SlotsThatAreNotNumbered0ToN_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new SimulatedFilterWheel(WheelId, [new FilterSlot(1, "L"), new FilterSlot(2, "R")]));
        Assert.Throws<ArgumentException>(() => new SimulatedFilterWheel(WheelId, [new FilterSlot(0, "L"), new FilterSlot(0, "R")]));
        Assert.Throws<ArgumentException>(() => new SimulatedFilterWheel(WheelId, [new FilterSlot(1, "R"), new FilterSlot(0, "L")]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void AStartSlotThatDoesNotExist_IsRejected(int start)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedFilterWheel(WheelId, Seven, startSlotIndex: start));
    }

    [Fact]
    public void AnEmptyMoveDuration_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedFilterWheel(WheelId, Seven, moveDuration: TimeSpan.Zero));
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
        var wheel = new SimulatedFilterWheel(WheelId, Seven, events: bus);

        await wheel.ConnectAsync();
        await wheel.DisconnectAsync();

        Assert.Equal(
            [DeviceConnectionState.Connecting, DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected],
            states);
    }

    [Fact]
    public async Task ACancelledConnect_LeavesTheWheelDisconnected()
    {
        var wheel = new SimulatedFilterWheel(WheelId, Seven);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wheel.ConnectAsync(cts.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
    }

    // Moving

    [Fact]
    public async Task MoveToSlot_ArrivesAtTheSlot_AndEndsIdle()
    {
        var wheel = await Connected();

        await wheel.MoveToSlotAsync(4);

        Assert.Equal(new FilterSlot(4, "Ha"), wheel.CurrentSlot);
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
    }

    [Fact]
    public async Task MoveToSlot_PublishesMotionSlotAndMotion_InThatOrder()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var wheel = await Connected(bus);

        await wheel.MoveToSlotAsync(4);

        Assert.Equal(
            new ISideraEvent[]
            {
                new FilterWheelMotionStateChanged(WheelId, FilterWheelMotionState.Idle, FilterWheelMotionState.Moving, new FilterSlot(0, "L")),
                new FilterWheelSlotChanged(WheelId, new FilterSlot(0, "L"), new FilterSlot(4, "Ha")),
                new FilterWheelMotionStateChanged(WheelId, FilterWheelMotionState.Moving, FilterWheelMotionState.Idle, new FilterSlot(4, "Ha")),
            },
            recorder.Events);
    }

    [Fact]
    public async Task TheMotionStateIsMoving_WhileTheWheelTurns()
    {
        var wheel = await Connected(move: TimeSpan.FromMilliseconds(400));

        var move = wheel.MoveToSlotAsync(3);

        Assert.Equal(FilterWheelMotionState.Moving, wheel.MotionState);
        Assert.Equal(new FilterSlot(0, "L"), wheel.CurrentSlot);
        await move.WaitAsync(Bound);
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
    }

    [Fact]
    public async Task AMoveToTheCurrentSlot_CompletesAtOnce_AndPublishesNothing()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var wheel = await Connected(bus, move: TimeSpan.FromSeconds(30), start: 2);

        await wheel.MoveToSlotAsync(2).WaitAsync(Bound);

        Assert.Empty(recorder.Events);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public async Task ASlotThatDoesNotExist_IsRejected_WithoutMovingOrPublishing(int slot)
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var wheel = await Connected(bus);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => wheel.MoveToSlotAsync(slot));

        Assert.Equal(new FilterSlot(0, "L"), wheel.CurrentSlot);
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task MoveToSlot_RequiresAConnectedWheel()
    {
        var wheel = new SimulatedFilterWheel(WheelId, Seven, moveDuration: Quick);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.MoveToSlotAsync(1));

        Assert.Equal("Filter wheel is not connected.", error.Message);
    }

    [Fact]
    public async Task AMoveWhileMoving_IsRejected_AndTheRunningMoveIsUnaffected()
    {
        var wheel = await Connected(move: TimeSpan.FromMilliseconds(300));
        var first = wheel.MoveToSlotAsync(1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.MoveToSlotAsync(2));
        await first.WaitAsync(Bound);

        Assert.Equal("The filter wheel is already moving.", error.Message);
        Assert.Equal(new FilterSlot(1, "R"), wheel.CurrentSlot);
    }

    // Cancellation

    [Fact]
    public async Task ACancelledMove_StopsAtTheStartSlot_PublishesNoSlot_AndLeavesNoStaleMovingState()
    {
        var bus = new EventBus();
        var recorder = new Recorder(bus);
        var wheel = await Connected(bus, move: TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource();

        var move = wheel.MoveToSlotAsync(5, cts.Token);
        Assert.Equal(FilterWheelMotionState.Moving, wheel.MotionState);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        Assert.Equal(new FilterSlot(0, "L"), wheel.CurrentSlot);
        Assert.Equal(
            new ISideraEvent[]
            {
                new FilterWheelMotionStateChanged(WheelId, FilterWheelMotionState.Idle, FilterWheelMotionState.Moving, new FilterSlot(0, "L")),
                new FilterWheelMotionStateChanged(WheelId, FilterWheelMotionState.Moving, FilterWheelMotionState.Idle, new FilterSlot(0, "L")),
            },
            recorder.Events);
    }

    [Fact]
    public async Task Disconnecting_WhileMoving_IsRejected_AndTheMoveFinishes()
    {
        var wheel = await Connected(move: TimeSpan.FromMilliseconds(300));
        var move = wheel.MoveToSlotAsync(2);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.DisconnectAsync());
        await move.WaitAsync(Bound);

        Assert.Equal("Cannot disconnect while the filter wheel is moving.", error.Message);
        Assert.Equal(DeviceConnectionState.Connected, wheel.ConnectionState);
        await wheel.DisconnectAsync();
        Assert.Equal(new FilterSlot(2, "G"), wheel.CurrentSlot); // the slot survives a disconnect
    }

    [Fact]
    public async Task TheSameMovesPublishTheSameEvents_EveryTime()
    {
        var runs = new List<IReadOnlyList<ISideraEvent>>();
        for (var i = 0; i < 3; i++)
        {
            var bus = new EventBus();
            var recorder = new Recorder(bus);
            var wheel = await Connected(bus);
            await wheel.MoveToSlotAsync(4);
            await wheel.MoveToSlotAsync(1);
            runs.Add(recorder.Events);
        }

        Assert.Equal(6, runs[0].Count);
        Assert.All(runs.Skip(1), run => Assert.Equal(runs[0], run));
    }
}

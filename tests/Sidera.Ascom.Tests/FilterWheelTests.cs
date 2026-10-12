using Sidera.Ascom.FilterWheels;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom.Tests;

/// <summary>The ASCOM filter wheel adapter against a fake driver: lifetime, slots, moves, errors. Nothing is retried; a filter wheel cannot be halted.</summary>
public class FilterWheelTests
{
    private sealed record Rig(AscomFilterWheel Wheel, FakeDriverFactory Drivers, CallLog Log, EventSink Events);

    private static async Task<Rig> Connected(Action<FakeFilterWheelDriver>? configure = null, AscomTimings? timings = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFilterWheel = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var wheel = new AscomFilterWheel(
            new DeviceId("wheel.test"), "Test Wheel", "ASCOM.Test.FilterWheel", drivers, events, logs.Factory.CreateLogger("wheel"), timings ?? FastTimings.Create());
        await wheel.ConnectAsync();
        return new Rig(wheel, drivers, log, events);
    }

    [Fact]
    public async Task Connecting_UsesTheStaThread_AndTakesTheSlotsFromTheNamesOfTheDriver()
    {
        var rig = await Connected();

        Assert.Equal(DeviceConnectionState.Connected, rig.Wheel.ConnectionState);
        Assert.Equal(["L", "R", "G", "B", "Ha"], rig.Wheel.Slots.Select(s => s.Name));
        Assert.Equal([0, 1, 2, 3, 4], rig.Wheel.Slots.Select(s => s.Index));
        Assert.Equal(1, rig.Wheel.CurrentSlot.Index); // where the driver says the wheel is
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        rig.Log.AssertOneStaThread();
        Assert.Equal(["create ASCOM.Test.FilterWheel", "Connected = true"], rig.Log.Names.Take(2));
    }

    [Fact]
    public async Task ADeviceThatWasOnlyCreated_TouchesNoDriver()
    {
        var log = new CallLog();
        var wheel = new AscomFilterWheel(new DeviceId("wheel.x"), "X", "ASCOM.X", new FakeDriverFactory(log));

        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
        Assert.Empty(log.Calls);
        Assert.Empty(wheel.Slots);
        Assert.Throws<InvalidOperationException>(() => wheel.CurrentSlot);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AFilterWithoutAName_IsCalledByItsNumber()
    {
        var rig = await Connected(d => d.NameList = ["L", "", " ", "Ha"]);

        Assert.Equal(["L", "Filter 2", "Filter 3", "Ha"], rig.Wheel.Slots.Select(s => s.Name));
    }

    [Fact]
    public async Task AWheelWithoutFilters_IsRejected_AndLeavesNothingBehind()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFilterWheel = d => d.NameList = [] };
        var wheel = new AscomFilterWheel(new DeviceId("wheel.x"), "X", "ASCOM.X", drivers, null, null, FastTimings.Create());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wheel.ConnectAsync());

        Assert.Contains("no filters", ex.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
        Assert.True(drivers.FilterWheels.Single().Disposed);
    }

    [Fact]
    public async Task APositionOutsideTheSlots_IsRejected_RatherThanAssumed()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFilterWheel = d => d.StartPosition = 9 };
        var wheel = new AscomFilterWheel(new DeviceId("wheel.x"), "X", "ASCOM.X", drivers, null, null, FastTimings.Create());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wheel.ConnectAsync());

        Assert.Contains("position 9", ex.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
    }

    [Fact]
    public async Task AWheelThatIsStillTurningAtConnect_IsGivenAMoment_ThenRejected()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFilterWheel = d => d.StartPosition = -1 };
        var wheel = new AscomFilterWheel(new DeviceId("wheel.x"), "X", "ASCOM.X", drivers, null, null, FastTimings.Create());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => wheel.ConnectAsync());

        Assert.Contains("still turning", ex.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, wheel.ConnectionState);
    }

    [Fact]
    public async Task Moving_SetsThePosition_WaitsUntilItIsNoLongerMinusOne_AndPublishesTheEventsOfAMove()
    {
        var rig = await Connected();

        await rig.Wheel.MoveToSlotAsync(4);

        Assert.Equal("Ha", rig.Wheel.CurrentSlot.Name);
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        Assert.Equal(1, rig.Log.Count("Position = 4"));
        var motions = rig.Events.Of<FilterWheelMotionStateChanged>().ToList();
        Assert.Equal([FilterWheelMotionState.Moving, FilterWheelMotionState.Idle], motions.Select(m => m.NewState));
        Assert.Equal(1, motions[0].Slot.Index);
        Assert.Equal(4, motions[1].Slot.Index);
        var changed = Assert.Single(rig.Events.Of<FilterWheelSlotChanged>());
        Assert.Equal((1, 4), (changed.PreviousSlot.Index, changed.Slot.Index));
        rig.Log.AssertOneStaThread();
    }

    [Fact]
    public async Task MovingToTheSlotItIsAt_SetsNothing_AndPublishesNothing()
    {
        var rig = await Connected();

        await rig.Wheel.MoveToSlotAsync(1);

        Assert.Equal(0, rig.Log.Names.Count(n => n.StartsWith("Position =", StringComparison.Ordinal)));
        Assert.Empty(rig.Events.Of<FilterWheelMotionStateChanged>());
        Assert.Empty(rig.Events.Of<FilterWheelSlotChanged>());
    }

    [Fact]
    public async Task AWheelThatWasTurnedByHand_IsReadAgainBeforeAMove()
    {
        var rig = await Connected();
        rig.Drivers.FilterWheels.Single().StartPosition = 3;

        await rig.Wheel.MoveToSlotAsync(0);

        var changed = Assert.Single(rig.Events.Of<FilterWheelSlotChanged>());
        Assert.Equal((3, 0), (changed.PreviousSlot.Index, changed.Slot.Index));
    }

    [Fact]
    public async Task ASlotThatDoesNotExist_IsRefused_BeforeAnythingIsSet()
    {
        var rig = await Connected();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Wheel.MoveToSlotAsync(5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Wheel.MoveToSlotAsync(-1));

        Assert.Equal(0, rig.Log.Names.Count(n => n.StartsWith("Position =", StringComparison.Ordinal)));
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
    }

    [Fact]
    public async Task AMoveOfAWheelThatIsNotConnected_IsRefused()
    {
        var wheel = new AscomFilterWheel(new DeviceId("wheel.x"), "X", "ASCOM.X", new FakeDriverFactory(new CallLog()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => wheel.MoveToSlotAsync(0));
    }

    [Fact]
    public async Task ASecondMove_WhileTheWheelIsTurning_IsRefused_AndDisconnectingIsRefusedToo()
    {
        var rig = await Connected(d => d.HoldMove = true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = rig.Wheel.MoveToSlotAsync(3, cts.Token);
        await WaitUntilAsync(() => rig.Wheel.MotionState == FilterWheelMotionState.Moving);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Wheel.MoveToSlotAsync(2));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Wheel.DisconnectAsync());
        Assert.Contains("moving", ex.Message);

        rig.Drivers.FilterWheels.Single().Arrive();
        await first;
        Assert.Equal(3, rig.Wheel.CurrentSlot.Index);
        await rig.Wheel.DisconnectAsync();
    }

    [Fact]
    public async Task ADriverThatThrowsOnMove_IsNotRetried_AndTheWheelIsIdleAtItsSlot()
    {
        var rig = await Connected(d => d.MoveThrows = new InvalidOperationException("filter wheel jammed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Wheel.MoveToSlotAsync(3));

        Assert.Contains("jammed", ex.Message);
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        Assert.Equal(1, rig.Wheel.CurrentSlot.Index);
        Assert.Empty(rig.Events.Of<FilterWheelSlotChanged>());
        Assert.Equal(1, rig.Log.Count("Position = 3"));
    }

    [Fact]
    public async Task AWheelThatStopsAtAnotherSlot_IsAnError_AndTheSlotItReachedIsKept()
    {
        var rig = await Connected(d => d.ArriveAt = 2);

        var ex = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Wheel.MoveToSlotAsync(4));

        Assert.Contains("G", ex.Message);
        Assert.Contains("Ha", ex.Message);
        Assert.Equal(2, rig.Wheel.CurrentSlot.Index);
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        var changed = Assert.Single(rig.Events.Of<FilterWheelSlotChanged>());
        Assert.Equal((1, 2), (changed.PreviousSlot.Index, changed.Slot.Index));
    }

    [Fact]
    public async Task AMoveThatTakesTooLong_SaysTheWheelMayStillBeTurning_AndDoesNotRetry()
    {
        var rig = await Connected(d => d.HoldMove = true, FastTimings.Create(moveTimeout: TimeSpan.FromMilliseconds(100)));

        var ex = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Wheel.MoveToSlotAsync(3));

        Assert.Contains("may still be turning", ex.Message);
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        Assert.Equal(1, rig.Log.Count("Position = 3"));
    }

    [Fact]
    public async Task Cancelling_CannotStopTheWheel_SoItWaitsForIt_AndShowsWhereItEnded()
    {
        var rig = await Connected(d => d.MovePolls = 8);
        using var cts = new CancellationTokenSource();
        var move = rig.Wheel.MoveToSlotAsync(3, cts.Token);
        await WaitUntilAsync(() => rig.Log.Count("Position = 3") == 1);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(FilterWheelMotionState.Idle, rig.Wheel.MotionState);
        Assert.Equal(3, rig.Wheel.CurrentSlot.Index); // the wheel finished turning; Sidera shows where it is
        Assert.DoesNotContain(rig.Log.Names, n => n.Contains("Halt", StringComparison.Ordinal));
        var last = rig.Events.Of<FilterWheelMotionStateChanged>().Last();
        Assert.Equal((FilterWheelMotionState.Idle, 3), (last.NewState, last.Slot.Index));
    }

    [Fact]
    public async Task Disconnecting_ReleasesTheDriver_AndForgetsTheSlots()
    {
        var rig = await Connected();

        await rig.Wheel.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Wheel.ConnectionState);
        Assert.True(rig.Drivers.FilterWheels.Single().Disposed);
        Assert.Empty(rig.Wheel.Slots);
        Assert.Contains("Connected = false", rig.Log.Names);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The condition was not reached in time.");
            await Task.Delay(5);
        }
    }
}

using Astra.Ascom.Focusers;
using Astra.Ascom.Infrastructure;
using Astra.Ascom.Tests;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Astra.Ascom.Tests;

/// <summary>The ASCOM focuser adapter against a fake driver: lifetime, moves, stopping, errors. Nothing is retried.</summary>
public class FocuserTests
{
    private sealed record Rig(AscomFocuser Focuser, FakeDriverFactory Drivers, CallLog Log, EventSink Events, LogCapture Logs);

    private static Rig Create(Action<FakeFocuserDriver>? configure = null, AscomTimings? timings = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFocuser = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var focuser = new AscomFocuser(
            new DeviceId("focuser.test"), "Test Focuser", "ASCOM.Test.Focuser", drivers, events,
            logs.Factory.CreateLogger("focuser"), timings ?? FastTimings.Create());
        return new Rig(focuser, drivers, log, events, logs);
    }

    private static async Task<Rig> Connected(Action<FakeFocuserDriver>? configure = null, AscomTimings? timings = null)
    {
        var rig = Create(configure, timings);
        await rig.Focuser.ConnectAsync();
        return rig;
    }

    // Connecting

    [Fact]
    public async Task Connecting_CreatesAndConnectsTheDriverOnTheStaThread_ReadsTheRange_AndSaysSo()
    {
        var rig = Create();

        await rig.Focuser.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, rig.Focuser.ConnectionState);
        Assert.Equal((0, 50000, 25000), (rig.Focuser.MinPosition, rig.Focuser.MaxPosition, rig.Focuser.Position));
        Assert.Equal(["create ASCOM.Test.Focuser", "Connected = true"], rig.Log.Names.Take(2));
        rig.Log.AssertOneStaThread();
        Assert.Equal(
            [(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting), (DeviceConnectionState.Connecting, DeviceConnectionState.Connected)],
            rig.Events.Of<DeviceConnectionStateChanged>().Select(e => (e.PreviousState, e.NewState)));
    }

    [Fact]
    public void ADeviceThatWasOnlyCreated_TouchesNoDriver()
    {
        var rig = Create();

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.Empty(rig.Log.Calls);
        Assert.Empty(rig.Drivers.Focusers);
    }

    [Fact]
    public async Task TheBackendIsDescribed_ForTheUserInterface()
    {
        var rig = Create();

        var described = Assert.IsAssignableFrom<IBackendDescribed>(rig.Focuser);
        Assert.Equal(("ASCOM", "ASCOM.Test.Focuser"), (described.BackendName, described.DriverId));
        Assert.Equal(DeviceType.Focuser, rig.Focuser.Type);
        await rig.Focuser.DisposeAsync();
    }

    [Fact]
    public async Task ARelativeFocuser_Connects_ButHasNoPosition_AndRefusesAMoveToATarget()
    {
        var rig = await Connected(d => d.Absolute = false);

        Assert.Equal(DeviceConnectionState.Connected, rig.Focuser.ConnectionState);
        Assert.False(rig.Focuser.IsAbsolute);
        Assert.False(rig.Focuser.Capabilities.Value!.Absolute);
        Assert.Null(rig.Focuser.Telemetry!.Position);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.MoveToAsync(100));
        Assert.Contains("relative focuser", failure.Message);
        Assert.DoesNotContain(rig.Log.Names, n => n.StartsWith("Move ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailingConnect_ReleasesTheDriver_TranslatesTheError_AndIsNotRetried()
    {
        var rig = Create(d => d.ConnectThrows = new System.Runtime.InteropServices.COMException("no device", unchecked((int)0x80004005)));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Focuser.ConnectAsync());

        Assert.Contains("Could not connect Test Focuser (ASCOM.Test.Focuser)", failure.Message);
        Assert.Contains("no device", failure.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.Equal(1, rig.Log.Count("Connected = true"));
        Assert.True(Assert.Single(rig.Drivers.Focusers).Disposed);
    }

    [Fact]
    public async Task ADriverThatCannotBeCreated_IsReported_AndLeavesNothingBehind()
    {
        var rig = Create();
        rig.Drivers.CreateThrows = new System.Runtime.InteropServices.COMException("class not registered", unchecked((int)0x80040154));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Focuser.ConnectAsync());

        Assert.Contains("class not registered", failure.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.Empty(rig.Drivers.Focusers);
    }

    [Fact]
    public async Task ADriverThatConnectsTooSlowly_TimesOut_AndIsReleasedWhenItReturns()
    {
        var rig = Create(d => d.ConnectBlocks = TimeSpan.FromMilliseconds(400), FastTimings.Create(connectTimeout: TimeSpan.FromMilliseconds(80)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Focuser.ConnectAsync());

        Assert.Contains("did not connect within", failure.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        var driver = Assert.Single(rig.Drivers.Focusers);
        SpinWait.SpinUntil(() => driver.Disposed, TimeSpan.FromSeconds(5));
        Assert.True(driver.Disposed);
        Assert.Equal(["Connected = false", "Dispose"], rig.Log.Names.TakeLast(2)); // disconnected before it was released
    }

    [Fact]
    public async Task ACancelledConnect_ReleasesWhatItCreated()
    {
        var rig = Create(d => d.ConnectBlocks = TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Focuser.ConnectAsync(cts.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        var driver = Assert.Single(rig.Drivers.Focusers);
        SpinWait.SpinUntil(() => driver.Disposed, TimeSpan.FromSeconds(5));
        Assert.True(driver.Disposed);
    }

    // Disconnecting and disposing

    [Fact]
    public async Task Disconnecting_DisconnectsThenReleasesTheDriver_OnTheSameThread_AndDropsIt()
    {
        var rig = await Connected();

        await rig.Focuser.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.Equal(["Connected = false", "Dispose"], rig.Log.Names.TakeLast(2));
        rig.Log.AssertOneStaThread();
        Assert.True(rig.Drivers.Focusers[0].Disposed);
        Assert.Equal(
            [DeviceConnectionState.Connecting, DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected],
            rig.Events.Of<DeviceConnectionStateChanged>().Select(e => e.NewState));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.MoveToAsync(100)); // the released driver cannot be reached
    }

    [Fact]
    public async Task ADisconnectedFocuser_CanBeConnectedAgain_WithANewDriver()
    {
        var rig = await Connected();
        await rig.Focuser.DisconnectAsync();

        await rig.Focuser.ConnectAsync();

        Assert.Equal(2, rig.Drivers.Focusers.Count);
        Assert.NotSame(rig.Drivers.Focusers[0], rig.Drivers.Focusers[1]);
        Assert.True(rig.Drivers.Focusers[0].Disposed);
        Assert.False(rig.Drivers.Focusers[1].Disposed);
        Assert.Equal(2, rig.Log.Calls.Where(c => c.Name.StartsWith("create")).Select(c => c.ThreadId).Distinct().Count()); // a thread per connection
    }

    [Fact]
    public async Task DisconnectingWhileMoving_IsRefused_AndTheFocuserStaysConnected()
    {
        var rig = await Connected(d => d.HoldMove = true);
        var move = rig.Focuser.MoveToAsync(30000);
        await WaitUntilMoving(rig);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.DisconnectAsync());

        Assert.Contains("while the focuser is moving", failure.Message);
        Assert.Equal(DeviceConnectionState.Connected, rig.Focuser.ConnectionState);
        rig.Drivers.Focusers[0].Arrive();
        await move;
    }

    [Fact]
    public async Task Dispose_IsIdempotent_ReleasesAConnectedDevice_EvenWhileItMoves()
    {
        var rig = await Connected(d => d.HoldMove = true);
        var move = rig.Focuser.MoveToAsync(30000);
        await WaitUntilMoving(rig);

        await rig.Focuser.DisposeAsync();
        await rig.Focuser.DisposeAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.True(rig.Drivers.Focusers[0].Disposed);
        Assert.Equal(1, rig.Log.Count("Dispose"));
        await Assert.ThrowsAnyAsync<Exception>(() => move); // the move cannot finish on a released device
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.MoveToAsync(100));
    }

    [Fact]
    public async Task ADisposedFocuser_CannotBeConnected()
    {
        var rig = Create();
        await rig.Focuser.DisposeAsync();

        await rig.Focuser.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, rig.Focuser.ConnectionState);
        Assert.Empty(rig.Drivers.Focusers);
    }

    // Moving

    [Fact]
    public async Task AMove_IsOneMoveCall_ThenPollingOfIsMoving_AndReportsTheEventsOfASimulatedMove()
    {
        var rig = await Connected();

        await rig.Focuser.MoveToAsync(31000);

        Assert.Equal(1, rig.Log.Count("Move 31000"));
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
        Assert.True(rig.Log.Count("get IsMoving") >= 2);
        Assert.Equal((31000, FocuserMotionState.Idle), (rig.Focuser.Position, rig.Focuser.MotionState));
        Assert.Equal(
            [(FocuserMotionState.Idle, FocuserMotionState.Moving, 25000), (FocuserMotionState.Moving, FocuserMotionState.Idle, 31000)],
            rig.Events.Of<FocuserMotionStateChanged>().Select(e => (e.PreviousState, e.NewState, e.Position)));
        var position = Assert.Single(rig.Events.Of<FocuserPositionChanged>());
        Assert.Equal((25000, 31000), (position.PreviousPosition, position.Position));
        rig.Log.AssertOneStaThread();
    }

    [Fact]
    public async Task TheEventsOfAMove_ComeInTheOrderMotionStartedPositionMotionEnded()
    {
        var rig = await Connected();

        await rig.Focuser.MoveToAsync(26000);

        Assert.Equal(
            ["FocuserMotionStateChanged", "FocuserPositionChanged", "FocuserMotionStateChanged"],
            rig.Events.Events.Where(e => e is not DeviceConnectionStateChanged).Select(e => e.GetType().Name));
    }

    [Fact]
    public async Task AMoveToTheCurrentPosition_MovesNothing_AndPublishesNothing()
    {
        var rig = await Connected();
        var before = rig.Events.Events.Count;

        await rig.Focuser.MoveToAsync(25000);

        Assert.Equal(0, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
        Assert.Equal(before, rig.Events.Events.Count);
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState);
    }

    [Fact]
    public async Task TheStartIsTheRealPosition_NotTheOneRememberedFromConnect()
    {
        var rig = await Connected();
        rig.Drivers.Focusers[0].PositionValue = 20000; // moved by hand meanwhile

        await rig.Focuser.MoveToAsync(21000);

        Assert.Equal(20000, rig.Events.Of<FocuserPositionChanged>().Single().PreviousPosition);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50001)]
    public async Task ATargetOutsideTheRange_IsRefused_BeforeAnythingMoves(int target)
    {
        var rig = await Connected();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Focuser.MoveToAsync(target));

        Assert.Equal(0, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState);
    }

    [Fact]
    public async Task TheLimitsThemselves_AreValidTargets()
    {
        var rig = await Connected();

        await rig.Focuser.MoveToAsync(0);
        await rig.Focuser.MoveToAsync(50000);

        Assert.Equal(50000, rig.Focuser.Position);
    }

    [Fact]
    public async Task AFocuserThatIsNotConnected_RefusesToMove()
    {
        var rig = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.MoveToAsync(100));
    }

    [Fact]
    public async Task ASecondMoveWhileOneRuns_IsRefused_AndDoesNotDisturbTheFirst()
    {
        var rig = await Connected(d => d.HoldMove = true);
        var first = rig.Focuser.MoveToAsync(30000);
        await WaitUntilMoving(rig);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Focuser.MoveToAsync(40000));

        rig.Drivers.Focusers[0].Arrive();
        await first;
        Assert.Equal(30000, rig.Focuser.Position);
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
    }

    [Fact]
    public async Task APositionThatCannotBeReadWhileMoving_DoesNotBreakTheMove()
    {
        var rig = await Connected(d => d.PositionThrowsWhileMoving = new ASCOM.PropertyNotImplementedException("Position", false));

        await rig.Focuser.MoveToAsync(30000);

        Assert.Equal(30000, rig.Focuser.Position);
    }

    // Stopping

    [Fact]
    public async Task Cancelling_CallsHalt_ReportsWhereTheFocuserStopped_AndSaysItWasConfirmed()
    {
        var rig = await Connected(d => (d.HoldMove, d.HaltStops) = (true, true));
        using var cts = new CancellationTokenSource();
        var move = rig.Focuser.MoveToAsync(35000, cts.Token);
        await WaitUntilMoving(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(1, rig.Log.Count("Halt"));
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState);
        Assert.Equal(30000, rig.Focuser.Position); // halfway, as the driver reports it: never the target
        var ended = rig.Events.Of<FocuserMotionStateChanged>().Last();
        Assert.Equal((FocuserMotionState.Idle, 30000), (ended.NewState, ended.Position));
        Assert.Equal(30000, rig.Events.Of<FocuserPositionChanged>().Single().Position);
        Assert.NotEmpty(rig.Logs.Containing("Test Focuser confirmed stopped after the cancelled move"));
        Assert.Empty(rig.Logs.Containing("could not be confirmed stopped"));
    }

    [Fact]
    public async Task AfterAHalt_ThePositionIsReadUntilItHasSettled_NotJustOnce()
    {
        // The real EAF says "not moving" at once after Halt, but its position still catches up for a moment.
        var rig = await Connected(d => (d.HoldMove, d.HaltStops, d.StalePositionReadsAfterHalt) = (true, true, 2));
        using var cts = new CancellationTokenSource();
        var move = rig.Focuser.MoveToAsync(35000, cts.Token);
        await WaitUntilMoving(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(30000, rig.Focuser.Position);
        Assert.Equal(30000, rig.Events.Of<FocuserMotionStateChanged>().Last().Position);
    }

    [Fact]
    public async Task ADriverWithoutHalt_IsNotClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldMove, d.HaltThrows) = (true, new ASCOM.MethodNotImplementedException("Halt")));
        using var cts = new CancellationTokenSource();
        var move = rig.Focuser.MoveToAsync(35000, cts.Token);
        await WaitUntilMoving(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState); // Astra has stopped waiting ...
        Assert.NotEmpty(rig.Logs.Containing("could not be confirmed stopped")); // ... and says it cannot vouch for the hardware
        Assert.Empty(rig.Logs.Containing("Test Focuser confirmed stopped after the cancelled move"));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Halt did not work"));
    }

    [Fact]
    public async Task AHaltThatTheFocuserIgnores_IsNotClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldMove, d.HaltStops) = (true, false));
        using var cts = new CancellationTokenSource();
        var move = rig.Focuser.MoveToAsync(35000, cts.Token);
        await WaitUntilMoving(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(1, rig.Log.Count("Halt"));
        Assert.NotEmpty(rig.Logs.Containing("could not be confirmed stopped"));
    }

    [Fact]
    public async Task ADriverErrorOnMove_IsTranslated_HaltIsTried_TheMoveIsNotRetried_AndTheStateIsIdle()
    {
        var rig = await Connected(d => d.MoveThrows = new ASCOM.InvalidValueException("Move", "99999", "0 to 50000"));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Focuser.MoveToAsync(30000));

        Assert.Contains("Could not move Test Focuser", failure.Message);
        Assert.Contains("rejected a value", failure.Message);
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
        Assert.Equal(1, rig.Log.Count("Halt"));
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState);
        Assert.Equal(FocuserMotionState.Idle, rig.Events.Of<FocuserMotionStateChanged>().Last().NewState);
        Assert.Empty(rig.Events.Of<FocuserPositionChanged>()); // the target was never reported as reached
    }

    [Fact]
    public async Task AMoveThatTakesTooLong_TimesOut_HaltsTheFocuser_AndSaysWhetherItStopped()
    {
        var rig = await Connected(d => (d.HoldMove, d.HaltStops) = (true, true), FastTimings.Create(moveTimeout: TimeSpan.FromMilliseconds(80)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Focuser.MoveToAsync(40000));

        Assert.Contains("did not finish moving to 40000", failure.Message);
        Assert.Contains("accepted Halt", failure.Message);
        Assert.Equal(FocuserMotionState.Idle, rig.Focuser.MotionState);
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("Move ")));
    }

    [Fact]
    public async Task ATimeoutOfAFocuserThatCannotBeStopped_SaysItMayStillBeMoving()
    {
        var rig = await Connected(
            d => (d.HoldMove, d.HaltThrows) = (true, new ASCOM.MethodNotImplementedException("Halt")),
            FastTimings.Create(moveTimeout: TimeSpan.FromMilliseconds(80)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Focuser.MoveToAsync(40000));

        Assert.Contains("may still be moving", failure.Message);
        Assert.DoesNotContain("accepted Halt", failure.Message);
    }

    [Fact]
    public async Task EveryDriverCall_AfterConnect_ComesOnTheOneStaThread()
    {
        var rig = await Connected();
        await rig.Focuser.MoveToAsync(26000);
        await rig.Focuser.DisconnectAsync();

        rig.Log.AssertOneStaThread();
    }

    // The move has reached the driver: the state says Moving before the call is made, so that alone is not enough.
    private static Task WaitUntilMoving(Rig rig) =>
        WaitUntil(() => rig.Focuser.MotionState == FocuserMotionState.Moving && rig.Log.Calls.Any(c => c.Name.StartsWith("Move ")));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }
}

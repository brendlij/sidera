using Sidera.Ascom.Infrastructure;
using Sidera.Ascom.Rotators;
using Sidera.Core.Devices;
using Sidera.Core.Rotators;
using Sidera.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom.Tests;

/// <summary>The ASCOM rotator adapter against a fake driver: lifetime, capabilities, moves, halting, errors. Nothing is retried.</summary>
public class RotatorTests
{
    private sealed record Rig(AscomRotator Rotator, FakeDriverFactory Drivers, CallLog Log, EventSink Events, LogCapture Logs);

    private static async Task<Rig> Connected(Action<FakeRotatorDriver>? configure = null, AscomTimings? timings = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureRotator = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var rotator = new AscomRotator(
            new DeviceId("rotator.test"), "Test Rotator", "ASCOM.Test.Rotator", drivers, events, logs.Factory.CreateLogger("rotator"), timings ?? FastTimings.Create());
        await rotator.ConnectAsync();
        return new Rig(rotator, drivers, log, events, logs);
    }

    [Fact]
    public async Task Connecting_UsesTheStaThread_ReadsThePosition_AndProbesWhatTheDriverCan()
    {
        var rig = await Connected();

        Assert.Equal(DeviceConnectionState.Connected, rig.Rotator.ConnectionState);
        Assert.Equal(42, rig.Rotator.Position);
        Assert.Equal(12, rig.Rotator.MechanicalPosition);
        var c = rig.Rotator.Capabilities.Value!;
        Assert.True(c.AbsoluteMove && c.RelativeMove && c.CanReverse && c.HasMechanicalPosition);
        Assert.True(c.CanSync); // the fake driver says interface version 3
        Assert.Null(c.CanHalt); // not known until a halt was tried
        Assert.Equal(0.01, c.StepSizeDegrees);
        rig.Log.AssertOneStaThread();
        Assert.Equal(["create ASCOM.Test.Rotator", "Connected = true"], rig.Log.Names.Take(2));
    }

    [Fact]
    public async Task ADeviceThatWasOnlyCreated_TouchesNoDriver()
    {
        var log = new CallLog();
        var rotator = new AscomRotator(new DeviceId("rotator.x"), "X", "ASCOM.X", new FakeDriverFactory(log));

        Assert.Equal(DeviceConnectionState.Disconnected, rotator.ConnectionState);
        Assert.Empty(log.Calls);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ADriverWithoutAMechanicalPosition_IsNotClaimedToHaveOne()
    {
        var rig = await Connected(d => d.MechanicalValue = null);

        Assert.False(rig.Rotator.Capabilities.Value!.HasMechanicalPosition);
        Assert.Null(rig.Rotator.MechanicalPosition);
    }

    [Fact]
    public async Task ADriverThatCannotReverse_SaysSo_AndRefusesToReverse()
    {
        var rig = await Connected(d => d.CanReverseValue = false);

        Assert.False(rig.Rotator.Capabilities.Value!.CanReverse);
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => rig.Rotator.SetReversedAsync(true));
    }

    [Fact]
    public async Task AnAbsoluteMove_CommandsTheDriver_PollsUntilItArrives_AndPublishesTheEvents()
    {
        var rig = await Connected();

        await rig.Rotator.MoveToAsync(90);

        Assert.Equal(90, rig.Rotator.Position);
        Assert.Equal(RotatorMotionState.Idle, rig.Rotator.MotionState);
        Assert.Contains("MoveAbsolute 90", rig.Log.Names);
        Assert.Equal(
            [(RotatorMotionState.Idle, RotatorMotionState.Moving), (RotatorMotionState.Moving, RotatorMotionState.Idle)],
            rig.Events.Of<RotatorMotionStateChanged>().Select(e => (e.PreviousState, e.NewState)));
        var changed = Assert.Single(rig.Events.Of<RotatorPositionChanged>());
        Assert.Equal((42.0, 90.0), (changed.PreviousPosition, changed.Position));
        rig.Log.AssertOneStaThread();
    }

    [Theory]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(725, 5)]
    public async Task APositionOutsideZeroTo360_IsCommandedAsItsAngleOnTheCircle(double asked, double commanded)
    {
        var rig = await Connected();

        await rig.Rotator.MoveToAsync(asked);

        Assert.Contains($"MoveAbsolute {commanded}", rig.Log.Names);
    }

    [Fact]
    public async Task ARelativeMove_UsesTheRelativeCall()
    {
        var rig = await Connected();

        await ((IRotatorControl)rig.Rotator).MoveByAsync(-5);

        Assert.Contains("Move -5", rig.Log.Names);
        Assert.Equal(37, rig.Rotator.Position);
    }

    [Fact]
    public async Task AMoveByNothing_DoesNothing()
    {
        var rig = await Connected();

        await ((IRotatorControl)rig.Rotator).MoveByAsync(0);

        Assert.DoesNotContain(rig.Log.Names, n => n.StartsWith("Move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANumberThatIsNotAnAngle_IsRefusedBeforeAnythingMoves()
    {
        var rig = await Connected();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Rotator.MoveToAsync(double.NaN));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ((IRotatorControl)rig.Rotator).MoveByAsync(double.PositiveInfinity));
        Assert.DoesNotContain(rig.Log.Names, n => n.StartsWith("Move", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_HaltsTheRotator_ConfirmsItStopped_AndLeavesItAtWhereItWas()
    {
        var rig = await Connected(d => d.HoldMove = true);
        using var cts = new CancellationTokenSource();

        var move = rig.Rotator.MoveToAsync(180, cts.Token);
        await WaitUntil(() => rig.Rotator.MotionState == RotatorMotionState.Moving && rig.Log.Names.Contains("MoveAbsolute 180"));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Contains("Halt", rig.Log.Names);
        Assert.Equal(RotatorMotionState.Idle, rig.Rotator.MotionState);
        Assert.True(rig.Rotator.Capabilities.Value!.CanHalt);
        Assert.NotEqual(180, rig.Rotator.Position); // the target is never reported as reached
        Assert.Equal(RotatorMotionState.Idle, rig.Events.Of<RotatorMotionStateChanged>().Last().NewState);
    }

    [Fact]
    public async Task ADriverWithoutHalt_IsNeverClaimedToHaveStopped()
    {
        var rig = await Connected(d =>
        {
            d.HoldMove = true;
            d.HaltThrows = new NotImplementedException("Halt");
        });
        using var cts = new CancellationTokenSource();
        var move = rig.Rotator.MoveToAsync(180, cts.Token);
        await WaitUntil(() => rig.Log.Names.Contains("MoveAbsolute 180"));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.False(rig.Rotator.Capabilities.Value!.CanHalt);
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("could not be confirmed stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHaltFromOutside_StopsTheRunningMove_WithoutWaitingForIt()
    {
        var rig = await Connected(d => d.MovePolls = 1000);
        var move = rig.Rotator.MoveToAsync(180);
        await WaitUntil(() => rig.Rotator.MotionState == RotatorMotionState.Moving && rig.Log.Names.Contains("MoveAbsolute 180"));

        await ((IRotatorControl)rig.Rotator).HaltAsync();
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RotatorMotionState.Idle, rig.Rotator.MotionState);
        Assert.Equal(111, rig.Rotator.Position); // half way from 42 to 180: where the fake driver stopped
    }

    [Fact]
    public async Task AMoveThatNeverEnds_TimesOut_HaltsTheRotator_AndSaysWhetherItStopped()
    {
        var rig = await Connected(d => d.HoldMove = true, FastTimings.Create(moveTimeout: TimeSpan.FromMilliseconds(100)));

        var ex = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Rotator.MoveToAsync(180));

        Assert.Contains("did not finish moving", ex.Message);
        Assert.Contains("Halt", rig.Log.Names);
        Assert.Equal(RotatorMotionState.Idle, rig.Rotator.MotionState);
    }

    [Fact]
    public async Task AFailingMove_IsNotRetried_AndTheRotatorIsLeftIdle()
    {
        var rig = await Connected(d => d.MoveThrows = new InvalidOperationException("stalled"));

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Rotator.MoveToAsync(90));

        Assert.Equal(1, rig.Log.Names.Count(n => n.StartsWith("MoveAbsolute", StringComparison.Ordinal)));
        Assert.Equal(RotatorMotionState.Idle, rig.Rotator.MotionState);
    }

    [Fact]
    public async Task ASecondMove_WhileOneRuns_IsRefused()
    {
        var rig = await Connected(d => d.HoldMove = true);
        using var cts = new CancellationTokenSource();
        var first = rig.Rotator.MoveToAsync(90, cts.Token);
        await WaitUntil(() => rig.Rotator.MotionState == RotatorMotionState.Moving);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rotator.MoveToAsync(10));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task ADisconnectWhileMoving_IsRefused_ThenWorksAfterTheHalt()
    {
        var rig = await Connected(d => d.HoldMove = true);
        using var cts = new CancellationTokenSource();
        var move = rig.Rotator.MoveToAsync(90, cts.Token);
        await WaitUntil(() => rig.Rotator.MotionState == RotatorMotionState.Moving);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Rotator.DisconnectAsync());

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        await rig.Rotator.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, rig.Rotator.ConnectionState);
        Assert.Null(rig.Rotator.Capabilities.Value);
        Assert.True(rig.Drivers.Rotators.Single().Disposed);
    }

    [Fact]
    public async Task ASync_ChangesWhatTheDriverCallsThePosition_AndAnUnsyncableDriverRefusesIt()
    {
        var rig = await Connected();

        await ((IRotatorControl)rig.Rotator).SyncAsync(100);

        Assert.Contains("Sync 100", rig.Log.Names);
        Assert.Equal(100, rig.Rotator.Position);

        var old = await Connected(d => d.Identity = new DriverMetadata("Old", "old driver", "info", "1", 1));
        Assert.False(old.Rotator.Capabilities.Value!.CanSync);
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => ((IRotatorControl)old.Rotator).SyncAsync(10));
    }

    [Fact]
    public async Task Reversing_IsPassedToTheDriver_AndShownInTheTelemetry()
    {
        var rig = await Connected();

        await ((IRotatorControl)rig.Rotator).SetReversedAsync(true);

        Assert.Contains("Reverse = True", rig.Log.Names);
        Assert.True(rig.Rotator.Telemetry!.Reversed);
    }

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

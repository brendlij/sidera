using Sidera.Ascom.Infrastructure;
using Sidera.Ascom.Mounts;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom.Tests;

/// <summary>The ASCOM mount adapter against a fake driver: state first, async and blocking slews, stopping, errors.</summary>
public class MountTests
{
    private sealed record Rig(AscomMount Mount, FakeDriverFactory Drivers, CallLog Log, EventSink Events, LogCapture Logs);

    private static async Task<Rig> Connected(Action<FakeMountDriver>? configure = null, AscomTimings? timings = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureMount = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var mount = new AscomMount(
            new DeviceId("mount.test"), "Test Mount", "ASCOM.Test.Telescope", drivers, events,
            logs.Factory.CreateLogger("mount"), timings ?? FastTimings.Create());
        await mount.ConnectAsync();
        return new Rig(mount, drivers, log, events, logs);
    }

    private static IEnumerable<string> Slews(Rig rig) => rig.Log.Names.Where(n => n.StartsWith("SlewToCoordinates"));

    [Fact]
    public async Task Connecting_ReadsTheStateOnTheStaThread_AndStartsInTheStateTheMountIsIn()
    {
        var rig = await Connected();

        Assert.Equal(DeviceConnectionState.Connected, rig.Mount.ConnectionState);
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
        Assert.Equal((3.0, 10.0), (rig.Mount.Coordinates.RightAscensionHours, rig.Mount.Coordinates.DeclinationDegrees));
        rig.Log.AssertOneStaThread();
        Assert.Equal(DeviceType.Mount, rig.Mount.Type);
        Assert.Equal("ASCOM", ((IBackendDescribed)rig.Mount).BackendName);
    }

    [Fact]
    public async Task AMountThatIsNotTracking_StartsIdle()
    {
        var rig = await Connected(d => d.TrackingValue = false);

        Assert.Equal(MountMotionState.Idle, rig.Mount.MotionState);
    }

    [Fact]
    public async Task AnAsynchronousSlew_StartsOnce_PollsSlewing_AndEndsTracking()
    {
        var rig = await Connected();

        await rig.Mount.SlewToAsync(new CelestialCoordinates(5.5, -5));

        Assert.Equal(["SlewToCoordinatesAsync 5.5 -5"], Slews(rig));
        Assert.True(rig.Log.Count("get Slewing") >= 3);
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
        Assert.Equal((5.5, -5.0), (rig.Mount.Coordinates.RightAscensionHours, rig.Mount.Coordinates.DeclinationDegrees));
        Assert.Equal(
            [(MountMotionState.Idle, MountMotionState.Slewing), (MountMotionState.Slewing, MountMotionState.Tracking)],
            rig.Events.Of<MountMotionStateChanged>().Select(e => (e.PreviousState, e.NewState)));
        var arrived = rig.Events.Of<MountMotionStateChanged>().Last();
        Assert.Equal((5.5, -5.0), (arrived.Coordinates.RightAscensionHours, arrived.Coordinates.DeclinationDegrees));
        rig.Log.AssertOneStaThread();
    }

    [Fact]
    public async Task TheStateIsReadBeforeAnythingMoves()
    {
        var rig = await Connected();
        rig.Log.Add("marker");

        await rig.Mount.SlewToAsync(new CelestialCoordinates(5.5, -5));

        var names = rig.Log.Names.SkipWhile(n => n != "marker").ToList();
        var slew = names.FindIndex(n => n.StartsWith("SlewToCoordinates"));
        Assert.True(names.IndexOf("get AtPark") is >= 0 and var park && park < slew);
        Assert.True(names.IndexOf("get Slewing") is >= 0 and var slewing && slewing < slew);
    }

    [Fact]
    public async Task ABlockingSlew_IsCalledOnce_WhenTheDriverHasNoAsynchronousOne()
    {
        var rig = await Connected(d => d.CanSlewAsync = false);

        await rig.Mount.SlewToAsync(new CelestialCoordinates(7, 20));

        Assert.Equal(["SlewToCoordinates 7 20"], Slews(rig));
        Assert.Equal((7.0, 20.0), (rig.Mount.Coordinates.RightAscensionHours, rig.Mount.Coordinates.DeclinationDegrees));
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
    }

    [Fact]
    public async Task AMountThatCannotSlew_IsRefused_WithoutAnySlewCall()
    {
        var rig = await Connected(d => d.CanSlew = false);

        var failure = await Assert.ThrowsAsync<AscomUnsupportedException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(1, 1)));

        Assert.Contains("cannot slew", failure.Message);
        Assert.Empty(Slews(rig));
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
        Assert.Empty(rig.Events.Of<MountMotionStateChanged>());
    }

    [Fact]
    public async Task AParkedMount_IsNotSlewed_AndNotUnparked()
    {
        var rig = await Connected(d => d.AtParkValue = true);

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(1, 1)));

        Assert.Contains("is parked", failure.Message);
        Assert.Empty(Slews(rig));
        Assert.DoesNotContain("Unpark", rig.Log.Names);
        Assert.Empty(rig.Events.Of<MountMotionStateChanged>());
    }

    [Fact]
    public async Task AMountThatIsAlreadySlewing_IsNotAskedToSlewAgain()
    {
        var rig = await Connected();
        rig.Drivers.Mounts[0].HoldSlew = true;
        rig.Drivers.Mounts[0].SlewToCoordinatesAsync(1, 1); // somebody else started a slew
        rig.Log.Add("marker");

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(5, 5)));

        Assert.Single(Slews(rig)); // only the foreign one
    }

    [Fact]
    public async Task TrackingIsSwitchedOn_WhenTheMountIsNotTrackingAndCanBeTold()
    {
        var rig = await Connected(d => d.TrackingValue = false);

        await rig.Mount.SlewToAsync(new CelestialCoordinates(5.5, -5));

        Assert.Equal(1, rig.Log.Count("Tracking = True"));
        Assert.True(rig.Log.Names.ToList().IndexOf("Tracking = True") < rig.Log.Names.ToList().FindIndex(n => n.StartsWith("SlewToCoordinates")));
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
    }

    [Fact]
    public async Task AMountThatCannotTrack_SlewsAnyway_AndEndsIdle()
    {
        var rig = await Connected(d => (d.TrackingValue, d.CanSetTracking) = (false, false));

        await rig.Mount.SlewToAsync(new CelestialCoordinates(5.5, -5));

        Assert.Equal(0, rig.Log.Count("Tracking = True"));
        Assert.Equal(MountMotionState.Idle, rig.Mount.MotionState);
    }

    [Fact]
    public async Task ASlewIsNeverRetried_WhenTheDriverFails()
    {
        var rig = await Connected(d => d.SlewThrows = new ASCOM.InvalidOperationException("tracking is off"));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(5.5, -5)));

        Assert.Contains("Could not slew Test Mount", failure.Message);
        Assert.Contains("refused the operation", failure.Message);
        Assert.Single(Slews(rig));
        Assert.Equal(1, rig.Log.Count("AbortSlew"));
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
        Assert.Equal(MountMotionState.Tracking, rig.Events.Of<MountMotionStateChanged>().Last().NewState);
    }

    [Fact]
    public async Task Cancelling_CallsAbortSlew_AndReportsWhereTheMountStopped()
    {
        var rig = await Connected(d => (d.HoldSlew, d.AbortStops) = (true, true));
        using var cts = new CancellationTokenSource();
        var slew = rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30), cts.Token);
        await WaitUntilSlewing(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
        Assert.Equal(1, rig.Log.Count("AbortSlew"));
        Assert.Single(Slews(rig));
        Assert.Equal(MountMotionState.Tracking, rig.Mount.MotionState);
        Assert.Equal((4.0, 20.0), (rig.Mount.Coordinates.RightAscensionHours, rig.Mount.Coordinates.DeclinationDegrees)); // halfway: not the target
        Assert.NotEmpty(rig.Logs.Containing("Test Mount confirmed stopped after the cancelled slew"));
    }

    [Fact]
    public async Task AnAbortThatFails_IsNotClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldSlew, d.AbortThrows) = (true, new ASCOM.MethodNotImplementedException("AbortSlew")));
        using var cts = new CancellationTokenSource();
        var slew = rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30), cts.Token);
        await WaitUntilSlewing(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
        Assert.NotEmpty(rig.Logs.Containing("Test Mount could not be confirmed stopped after the cancelled slew"));
        Assert.Empty(rig.Logs.Containing("Test Mount confirmed stopped"));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("AbortSlew did not work"));
    }

    [Fact]
    public async Task AnAbortThatTheMountIgnores_IsNotClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldSlew, d.AbortStops) = (true, false));
        using var cts = new CancellationTokenSource();
        var slew = rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30), cts.Token);
        await WaitUntilSlewing(rig);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
        Assert.NotEmpty(rig.Logs.Containing("could not be confirmed stopped"));
    }

    [Fact]
    public async Task ASlewThatTakesTooLong_TimesOut_AbortsTheMount_AndSaysWhetherItStopped()
    {
        var rig = await Connected(d => (d.HoldSlew, d.AbortStops) = (true, true), FastTimings.Create(slewTimeout: TimeSpan.FromMilliseconds(80)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30)));

        Assert.Contains("did not finish slewing", failure.Message);
        Assert.Contains("accepted AbortSlew", failure.Message);
        Assert.Single(Slews(rig));
        Assert.NotEqual(MountMotionState.Slewing, rig.Mount.MotionState);
    }

    [Fact]
    public async Task ATimeoutOfAMountThatCannotBeStopped_SaysItMayStillBeSlewing()
    {
        var rig = await Connected(
            d => (d.HoldSlew, d.AbortThrows) = (true, new ASCOM.MethodNotImplementedException("AbortSlew")),
            FastTimings.Create(slewTimeout: TimeSpan.FromMilliseconds(80)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30)));

        Assert.Contains("may still be slewing", failure.Message);
    }

    [Fact]
    public async Task DisconnectingWhileSlewing_IsRefused()
    {
        var rig = await Connected(d => d.HoldSlew = true);
        var slew = rig.Mount.SlewToAsync(new CelestialCoordinates(5, 30));
        await WaitUntilSlewing(rig);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Mount.DisconnectAsync());

        Assert.Contains("while the mount is slewing", failure.Message);
        Assert.Equal(DeviceConnectionState.Connected, rig.Mount.ConnectionState);
        rig.Drivers.Mounts[0].Arrive();
        await slew;
    }

    [Fact]
    public async Task Disconnecting_DisconnectsThenReleasesTheDriver()
    {
        var rig = await Connected();

        await rig.Mount.DisconnectAsync();

        Assert.Equal(["Connected = false", "Dispose"], rig.Log.Names.TakeLast(2));
        Assert.True(rig.Drivers.Mounts[0].Disposed);
        rig.Log.AssertOneStaThread();
    }

    [Fact]
    public async Task AMountThatIsNotConnected_RefusesToSlew()
    {
        var mount = new AscomMount(new DeviceId("mount.x"), "X", "ASCOM.X", new FakeDriverFactory(new CallLog()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewToAsync(new CelestialCoordinates(1, 1)));
    }

    // Coordinates from the driver

    [Theory]
    [InlineData(24.0, 0.0)]
    [InlineData(-0.0001, 23.9999)]
    [InlineData(25.5, 1.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(23.999, 23.999)]
    public void RightAscension_IsWrappedIntoZeroToTwentyFourHours(double reported, double expected)
    {
        var coordinates = AscomMount.Normalize(reported, 10);

        Assert.Equal(expected, coordinates.RightAscensionHours, 6);
    }

    [Theory]
    [InlineData(90.0000001, 90.0)]
    [InlineData(-90.0000001, -90.0)]
    [InlineData(45.5, 45.5)]
    public void Declination_AtThePoles_IsClampedOnlyWithinRounding(double reported, double expected)
    {
        Assert.Equal(expected, AscomMount.Normalize(1, reported).DeclinationDegrees, 6);
    }

    [Theory]
    [InlineData(91.0)]
    [InlineData(-120.0)]
    [InlineData(double.NaN)]
    public void ADeclinationBeyondThePoles_OrNotANumber_IsRefused(double reported)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AscomMount.Normalize(1, reported));
    }

    [Fact]
    public void ARightAscensionThatIsNotANumber_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AscomMount.Normalize(double.PositiveInfinity, 1));
    }

    private static async Task WaitUntilSlewing(Rig rig)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!(rig.Mount.MotionState == MountMotionState.Slewing && rig.Log.Names.Any(n => n.StartsWith("SlewToCoordinatesAsync"))))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the slew.");
            await Task.Delay(2);
        }
    }
}

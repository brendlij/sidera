using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;

namespace Sidera.Runtime.Tests.Mounts;

public class SimulatedMountTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(20);

    private static readonly DeviceId MountId = new("mount.eq6");
    private static readonly CelestialCoordinates Target = new(5.5, 22.5);

    // Coordinates

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12.5, 45.25)]
    [InlineData(23.999, -90)]
    [InlineData(6, 90)]
    public void Coordinates_AcceptValidValues(double ra, double dec)
    {
        var coordinates = new CelestialCoordinates(ra, dec);

        Assert.Equal(ra, coordinates.RightAscensionHours);
        Assert.Equal(dec, coordinates.DeclinationDegrees);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(24)]
    [InlineData(30)]
    public void Coordinates_RejectInvalidRightAscension(double ra)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CelestialCoordinates(ra, 0));
    }

    [Theory]
    [InlineData(-90.01)]
    [InlineData(90.01)]
    [InlineData(180)]
    public void Coordinates_RejectInvalidDeclination(double dec)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CelestialCoordinates(0, dec));
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(0, double.NaN)]
    [InlineData(0, double.NegativeInfinity)]
    public void Coordinates_RejectNaNAndInfinity(double ra, double dec)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CelestialCoordinates(ra, dec));
    }

    [Fact]
    public void Coordinates_HaveValueEquality()
    {
        Assert.Equal(new CelestialCoordinates(1, 2), new CelestialCoordinates(1, 2));
        Assert.NotEqual(new CelestialCoordinates(1, 2), new CelestialCoordinates(1, 3));
    }

    // SimulatedMount

    [Fact]
    public void NewMount_StartsDisconnected_IdleAtTheDefaultCoordinates()
    {
        var mount = new SimulatedMount(MountId);

        Assert.Equal(DeviceType.Mount, mount.Type);
        Assert.Equal(DeviceConnectionState.Disconnected, mount.ConnectionState);
        Assert.Equal(MountMotionState.Idle, mount.MotionState);
        Assert.Equal(SimulatedMount.DefaultCoordinates, mount.Coordinates);
    }

    [Fact]
    public async Task ConnectAndDisconnect_ChangeTheConnectionState()
    {
        var mount = new SimulatedMount(MountId);

        await mount.ConnectAsync();
        Assert.Equal(DeviceConnectionState.Connected, mount.ConnectionState);

        await mount.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, mount.ConnectionState);
    }

    [Fact]
    public async Task Slew_RequiresAConnectedMount()
    {
        var mount = new SimulatedMount(MountId, slewDuration: Quick);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewToAsync(Target));

        Assert.Equal("Mount is not connected.", error.Message);
        Assert.Equal(MountMotionState.Idle, mount.MotionState);
    }

    [Fact]
    public async Task Slew_GoesThroughSlewingAndEndsTrackingAtTheTarget()
    {
        var mount = new SimulatedMount(MountId, slewDuration: TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource();

        var slew = mount.SlewToAsync(Target, cts.Token);

        Assert.Equal(MountMotionState.Slewing, mount.MotionState);
        // Not there yet: it is on its way, still at the start after a moment of a ten second slew (it reports where it is, not where it is going).
        Assert.True(Sidera.Core.Astrometry.SkyMath.AngularSeparationDegrees(mount.Coordinates, SimulatedMount.DefaultCoordinates) < 1);
        Assert.NotEqual(Target, mount.Coordinates);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);

        var quick = new SimulatedMount(MountId, slewDuration: Quick);
        await quick.ConnectAsync();
        await quick.SlewToAsync(Target);

        Assert.Equal(MountMotionState.Tracking, quick.MotionState);
        Assert.Equal(Target, quick.Coordinates);
    }

    [Fact]
    public async Task CancelledSlew_EndsIdle_KeepsLastReachedCoordinates_AndNeverReportsTheTarget()
    {
        var mount = new SimulatedMount(MountId, slewDuration: TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource();

        var slew = mount.SlewToAsync(Target, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew.WaitAsync(Bound));

        Assert.Equal(MountMotionState.Idle, mount.MotionState);
        Assert.Equal(SimulatedMount.DefaultCoordinates, mount.Coordinates);
    }

    [Fact]
    public async Task AfterACancelledSlew_ANewSlewStillWorks()
    {
        var mount = new SimulatedMount(MountId, slewDuration: Quick);
        await mount.ConnectAsync();
        using (var cts = new CancellationTokenSource())
        {
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mount.SlewToAsync(Target, cts.Token));
        }

        await mount.SlewToAsync(Target);

        Assert.Equal(MountMotionState.Tracking, mount.MotionState);
        Assert.Equal(Target, mount.Coordinates);
    }

    [Fact]
    public async Task Slew_WhileSlewing_AndDisconnectWhileSlewing_AreRejected()
    {
        var mount = new SimulatedMount(MountId, slewDuration: TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var slew = mount.SlewToAsync(Target, cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewToAsync(new CelestialCoordinates(1, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.DisconnectAsync());

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
        Assert.Equal(DeviceConnectionState.Connected, mount.ConnectionState);
    }

    // Events and StateStore

    [Fact]
    public async Task MotionStateChanges_ArePublished_Idle_Slewing_Tracking()
    {
        var bus = new EventBus();
        var events = new List<MountMotionStateChanged>();
        bus.Subscribe<MountMotionStateChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });
        var mount = new SimulatedMount(MountId, events: bus, slewDuration: Quick);
        await mount.ConnectAsync();

        await mount.SlewToAsync(Target);

        Assert.Equal(
            new[]
            {
                (MountMotionState.Idle, MountMotionState.Slewing),
                (MountMotionState.Slewing, MountMotionState.Tracking),
            },
            events.Select(e => (e.PreviousState, e.NewState)));
        Assert.Equal(SimulatedMount.DefaultCoordinates, events[0].Coordinates);
        Assert.Equal(Target, events[1].Coordinates);
        Assert.All(events, e => Assert.Equal(MountId, e.DeviceId));
    }

    [Fact]
    public async Task CancelledSlew_PublishesSlewingThenIdle_NeverTracking()
    {
        var bus = new EventBus();
        var states = new List<MountMotionState>();
        bus.Subscribe<MountMotionStateChanged>((e, _) =>
        {
            lock (states)
            {
                states.Add(e.NewState);
            }

            return Task.CompletedTask;
        });
        var mount = new SimulatedMount(MountId, events: bus, slewDuration: TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource();

        var slew = mount.SlewToAsync(Target, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);

        lock (states)
        {
            Assert.Equal(new[] { MountMotionState.Slewing, MountMotionState.Idle }, states);
        }
    }

    [Fact]
    public async Task StateStore_ReflectsSlewingThenTrackingAndTheNewCoordinates()
    {
        await using var host = new SideraRuntimeHost();
        var mount = host.AddSimulatedMount(MountId, "EQ6", TimeSpan.FromSeconds(10));
        await mount.ConnectAsync();
        using var cts = new CancellationTokenSource();

        var slew = mount.SlewToAsync(Target, cts.Token);
        Assert.True(host.StateStore.TryGet(MountId, out var slewing));
        Assert.Equal(MountMotionState.Slewing, slewing!.MotionState);
        Assert.Equal(DeviceConnectionState.Connected, slewing.ConnectionState);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);

        var quick = host.AddSimulatedMount(new DeviceId("mount.quick"), "Quick", Quick);
        await quick.ConnectAsync();
        await quick.SlewToAsync(Target);

        Assert.True(host.StateStore.TryGet(quick.Id, out var tracking));
        Assert.Equal(MountMotionState.Tracking, tracking!.MotionState);
        Assert.Equal(Target, tracking.Coordinates);
    }

    [Fact]
    public async Task Host_RegistersAndRetrievesTheSimulatedMount()
    {
        await using var host = new SideraRuntimeHost();

        var mount = host.AddSimulatedMount(MountId, "EQ6");

        Assert.True(host.DeviceRegistry.TryGet(MountId, out var device));
        Assert.Same(mount, device);
        Assert.IsAssignableFrom<IMount>(device);
    }
}

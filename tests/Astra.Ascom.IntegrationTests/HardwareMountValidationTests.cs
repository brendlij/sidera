using Astra.Ascom.Drivers;
using Astra.Ascom.Mounts;
using Astra.Core.Devices;
using Astra.Core.Mounts;
using Xunit.Abstractions;

namespace Astra.Ascom.IntegrationTests;

/// <summary>
/// The real mount. Reading is safe and runs with <c>ASTRA_ASCOM_MOUNT</c>. Every physical action has a gate of its own and does
/// nothing without it: <c>ASTRA_ASCOM_MOUNT_TRACKING_OK=1</c> (switches tracking once and puts it back),
/// <c>ASTRA_ASCOM_MOUNT_SLEW_OK=1</c> (a slew of a fraction of a degree, and one that is stopped part of the way),
/// <c>ASTRA_ASCOM_MOUNT_AXIS_OK=1</c> (the slowest rate of one axis for a second). Sync, park, unpark and find-home have no real
/// test: they change the mount's model or its state and are only tested against the ASCOM simulator. Even with a gate set a test
/// first checks that the driver's state looks alive: a mount that is not powered or linked answers with zeros and a standing clock.
/// </summary>
public sealed class HardwareMountValidationTests(ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static bool Gate(string name) => Env(name) == "1";

    private AscomMount NewMount() => new(new DeviceId("mount.real"), "Real Mount", Env("ASTRA_ASCOM_MOUNT"), Drivers, logger: new OutputLogger(output));

    // Not proof that a mount is attached behind the driver (a driver can compute all of this), but it refuses the obvious
    // case of a driver that answers with nothing: no site, no sidereal time, or a mount that claims to be slewing.
    private async Task<string?> WhyNotAliveAsync(AscomMount mount)
    {
        await mount.RefreshAsync();
        var first = mount.Telemetry!;
        if (first.Slewing)
        {
            return "the driver says the mount is slewing";
        }

        if (first.SiderealTimeHours is not { } lst || lst < 0 || lst >= 24)
        {
            return $"the sidereal time is {first.SiderealTimeHours}";
        }

        if (mount.Site is not { } site || (site.LatitudeDegrees == 0 && site.LongitudeDegrees == 0))
        {
            return "the site is 0 / 0";
        }

        await Task.Delay(TimeSpan.FromSeconds(3));
        await mount.RefreshAsync();
        var second = mount.Telemetry!;
        if (second.SiderealTimeHours == first.SiderealTimeHours)
        {
            return "the sidereal time does not advance";
        }

        if (second.AtPark)
        {
            return "the mount is parked (Astra does not unpark it in a test)";
        }

        return null;
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_Telemetry_ThroughAstra_ReadOnly()
    {
        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            var c = mount.Capabilities.Value!;
            await mount.RefreshAsync();
            var t = mount.Telemetry!;
            output.WriteLine($"coordinates {t.Coordinates} altaz {t.Horizontal} tracking {t.Tracking} rate {t.Rate} slewing {t.Slewing} atPark {t.AtPark} atHome {t.AtHome} " +
                $"pier {t.SideOfPier} lst {t.SiderealTimeHours} utc {t.UtcDate:o} site {mount.Site}");
            output.WriteLine($"capabilities slew {c.CanSlew}/{c.CanSlewAsync} sync {c.CanSync} park {c.CanPark}/{c.CanUnpark} home {c.CanFindHome} tracking {c.CanSetTracking} " +
                $"axes {c.CanMovePrimaryAxis}/{c.CanMoveSecondaryAxis} rates {string.Join(" ", c.AxisRates.Select(r => $"{r.Key}:[{string.Join(",", r.Value.Select(x => $"{x.Minimum}-{x.Maximum}"))}]"))}");
            output.WriteLine("alive check: " + (await WhyNotAliveAsync(mount) ?? "looks alive"));
            Assert.False(t.Slewing);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_Tracking_IsSwitchedOnceAndPutBack_OnlyWithItsGate()
    {
        if (!Gate("ASTRA_ASCOM_MOUNT_TRACKING_OK"))
        {
            return;
        }

        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var initial = mount.Telemetry!.Tracking;
            output.WriteLine($"tracking is {initial}");
            try
            {
                await mount.SetTrackingAsync(!initial);
                await mount.RefreshAsync();
                output.WriteLine($"tracking switched to {!initial}: driver reads {mount.Telemetry!.Tracking}");
                Assert.Equal(!initial, mount.Telemetry!.Tracking);
            }
            finally
            {
                await mount.SetTrackingAsync(initial);
                await mount.RefreshAsync();
                output.WriteLine($"tracking put back to {initial}: driver reads {mount.Telemetry!.Tracking}");
            }

            Assert.Equal(initial, mount.Telemetry!.Tracking);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_SlewsAFractionOfADegree_AndBack_OnlyWithItsGate()
    {
        if (!Gate("ASTRA_ASCOM_MOUNT_SLEW_OK"))
        {
            return;
        }

        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var start = mount.Coordinates;
            var target = new CelestialCoordinates((start.RightAscensionHours + 0.02) % 24, start.DeclinationDegrees);
            output.WriteLine($"from {start} to {target} (0.3 degrees of right ascension)");
            var slew = mount.SlewToAsync(target);
            await Task.Delay(500);
            output.WriteLine($"during the slew: Astra state {mount.MotionState}");
            await slew;
            await mount.RefreshAsync();
            output.WriteLine($"arrived at {mount.Coordinates}, slewing {mount.Telemetry!.Slewing}");
            Assert.False(mount.Telemetry!.Slewing);
            Assert.Equal(target.RightAscensionHours, mount.Coordinates.RightAscensionHours, 2);

            await mount.SlewToAsync(start);
            output.WriteLine($"back at {mount.Coordinates}");
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_AStopDuringASlew_EndsItAndLeavesTheMountUsable_OnlyWithItsGate()
    {
        if (!Gate("ASTRA_ASCOM_MOUNT_SLEW_OK"))
        {
            return;
        }

        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var start = mount.Coordinates;
            var target = new CelestialCoordinates((start.RightAscensionHours + 0.1) % 24, start.DeclinationDegrees);
            var slew = mount.SlewToAsync(target);
            await Task.Delay(1000);
            await mount.StopAsync();
            var outcome = await Record.ExceptionAsync(() => slew);
            await mount.RefreshAsync();
            output.WriteLine($"slew task ended with {outcome?.GetType().Name ?? "completion"}; slewing {mount.Telemetry!.Slewing}; at {mount.Coordinates}; state {mount.MotionState}");
            Assert.False(mount.Telemetry!.Slewing);

            await mount.SlewToAsync(start);
            output.WriteLine($"back at {mount.Coordinates}");
        }
        finally
        {
            await mount.StopAsync();
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_MovesAnAxisAtItsSlowestRateForASecond_AndStops_OnlyWithItsGate()
    {
        if (!Gate("ASTRA_ASCOM_MOUNT_AXIS_OK"))
        {
            return;
        }

        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var c = mount.Capabilities.Value!;
            var axis = MountAxis.Primary;
            Assert.True(c.CanMove(axis));
            // The range may start at 0 (the ASI mount reports 0 to 6): then about the sidereal rate, 15 arc seconds per second.
            var slowest = c.AxisRates[axis].Min(r => r.Minimum > 0 ? r.Minimum : Math.Min(r.Maximum, 0.0042));
            Assert.True(slowest > 0);
            var before = mount.Coordinates;
            try
            {
                await mount.MoveAxisAsync(axis, slowest);
                await Task.Delay(1000);
            }
            finally
            {
                await mount.StopAsync();
            }

            await mount.RefreshAsync();
            output.WriteLine($"axis moved at {slowest} deg/s for a second: {before} -> {mount.Coordinates}, slewing {mount.Telemetry!.Slewing}");
            Assert.False(mount.Telemetry!.Slewing);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }
}

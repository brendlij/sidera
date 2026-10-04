using Sidera.Core;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Mounts;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Runtime;
using Xunit.Abstractions;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// The real mount. Reading is safe and runs with <c>SIDERA_ASCOM_MOUNT</c>. Every physical action has a gate of its own and does
/// nothing without it: <c>SIDERA_ASCOM_MOUNT_TRACKING_OK=1</c> (switches tracking once and puts it back),
/// <c>SIDERA_ASCOM_MOUNT_SLEW_OK=1</c> (a slew of a fraction of a degree, and one that is stopped part of the way),
/// <c>SIDERA_ASCOM_MOUNT_AXIS_OK=1</c> (the slowest rate of one axis for a second), <c>SIDERA_ASCOM_MOUNT_SITE_WRITE_OK=1</c> (writes the site the mount reports back to it, unchanged). Sync, park, unpark and find-home have no real
/// test: they change the mount's model or its state and are only tested against the ASCOM simulator. Even with a gate set a test
/// first checks that the driver's state looks alive: a mount that is not powered or linked answers with zeros and a standing clock.
/// </summary>
public sealed class HardwareMountValidationTests(ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();

    private static string Env(string name) => SideraEnvironment.Get(name) ?? string.Empty;

    private static bool Gate(string name) => Env(name) == "1";

    private AscomMount NewMount() => new(new DeviceId("mount.real"), "Real Mount", Env("SIDERA_ASCOM_MOUNT"), Drivers, logger: new OutputLogger(output));

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
            return "the mount is parked (Sidera does not unpark it in a test)";
        }

        return null;
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_Telemetry_ThroughSidera_ReadOnly()
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

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_Site_IsReadAndValidated_ReadOnly()
    {
        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            var c = mount.Capabilities.Value!;
            output.WriteLine($"has site {c.HasSite}; site write {c.SiteWrite}");
            Assert.True(c.HasSite, "the mount reports no site");
            var site = mount.Site!;
            var place = site.ToObservingSite();
            output.WriteLine($"raw {site.LatitudeDegrees} / {site.LongitudeDegrees} / {site.ElevationMeters} m");
            output.WriteLine(place is null
                ? "NOT A PLACE: out of range or 0 / 0 (the mount holds no site)"
                : $"{Sidera.Core.Location.GeoCoordinateFormat.FormatLatitude(place.LatitudeDegrees)}, {Sidera.Core.Location.GeoCoordinateFormat.FormatLongitude(place.LongitudeDegrees)}, {Sidera.Core.Location.GeoCoordinateFormat.FormatElevation(place.ElevationMeters)}");

            // What Sidera would say against a site a few meters away and against one far away, from the real values.
            if (place is not null)
            {
                var near = new Sidera.Core.Location.ObservingSite(Math.Clamp(place.LatitudeDegrees + 0.00003, -90, 90), place.LongitudeDegrees, place.ElevationMeters);
                var far = new Sidera.Core.Location.ObservingSite(Math.Clamp(place.LatitudeDegrees + 1, -90, 90), place.LongitudeDegrees, place.ElevationMeters);
                output.WriteLine($"3 m away: {Sidera.Runtime.Location.MountSiteSynchronizer.Assess(near, site, c.SiteWrite).Situation}; 1 degree away: {Sidera.Runtime.Location.MountSiteSynchronizer.Assess(far, site, c.SiteWrite).Situation}");
            }
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_Site_IsWrittenBackUnchanged_OnlyWithItsGate()
    {
        if (!Gate("SIDERA_ASCOM_MOUNT_SITE_WRITE_OK"))
        {
            output.WriteLine("NOT RUN: set SIDERA_ASCOM_MOUNT_SITE_WRITE_OK=1 to write the site that the mount reports back to it.");
            return;
        }

        var mount = NewMount();
        await mount.ConnectAsync();
        try
        {
            var before = mount.Site!;
            var place = before.ToObservingSite();
            if (place is null)
            {
                output.WriteLine($"NOT RUN: the mount reports no place ({before}); Sidera does not invent one to write.");
                return;
            }

            // The same values: if the mount takes them nothing changes, and if it refuses it says so.
            var outcome = await Sidera.Runtime.Location.MountSiteSynchronizer.SendToMountAsync(mount, place);
            output.WriteLine($"outcome: succeeded {outcome.Succeeded}; {outcome.Problem}; read back {outcome.ReadBack}; before {before}");
            Assert.True(outcome.Succeeded || outcome.Problem is not null);
            Assert.Equal(before.LatitudeDegrees, mount.Site!.LatitudeDegrees, 3);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_Tracking_IsSwitchedOnceAndPutBack_OnlyWithItsGate()
    {
        if (!Gate("SIDERA_ASCOM_MOUNT_TRACKING_OK"))
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

    // A target near the one the mount has, along declination: a change of right ascension at the pole (where the mount waits at home)
    // would move nothing at all, and nothing is ever sent to a fixed or named place. Never more than the given number of degrees.
    private static CelestialCoordinates Nearby(CelestialCoordinates from, double degrees) =>
        new(from.RightAscensionHours, from.DeclinationDegrees > 0 ? from.DeclinationDegrees - degrees : from.DeclinationDegrees + degrees);

    // The slews go through the host, the way the Mount page starts one: the same lease, the same events.
    private async Task<(SideraRuntimeHost Host, AscomMount Mount)> HostedMountAsync()
    {
        var host = new SideraRuntimeHost();
        var mount = NewMount();
        host.AddDevice(mount);
        await host.DeviceOperations.ConnectAsync(mount.Id);
        return (host, mount);
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_SlewsHalfADegree_AndBack_OnlyWithItsGate()
    {
        if (!Gate("SIDERA_ASCOM_MOUNT_SLEW_OK"))
        {
            return;
        }

        var (host, mount) = await HostedMountAsync();
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var start = mount.Coordinates;
            var target = Nearby(start, 0.5);
            Assert.NotEqual(new CelestialCoordinates(0, 0), target);
            output.WriteLine($"from {start} to {target} (0.5 degrees of declination)");

            var states = new List<MountMotionState>();
            using var watching = new CancellationTokenSource();
            var watcher = Task.Run(async () =>
            {
                while (!watching.IsCancellationRequested)
                {
                    var state = mount.MotionState;
                    if (states.Count == 0 || states[^1] != state)
                    {
                        states.Add(state);
                    }

                    await Task.Delay(25);
                }
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await host.DeviceOperations.SlewToAsync(mount.Id, target);
            output.WriteLine($"the slew took {clock.ElapsedMilliseconds} ms; states seen: {string.Join(" -> ", states)}");
            await watching.CancelAsync();
            await watcher;

            await mount.RefreshAsync();
            output.WriteLine($"arrived at {mount.Coordinates}, driver slewing {mount.Telemetry!.Slewing}, Sidera state {mount.MotionState}");
            Assert.False(mount.Telemetry!.Slewing);
            Assert.Equal(target.DeclinationDegrees, mount.Coordinates.DeclinationDegrees, 1);

            // Back where it was. A second slew only starts when the first one has let go of the mount.
            await host.DeviceOperations.SlewToAsync(mount.Id, start);
            await mount.RefreshAsync();
            output.WriteLine($"back at {mount.Coordinates}");
            Assert.Equal(start.DeclinationDegrees, mount.Coordinates.DeclinationDegrees, 1);
        }
        finally
        {
            await mount.StopAsync();
            await host.DeviceOperations.DisconnectAsync(mount.Id);
            await host.DisposeAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_AStopDuringASlew_EndsItAndLeavesTheMountUsable_OnlyWithItsGate()
    {
        if (!Gate("SIDERA_ASCOM_MOUNT_SLEW_OK"))
        {
            return;
        }

        var (host, mount) = await HostedMountAsync();
        var start = mount.Coordinates;
        try
        {
            Assert.Null(await WhyNotAliveAsync(mount));
            var target = Nearby(start, 2);
            output.WriteLine($"from {start} to {target} (2 degrees of declination), to be stopped");
            var slew = host.DeviceOperations.SlewToAsync(mount.Id, target);
            var sawSlewing = false;
            for (var i = 0; i < 40 && !slew.IsCompleted; i++)
            {
                await mount.RefreshAsync();
                if (mount.Telemetry!.Slewing)
                {
                    sawSlewing = true;
                    break;
                }

                await Task.Delay(50);
            }

            output.WriteLine($"the mount reported slewing before the stop: {sawSlewing}");
            await mount.StopAsync();
            var outcome = await Record.ExceptionAsync(() => slew);
            await mount.RefreshAsync();
            output.WriteLine($"slew task ended with {outcome?.GetType().Name ?? "completion"}; driver slewing {mount.Telemetry!.Slewing}; at {mount.Coordinates}; Sidera state {mount.MotionState}");
            Assert.False(mount.Telemetry!.Slewing);

            // Usable afterwards: another read, and a slew back that is accepted.
            await mount.RefreshAsync();
            await host.DeviceOperations.SlewToAsync(mount.Id, start);
            await mount.RefreshAsync();
            output.WriteLine($"back at {mount.Coordinates}");
            Assert.Equal(start.DeclinationDegrees, mount.Coordinates.DeclinationDegrees, 1);
        }
        finally
        {
            await mount.StopAsync();
            await host.DeviceOperations.DisconnectAsync(mount.Id);
            await host.DisposeAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_MovesAnAxisAtItsSlowestRateForASecond_AndStops_OnlyWithItsGate()
    {
        if (!Gate("SIDERA_ASCOM_MOUNT_AXIS_OK"))
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

using Sidera.Ascom.Drivers;
using Sidera.Ascom.Mounts;
using Sidera.Core;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Xunit.Abstractions;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// The physical part of a meridian flip on a real mount, for a person at the telescope. It runs only with <c>SIDERA_ASCOM_MOUNT=&lt;ProgId&gt;</c> and does nothing that moves the mount
/// without <c>SIDERA_MERIDIAN_FLIP_OK=1</c> and a target that the person chose with <c>SIDERA_MERIDIAN_FLIP_TARGET=&lt;ra hours&gt;,&lt;dec degrees&gt;</c> (a target that is just past the meridian, with
/// the mechanics clear). Without the gate it only reads and reports where the target is: its hour angle, the side of the pier, and when it crosses. With the gate it slews to the target, once,
/// and reports the side of the pier before and after; plate solving, centering and guiding are not part of it (they have their own tests), and the mount is never synchronized.
/// </summary>
public sealed class MeridianFlipHardwareTests(ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();

    private static string Env(string name) => SideraEnvironment.Get(name) ?? string.Empty;

    private static (double Ra, double Dec)? Target()
    {
        var parts = Env("SIDERA_MERIDIAN_FLIP_TARGET").Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ra)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dec)
            ? (ra, dec)
            : null;
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_ReportsWhereTheMeridianIs_AndFlipsOnlyWithItsGate()
    {
        var mount = new AscomMount(new DeviceId("mount.real"), "Real Mount", Env("SIDERA_ASCOM_MOUNT"), Drivers, logger: new OutputLogger(output));
        await mount.ConnectAsync();
        try
        {
            await mount.RefreshAsync();
            var telemetry = mount.Telemetry!;
            var site = mount.Site?.ToObservingSite();
            output.WriteLine($"pointing RA {mount.Coordinates.RightAscensionHours:0.####} h Dec {mount.Coordinates.DeclinationDegrees:0.###}°, side of pier {telemetry.SideOfPier}, slewing {telemetry.Slewing}, parked {telemetry.AtPark}");
            Assert.NotNull(site);

            if (Target() is not { } target)
            {
                output.WriteLine("SIDERA_MERIDIAN_FLIP_TARGET=<ra hours>,<dec degrees> is not set: nothing is calculated for a target.");
                return;
            }

            var hourAngle = MeridianFlipTiming.HourAngleHours(target.Ra, DateTime.UtcNow, site!.LongitudeDegrees);
            output.WriteLine($"target RA {target.Ra:0.####} h Dec {target.Dec:0.###}°: hour angle {hourAngle * 60:0.#} min ({(hourAngle < 0 ? "crosses the meridian in" : "crossed the meridian")} {Math.Abs(hourAngle) * 60:0.#} min)");

            if (Env("SIDERA_MERIDIAN_FLIP_OK") != "1")
            {
                output.WriteLine("SIDERA_MERIDIAN_FLIP_OK=1 is not set: the mount is not moved.");
                return;
            }

            if (telemetry.Slewing || telemetry.AtPark)
            {
                output.WriteLine("The mount is slewing or parked: the flip is not started (Sidera does not unpark it in a test).");
                return;
            }

            var before = telemetry.SideOfPier;
            await mount.SlewToAsync(new CelestialCoordinates(target.Ra, target.Dec)).WaitAsync(TimeSpan.FromMinutes(5));
            await mount.RefreshAsync();
            output.WriteLine($"flip slew done: side of pier {before} to {mount.Telemetry!.SideOfPier}, now RA {mount.Coordinates.RightAscensionHours:0.####} h Dec {mount.Coordinates.DeclinationDegrees:0.###}°, slewing {mount.Telemetry.Slewing}");
            Assert.False(mount.Telemetry.Slewing);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }
}

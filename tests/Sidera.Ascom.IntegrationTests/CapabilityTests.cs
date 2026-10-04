using Sidera.Core;
using Sidera.Ascom.Cameras;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Focusers;
using Sidera.Ascom.Infrastructure;
using Sidera.Ascom.Mounts;
using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Mounts;

namespace Sidera.Ascom.IntegrationTests;

/// <summary>
/// The capability model against real drivers: the ASCOM simulators of the installed Platform (<c>SIDERA_ASCOM_TESTS=1</c>) and,
/// reading only, the real devices (<c>SIDERA_ASCOM_FOCUSER</c>, <c>_MOUNT</c>, <c>_CAMERA</c>). Nothing here moves a mount or
/// a focuser; the camera tests change binning and the region and put them back.
/// </summary>
public class CapabilityTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string CameraProgId = "ASCOM.Simulator.Camera";
    private const string FocuserProgId = "ASCOM.Simulator.Focuser";
    private const string MountProgId = "ASCOM.Simulator.Telescope";

    private static readonly ComAscomDriverFactory Drivers = new();
    private static string Env(string name) => SideraEnvironment.Get(name) ?? string.Empty;

    private void Describe(CameraCapabilities c) => output.WriteLine(
        $"Camera: {c.Driver.Name} v{c.Driver.DriverVersion} (interface {c.Driver.InterfaceVersion}) sensor {c.SensorWidth}x{c.SensorHeight} {c.SensorType} bayer {c.BayerOffset} " +
        $"maxADU {c.MaxAdu} pixel {c.PixelSizeXMicrons}x{c.PixelSizeYMicrons} bin {c.MaxBinX}x{c.MaxBinY} asym {c.CanAsymmetricBin} subframe {c.SupportsSubframe} " +
        $"gain {Describe(c.Gain)} offset {Describe(c.Offset)} readout [{string.Join("|", c.ReadoutModes)}] fast {c.CanFastReadout} cooling {c.SupportsCooling} " +
        $"(setTemp {c.CanSetCcdTemperature}, cooler {c.HasCooler}, power {c.CanGetCoolerPower}, ccd {c.HasCcdTemperature}, sink {c.HasHeatSinkTemperature}) " +
        $"abort {c.CanAbortExposure} stop {c.CanStopExposure} exposure {c.MinExposureSeconds}..{c.MaxExposureSeconds} notes [{string.Join("; ", c.Notes)}]");

    private static string Describe(IntegerControl? control) => control is null
        ? "none"
        : control.IsList ? $"list[{string.Join("|", control.Choices)}]" : $"{control.Minimum}..{control.Maximum}";

    private void Describe(MountCapabilities c) => output.WriteLine(
        $"Mount: {c.Driver.Name} v{c.Driver.DriverVersion} (interface {c.Driver.InterfaceVersion}) slew {c.CanSlew}/{c.CanSlewAsync} altaz {c.CanSlewAltAz}/{c.CanSlewAltAzAsync} " +
        $"sync {c.CanSync} park {c.CanPark}/{c.CanUnpark}/{c.CanSetPark} home {c.CanFindHome} tracking {c.CanSetTracking} rates [{string.Join("|", c.TrackingRates)}] " +
        $"pulse {c.CanPulseGuide} guideRates {c.CanSetGuideRates} axes {c.CanMovePrimaryAxis}/{c.CanMoveSecondaryAxis}/{c.CanMoveTertiaryAxis} " +
        $"system {c.EquatorialSystem} alignment {c.Alignment} refraction {c.HasRefractionSetting} site {c.HasSite} lst {c.HasSiderealTime} utc {c.HasUtcDate} altaz-read {c.HasAltAz} " +
        $"predictPier {c.CanPredictPierSide} notes [{string.Join("; ", c.Notes)}]");

    private void Describe(FocuserCapabilities c) => output.WriteLine(
        $"Focuser: {c.Driver.Name} v{c.Driver.DriverVersion} (interface {c.Driver.InterfaceVersion}) absolute {c.Absolute} max {c.MaxStep} increment {c.MaxIncrement} " +
        $"step {c.StepSizeMicrons} temperature {c.HasTemperature} tempComp {c.TempCompAvailable} halt {c.CanHalt} notes [{string.Join("; ", c.Notes)}]");

    // ---- ASCOM simulators

    [AscomFact]
    public async Task TheCameraSimulator_ReportsItsCapabilities_AfterTheConnect_AndNotBefore()
    {
        var camera = new AscomCamera(new DeviceId("camera.sim"), "Camera Simulator", CameraProgId, Drivers);
        Assert.Equal(CapabilityStatus.Unknown, camera.Capabilities.Status);

        await camera.ConnectAsync();
        try
        {
            var c = camera.Capabilities.Value!;
            Describe(c);
            Assert.Equal((800, 600), (c.SensorWidth, c.SensorHeight));
            Assert.True(c.SupportsSubframe);
            Assert.True(c.MaxBinX >= 1);
            Assert.NotNull(camera.Settings);
            Assert.NotNull(c.Driver.Name);
        }
        finally
        {
            await camera.DisconnectAsync();
        }

        Assert.Equal(CapabilityStatus.Unknown, camera.Capabilities.Status);
    }

    [AscomFact]
    public async Task TheCameraSimulator_AppliesBinningAndARegion_AndTheFrameFollows()
    {
        var camera = new AscomCamera(new DeviceId("camera.sim"), "Camera Simulator", CameraProgId, Drivers);
        await camera.ConnectAsync();
        try
        {
            var c = camera.Capabilities.Value!;
            if (c.MaxBinX >= 2)
            {
                await camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });
                var binned = await camera.ExposeAsync(TimeSpan.FromSeconds(0.3));
                Assert.Equal((c.SensorWidth / 2, c.SensorHeight / 2), (binned.Width, binned.Height));
                await camera.ApplyAsync(new CameraSettings { BinX = 1, BinY = 1 });
            }

            await camera.ApplyAsync(new CameraSettings { StartX = 100, StartY = 50, NumX = 200, NumY = 120 });
            var region = await camera.ExposeAsync(TimeSpan.FromSeconds(0.3));
            Assert.Equal((200, 120), (region.Width, region.Height));

            await camera.ApplyAsync(new CameraSettings { StartX = 0, StartY = 0, NumX = c.SensorWidth, NumY = c.SensorHeight });
            var full = await camera.ExposeAsync(TimeSpan.FromSeconds(0.3));
            Assert.Equal((c.SensorWidth, c.SensorHeight), (full.Width, full.Height));

            await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { BinX = c.MaxBinX + 1, BinY = c.MaxBinX + 1 }));
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheCameraSimulator_ExposesWithItsSettingsAppliedFirst_AndTheFrameSaysWhatItWasTakenWith()
    {
        var camera = new AscomCamera(new DeviceId("camera.sim"), "Camera Simulator", CameraProgId, Drivers);
        await camera.ConnectAsync();
        try
        {
            var c = camera.Capabilities.Value!;
            var plan = AcquisitionResolver.Resolve(
                new AcquisitionIntent { BinX = 2, BinY = 2, Region = AcquisitionRegion.Full }, null, TimeSpan.FromSeconds(0.3), camera.Capabilities, camera.Settings);
            Assert.True(plan.IsValid, string.Join(" ", plan.Problems));

            var frame = await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(0.3), plan.FrameType, plan.Change));

            Assert.Equal((c.SensorWidth / 2, c.SensorHeight / 2), (frame.Width, frame.Height));
            Assert.Equal((2, 2), (frame.Acquisition!.BinX, frame.Acquisition.BinY));
            Assert.Equal(FrameType.Light, frame.Acquisition.FrameType);

            var back = AcquisitionResolver.Resolve(
                new AcquisitionIntent { BinX = 1, BinY = 1, Region = AcquisitionRegion.Full }, null, TimeSpan.FromSeconds(0.3), camera.Capabilities, camera.Settings);
            await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(0.3), back.FrameType, back.Change));
            Assert.Equal((c.SensorWidth, c.SensorHeight), (camera.Settings!.NumX, camera.Settings.NumY));
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheTelescopeSimulator_ReportsItsCapabilities_AndItsTelemetry()
    {
        var mount = new AscomMount(new DeviceId("mount.sim"), "Telescope Simulator", MountProgId, Drivers);
        await mount.ConnectAsync();
        try
        {
            var c = mount.Capabilities.Value!;
            Describe(c);
            Assert.True(c.CanSlew);
            Assert.NotEmpty(c.TrackingRates);
            var t = mount.Telemetry!;
            Assert.NotNull(t.Coordinates);
            Assert.NotNull(t.SideOfPier);
            if (c.HasSite)
            {
                Assert.NotNull(mount.Site);
            }

            await mount.RefreshAsync();
            Assert.NotNull(mount.Telemetry);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheTelescopeSimulator_SwitchesTrackingAndItsRate_ThenParksAndUnparks()
    {
        var mount = new AscomMount(new DeviceId("mount.sim"), "Telescope Simulator", MountProgId, Drivers);
        await mount.ConnectAsync();
        try
        {
            var c = mount.Capabilities.Value!;
            if (mount.Telemetry!.AtPark && c.CanUnpark)
            {
                await mount.UnparkAsync();
            }

            if (c.CanSetTracking)
            {
                await mount.SetTrackingAsync(true);
                Assert.True(mount.Telemetry!.Tracking);
                if (c.TrackingRates.Contains(TrackingRate.Lunar))
                {
                    await mount.SetTrackingRateAsync(TrackingRate.Lunar);
                    Assert.Equal(TrackingRate.Lunar, mount.Telemetry!.Rate);
                    await mount.SetTrackingRateAsync(TrackingRate.Sidereal);
                }
            }

            if (c is { CanPark: true, CanUnpark: true })
            {
                await mount.ParkAsync();
                Assert.True(mount.Telemetry!.AtPark);
                await Assert.ThrowsAsync<AscomDeviceException>(() => mount.SlewToAsync(new CelestialCoordinates(1, 1)));
                await mount.UnparkAsync();
                Assert.False(mount.Telemetry!.AtPark);
            }
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheFocuserSimulator_ReportsItsCapabilities_AndMovesBySteps()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.sim"), "Focuser Simulator", FocuserProgId, Drivers);
        await focuser.ConnectAsync();
        try
        {
            var c = focuser.Capabilities.Value!;
            Describe(c);
            Assert.True(c.Absolute);
            Assert.True(c.MaxStep > 0);
            var before = focuser.Position;
            var step = Math.Min(200, c.MaxIncrement ?? 200);
            await focuser.MoveByAsync(step);
            await focuser.RefreshAsync();
            Assert.Equal(before + step, focuser.Telemetry!.Position);
            await focuser.MoveToAsync(before);
            Assert.Equal(before, focuser.Position);
        }
        finally
        {
            await focuser.DisconnectAsync();
        }
    }

    // ---- Real hardware: reading only (the camera changes binning and region and puts them back)

    [HardwareFact("SIDERA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_ReportsItsCapabilities()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", Env("SIDERA_ASCOM_FOCUSER"), Drivers);
        await focuser.ConnectAsync();
        try
        {
            var c = focuser.Capabilities.Value!;
            Describe(c);
            Assert.True(c.Absolute);
            output.WriteLine($"Telemetry: position {focuser.Telemetry!.Position}, temperature {focuser.Telemetry.Temperature}, tempComp {focuser.Telemetry.TempComp}");
        }
        finally
        {
            await focuser.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_MOUNT")]
    public async Task TheRealMount_ReportsItsCapabilities_WithoutMoving()
    {
        var mount = new AscomMount(new DeviceId("mount.real"), "Real Mount", Env("SIDERA_ASCOM_MOUNT"), Drivers);
        await mount.ConnectAsync();
        try
        {
            var c = mount.Capabilities.Value!;
            Describe(c);
            var t = mount.Telemetry!;
            output.WriteLine(
                $"Telemetry: {t.Coordinates}, tracking {t.Tracking} {t.Rate}, parked {t.AtPark}, home {t.AtHome}, pier {t.SideOfPier}, " +
                $"lst {t.SiderealTimeHours}, utc {t.UtcDate}, horizontal {t.Horizontal?.AltitudeDegrees}/{t.Horizontal?.AzimuthDegrees}, site {mount.Site}");
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ExposesWithResolvedSettings_AndAnInvalidOneStartsNothing()
    {
        var camera = new AscomCamera(new DeviceId("camera.real"), "Real Camera", Env("SIDERA_ASCOM_CAMERA"), Drivers);
        await camera.ConnectAsync();
        try
        {
            var original = camera.Settings!;
            var invalid = AcquisitionResolver.Resolve(
                new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(100000) }, null, TimeSpan.FromSeconds(0.1), camera.Capabilities, camera.Settings);
            Assert.Equal(AcquisitionStatus.Invalid, invalid.Status);
            output.WriteLine(string.Join(" ", invalid.Problems));

            var plan = AcquisitionResolver.Resolve(
                new AcquisitionIntent { BinX = 2, BinY = 2 }, null, TimeSpan.FromSeconds(0.1), camera.Capabilities, camera.Settings);
            var frame = await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(0.1), plan.FrameType, plan.Change));
            output.WriteLine($"Frame {frame.Width}x{frame.Height}, acquisition {frame.Acquisition}");
            Assert.Equal((2, 2), (frame.Acquisition!.BinX, frame.Acquisition.BinY));

            var back = AcquisitionResolver.Resolve(
                new AcquisitionIntent { BinX = 1, BinY = 1, Region = AcquisitionRegion.Full }, null, TimeSpan.FromSeconds(0.1), camera.Capabilities, camera.Settings);
            await camera.ApplyAsync(back.Change);
            Assert.Equal(original.BinX, camera.Settings!.BinX);
            Assert.Equal(original.NumX, camera.Settings.NumX);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("SIDERA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ReportsItsCapabilities_AndAppliesBinningAndARegion_AndPutsThemBack()
    {
        var camera = new AscomCamera(new DeviceId("camera.real"), "Real Camera", Env("SIDERA_ASCOM_CAMERA"), Drivers);
        await camera.ConnectAsync();
        try
        {
            var c = camera.Capabilities.Value!;
            Describe(c);
            var original = camera.Settings!;
            output.WriteLine($"Settings: {original}");
            output.WriteLine($"Telemetry: {camera.Telemetry}");

            if (c.MaxBinX >= 2)
            {
                await camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });
                var binned = await camera.ExposeAsync(TimeSpan.FromSeconds(0.1));
                output.WriteLine($"Binned frame: {binned.Width}x{binned.Height}");
                Assert.Equal((c.SensorWidth / 2, c.SensorHeight / 2), (binned.Width, binned.Height));
            }

            await camera.ApplyAsync(new CameraSettings { BinX = 1, BinY = 1, StartX = 0, StartY = 0, NumX = c.SensorWidth, NumY = c.SensorHeight });
            await camera.ApplyAsync(new CameraSettings { StartX = 100, StartY = 100, NumX = 400, NumY = 300 });
            var region = await camera.ExposeAsync(TimeSpan.FromSeconds(0.1));
            Assert.Equal((400, 300), (region.Width, region.Height));
            await camera.ApplyAsync(new CameraSettings { StartX = 0, StartY = 0, NumX = c.SensorWidth, NumY = c.SensorHeight });
            Assert.Equal(c.SensorWidth, camera.Settings!.NumX);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }
}

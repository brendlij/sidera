using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Core.Mounts;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Tests.Devices;

/// <summary>
/// The simulated devices speak the same capability model as every other backend: unknown until connected, honest about what
/// they simulate, and the same rules for settings.
/// </summary>
public class SimulatedCapabilityTests
{
    // ---- Camera

    private static async Task<SimulatedCamera> ConnectedCamera()
    {
        var camera = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        await camera.ConnectAsync();
        return camera;
    }

    [Fact]
    public async Task ACamera_HasUnknownCapabilitiesUntilConnected_AndAgainAfterwards()
    {
        var camera = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var changes = 0;
        camera.CapabilitiesChanged += (_, _) => changes++;
        Assert.Equal(CapabilityStatus.Unknown, camera.Capabilities.Status);
        Assert.Null(camera.Settings);

        await camera.ConnectAsync();
        Assert.True(camera.Capabilities.IsAvailable);
        var c = camera.Capabilities.Value!;
        Assert.Equal((800, 600), (c.SensorWidth, c.SensorHeight));
        Assert.NotNull(c.Gain);
        Assert.True(c.SupportsBinning);
        Assert.True(c.SupportsCooling);

        await camera.DisconnectAsync();
        Assert.Equal(CapabilityStatus.Unknown, camera.Capabilities.Status);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Binning_ChangesTheFrameSize_AndTheSubframeIsReset()
    {
        var camera = await ConnectedCamera();
        await camera.ApplyAsync(new CameraSettings { StartX = 10, StartY = 10, NumX = 100, NumY = 100 });

        await camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });
        var frame = await camera.ExposeAsync(TimeSpan.FromMilliseconds(10));

        Assert.Equal((400, 300), (frame.Width, frame.Height));
        Assert.Equal((0, 0, 400, 300), (camera.Settings!.StartX, camera.Settings.StartY, camera.Settings.NumX, camera.Settings.NumY));
    }

    [Fact]
    public async Task ASubframe_ChangesTheFrameSize()
    {
        var camera = await ConnectedCamera();
        await camera.ApplyAsync(new CameraSettings { StartX = 100, StartY = 50, NumX = 200, NumY = 120 });

        var frame = await camera.ExposeAsync(TimeSpan.FromMilliseconds(10));

        Assert.Equal((200, 120), (frame.Width, frame.Height));
    }

    [Fact]
    public async Task SettingsThatTheCameraDoesNotAccept_AreRefused_AndNothingChanges()
    {
        var camera = await ConnectedCamera();

        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { Gain = 101 }));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { BinX = 1, BinY = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { BinX = 5, BinY = 5 }));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { StartX = 700, NumX = 200, StartY = 0, NumY = 10 }));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { FastReadout = true }));

        Assert.Equal(0, camera.Settings!.Gain);
        Assert.Equal(1, camera.Settings.BinX);
    }

    [Fact]
    public async Task Cooling_MovesTheTemperatureTowardsTheTarget_AndTheCoolerIsOffAfterAConnect()
    {
        var camera = await ConnectedCamera();
        Assert.False(camera.Settings!.CoolerOn);
        Assert.Equal(SimulatedCamera.AmbientTemperature, camera.Telemetry!.CcdTemperature!.Value, 1);

        await camera.ApplyAsync(new CameraSettings { TargetTemperature = -10, CoolerOn = true });
        await Task.Delay(300);
        var cooled = camera.Telemetry!;

        Assert.True(cooled.CcdTemperature < SimulatedCamera.AmbientTemperature);
        Assert.True(cooled.CoolerPower > 0);
        Assert.True(cooled.CoolerOn);
    }

    [Fact]
    public async Task SettingsCannotChangeWhileExposing()
    {
        var camera = await ConnectedCamera();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(50);

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.ApplyAsync(new CameraSettings { Gain = 5 }));

        await exposure;
    }

    // ---- Focuser

    private static async Task<SimulatedFocuser> ConnectedFocuser()
    {
        var focuser = new SimulatedFocuser(new DeviceId("focuser.sim"), stepsPerSecond: 100000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        await focuser.ConnectAsync();
        return focuser;
    }

    [Fact]
    public async Task AFocuser_ReportsItsCapabilities_AndMovesBySteps()
    {
        var focuser = new SimulatedFocuser(new DeviceId("focuser.sim"), stepsPerSecond: 100000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        Assert.Equal(CapabilityStatus.Unknown, focuser.Capabilities.Status);
        Assert.Null(focuser.Telemetry);
        await focuser.ConnectAsync();
        var c = focuser.Capabilities.Value!;
        Assert.True(c.Absolute);
        Assert.Equal(SimulatedFocuser.DefaultMaxPosition, c.MaxStep);
        Assert.True(c.HasTemperature);
        Assert.True(c.TempCompAvailable);
        Assert.True(((IFocuser)focuser).IsAbsolute);

        await focuser.MoveByAsync(500);

        Assert.Equal(SimulatedFocuser.DefaultStartPosition + 500, focuser.Position);
        Assert.Equal(focuser.Position, focuser.Telemetry!.Position);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => focuser.MoveByAsync(1_000_000));
    }

    [Fact]
    public async Task TemperatureCompensation_IsRemembered()
    {
        var focuser = await ConnectedFocuser();

        await focuser.SetTempCompAsync(true);

        Assert.True(focuser.Telemetry!.TempComp);
    }

    // ---- Mount

    private static async Task<SimulatedMount> ConnectedMount()
    {
        var mount = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20));
        await mount.ConnectAsync();
        return mount;
    }

    [Fact]
    public async Task AMount_ReportsItsCapabilities_AndTelemetryOnlyWhenConnected()
    {
        var mount = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20));
        Assert.Equal(CapabilityStatus.Unknown, mount.Capabilities.Status);
        Assert.Null(mount.Telemetry);

        await mount.ConnectAsync();
        var c = mount.Capabilities.Value!;

        Assert.True(c.CanPark);
        Assert.True(c.CanSlewAltAz);
        Assert.False(c.CanMovePrimaryAxis);
        Assert.Equal(4, c.TrackingRates.Count);
        Assert.NotNull(mount.Telemetry!.Horizontal);
        Assert.NotNull(mount.Site);
    }

    [Fact]
    public async Task ParkingAMount_StopsTracking_AndAParkedMountRefusesToMove()
    {
        var mount = await ConnectedMount();
        await mount.SlewToAsync(new CelestialCoordinates(5, 20));
        Assert.Equal(MountMotionState.Tracking, mount.MotionState);

        await mount.ParkAsync();

        Assert.True(mount.Telemetry!.AtPark);
        Assert.Equal(MountMotionState.Idle, mount.MotionState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewToAsync(new CelestialCoordinates(1, 1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SetTrackingAsync(true));
        await mount.UnparkAsync();
        await mount.SetTrackingAsync(true);
        Assert.Equal(MountMotionState.Tracking, mount.MotionState);
    }

    [Fact]
    public async Task AnAltAzSlew_ArrivesWhereTheHorizontalPositionSaysItDoes()
    {
        var mount = await ConnectedMount();

        await mount.SlewToAltAzAsync(new HorizontalCoordinates(50, 200));

        var h = mount.Telemetry!.Horizontal!;
        Assert.Equal(50, h.AltitudeDegrees, 1);
        Assert.Equal(200, h.AzimuthDegrees, 1);
    }

    [Fact]
    public async Task Sync_ChangesWhatTheMountBelieves_AndHomeIsTheOrigin()
    {
        var mount = await ConnectedMount();

        await mount.SyncAsync(new CelestialCoordinates(7, 30));
        Assert.Equal(7, mount.Coordinates.RightAscensionHours);

        await mount.FindHomeAsync();
        Assert.True(mount.Telemetry!.AtHome);
    }

    [Fact]
    public async Task PulseGuide_MovesTheMountABit_AndGuideRatesMustBePositive()
    {
        var mount = await ConnectedMount();
        await mount.SyncAsync(new CelestialCoordinates(5, 10));

        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromMilliseconds(100));

        Assert.True(mount.Coordinates.DeclinationDegrees > 10);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mount.SetGuideRatesAsync(new GuideRates(0, 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mount.PulseGuideAsync(GuideDirection.North, TimeSpan.Zero));
        await Assert.ThrowsAsync<NotSupportedException>(() => mount.MoveAxisAsync(MountAxis.Primary, 0.5));
    }

    [Fact]
    public async Task PierSideFollowsTheHourAngle()
    {
        var mount = await ConnectedMount();
        var lst = mount.Telemetry!.SiderealTimeHours!.Value;

        var west = await mount.PredictPierSideAsync(new CelestialCoordinates((lst - 3 + 24) % 24, 20));
        var east = await mount.PredictPierSideAsync(new CelestialCoordinates((lst + 3) % 24, 20));

        Assert.Equal(PierSide.East, west);
        Assert.Equal(PierSide.West, east);
    }
}

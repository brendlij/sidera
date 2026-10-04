using Astra.Ascom.Cameras;
using Astra.Ascom.Focusers;
using Astra.Ascom.Infrastructure;
using Astra.Ascom.Mounts;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Core.Mounts;

namespace Astra.Ascom.Tests;

/// <summary>
/// Capabilities, settings and telemetry of the ASCOM adapters against fake drivers: probed after the connection, unknown
/// before and after, never from names, nothing changed to find them out.
/// </summary>
public class CapabilityTests
{
    private static AscomFocuser NewFocuser(Action<FakeFocuserDriver>? configure, out FakeDriverFactory drivers)
    {
        drivers = new FakeDriverFactory(new CallLog()) { ConfigureFocuser = configure };
        return new AscomFocuser(new DeviceId("focuser.cap"), "Cap Focuser", "ASCOM.Test.Focuser", drivers, null, null, FastTimings.Create());
    }

    private static AscomMount NewMount(Action<FakeMountDriver>? configure, out FakeDriverFactory drivers)
    {
        drivers = new FakeDriverFactory(new CallLog()) { ConfigureMount = configure };
        return new AscomMount(new DeviceId("mount.cap"), "Cap Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
    }

    private static AscomCamera NewCamera(Action<FakeCameraDriver>? configure, out FakeDriverFactory drivers)
    {
        drivers = new FakeDriverFactory(new CallLog()) { ConfigureCamera = configure };
        return new AscomCamera(new DeviceId("camera.cap"), "Cap Camera", "ASCOM.Test.Camera", drivers, null, null, FastTimings.Create());
    }

    // ---- Focuser

    [Fact]
    public async Task AFocuser_HasUnknownCapabilitiesUntilConnected_ThenProbedOnes_ThenUnknownAgain()
    {
        var focuser = NewFocuser(d => { d.TemperatureValue = 12.5; d.TempCompAvailableValue = true; }, out _);
        var changes = 0;
        focuser.CapabilitiesChanged += (_, _) => changes++;
        Assert.Equal(CapabilityStatus.Unknown, focuser.Capabilities.Status);
        Assert.Null(focuser.Telemetry);

        await focuser.ConnectAsync();

        var c = focuser.Capabilities.Value!;
        Assert.True(c.Absolute);
        Assert.Equal(50000, c.MaxStep);
        Assert.Equal(1.5, c.StepSizeMicrons);
        Assert.True(c.HasTemperature);
        Assert.True(c.TempCompAvailable);
        Assert.Null(c.CanHalt);
        Assert.Equal("Fake driver", c.Driver.Name);
        Assert.Equal(3, c.Driver.InterfaceVersion);
        Assert.Equal(12.5, focuser.Telemetry!.Temperature);

        await focuser.DisconnectAsync();

        Assert.Equal(CapabilityStatus.Unknown, focuser.Capabilities.Status);
        Assert.Null(focuser.Telemetry);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task AFocuserWithoutTemperature_ReportsNoTemperatureCapability()
    {
        var focuser = NewFocuser(null, out _);
        await focuser.ConnectAsync();

        Assert.False(focuser.Capabilities.Value!.HasTemperature);
        Assert.Null(focuser.Telemetry!.Temperature);
        Assert.Contains(focuser.Capabilities.Value.Notes, n => n.StartsWith("Temperature", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARelativeFocuser_MovesBySteps_WithoutAPosition_AndRespectsMaxIncrement()
    {
        var focuser = NewFocuser(d => { d.Absolute = false; d.MaxIncrementValue = 500; }, out var drivers);
        await focuser.ConnectAsync();

        await focuser.MoveByAsync(-300);

        Assert.Contains("Move -300", drivers.Log.Names);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => focuser.MoveByAsync(501));
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Null(focuser.Capabilities.Value!.MaxStep);
    }

    [Fact]
    public async Task AnAbsoluteFocuser_MoveBy_IsAMoveToThePositionPlusTheSteps_AndStaysInRange()
    {
        var focuser = NewFocuser(null, out var drivers);
        await focuser.ConnectAsync();

        await focuser.MoveByAsync(1000);

        Assert.Contains("Move 26000", drivers.Log.Names);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => focuser.MoveByAsync(100000));
    }

    [Fact]
    public async Task TemperatureCompensation_IsOnlyOfferedWhenTheDriverHasIt()
    {
        var without = NewFocuser(null, out _);
        await without.ConnectAsync();
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => without.SetTempCompAsync(true));

        var with = NewFocuser(d => d.TempCompAvailableValue = true, out var drivers);
        await with.ConnectAsync();
        await with.SetTempCompAsync(true);
        Assert.True(drivers.Focusers[0].TempCompValue);
        Assert.True(with.Telemetry!.TempComp);
    }

    [Fact]
    public async Task AHaltThatWorked_IsRememberedInTheCapabilities()
    {
        var focuser = NewFocuser(d => d.HoldMove = true, out _);
        await focuser.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var move = focuser.MoveToAsync(40000, cts.Token);
        await Task.Delay(50);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);

        Assert.True(focuser.Capabilities.Value!.CanHalt);
    }

    [Fact]
    public async Task ARefresh_ReadsTheStateAgain_AndRaisesStateChanged()
    {
        var focuser = NewFocuser(d => d.TemperatureValue = 10, out var drivers);
        await focuser.ConnectAsync();
        var raised = 0;
        focuser.StateChanged += (_, _) => raised++;
        drivers.Focusers[0].TemperatureValue = 7.25;

        await focuser.RefreshAsync();

        Assert.Equal(7.25, focuser.Telemetry!.Temperature);
        Assert.Equal(1, raised);
    }

    // ---- Mount

    [Fact]
    public async Task AMount_ProbesOnlyWhatItOffers_AndNeverInventsCapabilities()
    {
        var mount = NewMount(d => { d.CanPark = true; d.CanPulseGuide = true; d.MovableAxes.Add(MountAxis.Primary); d.HasAltAzValue = false; }, out _);
        Assert.Equal(CapabilityStatus.Unknown, mount.Capabilities.Status);

        await mount.ConnectAsync();

        var c = mount.Capabilities.Value!;
        Assert.True(c.CanSlew);
        Assert.True(c.CanPark);
        Assert.True(c.CanPulseGuide);
        Assert.False(c.CanSync);
        Assert.False(c.CanSlewAltAz);
        Assert.False(c.CanFindHome);
        Assert.False(c.HasAltAz);
        Assert.True(c.CanMove(MountAxis.Primary));
        Assert.False(c.CanMove(MountAxis.Secondary));
        Assert.Single(c.AxisRates[MountAxis.Primary]);
        Assert.Equal([TrackingRate.Sidereal, TrackingRate.Lunar], c.TrackingRates);
        Assert.Null(mount.Telemetry!.Horizontal);
        Assert.Null(mount.Telemetry.AtHome);
        Assert.True(mount.Site is { LatitudeDegrees: 50.1 });
        Assert.False(c.CanPredictPierSide);
    }

    [Fact]
    public async Task AMount_ThatIsDisconnected_HasUnknownCapabilities_AndNoTelemetry()
    {
        var mount = NewMount(null, out _);
        await mount.ConnectAsync();
        await mount.DisconnectAsync();

        Assert.Equal(CapabilityStatus.Unknown, mount.Capabilities.Status);
        Assert.Null(mount.Telemetry);
        Assert.Null(mount.Site);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.RefreshAsync());
    }

    [Fact]
    public async Task OperationsTheMountCannotDo_AreRefusedBeforeTheDriverIsTouched()
    {
        var mount = NewMount(null, out var drivers);
        await mount.ConnectAsync();

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.ParkAsync());
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.SyncAsync(new CelestialCoordinates(1, 1)));
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.FindHomeAsync());
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.MoveAxisAsync(MountAxis.Primary, 0.5));
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.SlewToAltAzAsync(new HorizontalCoordinates(30, 100)));
        Assert.Empty(drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task ParkingAMount_PollsUntilItStopped_AndReportsParked()
    {
        var mount = NewMount(d => d.CanPark = true, out var drivers);
        await mount.ConnectAsync();

        await mount.ParkAsync();

        Assert.Contains("Park", drivers.Mounts[0].Operations);
        Assert.True(mount.Telemetry!.AtPark);
    }

    [Fact]
    public async Task AParkedMount_IsNotMovedByPulseGuideMoveAxisSyncOrAltAzSlew()
    {
        var mount = NewMount(
            d =>
            {
                d.AtParkValue = true;
                d.CanPulseGuide = true;
                d.CanSync = true;
                d.CanSlewAltAzAsync = true;
                d.MovableAxes.Add(MountAxis.Primary);
            },
            out var drivers);
        await mount.ConnectAsync();

        await Assert.ThrowsAsync<AscomDeviceException>(() => mount.PulseGuideAsync(GuideDirection.East, TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<AscomDeviceException>(() => mount.MoveAxisAsync(MountAxis.Primary, 0.5));
        await Assert.ThrowsAsync<AscomDeviceException>(() => mount.SyncAsync(new CelestialCoordinates(1, 1)));
        await Assert.ThrowsAsync<AscomDeviceException>(() => mount.SlewToAltAzAsync(new HorizontalCoordinates(30, 100)));
        Assert.Empty(drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task MoveAxis_ChecksTheRateAgainstTheAxisRates_AndZeroStops()
    {
        var mount = NewMount(d => d.MovableAxes.Add(MountAxis.Primary), out var drivers);
        await mount.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mount.MoveAxisAsync(MountAxis.Primary, 5));
        await mount.MoveAxisAsync(MountAxis.Primary, -1.0);
        await mount.MoveAxisAsync(MountAxis.Primary, 0);

        Assert.Equal(["MoveAxis Primary -1", "MoveAxis Primary 0"], drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task PulseGuide_ChecksTheDuration_AndWaitsForTheGuidingToEnd()
    {
        var mount = NewMount(d => d.CanPulseGuide = true, out var drivers);
        await mount.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mount.PulseGuideAsync(GuideDirection.North, TimeSpan.Zero));
        await mount.PulseGuideAsync(GuideDirection.North, TimeSpan.FromMilliseconds(250));

        Assert.Equal(["PulseGuide North 250"], drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task Tracking_IsReadBack_AndADriverThatDoesNotReachTheStateIsReportedAsFailed()
    {
        var mount = NewMount(d => (d.TrackingValue, d.IgnoresTrackingChange) = (false, true), out var drivers);
        await mount.ConnectAsync();

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => mount.SetTrackingAsync(true));

        Assert.Contains("still reports tracking off", failure.Message);
        Assert.Contains("Tracking = True", drivers.Mounts[0].Log.Calls.Select(c => c.Name));
        Assert.False(mount.Telemetry!.Tracking);
        Assert.NotEqual(MountMotionState.Tracking, mount.MotionState);
    }

    [Fact]
    public async Task Tracking_ThatTheDriverReaches_IsConfirmedByReadingItBack()
    {
        var mount = NewMount(d => d.TrackingValue = false, out var drivers);
        await mount.ConnectAsync();

        await mount.SetTrackingAsync(true);

        Assert.True(drivers.Mounts[0].TrackingValue);
        Assert.True(mount.Telemetry!.Tracking);
        Assert.Equal(MountMotionState.Tracking, mount.MotionState);
    }

    [Fact]
    public async Task AnAxisThatIsStillMovingWhenTheMountIsDisconnected_IsStoppedFirst()
    {
        var mount = NewMount(d => d.MovableAxes.Add(MountAxis.Primary), out var drivers);
        await mount.ConnectAsync();
        await mount.MoveAxisAsync(MountAxis.Primary, 1);

        await mount.DisconnectAsync();

        Assert.Equal(["MoveAxis Primary 1", "MoveAxis Primary 0"], drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task AnAxisIsNotStoppedAtTheDisconnect_WhenItWasAlreadyStopped()
    {
        var mount = NewMount(d => d.MovableAxes.Add(MountAxis.Primary), out var drivers);
        await mount.ConnectAsync();
        await mount.MoveAxisAsync(MountAxis.Primary, 1);
        await mount.MoveAxisAsync(MountAxis.Primary, 0);

        await mount.DisconnectAsync();

        Assert.Equal(["MoveAxis Primary 1", "MoveAxis Primary 0"], drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task AMoveAxisThatFails_StopsTheAxisAgain()
    {
        var mount = NewMount(d => d.MovableAxes.Add(MountAxis.Primary), out var drivers);
        await mount.ConnectAsync();
        drivers.Mounts[0].MoveAxisThrows = new InvalidOperationException("the driver failed");

        await Assert.ThrowsAnyAsync<Exception>(() => mount.MoveAxisAsync(MountAxis.Primary, 1));

        Assert.Equal(["MoveAxis Primary 1", "MoveAxis Primary 0"], drivers.Mounts[0].Operations);
    }

    [Fact]
    public async Task TrackingAndRates_AreOnlySetWhenOffered()
    {
        var mount = NewMount(null, out var drivers);
        await mount.ConnectAsync();

        await mount.SetTrackingAsync(false);
        await mount.SetTrackingRateAsync(TrackingRate.Lunar);
        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.SetTrackingRateAsync(TrackingRate.King));

        Assert.False(drivers.Mounts[0].TrackingValue);
        Assert.Equal(TrackingRate.Lunar, mount.Telemetry!.Rate);
        Assert.Equal(MountMotionState.Idle, mount.MotionState);
    }

    [Fact]
    public async Task AnAltAzSlew_UsesTheAsyncSlew_AndEndsWhereTheMountSays()
    {
        var mount = NewMount(d => d.CanSlewAltAzAsync = true, out var drivers);
        await mount.ConnectAsync();

        await mount.SlewToAltAzAsync(new HorizontalCoordinates(45, 180));

        Assert.Equal(["SlewToAltAzAsync"], drivers.Mounts[0].Operations);
        Assert.NotEqual(MountMotionState.Slewing, mount.MotionState);
    }

    [Fact]
    public async Task GuideRates_RefractionAndPierSide_FollowTheirCapabilities()
    {
        var mount = NewMount(d => { d.CanSetGuideRates = true; d.GuideRatesValue = (0.002, 0.002); d.RefractionValue = false; d.PredictsPierSide = true; }, out _);
        await mount.ConnectAsync();

        Assert.True(mount.Capabilities.Value!.CanPredictPierSide);
        Assert.Equal(PierSide.East, await mount.PredictPierSideAsync(new CelestialCoordinates(5, 5)));
        await mount.SetGuideRatesAsync(new GuideRates(0.004, 0.003));
        await mount.SetRefractionAsync(true);

        Assert.Equal(0.004, mount.Telemetry!.GuideRates!.RightAscensionDegreesPerSecond);
        Assert.True(mount.Telemetry.DoesRefraction);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => mount.SetGuideRatesAsync(new GuideRates(-1, 1)));
    }

    [Fact]
    public async Task AMountThatClaimsToSlewAtConnect_CanStillBeDisconnected_AndIsNeverSlewedMeanwhile()
    {
        var mount = NewMount(d => d.AlwaysReportsSlewing = true, out _);
        await mount.ConnectAsync();

        Assert.Equal(MountMotionState.Slewing, mount.MotionState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewToAsync(new CelestialCoordinates(1, 1)));
        await mount.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, mount.ConnectionState);
    }

    [Fact]
    public async Task ASiderealTimeOutsideTheDay_IsNotReported()
    {
        var mount = NewMount(d => d.SiderealTimeValue = -1, out _);
        await mount.ConnectAsync();

        Assert.Null(mount.Telemetry!.SiderealTimeHours);
    }

    [Fact]
    public async Task Stop_AbortsTheSlew_SetsEveryMovableAxisToZero_AndLeavesTrackingAlone()
    {
        var mount = NewMount(d => { d.MovableAxes.Add(MountAxis.Primary); d.MovableAxes.Add(MountAxis.Secondary); d.HoldSlew = true; }, out var drivers);
        await mount.ConnectAsync();
        var slew = mount.SlewToAsync(new CelestialCoordinates(8, 20));
        await Task.Delay(50);

        await mount.StopAsync();

        var operations = drivers.Mounts[0].Operations;
        Assert.Contains("AbortSlew", operations);
        Assert.Contains("MoveAxis Primary 0", operations);
        Assert.Contains("MoveAxis Secondary 0", operations);
        Assert.True(drivers.Mounts[0].TrackingValue);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
        Assert.NotEqual(MountMotionState.Slewing, mount.MotionState);
    }

    [Fact]
    public async Task Stop_IsAlwaysAllowed_EvenWhenParkedOrIdle_AndSaysWhenTheMountStillMoves()
    {
        var mount = NewMount(d => { d.AtParkValue = true; d.AbortThrows = new InvalidOperationException("parked"); }, out _);
        await mount.ConnectAsync();
        await mount.StopAsync(); // refused by the driver, but nothing moves: fine

        var moving = NewMount(d => { d.HoldSlew = true; d.AbortStops = false; }, out var drivers);
        await moving.ConnectAsync();
        var slew = moving.SlewToAsync(new CelestialCoordinates(8, 20));
        await Task.Delay(50);

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => moving.StopAsync());

        Assert.Contains("could not be confirmed stopped", failure.Message);
        drivers.Mounts[0].Arrive();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slew);
    }

    // ---- Camera

    [Fact]
    public async Task ACamera_ProbesGainOffsetBinningCoolingAndReadout_FromWhatTheDriverOffers()
    {
        var camera = NewCamera(
            d =>
            {
                d.SensorType = SensorKind.Rggb;
                d.MaxBinX = d.MaxBinY = 4;
                d.GainValue = 100; d.GainMinValue = 0; d.GainMaxValue = 500;
                d.OffsetValue = 10; d.OffsetsValue = ["Low", "High"];
                d.ReadoutModesValue = ["Normal", "Fast"];
                d.CanFastReadout = true;
                d.CanSetCcdTemperature = true; d.CoolerOnValue = false;
                d.CcdTemperatureValue = 15; d.CanGetCoolerPower = true; d.CoolerPowerValue = 0;
                d.CanAbort = true;
            },
            out _);
        Assert.Equal(CapabilityStatus.Unknown, camera.Capabilities.Status);

        await camera.ConnectAsync();

        var c = camera.Capabilities.Value!;
        Assert.Equal(SensorKind.Rggb, c.SensorType);
        Assert.Equal((0, 0), c.BayerOffset);
        Assert.Equal(IntegerControl.Range(0, 500), c.Gain);
        Assert.True(c.Offset!.IsList);
        Assert.Equal(["Low", "High"], c.Offset.Choices);
        Assert.Equal(["Normal", "Fast"], c.ReadoutModes);
        Assert.True(c.SupportsBinning);
        Assert.True(c.SupportsCooling);
        Assert.True(c.HasCooler);
        Assert.True(c.HasCcdTemperature);
        Assert.False(c.HasHeatSinkTemperature);
        Assert.True(c.CanGetCoolerPower);
        Assert.True(c.SupportsSubframe);
        Assert.Equal(3.76, c.PixelSizeXMicrons);
        Assert.Equal(15, camera.Telemetry!.CcdTemperature);
        Assert.Equal(100, camera.Settings!.Gain);
    }

    [Fact]
    public async Task ASimpleCamera_HasNoGainOffsetCoolingOrBinning()
    {
        var camera = NewCamera(null, out _);
        await camera.ConnectAsync();

        var c = camera.Capabilities.Value!;
        Assert.Null(c.Gain);
        Assert.Null(c.Offset);
        Assert.False(c.SupportsBinning);
        Assert.False(c.SupportsCooling);
        Assert.False(c.CanFastReadout);
        Assert.Empty(c.ReadoutModes);
        Assert.Null(c.BayerOffset);
        Assert.Null(camera.Settings!.Gain);
        Assert.Null(camera.Telemetry!.CcdTemperature);
    }

    [Fact]
    public async Task ApplyingSettings_ValidatesFirst_AndChangesNothingWhenAnyPartIsRefused()
    {
        var camera = NewCamera(d => { d.GainValue = 100; d.GainMinValue = 0; d.GainMaxValue = 500; }, out var drivers);
        await camera.ConnectAsync();

        var failure = await Assert.ThrowsAsync<ArgumentException>(
            () => camera.ApplyAsync(new CameraSettings { Gain = 200, Offset = 5 }));

        Assert.Contains("no offset", failure.Message);
        Assert.Empty(drivers.Cameras[0].Changes);
        Assert.Equal(100, camera.Settings!.Gain);

        await camera.ApplyAsync(new CameraSettings { Gain = 250 });
        Assert.Equal(250, camera.Settings!.Gain);
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { Gain = 501 }));
    }

    [Fact]
    public async Task Binning_ResetsTheSubframeToTheWholeSensorAtThatBinning()
    {
        var camera = NewCamera(
            d => { d.CameraXSize = 400; d.CameraYSize = 200; d.NumX = 400; d.NumY = 200; d.MaxBinX = d.MaxBinY = 4; d.CanAsymmetricBin = false; },
            out var drivers);
        await camera.ConnectAsync();

        await camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });

        var driver = drivers.Cameras[0];
        Assert.Equal((2, 2, 0, 0, 200, 100), (driver.BinX, driver.BinY, driver.StartX, driver.StartY, driver.NumX, driver.NumY));
        Assert.Equal((200, 100), (camera.Settings!.NumX, camera.Settings.NumY));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { BinX = 1, BinY = 2 }));
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { BinX = 5, BinY = 5 }));
    }

    [Fact]
    public async Task ASubframe_MustLieInsideTheSensor_AndBecomesTheFrameOfTheNextExposure()
    {
        var camera = NewCamera(d => { d.CameraXSize = 4; d.CameraYSize = 3; }, out var drivers);
        await camera.ConnectAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { StartX = 3, NumX = 2, StartY = 0, NumY = 1 }));
        await camera.ApplyAsync(new CameraSettings { StartX = 1, StartY = 1, NumX = 2, NumY = 2 });

        Assert.Equal((1, 1, 2, 2), (drivers.Cameras[0].StartX, drivers.Cameras[0].StartY, drivers.Cameras[0].NumX, drivers.Cameras[0].NumY));
    }

    [Fact]
    public async Task CoolingSettings_AreAppliedOnlyWhenTheCameraCanCool_AndCoolerIsNeverSwitchedOnByAConnect()
    {
        var camera = NewCamera(d => { d.CanSetCcdTemperature = true; d.CoolerOnValue = false; d.CcdTemperatureValue = 20; }, out var drivers);
        await camera.ConnectAsync();

        Assert.False(drivers.Cameras[0].CoolerOnValue);
        await camera.ApplyAsync(new CameraSettings { TargetTemperature = -10, CoolerOn = true });

        Assert.Equal(-10, drivers.Cameras[0].SetCcdTemperatureValue);
        Assert.True(camera.Settings!.CoolerOn);
        await Assert.ThrowsAsync<ArgumentException>(() => camera.ApplyAsync(new CameraSettings { TargetTemperature = double.NaN }));
    }

    [Fact]
    public async Task CameraSettings_CannotChangeWhileExposing()
    {
        var camera = NewCamera(d => { d.GainValue = 1; d.GainMinValue = 0; d.GainMaxValue = 10; d.HoldExposure = true; }, out _);
        await camera.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(1), cts.Token);
        await Task.Delay(50);

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.ApplyAsync(new CameraSettings { Gain = 5 }));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
    }

    [Fact]
    public async Task AFreshConnect_ProbesAgain_SoACameraBehindTheSameDriverMayOfferOtherThings()
    {
        var camera = NewCamera(d => d.GainValue = null, out var drivers);
        await camera.ConnectAsync();
        Assert.Null(camera.Capabilities.Value!.Gain);
        await camera.DisconnectAsync();

        drivers.ConfigureCamera = d => { d.GainValue = 5; d.GainMinValue = 0; d.GainMaxValue = 9; };
        await camera.ConnectAsync();

        Assert.NotNull(camera.Capabilities.Value!.Gain);
    }
}

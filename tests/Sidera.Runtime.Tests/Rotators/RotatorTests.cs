using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Rotators;

public sealed class SimulatedRotatorTests
{
    private static async Task<SimulatedRotator> ConnectedAsync(double start = 0, double speed = 3600, double skyOffset = 0, bool reversed = false)
    {
        var rotator = new SimulatedRotator(new DeviceId("rotator.sim"), startPosition: start, degreesPerSecond: speed) { SkyOffsetDegrees = skyOffset, ReversedMounting = reversed };
        await rotator.ConnectAsync();
        return rotator;
    }

    [Fact]
    public async Task ItSaysWhatItCanDo_OnlyWhileConnected()
    {
        var rotator = new SimulatedRotator(new DeviceId("rotator.sim"));
        Assert.False(rotator.Capabilities.IsAvailable);
        Assert.Null(rotator.Telemetry);

        await rotator.ConnectAsync();

        var c = rotator.Capabilities.Value!;
        Assert.True(c.AbsoluteMove && c.RelativeMove && c.CanSync && c.CanReverse && c.HasMechanicalPosition);
        Assert.True(c.CanHalt);
        await rotator.DisconnectAsync();
        Assert.False(rotator.Capabilities.IsAvailable);
        Assert.Equal(DeviceConnectionState.Disconnected, rotator.ConnectionState);
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(350, 10, 10)]
    [InlineData(10, 350, 350)]
    [InlineData(0, 360, 0)]
    [InlineData(0, -90, 270)]
    public async Task AnAbsoluteMove_ArrivesAtTheTarget_ByTheShortWay(double start, double target, double expected)
    {
        var rotator = await ConnectedAsync(start);

        await rotator.MoveToAsync(target);

        Assert.Equal(expected, rotator.Position, 6);
        Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
    }

    [Fact]
    public async Task TheShortWayRoundTheCircle_IsTaken()
    {
        var rotator = new SimulatedRotator(new DeviceId("r"), startPosition: 350, degreesPerSecond: 100);
        await rotator.ConnectAsync();
        var seen = new List<double>();

        var move = rotator.MoveToAsync(10);
        while (!move.IsCompleted)
        {
            seen.Add(rotator.Position);
            await Task.Delay(5);
        }

        await move;
        // 350 to 10 is 20 degrees through zero, never the 340 degrees back through 180.
        Assert.All(seen, p => Assert.True(p >= 350 || p <= 10, $"{p} is on the long way"));
    }

    [Fact]
    public async Task ARelativeMove_IsTheAngleThatWasAsked_AndWrapsOnTheCircle()
    {
        var rotator = await ConnectedAsync(10);

        await rotator.MoveByAsync(-30);
        Assert.Equal(340, rotator.Position, 6);

        await rotator.MoveByAsync(400);
        Assert.Equal(20, rotator.Position, 6);
    }

    [Fact]
    public async Task AHalt_StopsTheMoveWhereItStands_AndNotAtTheTarget()
    {
        var rotator = await ConnectedAsync(0, speed: 100);
        var move = rotator.MoveToAsync(180);
        await Task.Delay(300);

        await rotator.HaltAsync();
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        Assert.InRange(rotator.Position, 5, 175);
    }

    [Fact]
    public async Task Cancelling_StopsItWhereItStands_AndThrows()
    {
        var rotator = await ConnectedAsync(0, speed: 100);
        using var cts = new CancellationTokenSource();
        var move = rotator.MoveToAsync(180, cts.Token);
        await Task.Delay(300);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        Assert.InRange(rotator.Position, 5, 175);
    }

    [Fact]
    public async Task ASecondMove_WhileOneRuns_IsRefused_AndSoIsADisconnect()
    {
        var rotator = await ConnectedAsync(0, speed: 50);
        var move = rotator.MoveToAsync(180);
        await Task.Delay(50);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rotator.DisconnectAsync());

        await rotator.HaltAsync();
        await move;
    }

    [Fact]
    public async Task ADeviceThatIsNotConnected_CannotMove()
    {
        var rotator = new SimulatedRotator(new DeviceId("r"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rotator.HaltAsync());
    }

    [Fact]
    public async Task ASync_ChangesThePosition_NotTheMechanicalPosition()
    {
        var rotator = await ConnectedAsync(30);

        await rotator.SyncAsync(100);

        Assert.Equal(100, rotator.Position, 6);
        Assert.Equal(30, rotator.MechanicalPosition!.Value, 6);
        Assert.Equal(30, rotator.Telemetry!.MechanicalPosition!.Value, 6);
    }

    [Fact]
    public async Task TheMountingOfTheCamera_MakesThePositionAndTheSkyRotationTwoThings()
    {
        // Mechanical position 30°, the camera sits 12° off: a solve of the image says 42°.
        var rotator = await ConnectedAsync(0, skyOffset: 12);

        await rotator.MoveToAsync(30);

        Assert.Equal(30, rotator.Position, 6);
        Assert.Equal(42, rotator.CurrentSkyRotation, 6);
        Assert.NotEqual(rotator.Position, rotator.CurrentSkyRotation);
    }

    [Fact]
    public async Task AReversedMounting_TurnsTheSkyTheOtherWay()
    {
        var rotator = await ConnectedAsync(0, skyOffset: 100, reversed: true);

        Assert.Equal(100, rotator.SkyRotationAt(0), 6);
        Assert.Equal(70, rotator.SkyRotationAt(30), 6);
        Assert.Equal(-170, rotator.SkyRotationAt(270), 6);
    }

    [Fact]
    public async Task TheMovesAndTheirEnds_ArePublished()
    {
        var host = new SideraRuntimeHost();
        await using var _ = host;
        var rotator = host.AddSimulatedRotator(new DeviceId("rotator.sim"), "Rotator", degreesPerSecond: 3600);
        var motion = new List<RotatorMotionState>();
        var arrivals = new List<double>();
        host.EventBus.Subscribe<RotatorMotionStateChanged>((e, _) => { lock (motion) { motion.Add(e.NewState); } return Task.CompletedTask; });
        host.EventBus.Subscribe<RotatorPositionChanged>((e, _) => { lock (arrivals) { arrivals.Add(e.Position); } return Task.CompletedTask; });
        await rotator.ConnectAsync();

        await rotator.MoveToAsync(45);
        for (var i = 0; i < 400 && (motion.Count < 2 || arrivals.Count < 1); i++)
        {
            await Task.Delay(5);
        }

        Assert.Equal([RotatorMotionState.Moving, RotatorMotionState.Idle], motion);
        Assert.Equal([45.0], arrivals.Select(a => Math.Round(a, 6)));
    }

    [Fact]
    public async Task AFailingMove_ThrowsAndLeavesItIdle()
    {
        var rotator = await ConnectedAsync();
        rotator.FailMovesAfter = 1;
        await rotator.MoveToAsync(10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rotator.MoveToAsync(20));

        Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        Assert.Equal(10, rotator.Position, 6);
    }
}

public sealed class RotatorSkyModelTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(30, 0, 30)]
    [InlineData(30, 12, 42)]
    [InlineData(30, -12, 18)]
    [InlineData(350, 20, 10)]
    [InlineData(170, 20, -170)]
    [InlineData(0, 180, 180)]
    [InlineData(0, -180, 180)]
    public void ThePosition_PlusTheOffset_IsTheSkyRotation_InTheCanonicalRange(double position, double offset, double expectedSky)
    {
        var model = new RotatorSkyModel(offset);

        Assert.Equal(expectedSky, model.SkyRotationOf(position), 9);
    }

    [Theory]
    [InlineData(42, 12, 30)]
    [InlineData(10, 20, 350)]
    [InlineData(-170, 20, 170)]
    [InlineData(180, 0, 180)]
    [InlineData(0, 0, 0)]
    public void TheRotatorIsSentToTheSkyAngleMinusTheOffset_InZeroTo360(double desiredSky, double offset, double expectedPosition)
    {
        var model = new RotatorSkyModel(offset);

        Assert.Equal(expectedPosition, model.PositionFor(desiredSky), 9);
    }

    [Theory]
    [InlineData(false, 0, 12)]
    [InlineData(false, 60, -33.5)]
    [InlineData(true, 0, 12)]
    [InlineData(true, 60, 140)]
    public void TheTwoDirections_AreInverse(bool reversed, double offset, double desired)
    {
        var model = new RotatorSkyModel(offset, reversed);

        Assert.Equal(desired, model.SkyRotationOf(model.PositionFor(desired)), 9);
    }

    [Fact]
    public void AMeasurement_GivesTheOffset_AndTheCalibrationReproducesIt()
    {
        // The example of the specification: mechanical 42.0°, solved 87.5° gives an offset of +45.5°.
        var model = new RotatorSkyModel(0).Calibrated(42.0, 87.5, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(45.5, model.OffsetDegrees, 9);
        Assert.Equal(87.5, model.SkyRotationOf(42.0), 9);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), model.CalibratedAt);
    }

    [Theory]
    [InlineData(10, -170, 180)] // -170 - 10 = -180: the boundary is written as 180
    [InlineData(350, 10, 20)]
    [InlineData(0, 179, 179)]
    [InlineData(300, -100, -40)]
    public void TheOffsetOfAMeasurement_WrapsLikeEveryRotation(double position, double solved, double expectedOffset)
    {
        var model = new RotatorSkyModel(0).Calibrated(position, solved);

        Assert.Equal(expectedOffset, model.OffsetDegrees, 9);
        Assert.Equal(SkyMath.NormalizeRotationDegrees(solved), model.SkyRotationOf(position), 9);
    }

    [Fact]
    public void ACalibrationOfAReversedRotator_KeepsTheDirection()
    {
        var model = new RotatorSkyModel(0, Reversed: true).Calibrated(30, 80);

        Assert.True(model.Reversed);
        Assert.Equal(110, model.OffsetDegrees, 9); // 80 = -30 + 110
        Assert.Equal(80, model.SkyRotationOf(30), 9);
    }

    [Theory]
    [InlineData(179, -179, 2)]
    [InlineData(-179, 179, -2)]
    [InlineData(10, 350, -20)]
    [InlineData(350, 10, 20)]
    [InlineData(0, 180, 180)]
    [InlineData(0, -180, 180)]
    [InlineData(90, 90, 0)]
    [InlineData(-90, 90, 180)]
    [InlineData(1, 359, -2)]
    public void TheRotationError_IsTheShortSignedWay_NeverTheLongOne(double from, double to, double expected) =>
        Assert.Equal(expected, SkyMath.RotationDifferenceDegrees(from, to), 9);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(360, 0)]
    [InlineData(-1, 359)]
    [InlineData(725, 5)]
    [InlineData(359.9999999999, 359.9999999999)]
    public void APositionIsKeptInZeroTo360(double input, double expected) =>
        Assert.Equal(expected, RotatorSkyModel.Normalize360(input), 6);
}

public sealed class RotatorRigTests
{
    [Fact]
    public void ARigWithoutARotator_IsCompleteAsItIs()
    {
        var rig = new Rig(new RigId("rig"), "Rig", new DeviceId("camera"));

        Assert.Null(rig.RotatorId);
        Assert.Null(rig.RotatorModel);
    }

    [Fact]
    public void ARigWithARotator_KeepsItsCalibration_AndTheCalibrationGoesWithTheRotator()
    {
        var model = new RotatorSkyModel(45.5);
        var rig = new Rig(new RigId("rig"), "Rig", new DeviceId("camera"), new OpticalTrain(500), null, null, new DeviceId("rotator"), model);

        Assert.Equal(model, rig.RotatorModel);
        Assert.Equal(model, rig.WithOptics(new OpticalTrain(600)).RotatorModel);
        Assert.Null(rig.WithRotator(null).RotatorModel);
        Assert.Null(rig.WithRotator(new DeviceId("other")).RotatorModel); // a calibration is of one rotator
        Assert.Equal(new RotatorSkyModel(1), rig.WithRotatorModel(new RotatorSkyModel(1)).RotatorModel);
        Assert.Null(new Rig(new RigId("r"), "R", new DeviceId("c"), null, null, null, null, model).RotatorModel); // no rotator, no calibration
    }

    [Fact]
    public async Task TheRegistry_AcceptsARigWithARotator_AndRefusesOneThatIsNotRegisteredOrNotARotator()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera"), "Camera");
        host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator");
        host.AddSimulatedMount(new DeviceId("mount"), "Mount");

        host.AddRig(new Rig(new RigId("ok"), "Ok", camera.Id, null, null, null, new DeviceId("rotator")));

        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new RigId("a"), "A", camera.Id, null, null, null, new DeviceId("nothing"))));
        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new RigId("b"), "B", camera.Id, null, null, null, new DeviceId("mount"))));
        host.AddRig(new Rig(new RigId("plain"), "Plain", camera.Id));
    }

    [Fact]
    public async Task ARotatorThatARigNames_CannotBeRemovedFromTheHost()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera"), "Camera");
        host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator");
        host.AddRig(new Rig(new RigId("rig"), "Rig", camera.Id, null, null, null, new DeviceId("rotator")));

        Assert.Throws<InvalidOperationException>(() => host.RemoveDevice(new DeviceId("rotator")));
    }
}

public sealed class RotatorOperationTests
{
    private static async Task<(SideraRuntimeHost Host, SimulatedRotator Rotator, SimulatedCamera Camera)> CreateAsync(bool withRig = true)
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera"), "Camera");
        var rotator = host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator", degreesPerSecond: 3600);
        await rotator.ConnectAsync();
        if (withRig)
        {
            host.AddRig(new Rig(new RigId("rig"), "Rig", camera.Id, null, null, null, rotator.Id));
        }

        return (host, rotator, camera);
    }

    [Fact]
    public async Task AManualMove_GoesThroughTheOperationService_AndMovesTheRotator()
    {
        var (host, rotator, _) = await CreateAsync();
        await using var _ = host;

        await host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 120);
        await host.DeviceOperations.MoveRotatorByAsync(rotator.Id, -20);

        Assert.Equal(100, rotator.Position, 6);
    }

    [Fact]
    public async Task AManualMove_WaitsForTheExposureOfTheCameraOfItsRig_AndNeverRotatesDuringIt()
    {
        var (host, rotator, camera) = await CreateAsync();
        await using var _ = host;
        using var exposure = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(camera.Id)], CancellationToken.None);

        var move = host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 90);
        await Task.Delay(200);

        Assert.False(move.IsCompleted);
        Assert.Equal(0, rotator.Position);
        exposure.Dispose();
        await move.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(90, rotator.Position, 6);
    }

    [Fact]
    public async Task ARotatorWithoutARig_IsMovedWithoutWaitingForACamera()
    {
        var (host, rotator, camera) = await CreateAsync(withRig: false);
        await using var _ = host;
        using var exposure = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(camera.Id)], CancellationToken.None);

        await host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 90).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(90, rotator.Position, 6);
    }

    [Fact]
    public async Task AHalt_TakesNoResource_SoItWorksWhileAMoveHoldsTheRotator()
    {
        var host = new SideraRuntimeHost();
        await using var _ = host;
        var rotator = host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator", degreesPerSecond: 50);
        await rotator.ConnectAsync();
        var move = host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 180);
        await Task.Delay(150);

        await host.DeviceOperations.HaltRotatorAsync(rotator.Id).WaitAsync(TimeSpan.FromSeconds(5));
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(RotatorMotionState.Idle, rotator.MotionState);
        Assert.InRange(rotator.Position, 1, 179);
    }

    [Fact]
    public async Task Cancelling_StopsTheMove_AndFreesTheResources()
    {
        var host = new SideraRuntimeHost();
        await using var _ = host;
        var rotator = host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator", degreesPerSecond: 50);
        await rotator.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var move = host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 180, cts.Token);
        await Task.Delay(150);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        using var again = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(rotator.Id)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NothingMovesTheRotator_ByItself_OnConnectOrOnAddingARig()
    {
        var host = new SideraRuntimeHost();
        await using var _ = host;
        var camera = host.AddSimulatedCamera(new DeviceId("camera"), "Camera");
        var rotator = host.AddSimulatedRotator(new DeviceId("rotator"), "Rotator", startPosition: 77);
        host.AddRig(new Rig(new RigId("rig"), "Rig", camera.Id, null, null, null, rotator.Id, new RotatorSkyModel(10)));

        await rotator.ConnectAsync();
        await Task.Delay(100);

        Assert.Equal(77, rotator.Position, 6);
        Assert.Equal(0, rotator.MovesStarted);
    }
}

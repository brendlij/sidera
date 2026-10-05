using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Rotators;

/// <summary>
/// Rotating to a sky angle and verifying it with plate solves, and centering and rotating, on a simulated rotator whose sky rotation is the truth that the solver reports. The calibration
/// that the rig holds can differ from that truth (a rotator that was calibrated badly or mounted the other way), and the verification is what finds out.
/// </summary>
public sealed class RotationServiceTests
{
    private static readonly PlateSolveDefaults Defaults = new();
    private static readonly CelestialCoordinates Target = new(5, 30);
    private static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(1);

    /// <summary>A solver that looks at the simulated rotator: the rotation it reports is the real sky rotation, and the center is where the mount points (displaced once after a turn, when asked to).</summary>
    private sealed class RotatorAwareSolver(SimulatedRotator rotator, SimulatedMount mount) : IPlateSolver
    {
        private int _seenMoves;

        public string Name => "RotatorAware";

        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;

        public int Calls { get; private set; }

        /// <summary>The first solve after a move of the rotator reports a center that is off by this much (degrees); 0 for none.</summary>
        public double DisturbDegrees { get; set; }

        public Func<int, PlateSolveResult?>? Scripted { get; set; }

        public Func<int, CancellationToken, Task>? Gate { get; set; }

        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var call = ++Calls;
            if (Gate is not null)
            {
                await Gate(call, cancellationToken);
            }

            if (Scripted?.Invoke(call) is { } scripted)
            {
                return scripted;
            }

            var disturbed = DisturbDegrees > 0 && rotator.MovesStarted != _seenMoves;
            _seenMoves = rotator.MovesStarted;
            var center = disturbed ? SkyMath.FromTangentOffset(mount.Coordinates, DisturbDegrees, 0) : mount.Coordinates;
            return new PlateSolveResult { Success = true, Center = center, RotationDegrees = rotator.CurrentSkyRotation, Backend = Name };
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required Rig Rig { get; init; }
        public required SimulatedRotator Rotator { get; init; }
        public required SimulatedMount Mount { get; init; }
        public required RotatorAwareSolver Solver { get; init; }
        public RotationService Service => Host.Rotation!;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Harness> CreateAsync(
        double? modelOffset = 0, bool modelReversed = false, double trueOffset = 0, bool trueReversed = false, double start = 0, bool withRotator = true, bool connectRotator = true)
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        var rotator = host.AddSimulatedRotator(new("rotator"), "Rotator", start, 3600, trueOffset, trueReversed);
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        if (connectRotator)
        {
            await rotator.ConnectAsync();
        }

        var rig = new Rig(
            new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76),
            rotatorId: withRotator ? rotator.Id : null,
            rotatorModel: withRotator && modelOffset is { } offset ? new RotatorSkyModel(offset, modelReversed) : null);
        host.AddRig(rig);
        var solver = new RotatorAwareSolver(rotator, mount);
        host.ConfigurePlateSolver(solver);
        return new Harness { Host = host, Rig = rig, Rotator = rotator, Mount = mount, Solver = solver };
    }

    private static async Task AssertResourcesFreeAsync(Harness h)
    {
        using var lease = await h.Host.ResourceManager.AcquireAsync(
            [ResourceId.ForDevice(h.Mount.Id), ResourceId.ForDevice(h.Rig.CameraId), ResourceId.ForDevice(h.Rotator.Id)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(h.Service.IsBusy);
        Assert.False(h.Host.PlateSolving!.IsSolving);
    }

    private static Task<RotationResult> VerifyAsync(Harness h, double target, double tolerance = 0.5, int attempts = 4, CancellationToken token = default) =>
        h.Service.RotateAndVerifyAsync(h.Rig, h.Mount.Id, target, tolerance, attempts, Exposure, Defaults, cancellationToken: token);

    private static Task<CenterAndRotateResult> CenterAndRotateAsync(Harness h, double target = 30, int rounds = 3, int rotationAttempts = 4, CancellationToken token = default) =>
        h.Service.CenterAndRotateAsync(Target, target, h.Rig, h.Mount.Id, 5, 0.5, 4, rotationAttempts, rounds, Exposure, Defaults, cancellationToken: token);

    // ---- RotateToAngle

    [Fact]
    public async Task RotateToAngle_MovesByTheCalibration_AndDoesNotSolve()
    {
        await using var h = await CreateAsync(modelOffset: 20, trueOffset: 20);

        var result = await h.Service.RotateToAngleAsync(h.Rig, 50);

        Assert.True(result.Success);
        Assert.Equal(30, h.Rotator.Position, 6); // sky 50 = position + 20
        Assert.Equal(50, h.Rotator.CurrentSkyRotation, 6);
        Assert.Equal(30, result.PositionDegrees!.Value, 6);
        Assert.Null(result.SolvedRotationDegrees);
        Assert.Equal(0, h.Solver.Calls);
        Assert.Equal(0, h.Mount.SyncCount);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateToAngle_UsesTheDirectionOfTheCalibration()
    {
        await using var h = await CreateAsync(modelOffset: 0, modelReversed: true, trueReversed: true);

        await h.Service.RotateToAngleAsync(h.Rig, 40);

        Assert.Equal(320, h.Rotator.Position, 6); // sky = -position
        Assert.Equal(40, h.Rotator.CurrentSkyRotation, 6);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-180, 180)]
    [InlineData(540, 180)]
    public async Task RotateToAngle_TakesAnyAngle_AsTheSkyRotationOfSidera(double asked, double expectedSky)
    {
        await using var h = await CreateAsync();

        await h.Service.RotateToAngleAsync(h.Rig, asked);

        Assert.Equal(expectedSky, h.Rotator.CurrentSkyRotation, 6);
    }

    [Fact]
    public async Task RotateToAngle_WithoutACalibration_FailsClearly_AndMovesNothing()
    {
        await using var h = await CreateAsync(modelOffset: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RotateToAngleAsync(h.Rig, 30));

        Assert.Contains("not calibrated", ex.Message);
        Assert.Equal(0, h.Rotator.MovesStarted);
        Assert.Equal(0, h.Solver.Calls);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateToAngle_WithoutARotator_Fails()
    {
        await using var h = await CreateAsync(withRotator: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RotateToAngleAsync(h.Rig, 30));

        Assert.Contains("no rotator", ex.Message);
    }

    [Fact]
    public async Task RotateToAngle_WithADisconnectedRotator_Fails()
    {
        await using var h = await CreateAsync(connectRotator: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.RotateToAngleAsync(h.Rig, 30));

        Assert.Contains("not connected", ex.Message);
    }

    [Fact]
    public async Task RotateToAngle_ARotatorThatFailsToMove_IsAFailedResult_NotACrash()
    {
        await using var h = await CreateAsync();
        h.Rotator.FailMovesAfter = 0;

        var result = await h.Service.RotateToAngleAsync(h.Rig, 30);

        Assert.False(result.Success);
        Assert.Contains("Rotator move failed", result.Message);
        await AssertResourcesFreeAsync(h);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task ANonFiniteAngle_IsRefused(double angle)
    {
        await using var h = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Service.RotateToAngleAsync(h.Rig, angle));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => VerifyAsync(h, angle));
    }

    // ---- RotateAndVerify

    [Fact]
    public async Task RotateAndVerify_WithACorrectCalibration_NeedsOneMoveAndOneSolve()
    {
        await using var h = await CreateAsync(modelOffset: 15, trueOffset: 15);

        var result = await VerifyAsync(h, 60);

        Assert.True(result.Success);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, h.Solver.Calls);
        Assert.Equal(60, result.SolvedRotationDegrees!.Value, 6);
        Assert.Equal(0, result.ErrorDegrees!.Value, 6);
        Assert.Equal(1, h.Rotator.MovesStarted);
        Assert.Equal(0, h.Mount.SyncCount);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_TheSolveIsTheAuthority_ABadCalibrationIsCorrected()
    {
        // The rig believes the offset is 0; the sky is really 12 degrees further round.
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 12);

        var result = await VerifyAsync(h, 40);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, h.Solver.Calls);
        Assert.InRange(Math.Abs(h.Rotator.CurrentSkyRotation - 40), 0, 0.5);
        Assert.Equal(2, h.Rotator.MovesStarted);
    }

    [Fact]
    public async Task RotateAndVerify_CorrectsWithTheShortestSignedAngle_AcrossTheWrap()
    {
        // Target 179, and the real sky ends up at 182 = -178: the error is -3 degrees, not 357.
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 3);

        var result = await VerifyAsync(h, 179);

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.InRange(Math.Abs(SkyMath.RotationDifferenceDegrees(h.Rotator.CurrentSkyRotation, 179)), 0, 0.5);
        Assert.InRange(h.Rotator.Position, 170, 180); // it turned 3 degrees back, not 357 forwards
    }

    [Fact]
    public async Task RotateAndVerify_CorrectsTheOtherWayAcrossTheWrap()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: -3);

        var result = await VerifyAsync(h, -179);

        Assert.True(result.Success);
        Assert.InRange(Math.Abs(SkyMath.RotationDifferenceDegrees(h.Rotator.CurrentSkyRotation, -179)), 0, 0.5);
    }

    [Theory]
    [InlineData(0.4, true)]
    [InlineData(0.6, false)]
    public async Task RotateAndVerify_TheToleranceDecidesWhetherItCorrects(double off, bool oneAttempt)
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: off);

        var result = await VerifyAsync(h, 0, tolerance: 0.5);

        Assert.True(result.Success);
        Assert.Equal(oneAttempt ? 1 : 2, result.Attempts);
    }

    [Fact]
    public async Task RotateAndVerify_ADifferentToleranceIsHonoured()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 2);

        var loose = await VerifyAsync(h, 0, tolerance: 3);

        Assert.Equal(1, loose.Attempts);
    }

    [Fact]
    public void RotateAndVerify_ByDefault_TheToleranceIsHalfADegree_AndFourAttempts()
    {
        Assert.Equal(0.5, RotationService.DefaultToleranceDegrees);
        Assert.Equal(4, RotationService.DefaultMaxAttempts);
    }

    [Fact]
    public async Task RotateAndVerify_AReversedRotatorThatIsCalibratedAsNot_FailsInsteadOfTurningAway()
    {
        // The rig says "more position, more sky"; the rotator really is the other way. The first correction makes it worse, and the operation stops there.
        await using var h = await CreateAsync(modelOffset: 0, modelReversed: false, trueOffset: 10, trueReversed: true);

        var result = await VerifyAsync(h, 50, attempts: 6);

        Assert.False(result.Success);
        Assert.Equal(2, result.Attempts);
        Assert.Contains("direction", result.Message);
        Assert.Equal(2, h.Rotator.MovesStarted); // it did not go on turning
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_TheMaximumNumberOfAttempts_EndsItWithAFailure()
    {
        await using var h = await CreateAsync();
        h.Solver.Scripted = _ => new PlateSolveResult { Success = true, Center = Target, RotationDegrees = 50, Backend = "Scripted" }; // never changes, whatever is done

        var result = await VerifyAsync(h, 0, attempts: 3);

        Assert.False(result.Success);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, h.Solver.Calls);
        Assert.Contains("3 attempts", result.Message);
        Assert.Equal(-50, result.ErrorDegrees!.Value, 6);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_ASolveThatFails_IsAFailureWithItsMessage()
    {
        await using var h = await CreateAsync();
        h.Solver.Scripted = _ => PlateSolveResult.Failed("Scripted", PlateSolveFailure.NoSolution, "No solution.", TimeSpan.Zero);

        var result = await VerifyAsync(h, 30);

        Assert.False(result.Success);
        Assert.Equal("No solution.", result.Message);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_ASolveWithoutARotation_IsNotTakenForZero()
    {
        await using var h = await CreateAsync();
        h.Solver.Scripted = _ => new PlateSolveResult { Success = true, Center = Target, Backend = "Scripted" };

        var result = await VerifyAsync(h, 30);

        Assert.False(result.Success);
        Assert.Contains("did not say the rotation", result.Message);
    }

    [Fact]
    public async Task RotateAndVerify_ARotatorThatFailsOnACorrection_IsAFailure()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 10);
        h.Rotator.FailMovesAfter = 1; // the first move works, the correction does not

        var result = await VerifyAsync(h, 40);

        Assert.False(result.Success);
        Assert.Contains("Rotator move failed", result.Message);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_Cancelled_DuringASolve_StopsAndReleasesEverything()
    {
        await using var h = await CreateAsync();
        var started = new TaskCompletionSource();
        h.Solver.Gate = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cts = new CancellationTokenSource();

        var task = VerifyAsync(h, 30, token: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task RotateAndVerify_Cancelled_WhileTheRotatorMoves_HaltsIt()
    {
        await using var h = await CreateAsync();
        var slow = h.Host.AddSimulatedRotator(new("rotator.slow"), "Slow", 0, 20);
        await slow.ConnectAsync();
        var rig = new Rig(new("rig.slow"), "Slow", new("camera"), new OpticalTrain(500), rotatorId: slow.Id, rotatorModel: new RotatorSkyModel(0));
        h.Host.AddRig(rig);
        using var cts = new CancellationTokenSource();

        var task = h.Service.RotateAndVerifyAsync(rig, h.Mount.Id, 180, 0.5, 3, Exposure, Defaults, cancellationToken: cts.Token);
        while (slow.MotionState != RotatorMotionState.Moving)
        {
            await Task.Delay(5);
        }

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(RotatorMotionState.Idle, slow.MotionState);
        Assert.True(slow.Position < 179);
        Assert.False(h.Service.IsBusy);
    }

    [Fact]
    public async Task RotateAndVerify_WithoutACalibration_FailsClearly_AndDoesNotSolve()
    {
        await using var h = await CreateAsync(modelOffset: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(h, 30));

        Assert.Equal(0, h.Solver.Calls);
    }

    [Fact]
    public async Task ADisconnectOfTheRotatorWhileItMoves_IsRefused_AndTheRotationGoesOn()
    {
        await using var h = await CreateAsync();
        var slow = h.Host.AddSimulatedRotator(new("rotator.slow"), "Slow", 0, 200);
        await slow.ConnectAsync();
        var rig = new Rig(new("rig.slow"), "Slow", new("camera"), new OpticalTrain(500), rotatorId: slow.Id, rotatorModel: new RotatorSkyModel(0));
        h.Host.AddRig(rig);
        h.Solver.Scripted = _ => new PlateSolveResult { Success = true, Center = Target, RotationDegrees = 180, Backend = "Scripted" };

        var task = h.Service.RotateAndVerifyAsync(rig, h.Mount.Id, 180, 0.5, 3, Exposure, Defaults);
        while (slow.MotionState != RotatorMotionState.Moving)
        {
            await Task.Delay(5);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => slow.DisconnectAsync());

        var result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Success);
        Assert.Equal(DeviceConnectionState.Connected, slow.ConnectionState);
        using var lease = await h.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(slow.Id)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    // ---- Resource coordination

    [Fact]
    public async Task ARotation_WaitsForTheCameraThatIsExposing_AndDoesNotMoveBefore()
    {
        await using var h = await CreateAsync();
        var exposing = await h.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None);

        var rotation = h.Service.RotateToAngleAsync(h.Rig, 90);
        await Task.Delay(150);

        Assert.False(rotation.IsCompleted);
        Assert.Equal(0, h.Rotator.MovesStarted);
        exposing.Dispose();
        var result = await rotation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(1, h.Rotator.MovesStarted);
    }

    [Fact]
    public async Task AnExposure_WaitsForARotationThatTurns()
    {
        await using var h = await CreateAsync();
        var slow = h.Host.AddSimulatedRotator(new("rotator.slow"), "Slow", 0, 100);
        await slow.ConnectAsync();
        var rig = new Rig(new("rig.slow"), "Slow", new("camera"), new OpticalTrain(500), rotatorId: slow.Id, rotatorModel: new RotatorSkyModel(0));
        h.Host.AddRig(rig);

        var rotation = h.Service.RotateToAngleAsync(rig, 90);
        while (slow.MotionState != RotatorMotionState.Moving)
        {
            await Task.Delay(5);
        }

        var camera = h.Host.ResourceManager.AcquireAsync([ResourceId.ForDevice(rig.CameraId)], CancellationToken.None);
        await Task.Delay(100);
        Assert.False(camera.IsCompleted);

        await rotation.WaitAsync(TimeSpan.FromSeconds(5));
        using var lease = await camera.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RotatorMotionState.Idle, slow.MotionState);
    }

    [Fact]
    public async Task TwoOperationsOnTheSameRig_AreQueuedByTheLeases_NotRefused_AndNotRunTogether()
    {
        await using var h = await CreateAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Solver.Gate = async (call, token) =>
        {
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        var first = VerifyAsync(h, 30);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = h.Service.RotateToAngleAsync(h.Rig, 10);
        await Task.Delay(100);
        Assert.False(second.IsCompleted); // it waits for the rotator and the camera that the first one holds
        Assert.True(h.Service.IsBusy);

        release.SetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task APlateSolveOfTheSameCamera_WaitsForTheRotation_InsteadOfFailingOrDeadlocking()
    {
        await using var h = await CreateAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        h.Solver.Gate = async (call, token) =>
        {
            if (call == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };

        var rotation = VerifyAsync(h, 30);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var solve = h.Host.PlateSolving!.CaptureAndSolveAsync(h.Rig, h.Mount.Id, Exposure, Defaults);
        await Task.Delay(100);
        Assert.False(solve.IsCompleted);

        release.SetResult();

        Assert.True((await rotation.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True((await solve.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    // ---- CenterAndRotate

    [Fact]
    public async Task CenterAndRotate_CentersThenRotates_AndEndsWhenBothHold()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 0);

        var result = await CenterAndRotateAsync(h, target: 45);

        Assert.True(result.Success);
        Assert.Equal(1, result.Rounds);
        Assert.True(result.Centering!.Success);
        Assert.True(result.Rotation!.Success);
        Assert.InRange(result.PointingErrorArcseconds!.Value, 0, 5);
        Assert.InRange(Math.Abs(result.RotationErrorDegrees!.Value), 0, 0.5);
        Assert.Equal(45, h.Rotator.CurrentSkyRotation, 6);
        Assert.Equal(0, h.Mount.SyncCount);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_CentersAgain_WhenTurningMovedTheField()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 0);
        h.Solver.DisturbDegrees = 0.05; // 180 arcseconds, after the turn only

        var result = await CenterAndRotateAsync(h, target: 45);

        Assert.True(result.Success);
        Assert.Equal(2, result.Rounds);
        Assert.InRange(result.PointingErrorArcseconds!.Value, 0, 5);
        Assert.InRange(Math.Abs(h.Rotator.CurrentSkyRotation - 45), 0, 0.5);
        Assert.Equal(1, h.Rotator.MovesStarted); // the second round only checked the rotation; it did not turn it away and back
        Assert.Equal(0, h.Mount.SyncCount);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_WithABadCalibration_CorrectsTheRotation_AndStillEndsInBothTolerances()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 8);
        h.Solver.DisturbDegrees = 0.05;

        var result = await CenterAndRotateAsync(h, target: -100);

        Assert.True(result.Success);
        Assert.InRange(Math.Abs(SkyMath.RotationDifferenceDegrees(h.Rotator.CurrentSkyRotation, -100)), 0, 0.5);
        Assert.InRange(result.PointingErrorArcseconds!.Value, 0, 5);
    }

    [Fact]
    public async Task CenterAndRotate_IsBounded_ByTheNumberOfRounds()
    {
        await using var h = await CreateAsync();
        h.Solver.DisturbDegrees = 0.05;

        var result = await CenterAndRotateAsync(h, target: 45, rounds: 1);

        Assert.False(result.Success);
        Assert.Contains("rounds", result.Message);
        Assert.InRange(result.PointingErrorArcseconds!.Value, 100, 400);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_ACenteringThatFails_StopsBeforeTheRotatorMoves()
    {
        await using var h = await CreateAsync();
        h.Solver.Scripted = _ => PlateSolveResult.Failed("Scripted", PlateSolveFailure.NoSolution, "No solution.", TimeSpan.Zero);

        var result = await CenterAndRotateAsync(h);

        Assert.False(result.Success);
        Assert.StartsWith("Centering failed", result.Message);
        Assert.Equal(0, h.Rotator.MovesStarted);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_ARotationThatFails_IsAFailure_WithTheReason()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 10, trueReversed: true);

        var result = await CenterAndRotateAsync(h, target: 50);

        Assert.False(result.Success);
        Assert.StartsWith("Rotation failed", result.Message);
        Assert.NotNull(result.Centering);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_Cancelled_ReleasesTheMountTheCameraAndTheRotator()
    {
        await using var h = await CreateAsync();
        var started = new TaskCompletionSource();
        h.Solver.Gate = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        using var cts = new CancellationTokenSource();

        var task = CenterAndRotateAsync(h, token: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task CenterAndRotate_NeedsACalibratedRotator_AndDoesNotSlewWithout()
    {
        await using var h = await CreateAsync(modelOffset: null);
        var before = h.Mount.Coordinates;

        await Assert.ThrowsAsync<InvalidOperationException>(() => CenterAndRotateAsync(h));

        Assert.Equal(before, h.Mount.Coordinates);
        Assert.Equal(0, h.Solver.Calls);
    }

    // ---- Calibration

    [Fact]
    public async Task Calibrate_DerivesTheOffsetFromOneSolveAtTheCurrentPosition()
    {
        await using var h = await CreateAsync(modelOffset: null, trueOffset: 37, start: 20);
        var before = DateTimeOffset.UtcNow;

        var result = await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults);

        Assert.True(result.Success);
        Assert.Equal(1, h.Solver.Calls);
        Assert.Equal(20, result.PositionDegrees!.Value, 6);
        Assert.Equal(57, result.SolvedRotationDegrees!.Value, 6);
        Assert.Equal(37, result.Model!.OffsetDegrees, 6);
        Assert.False(result.Model.Reversed); // never guessed
        Assert.True(result.Model.CalibratedAt >= before);
        Assert.Equal(0, h.Rotator.MovesStarted);
        Assert.Equal(0, h.Mount.SyncCount);
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task Calibrate_KeepsTheDirectionTheRigHas()
    {
        await using var h = await CreateAsync(modelOffset: 0, modelReversed: true, trueOffset: 90, trueReversed: true, start: 30);

        var result = await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults);

        Assert.True(result.Model!.Reversed);
        Assert.Equal(90, result.Model.OffsetDegrees, 6);
        Assert.Equal(h.Rotator.CurrentSkyRotation, result.Model.SkyRotationOf(30), 6);
    }

    [Fact]
    public async Task Calibrate_KeepsNothing_TheRigIsAsItWas()
    {
        await using var h = await CreateAsync(modelOffset: null, trueOffset: 37);

        await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults);

        Assert.Null(h.Host.RigRegistry.GetAll().Single().RotatorModel);
    }

    [Fact]
    public async Task Calibrate_ThenRotate_ReachesTheSky()
    {
        await using var h = await CreateAsync(modelOffset: null, trueOffset: -75, start: 100);
        var calibrated = (await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults)).Model!;
        var rig = h.Rig.WithRotatorModel(calibrated);

        var result = await h.Service.RotateAndVerifyAsync(rig, h.Mount.Id, 10, 0.5, 4, Exposure, Defaults);

        Assert.True(result.Success);
        Assert.Equal(1, result.Attempts); // the calibration was right, so no correction
    }

    [Fact]
    public async Task Calibrate_WithASolveThatFails_GivesNoModel()
    {
        await using var h = await CreateAsync(modelOffset: null);
        h.Solver.Scripted = _ => PlateSolveResult.Failed("Scripted", PlateSolveFailure.NoSolution, "No solution.", TimeSpan.Zero);

        var result = await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults);

        Assert.False(result.Success);
        Assert.Null(result.Model);
        Assert.Equal("No solution.", result.Message);
    }

    [Fact]
    public async Task Calibrate_WithoutARotator_Fails()
    {
        await using var h = await CreateAsync(withRotator: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults));
    }

    // ---- Sequence actions

    [Fact]
    public async Task TheActions_RunTheOperations_AndFailTheStepWhenTheyFail()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 0);

        var rotate = await new RotateToAngleAction(h.Service, h.Rig, 30).ExecuteAsync(NoContext.Instance, default);
        Assert.IsType<RotationResult>(rotate.Payload);
        Assert.Equal(30, h.Rotator.CurrentSkyRotation, 6);

        var verify = await new RotateAndVerifyAction(h.Service, h.Rig, h.Mount.Id, 60, 0.5, 4, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default);
        Assert.True(((RotationResult)verify.Payload!).Success);

        var center = await new CenterAndRotateAction(h.Service, h.Rig, h.Mount.Id, Target, -20, 5, 0.5, 4, 4, 3, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default);
        Assert.True(((CenterAndRotateResult)center.Payload!).Success);
        Assert.Equal(-20, h.Rotator.CurrentSkyRotation, 6);

        h.Solver.Scripted = _ => PlateSolveResult.Failed("Scripted", PlateSolveFailure.NoSolution, "No solution.", TimeSpan.Zero);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RotateAndVerifyAction(h.Service, h.Rig, h.Mount.Id, 0, 0.5, 4, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new CenterAndRotateAction(h.Service, h.Rig, h.Mount.Id, Target, 0, 5, 0.5, 4, 4, 3, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default));
        await AssertResourcesFreeAsync(h);
    }

    [Fact]
    public async Task TheActions_NeverSynchronizeTheMount()
    {
        await using var h = await CreateAsync(modelOffset: 0, trueOffset: 6);
        h.Solver.DisturbDegrees = 0.05;

        await new RotateToAngleAction(h.Service, h.Rig, 30).ExecuteAsync(NoContext.Instance, default);
        await new RotateAndVerifyAction(h.Service, h.Rig, h.Mount.Id, 60, 0.5, 4, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default);
        await new CenterAndRotateAction(h.Service, h.Rig, h.Mount.Id, Target, -20, 5, 0.5, 4, 4, 3, Exposure, Defaults).ExecuteAsync(NoContext.Instance, default);
        await h.Service.CalibrateAsync(h.Rig, h.Mount.Id, Exposure, Defaults);
        await h.Host.PlateSolving!.CaptureAndSolveAsync(h.Rig, h.Mount.Id, Exposure, Defaults);

        Assert.Equal(0, h.Mount.SyncCount);
    }

    [Fact]
    public async Task TheActions_HaveNamesThatSayWhatTheyDo()
    {
        await using var h = await CreateAsync();

        Assert.Equal("Rotate to 30°", new RotateToAngleAction(h.Service, h.Rig, 30).Name);
        Assert.Contains("Rotate & Verify 12.5°", new RotateAndVerifyAction(h.Service, h.Rig, null, 12.5, 0.5, 4, Exposure, Defaults).Name);
        Assert.Contains("Center & Rotate", new CenterAndRotateAction(h.Service, h.Rig, h.Mount.Id, Target, 10, 5, 0.5, 4, 4, 3, Exposure, Defaults).Name);
    }

    // ---- The plate solve service primitives that a rotation uses

    [Fact]
    public async Task ThePrimitivesInALease_NeitherAcquireNorClaimTheService()
    {
        await using var h = await CreateAsync();
        using var lease = await h.Host.ResourceManager.AcquireAsync(
            [ResourceId.ForDevice(h.Mount.Id), ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None);

        // The caller holds the mount and the camera: these would wait forever if they asked for them again.
        var solve = await h.Host.PlateSolving!.CaptureAndSolveInLeaseAsync(h.Rig, h.Mount.Id, Exposure, Defaults).WaitAsync(TimeSpan.FromSeconds(5));
        var centering = await h.Host.PlateSolving.CenterTargetInLeaseAsync(Target, h.Rig, h.Mount.Id, 5, 4, Exposure, Defaults).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(solve.Success);
        Assert.True(centering.Success);
        Assert.False(h.Host.PlateSolving.IsSolving);
        Assert.Equal(0, h.Mount.SyncCount);
    }
}

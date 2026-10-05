using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Astrometry;

/// <summary>Slew &amp; Center and Sync Mount as sequence steps: the existing operations of the plate solve service, run by a sequence, with nothing added.</summary>
public sealed class PlateSolvingActionsTests
{
    private static readonly PlateSolveDefaults Defaults = new();
    private static readonly CelestialCoordinates Target = new(5, 30);

    private sealed class Harness
    {
        public required SideraRuntimeHost Host { get; init; }
        public required Rig Rig { get; init; }
        public required SimulatedMount Mount { get; init; }
        public required FakePlateSolver Solver { get; init; }
        public PlateSolveService Service => Host.PlateSolving!;
    }

    private static async Task<Harness> CreateAsync()
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76));
        host.AddRig(rig);
        var solver = new FakePlateSolver();
        host.ConfigurePlateSolver(solver);
        return new Harness { Host = host, Rig = rig, Mount = mount, Solver = solver };
    }

    private static SlewAndCenterAction Center(Harness h, CelestialCoordinates? target = null, int attempts = 4) =>
        new(h.Service, h.Rig, h.Mount.Id, target ?? Target, 5, attempts, TimeSpan.FromMilliseconds(1), Defaults);

    private static Task<Sidera.Core.Sequencing.SequenceStepResult> Run(Sidera.Core.Sequencing.ISequenceStep step, CancellationToken token = default) =>
        step.ExecuteAsync(Sidera.Runtime.Tests.Sequencing.NoContext.Instance, token);

    private static async Task AssertResourcesFree(Harness h)
    {
        using var lease = await h.Host.ResourceManager.AcquireAsync(
            [ResourceId.ForDevice(h.Mount.Id), ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(h.Service.IsSolving);
    }

    // ---- Slew & Center

    [Fact]
    public async Task AlreadyCentered_NeedsOneSolve_AndNoCorrection()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Success(Target);

        var result = await Run(Center(h));

        Assert.IsType<CenteringResult>(result.Payload);
        Assert.Equal(1, ((CenteringResult)result.Payload!).Attempts);
        Assert.Single(h.Solver.Requests);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task ACorrection_IsSlewedAndVerified_WithTheSphericalCorrectionTarget()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var off = SkyMath.FromTangentOffset(Target, 0.02, -0.01);
        h.Solver.Success(off);
        h.Solver.Success(Target);

        var result = (CenteringResult)(await Run(Center(h))).Payload!;

        Assert.True(result.Success);
        Assert.Equal(2, result.Attempts);
        var expected = SkyMath.CorrectedTarget(Target, off, Target);
        Assert.True(SkyMath.AngularSeparationDegrees(expected, h.Solver.Requests[1].ApproximateCenter!) < 1e-8);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task SeveralAttempts_AreMadeInOrder_AndStopWhenCentered()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Success(SkyMath.FromTangentOffset(Target, 0.03, 0));
        h.Solver.Success(SkyMath.FromTangentOffset(Target, 0.01, 0));
        h.Solver.Success(Target);

        var result = (CenteringResult)(await Run(Center(h))).Payload!;

        Assert.Equal(3, result.Attempts);
        Assert.Equal(3, h.Solver.Requests.Count);
    }

    [Fact]
    public async Task ATargetThatIsNeverReached_FailsTheStep_AfterTheMaximumAttempts_AndNoMore()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        for (var i = 0; i < 10; i++)
        {
            h.Solver.Success(SkyMath.FromTangentOffset(Target, 0.05, 0));
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Center(h, attempts: 3)));

        Assert.Contains("Maximum centering attempts", ex.Message);
        Assert.Equal(3, h.Solver.Requests.Count);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task ASolveFailure_FailsTheStep_WithTheReason()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Failure();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Center(h)));

        Assert.Contains("No solution", ex.Message);
        Assert.Single(h.Solver.Requests);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task AMountThatCannotSlew_FailsTheStep_BeforeAnyExposure()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        await h.Mount.DisconnectAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Center(h)));

        Assert.Empty(h.Solver.Requests);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task Cancelling_CancelsTheOperation_AndReleasesEverything()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Wait();
        using var cts = new CancellationTokenSource();

        var running = Run(Center(h), cts.Token);
        while (h.Solver.Requests.Count == 0)
        {
            await Task.Delay(2);
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task Centering_NeverSynchronizesTheMount()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var solved = SkyMath.FromTangentOffset(Target, 0.02, 0.02);
        h.Solver.Success(solved);
        h.Solver.Success(Target);

        await Run(Center(h));

        // A sync would have put the mount at the solved position of the first solve; it is where the corrected slew put it, near the target.
        Assert.True(SkyMath.AngularSeparationDegrees(h.Mount.Coordinates, solved) > 0.005);
    }

    [Fact]
    public async Task ARightAscensionWrap_IsCorrectedAcrossZeroHours()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var target = new CelestialCoordinates(23.9995, 0);
        var off = SkyMath.FromTangentOffset(target, 0.02, 0);
        h.Solver.Success(off);
        h.Solver.Success(target);

        var result = (CenteringResult)(await Run(Center(h, target))).Payload!;

        Assert.True(result.Success);
        Assert.True(SkyMath.AngularSeparationDegrees(SkyMath.CorrectedTarget(target, off, target), h.Solver.Requests[1].ApproximateCenter!) < 1e-8);
    }

    // ---- Sync Mount to Solved Position

    [Fact]
    public async Task AfterASuccessfulSolve_TheSyncGivesTheSolvedPositionToTheMount()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var solved = new CelestialCoordinates(7.25, -12.5);
        h.Solver.Success(solved);
        await h.Service.CaptureAndSolveAsync(h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults);

        await Run(new SyncMountToSolvedPositionAction(h.Service, h.Mount.Id));

        Assert.Equal(solved, h.Mount.Coordinates);
        Assert.Single(h.Solver.Requests); // no new solve was run
    }

    [Fact]
    public async Task WithoutAPreviousSolve_TheSyncFails_AndTheMountIsLeftAlone()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var before = h.Mount.Coordinates;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SyncMountToSolvedPositionAction(h.Service, h.Mount.Id)));

        Assert.Contains("no successful plate solve", ex.Message);
        Assert.Equal(before, h.Mount.Coordinates);
        Assert.Empty(h.Solver.Requests);
    }

    [Fact]
    public async Task AFailedLatestSolve_IsNotUsedForASync_EvenAfterAnEarlierSuccess()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Success(new CelestialCoordinates(7, 10));
        await h.Service.CaptureAndSolveAsync(h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults);
        h.Solver.Failure();
        await h.Service.CaptureAndSolveAsync(h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults);
        var before = h.Mount.Coordinates;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SyncMountToSolvedPositionAction(h.Service, h.Mount.Id)));

        Assert.Equal(before, h.Mount.Coordinates);
        Assert.Equal(2, h.Solver.Requests.Count);
    }

    [Fact]
    public async Task AMountThatCannotSync_FailsTheStepClearly()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Success(new CelestialCoordinates(7, 10));
        await h.Service.CaptureAndSolveAsync(h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults);
        var plain = new PlainMount(new DeviceId("mount.plain"));
        h.Host.AddDevice(plain);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SyncMountToSolvedPositionAction(h.Service, plain.Id)));

        Assert.Contains("cannot be synchronized", ex.Message);
    }

    [Fact]
    public async Task AFailingSync_FailsTheStep_AndTheResourcesAreFree()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        h.Solver.Success(new CelestialCoordinates(7, 10));
        await h.Service.CaptureAndSolveAsync(h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults);
        await h.Mount.ParkAsync(); // a parked mount refuses a sync

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new SyncMountToSolvedPositionAction(h.Service, h.Mount.Id)));

        await AssertResourcesFree(h);
    }

    [Fact]
    public async Task APlateSolveStep_NeverSyncs_OnItsOwn()
    {
        var h = await CreateAsync();
        await using var host = h.Host;
        var solved = new CelestialCoordinates(7.25, -12.5);
        h.Solver.Success(solved);
        var before = h.Mount.Coordinates;

        await Run(new PlateSolveAction(h.Service, h.Rig, h.Mount.Id, TimeSpan.FromMilliseconds(1), Defaults));

        Assert.Equal(before, h.Mount.Coordinates);
    }

    private sealed class PlainMount(DeviceId id) : IMount
    {
        public DeviceId Id { get; } = id;
        public string Name => "Plain mount";
        public DeviceType Type => DeviceType.Mount;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public MountMotionState MotionState => MountMotionState.Idle;
        public CelestialCoordinates Coordinates => new(1, 1);
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

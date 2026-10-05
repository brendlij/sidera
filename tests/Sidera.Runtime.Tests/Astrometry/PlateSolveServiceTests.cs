using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Runtime.Astrometry;

namespace Sidera.Runtime.Tests.Astrometry;

internal sealed class FakePlateSolver : IPlateSolver
{
    public string Name => "Scripted";
    public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
    public Queue<Func<PlateSolveRequest, CancellationToken, Task<PlateSolveResult>>> Script { get; } = new();
    public List<PlateSolveRequest> Requests { get; } = [];
    public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));
    public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Script.Dequeue()(request, cancellationToken);
    }
    public void Success(CelestialCoordinates center) => Script.Enqueue((_, _) => Task.FromResult(new PlateSolveResult { Success = true, Center = center, Backend = Name }));
    public void Failure() => Script.Enqueue((_, _) => Task.FromResult(PlateSolveResult.Failed(Name, PlateSolveFailure.NoSolution, "No solution.", TimeSpan.Zero)));
    public void Wait() => Script.Enqueue(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); });
}

public sealed class PlateSolveServiceTests
{
    private static readonly PlateSolveDefaults Defaults = new();
    private static readonly CelestialCoordinates Target = new(5, 30);

    private static async Task<(SideraRuntimeHost Host, Rig Rig, DeviceId Mount, FakePlateSolver Solver)> Harness()
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        await camera.ConnectAsync(); await mount.ConnectAsync();
        var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76));
        host.AddRig(rig);
        var solver = new FakePlateSolver(); host.ConfigurePlateSolver(solver);
        return (host, rig, mount.Id, solver);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CenteringConvergesWithBoundedCorrections(int attempts)
    {
        var h = await Harness(); await using var host = h.Host;
        for (var i = 1; i < attempts; i++) h.Solver.Success(SkyMath.FromTangentOffset(Target, .01 * (attempts - i), -.01));
        h.Solver.Success(Target);
        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 4, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.True(result.Success); Assert.Equal(attempts, result.Attempts);
        Assert.Equal(attempts, h.Solver.Requests.Count);
        Assert.False(host.PlateSolving.IsSolving);
        using var lease = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(h.Mount), ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(23.999, 0)] [InlineData(2, 89.9)]
    public async Task CorrectionsUseSphericalMath(double ra, double dec)
    {
        var h = await Harness(); await using var host = h.Host;
        var target = new CelestialCoordinates(ra, dec);
        var offset = SkyMath.FromTangentOffset(target, .01, -.01);
        h.Solver.Success(offset); h.Solver.Success(target);
        var result = await host.PlateSolving!.CenterTargetAsync(target, h.Rig, h.Mount, 5, 2, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.True(result.Success);
        var expected = SkyMath.CorrectedTarget(target, offset, target);
        Assert.True(SkyMath.AngularSeparationDegrees(expected, h.Solver.Requests[1].ApproximateCenter!) < 1e-8);
    }

    [Fact]
    public async Task MaxAttemptsStopsWithoutAnExtraSlew()
    {
        var h = await Harness(); await using var host = h.Host;
        h.Solver.Success(new(6, 30)); h.Solver.Success(new(6, 30));
        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 2, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.False(result.Success); Assert.Equal(2, h.Solver.Requests.Count); Assert.Contains("Maximum", result.Message);
    }

    [Fact]
    public async Task SolveFailureStopsLoop()
    {
        var h = await Harness(); await using var host = h.Host; h.Solver.Failure();
        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 3, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.False(result.Success); Assert.Single(h.Solver.Requests); Assert.Equal("No solution.", result.Message);
    }

    [Fact]
    public async Task MountFailureStopsBeforeExposure()
    {
        var h = await Harness(); await using var host = h.Host;
        await host.DeviceOperations.DisconnectAsync(h.Mount);
        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 3, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.False(result.Success); Assert.Empty(h.Solver.Requests); Assert.Contains("Mount slew failed", result.Message);
    }

    [Fact]
    public async Task CancellationReleasesAllResources()
    {
        var h = await Harness(); await using var host = h.Host; h.Solver.Wait();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 3, TimeSpan.FromMilliseconds(1), Defaults, cancellationToken: cancel.Token));
        Assert.False(host.PlateSolving!.IsSolving);
        using var lease = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(h.Mount), ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TimeoutIsAResultAndClearsBusyState()
    {
        var h = await Harness(); await using var host = h.Host; h.Solver.Wait();
        var result = await host.PlateSolving!.SolveAsync(new CameraFrame(1, 1, [1], TimeSpan.Zero), h.Rig, h.Mount,
            Defaults with { Timeout = TimeSpan.FromMilliseconds(20) });
        Assert.Equal(PlateSolveFailure.Timeout, result.Failure); Assert.False(host.PlateSolving.IsSolving);
        Assert.Same(result, host.PlateSolving.LastResult);
    }

    [Fact]
    public void HintResolutionUsesGeometryOverridesAndNoUnknownCenter()
    {
        var rig = new Rig(new("r"), "R", new("c"), new OpticalTrain(500));
        var frame = new CameraFrame(100, 80, new ushort[8000], TimeSpan.Zero);
        var request = PlateSolveHintResolver.Resolve(frame, rig, new(100, 80, 4, 5), null, Defaults,
            new PlateSolveOverrides { SearchRadiusDegrees = 3, BlindAllowed = false, Downsample = DownsamplePolicy.Of(2) });
        Assert.Null(request.ApproximateCenter);
        var geometry = OpticalTrainGeometry.Resolve(rig.Optics, new(100, 80, 4, 5));
        Assert.Equal(geometry.PixelScaleXArcsecPerPixel, request.PixelScaleXArcsecPerPixel);
        Assert.Equal(geometry.FieldOfViewYDegrees, request.FieldOfViewYDegrees);
        Assert.Equal(3, request.SearchRadiusDegrees); Assert.False(request.BlindAllowed); Assert.Equal(2, request.Downsample.Factor);
    }

    [Fact]
    public void HintResolutionAppliesBinningAndFrameDimensionsAndOverrideCenter()
    {
        var rig = new Rig(new("r"), "R", new("c"), new OpticalTrain(500, pixelSizeXMicrons: 4, pixelSizeYMicrons: 4));
        var frame = new CameraFrame(100, 80, new ushort[8000], TimeSpan.Zero) { Acquisition = new FrameAcquisition { BinX = 2, BinY = 2 } };
        var center = new CelestialCoordinates(6, 40);
        var request = PlateSolveHintResolver.Resolve(frame, rig, new(2000, 1600, 3, 3), Target, Defaults,
            new PlateSolveOverrides { Hints = new(center, null, null, null, null, null) });
        var geometry = OpticalTrainGeometry.Compute(500, 8, 8, 100, 80);
        Assert.Equal(center, request.ApproximateCenter); Assert.Equal(8, request.Image.PixelSizeXMicrons);
        Assert.Equal(geometry.FieldOfViewXDegrees, request.FieldOfViewXDegrees);
        Assert.Equal(geometry.PixelScaleXArcsecPerPixel, request.PixelScaleXArcsecPerPixel);
    }

    [Fact]
    public async Task CameraFailureReleasesCaptureLeaseAndReturnsClearFailure()
    {
        var h = await Harness(); await using var host = h.Host;
        h.Solver.Success(Target);
        Assert.True((await host.PlateSolving!.CaptureAndSolveAsync(h.Rig, h.Mount, TimeSpan.FromMilliseconds(1), Defaults)).Success);
        Assert.NotNull(host.PlateSolving.LastFrame);
        await host.DeviceOperations.DisconnectAsync(h.Rig.CameraId);
        var result = await host.PlateSolving!.CaptureAndSolveAsync(h.Rig, h.Mount, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.False(result.Success); Assert.Contains("Camera exposure failed", result.Message);
        Assert.Single(h.Solver.Requests); Assert.False(host.PlateSolving.IsSolving);
        Assert.Null(host.PlateSolving.LastFrame); Assert.Null(host.PlateSolving.LastRequest);
        using var lease = await host.ResourceManager.AcquireAsync([ResourceId.ForDevice(h.Rig.CameraId)], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task InsideToleranceStopsAfterFirstSolve()
    {
        var h = await Harness(); await using var host = h.Host;
        h.Solver.Success(SkyMath.FromTangentOffset(Target, 1.0 / 3600, 0));
        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 5, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.True(result.Success); Assert.Equal(1, result.Attempts);
        Assert.InRange(result.PointingErrorArcseconds!.Value, .99, 1.01);
    }

    [Fact]
    public async Task NeitherSolvingNorCenteringSynchronizesTheMount_ButAnExplicitSyncDoes()
    {
        var h = await Harness(); await using var host = h.Host;
        var mount = (Sidera.Runtime.Devices.SimulatedMount)host.DeviceRegistry.GetAll().OfType<Sidera.Runtime.Devices.SimulatedMount>().Single();
        var offset = SkyMath.FromTangentOffset(Target, .02, .02);
        h.Solver.Success(offset); h.Solver.Success(Target);

        var result = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount, 5, 3, TimeSpan.FromMilliseconds(1), Defaults);

        // A sync would have set the mount to the solved position; the mount is where the last slew put it.
        Assert.True(result.Success);
        var seenByMount = mount.Coordinates;
        Assert.True(SkyMath.AngularSeparationDegrees(seenByMount, h.Solver.Requests[1].ApproximateCenter!) < 1e-6 || SkyMath.AngularSeparationDegrees(seenByMount, Target) < 0.05);

        h.Solver.Success(offset);
        await host.PlateSolving.CaptureAndSolveAsync(h.Rig, h.Mount, TimeSpan.FromMilliseconds(1), Defaults);
        Assert.True(SkyMath.AngularSeparationDegrees(mount.Coordinates, offset) > 0.01, "a plain solve moved or synced the mount");

        await host.PlateSolving.SyncMountToSolvedPositionAsync(h.Mount);

        Assert.True(SkyMath.AngularSeparationDegrees(mount.Coordinates, offset) < 1e-9);
    }

    [Fact]
    public async Task AnExplicitSync_NeedsASuccessfulSolve()
    {
        var h = await Harness(); await using var host = h.Host;

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.PlateSolving!.SyncMountToSolvedPositionAsync(h.Mount));
    }
}

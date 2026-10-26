using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Astrometry;

/// <summary>What centering does for a real mount: it lets the mount settle, tries a solve that found nothing again before it gives up, and starts tracking when the mount does not track.</summary>
public sealed class CenteringOptionsTests
{
    private static readonly PlateSolveDefaults Defaults = new();
    private static readonly CelestialCoordinates Target = new(5, 30);

    private static async Task<(SideraRuntimeHost Host, Rig Rig, SimulatedMount Mount, FakePlateSolver Solver)> Harness(CenteringOptions options)
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
        host.PlateSolving!.Centering = options;
        return (host, rig, mount, solver);
    }

    private static Task<CenteringResult> Center(SideraRuntimeHost host, Rig rig, DeviceId mount, int attempts, List<string>? progress = null) =>
        host.PlateSolving!.CenterTargetAsync(
            Target, rig, mount, 5, attempts, TimeSpan.FromMilliseconds(1), Defaults, progress: progress is null ? null : new Progress<CenteringProgress>(p => progress.Add(p.Stage)));

    [Fact]
    public void NothingIsOnByDefault_AndTheProductionOptionsSettleRetryAndTrack()
    {
        Assert.Equal(new CenteringOptions(TimeSpan.Zero, 0, false), CenteringOptions.None);
        Assert.True(CenteringOptions.Production.SettleAfterSlew >= TimeSpan.FromSeconds(1));
        Assert.True(CenteringOptions.Production.SolveRetries >= 1);
        Assert.True(CenteringOptions.Production.EnsureTracking);
    }

    [Fact]
    public async Task ASolveThatFindsNothing_IsTriedAgainWithoutSlewingAgain_BeforeCenteringGivesUp()
    {
        var h = await Harness(new CenteringOptions(TimeSpan.Zero, 2, false));
        await using var host = h.Host;
        h.Solver.Failure();
        h.Solver.Failure();
        h.Solver.Success(Target);

        var result = await Center(host, h.Rig, h.Mount.Id, 3);

        Assert.True(result.Success);
        Assert.Equal(1, result.Attempts); // one slew, three exposures
        Assert.Equal(3, h.Solver.Requests.Count);
    }

    [Fact]
    public async Task ASolveThatKeepsFailing_GivesUpAfterTheRetries()
    {
        var h = await Harness(new CenteringOptions(TimeSpan.Zero, 2, false));
        await using var host = h.Host;
        h.Solver.Failure();
        h.Solver.Failure();
        h.Solver.Failure();

        var result = await Center(host, h.Rig, h.Mount.Id, 3);

        Assert.False(result.Success);
        Assert.Equal(3, h.Solver.Requests.Count);
        Assert.Equal("No solution.", result.Message);
    }

    [Fact]
    public async Task WithoutRetries_ASolveThatFindsNothingEndsCenteringAtOnce()
    {
        var h = await Harness(CenteringOptions.None);
        await using var host = h.Host;
        h.Solver.Failure();

        var result = await Center(host, h.Rig, h.Mount.Id, 3);

        Assert.False(result.Success);
        Assert.Single(h.Solver.Requests);
    }

    [Fact]
    public async Task TheMountIsLeftAloneAfterASlew_BeforeTheExposureOfTheSolve()
    {
        var h = await Harness(new CenteringOptions(TimeSpan.FromMilliseconds(300), 0, false));
        await using var host = h.Host;
        h.Solver.Success(Target);
        var progress = new List<string>();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await Center(host, h.Rig, h.Mount.Id, 1, progress);
        clock.Stop();

        Assert.True(result.Success);
        Assert.True(clock.ElapsedMilliseconds >= 280, $"centering took {clock.ElapsedMilliseconds} ms");
        await Task.Delay(50);
        Assert.Contains("Settle", progress);
        Assert.True(progress.IndexOf("Settle") < progress.IndexOf("Solve"));
    }

    [Fact]
    public async Task AMountThatIsNotTracking_IsToldToTrack_BeforeTheExposure_AndThatIsSaid()
    {
        var h = await Harness(new CenteringOptions(TimeSpan.Zero, 0, true));
        await using var host = h.Host;
        var progress = new List<string>();
        var tracking = false;
        h.Solver.Script.Enqueue((_, _) =>
        {
            tracking = h.Mount.Telemetry!.Tracking;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = Target, Backend = "Scripted" });
        });

        // A mount that stops tracking when a slew ends (a limit, a setting of its own): the event of the end of the slew is awaited by the slew, so it is off before the frame is taken.
        using var subscription = host.EventBus.Subscribe<MountMotionStateChanged>(async (e, _) =>
        {
            if (e.PreviousState == MountMotionState.Slewing && e.NewState == MountMotionState.Tracking)
            {
                await h.Mount.SetTrackingAsync(false);
            }
        });

        var result = await Center(host, h.Rig, h.Mount.Id, 1, progress);

        Assert.True(result.Success);
        Assert.True(tracking, "the mount tracked when the frame was taken");
        await Task.Delay(50);
        Assert.Contains("Tracking started", progress);
    }

    [Fact]
    public async Task AMountThatTracks_IsNotTouched()
    {
        var h = await Harness(new CenteringOptions(TimeSpan.Zero, 0, true));
        await using var host = h.Host;
        h.Solver.Success(Target);
        var progress = new List<string>();

        await Center(host, h.Rig, h.Mount.Id, 1, progress);

        await Task.Delay(50);
        Assert.DoesNotContain("Tracking started", progress);
    }

    [Fact]
    public async Task ACenteringThatFails_SaysWhereItGotTo_AndAnotherOneGoesOnFromThere()
    {
        var h = await Harness(CenteringOptions.None);
        await using var host = h.Host;
        var offset = SkyMath.FromTangentOffset(Target, 0.5, 0.2);
        h.Solver.Success(offset);
        h.Solver.Success(offset);

        var failed = await host.PlateSolving!.CenterTargetAsync(Target, h.Rig, h.Mount.Id, 5, 2, TimeSpan.FromMilliseconds(1), Defaults);

        Assert.False(failed.Success);
        Assert.NotNull(failed.Commanded);
        Assert.True(SkyMath.AngularSeparationDegrees(failed.Commanded!, Target) > 0.1, "the corrections moved the commanded position away from the target");

        h.Solver.Success(Target);
        var again = await host.PlateSolving.CenterTargetAsync(Target, h.Rig, h.Mount.Id, 5, 2, TimeSpan.FromMilliseconds(1), Defaults, startAt: failed.Commanded);

        Assert.True(again.Success);
        Assert.True(SkyMath.AngularSeparationDegrees(h.Solver.Requests[^1].ApproximateCenter!, failed.Commanded!) < 1e-6, "the first slew of the second centering went to the corrected position");
    }
}

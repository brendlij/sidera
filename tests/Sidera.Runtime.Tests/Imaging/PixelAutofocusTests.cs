using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Imaging;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Imaging;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Imaging;

/// <summary>
/// Autofocus on real pixels: the simulated camera draws stars whose width follows the focuser position (through a
/// Gaussian sigma), the frame analysis finds them and measures their half flux radius, and the autofocus that is
/// unchanged moves, exposes, measures and fits. Nothing here hands the algorithm the model of the simulation.
/// </summary>
public class PixelAutofocusTests
{
    private static readonly RigId MainRig = new("rig.main");
    private static readonly DeviceId MainCamera = new("camera.main");
    private static readonly DeviceId MainFocuser = new("focuser.main");
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly AutofocusOptions Quick = new(TimeSpan.FromMilliseconds(20), 400, 7);

    private static async Task<(SideraRuntimeHost Host, SimulatedCamera Camera, SimulatedFocuser Focuser)> Create(
        int start, int best = 20000, SimulatedSkyOptions? sky = null, FrameAnalysisOptions? analysis = null)
    {
        var host = new SideraRuntimeHost(analysis);
        var camera = host.AddSimulatedCamera(MainCamera, "Main Camera", seed: 1);
        var focuser = host.AddSimulatedFocuser(MainFocuser, "Main Focuser", start, stepsPerSecond: 1_000_000, minimumMoveDuration: TimeSpan.FromMilliseconds(1));
        host.AddRig(new Rig(MainRig, "Main Rig", MainCamera, Optics, MainFocuser));
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(best));
        if (sky is not null)
        {
            camera.Sky = new SimulatedSky(1, sky);
        }

        await camera.ConnectAsync();
        await focuser.ConnectAsync();
        return (host, camera, focuser);
    }

    private static Task<SequenceStepResult> Focus(SideraRuntimeHost host, AutofocusOptions? options = null)
    {
        host.RigRegistry.TryGet(MainRig, out var rig);
        var action = AutofocusAction.ForRig(host.DeviceRegistry, rig!, options ?? Quick, host.FocusMetricProvider, host.EventBus);
        return action.ExecuteAsync(NoContext.Instance, CancellationToken.None);
    }

    // The frames the simulation draws

    [Theory]
    [InlineData(0)]
    [InlineData(300)]
    [InlineData(-900)]
    [InlineData(1500)]
    public async Task TheStarsOfTheFrame_HaveTheHfrThatFocusGivesThem_Measured_NotHandedOver(int offset)
    {
        var (host, camera, focuser) = await Create(20000 + offset);
        await using var scope = host;

        var frame = await camera.ExposeAsync(TimeSpan.FromMilliseconds(10));
        var metrics = host.FrameAnalyzer.Analyze(frame).Metrics;

        var model = new SimulatedFocusModel(20000).HfrAt(focuser.Position);
        Assert.True(metrics.UsableStarCount >= 15, $"{metrics.UsableStarCount} usable stars");
        Assert.InRange(metrics.MedianHfr!.Value, model * 0.90, model * 1.05); // the analysis reads pixels; it agrees within a few percent
    }

    [Fact]
    public async Task TheStars_StayWhereTheyAre_WhateverFocusDoes_AndOnlyBecomeBroader()
    {
        var (host, camera, focuser) = await Create(20000);
        await using var scope = host;
        var inFocus = host.FrameAnalyzer.Analyze(await camera.ExposeAsync(TimeSpan.FromMilliseconds(10)));
        await focuser.MoveToAsync(21500);
        var blurred = host.FrameAnalyzer.Analyze(await camera.ExposeAsync(TimeSpan.FromMilliseconds(10)));

        Assert.True(blurred.Metrics.MedianHfr > inFocus.Metrics.MedianHfr * 1.8);
        // The brightest stars of both frames are the same stars, at the same places.
        var a = inFocus.Stars.Take(8).OrderBy(s => s.X).ToList();
        var b = blurred.Stars.Where(s => a.Any(t => Math.Abs(t.X - s.X) < 0.3 && Math.Abs(t.Y - s.Y) < 0.3)).ToList();
        Assert.True(b.Count >= 6, $"only {b.Count} of 8 stars found again");
    }

    [Fact]
    public async Task TheHfrFollowsTheFocuser_DownTowardsFocus_ThenUpAgain()
    {
        var (host, camera, focuser) = await Create(18000);
        await using var scope = host;
        var hfrs = new List<double>();

        foreach (var position in new[] { 18000, 18800, 19400, 19800, 20000, 20200, 20600, 21200, 22000 })
        {
            await focuser.MoveToAsync(position);
            hfrs.Add(host.FrameAnalyzer.Analyze(await camera.ExposeAsync(TimeSpan.FromMilliseconds(10))).Metrics.MedianHfr!.Value);
        }

        var lowest = hfrs.IndexOf(hfrs.Min());
        Assert.Equal(4, lowest); // at 20000
        Assert.True(hfrs.Take(5).Zip(hfrs.Skip(1).Take(4), (a, b) => b < a).All(x => x), string.Join(", ", hfrs));
        Assert.True(hfrs.Skip(4).Zip(hfrs.Skip(5), (a, b) => b > a).All(x => x), string.Join(", ", hfrs));
    }

    [Fact]
    public async Task TheSameExposureNumber_GivesTheSameFrame_EveryTime()
    {
        var sky = new SimulatedSky(7);

        var a = sky.Render(2.0, TimeSpan.FromSeconds(1), 3);
        var b = new SimulatedSky(7).Render(2.0, TimeSpan.FromSeconds(1), 3);
        var c = sky.Render(2.0, TimeSpan.FromSeconds(1), 4);

        Assert.True(a.Pixels.Span.SequenceEqual(b.Pixels.Span));
        Assert.False(a.Pixels.Span.SequenceEqual(c.Pixels.Span)); // the noise differs from exposure to exposure
        await Task.CompletedTask;
    }

    [Fact]
    public void TheSkyDrawsGaussians_OfConstantFlux_ABroaderStarIsDimmer()
    {
        var sky = new SimulatedSky(2, new SimulatedSkyOptions(StarCount: 1, NoiseSigma: 0));
        var star = sky.Stars[0];

        var narrow = sky.Render(1.5, TimeSpan.FromSeconds(1), 0);
        var broad = sky.Render(4.5, TimeSpan.FromSeconds(1), 0);

        double Peak(CameraFrame f) => f.Pixels.Span.ToArray().Max() - 800;
        double Sum(CameraFrame f) => f.Pixels.Span.ToArray().Sum(p => (double)p - 800);
        Assert.True(Peak(narrow) > Peak(broad) * 5);
        Assert.InRange(Sum(narrow), star.Flux * 0.97, star.Flux * 1.03);
        Assert.InRange(Sum(broad), star.Flux * 0.97, star.Flux * 1.03);
    }

    [Fact]
    public async Task ACameraWithoutASimulatedFocus_KeepsItsRandomSky_AsBefore()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.lonely"), "Lonely");
        await camera.ConnectAsync();

        var frame = await camera.ExposeAsync(TimeSpan.FromMilliseconds(10));

        Assert.Equal((800, 600), (frame.Width, frame.Height));
        Assert.Contains(frame.Pixels.ToArray(), p => p > 10_000);
    }

    // Autofocus on those frames

    [Theory]
    [InlineData(19300)] // left of focus
    [InlineData(20700)] // right of focus
    [InlineData(20010)] // near focus
    public async Task Autofocus_FindsFocus_FromThePixels_WhereverItStarts(int start)
    {
        var (host, _, focuser) = await Create(start);
        await using var scope = host;

        var result = Assert.IsType<AutofocusResult>((await Focus(host)).Payload);

        Assert.InRange(result.BestPosition, 19950, 20050);
        Assert.Equal(result.BestPosition, focuser.Position);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.NotNull(result.Verification);
        Assert.Equal(7, result.Measurements.Count);
    }

    [Fact]
    public async Task Autofocus_FromFarAway_PutsThePatternAroundTheMinimum_AndStillConverges()
    {
        var (host, _, _) = await Create(18200);
        await using var scope = host;

        var result = Assert.IsType<AutofocusResult>((await Focus(host)).Payload);

        Assert.Equal(2, result.Attempts);
        Assert.InRange(result.BestPosition, 19950, 20050);
    }

    [Fact]
    public async Task TheMeasurementsOfAutofocus_AreThoseOfTheFramesItTook()
    {
        var (host, _, _) = await Create(19600);
        await using var scope = host;
        var seen = new List<AutofocusProgress>();
        host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            lock (seen) { seen.Add(e.Progress); }
            return Task.CompletedTask;
        });

        var result = Assert.IsType<AutofocusResult>((await Focus(host)).Payload);

        // Every HFR the run reports is the median of the stars of a frame: more than the 1.8 px of perfect focus, and it
        // grows away from the focus position.
        var samples = result.Measurements.OrderBy(m => Math.Abs(m.FocuserPosition - 20000)).ToList();
        Assert.True(samples[0].Hfr < samples[^1].Hfr);
        Assert.All(result.Measurements, m => Assert.True(m.Hfr > 1.3));
        Assert.Equal(seen.Where(p => p is { Phase: AutofocusPhase.Measuring, SampleIndex: > 0 }).Select(p => p.Hfr!.Value), result.Measurements.Select(m => m.Hfr));
    }

    [Fact]
    public async Task AFrameWithTooFewStars_FailsTheAutofocus_Clearly_AndLeavesEveryDeviceIdle()
    {
        var (host, camera, focuser) = await Create(19600, sky: new SimulatedSkyOptions(StarCount: 3));
        await using var scope = host;

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Focus(host));

        Assert.Equal("Autofocus failed: only 3 usable stars were detected; at least 5 are required.", error.Message);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.False(host.ResourceManager.IsHeld(Sidera.Core.Resources.ResourceId.ForDevice(MainCamera)));
    }

    [Fact]
    public async Task AFrameWithNoStars_FailsTheAutofocus_Clearly()
    {
        var (host, _, _) = await Create(19600, sky: new SimulatedSkyOptions(StarCount: 0));
        await using var scope = host;

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Focus(host));

        Assert.Equal("Autofocus failed: no stars were detected.", error.Message);
    }

    [Fact]
    public async Task AFieldWithSaturatedStars_StillFocusesOnTheOtherStars()
    {
        var (host, _, _) = await Create(19500, sky: new SimulatedSkyOptions(SaturatedStars: 4));
        await using var scope = host;

        var result = Assert.IsType<AutofocusResult>((await Focus(host)).Payload);

        Assert.InRange(result.BestPosition, 19930, 20070);
    }

    [Fact]
    public async Task ANoisySky_GivesAResultWithinToleranceThatIsTheSameEveryTime()
    {
        var results = new List<int>();
        for (var run = 0; run < 2; run++)
        {
            var (host, _, _) = await Create(19500, sky: new SimulatedSkyOptions(NoiseSigma: 40));
            await using var scope = host;
            results.Add(Assert.IsType<AutofocusResult>((await Focus(host)).Payload).BestPosition);
        }

        Assert.Equal(results[0], results[1]);
        Assert.InRange(results[0], 19900, 20100);
    }

    [Fact]
    public async Task AFieldWithHotPixels_IsNotMisledByThem()
    {
        var (host, _, _) = await Create(19500, sky: new SimulatedSkyOptions(HotPixels: 25));
        await using var scope = host;

        var result = Assert.IsType<AutofocusResult>((await Focus(host)).Payload);

        Assert.InRange(result.BestPosition, 19950, 20050);
    }

    [Fact]
    public async Task TheMinimumNumberOfStars_IsTheOneOfTheAnalysisOptions()
    {
        var (host, _, _) = await Create(19600, sky: new SimulatedSkyOptions(StarCount: 8), analysis: new FrameAnalysisOptions(MinimumUsableStars: 12));
        await using var scope = host;

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Focus(host));

        Assert.Contains("at least 12 are required", error.Message);
    }

    [Fact]
    public async Task TheDirectSimulatedMetric_IsStillThere_ForTheTestsOfTheAlgorithm()
    {
        var (host, _, focuser) = await Create(19500);
        await using var scope = host;
        host.RigRegistry.TryGet(MainRig, out var rig);
        var action = AutofocusAction.ForRig(host.DeviceRegistry, rig!, Quick, host.FocusMetrics, host.EventBus);

        var result = Assert.IsType<AutofocusResult>((await action.ExecuteAsync(NoContext.Instance, default)).Payload);

        Assert.InRange(result.BestPosition, 19990, 20010); // the direct metric is exact
        Assert.Equal(result.BestPosition, focuser.Position);
    }
}

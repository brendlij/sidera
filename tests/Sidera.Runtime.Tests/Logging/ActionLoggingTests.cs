using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Diagnostics;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;
using Sidera.Runtime.Tests.Sequencing;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

/// <summary>The diagnostics of the dither and the autofocus actions, and of the frame analysis behind autofocus.</summary>
public class ActionLoggingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly DeviceId GuiderId = new("guider.main");
    private static readonly DeviceId MountId = new("mount.eq6");
    private static readonly DeviceId MainId = new("camera.main");
    private static readonly DeviceId WideId = new("camera.wide");
    private static readonly GuidingSettleOptions Settle = new(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));

    // Dither

    private static async Task<(SideraRuntimeHost Host, FakeGuider Guider, LogCapture Log)> DitherRig()
    {
        var log = new LogCapture();
        var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        foreach (var camera in new[] { MainId, WideId })
        {
            var fake = new FakeCamera(camera.Value);
            await fake.ConnectAsync();
            host.AddDevice(fake);
        }

        host.AddDevice(new FakeMount(MountId.Value));
        var guider = new FakeGuider(GuiderId.Value, guiding: true);
        host.AddDevice(guider);
        return (host, guider, log);
    }

    private static DitherAction Dither(SideraRuntimeHost host, GuidingSettleOptions? settle = null) =>
        new(host.DeviceRegistry, GuiderId, MountId, [MainId, WideId], 1.5, settle,
            host.LoggerFactory.CreateLogger<DitherAction>());

    private static SequenceRunner Runner(SideraRuntimeHost host) =>
        new(host.ResourceManager, host.SafePointCoordinator, host.LoggerFactory.CreateLogger<SequenceRunner>());

    [Fact]
    public async Task ADitherWithSettle_IsLoggedFromRequestThroughSettleToCompletion()
    {
        var (host, guider, log) = await DitherRig();
        await using var _ = host;

        await Runner(host).RunAsync(new Sequence("s", [Dither(host, Settle)])).WaitAsync(Bound);

        var requested = log.Single(LogLevel.Information, "Dither requested");
        Assert.Equal(1.5, requested.Properties["AmplitudePixels"]);
        Assert.Equal("guider.main", requested.Properties["GuiderId"]?.ToString());
        Assert.Equal("mount.eq6", requested.Properties["MountId"]?.ToString());
        Assert.Equal("camera.main, camera.wide", requested.Properties["CameraIds"]);
        var started = log.Single(LogLevel.Information, "Dither started");
        Assert.Equal(2, started.Properties["CameraCount"]);
        log.Single(LogLevel.Information, "Dither completed in");
        var settleStarted = log.Single(LogLevel.Information, "Settle started");
        Assert.Equal(0.5, settleStarted.Properties["MaximumErrorPixels"]);
        Assert.Equal(1.0, settleStarted.Properties["StableSeconds"]);
        Assert.Equal(10.0, settleStarted.Properties["TimeoutSeconds"]);
        log.Single(LogLevel.Information, "Settle succeeded");
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);

        var order = log.Entries.Select(e => e.Message).ToList();
        Assert.True(order.FindIndex(m => m.StartsWith("Dither requested")) < order.FindIndex(m => m.StartsWith("Dither started")));
        Assert.True(order.FindIndex(m => m.StartsWith("Dither completed")) < order.FindIndex(m => m.StartsWith("Settle started")));
        Assert.True(order.FindIndex(m => m.StartsWith("Settle started")) < order.FindIndex(m => m.StartsWith("Settle succeeded")));
        Assert.Equal(1, guider.DitherCalls);
        Assert.Equal(runnerTag(log, "Dither started"), runnerTag(log, "Dither requested"));
    }

    private static object? runnerTag(LogCapture log, string text) =>
        log.Entries.First(e => e.Message.StartsWith(text, StringComparison.Ordinal)).ScopeValue(LogContext.SequenceExecutionId);

    [Fact]
    public async Task ASettleTimeout_IsAWarningWithItsException_AndTheRunFailsAsAnError()
    {
        var (host, guider, log) = await DitherRig();
        await using var _ = host;
        guider.SettleFailure = new GuidingSettleTimeoutException("Guiding did not settle within 10 s.");

        await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() =>
            Runner(host).RunAsync(new Sequence("s", [Dither(host, Settle)])).WaitAsync(Bound));

        var warning = log.Single(LogLevel.Warning, "Settle timed out");
        Assert.IsType<GuidingSettleTimeoutException>(warning.Exception);
        Assert.Equal(10.0, warning.Properties["TimeoutSeconds"]);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Settle succeeded"));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Error && e.Message.StartsWith("Sequence s failed"));
    }

    [Fact]
    public async Task ADitherThatFails_IsAnErrorWithTheException()
    {
        var (host, guider, log) = await DitherRig();
        await using var _ = host;
        guider.Failure = new InvalidOperationException("PHD2 lost the guide star");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Runner(host).RunAsync(new Sequence("s", [Dither(host)])).WaitAsync(Bound));

        var error = log.Entries.Single(e => e.Level == LogLevel.Error && e.Message.StartsWith("Dither failed"));
        Assert.Equal("PHD2 lost the guide star", error.Exception!.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Dither completed"));
    }

    [Fact]
    public async Task ACancelledDither_IsInformation()
    {
        var (host, guider, log) = await DitherRig();
        await using var _ = host;
        guider.Block = true;
        using var cts = new CancellationTokenSource();

        var run = Runner(host).RunAsync(new Sequence("s", [Dither(host)]), cts.Token);
        await guider.GateOf("dither", 1).Started.Task.WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        log.Single(LogLevel.Information, "Dither cancelled");
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ADitherInACoordinatedBlock_LogsTheWholeTrail_InTheContextOfItsGroup()
    {
        var (host, _, log) = await DitherRig();
        await using var _ = host;
        var group = new CoordinationGroupId("multirig.test");
        var block = new ParallelStep(
            "Block", [new SequenceGroup("Imaging", [Dither(host, Settle)]), new SequenceGroup("Wide", [new SafePointStep()])], group);

        await Runner(host).RunAsync(new Sequence("s", [block])).WaitAsync(Bound);

        var requested = log.Single(LogLevel.Information, "Dither requested");
        Assert.Equal("multirig.test", requested.ScopeValue(LogContext.CoordinationGroupId));
        Assert.NotNull(requested.ScopeValue(LogContext.ParticipantId));
        log.Single(LogLevel.Information, "coordinated operation requested by");
        log.Single(LogLevel.Information, "the coordinated operation starts");
        log.Single(LogLevel.Information, "Dither started");
        log.Single(LogLevel.Information, "Settle succeeded");
        log.Single(LogLevel.Information, "coordinated operation completed");
    }

    // Autofocus and frame analysis

    private static readonly RigId MainRig = new("rig.main");
    private static readonly DeviceId Focuser = new("focuser.main");
    private static readonly AutofocusOptions Quick = new(TimeSpan.FromMilliseconds(20), 400, 7);

    private static async Task<(SideraRuntimeHost Host, SimulatedFocuser Focuser, LogCapture Log)> AutofocusRig(
        int start, SimulatedSkyOptions? sky = null)
    {
        var log = new LogCapture();
        var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        var camera = host.AddSimulatedCamera(MainId, "Main Camera", seed: 1);
        var focuser = host.AddSimulatedFocuser(Focuser, "Main Focuser", start, stepsPerSecond: 1_000_000, minimumMoveDuration: TimeSpan.FromMilliseconds(1));
        host.AddRig(new Rig(MainRig, "Main Rig", MainId, new OpticalTrain(750, 150, 3.76, 23.5, 15.7, 6248, 4176), Focuser));
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000));
        if (sky is not null)
        {
            camera.Sky = new SimulatedSky(1, sky);
        }

        await camera.ConnectAsync();
        await focuser.ConnectAsync();
        return (host, focuser, log);
    }

    private static Task<SequenceStepResult> RunAutofocus(SideraRuntimeHost host, CancellationToken cancellationToken = default)
    {
        host.RigRegistry.TryGet(MainRig, out var rig);
        var action = AutofocusAction.ForRig(
            host.DeviceRegistry, rig!, Quick, host.FocusMetricProvider, host.EventBus,
            host.LoggerFactory.CreateLogger<AutofocusAction>());
        return action.ExecuteAsync(NoContext.Instance, cancellationToken);
    }

    [Fact]
    public async Task Autofocus_LogsItsStartAndItsResult_WithPositionsHfrSamplesAndDuration()
    {
        var (host, focuser, log) = await AutofocusRig(19600);
        await using var _ = host;

        var result = Assert.IsType<AutofocusResult>((await RunAutofocus(host)).Payload);

        var started = log.Single(LogLevel.Information, "Autofocus started for rig rig.main");
        Assert.Equal("camera.main", started.Properties["CameraId"]?.ToString());
        Assert.Equal("focuser.main", started.Properties["FocuserId"]?.ToString());
        Assert.Equal(19600, started.Properties["InitialPosition"]);
        Assert.Equal(7, started.Properties["SampleCount"]);
        var completed = log.Single(LogLevel.Information, "Autofocus completed for rig rig.main");
        Assert.Equal(result.BestPosition, completed.Properties["BestPosition"]);
        Assert.Equal(result.BestHfr, (double)completed.Properties["BestHfr"]!, 6);
        Assert.Equal(result.Measurements.Count, completed.Properties["SampleCount"]);
        Assert.Equal(result.Attempts, completed.Properties["Passes"]);
        Assert.IsType<double>(completed.Properties["DurationSeconds"]);
        Assert.Equal("rig.main", started.ScopeValue(LogContext.RigId));
        Assert.Equal(result.BestPosition, focuser.Position);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning && e.Category != nameof(Sidera.Runtime.Imaging.FrameAnalyzer));
    }

    [Fact]
    public async Task EveryFocusMeasurement_IsDebug_WithItsPositionAndHfr()
    {
        var (host, _, log) = await AutofocusRig(19600);
        await using var _ = host;

        var result = Assert.IsType<AutofocusResult>((await RunAutofocus(host)).Payload);

        var samples = log.Entries.Where(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Autofocus sample")).ToList();
        Assert.Equal(result.Measurements.Count, samples.Count);
        Assert.Equal(result.Measurements.Select(m => m.FocuserPosition), samples.Select(s => (int)s.Properties["Position"]!));
        Assert.Equal(result.Measurements.Select(m => Math.Round(m.Hfr, 6)), samples.Select(s => Math.Round((double)s.Properties["Hfr"]!, 6)));
        Assert.All(samples, s => Assert.Equal("rig.main", s.ScopeValue(LogContext.RigId)));
    }

    [Fact]
    public async Task AFailedAutofocus_IsAnErrorWithTheException_AndFrameAnalysisWarnsAboutTheStars()
    {
        var (host, _, log) = await AutofocusRig(19600, new SimulatedSkyOptions(StarCount: 3));
        await using var _ = host;

        await Assert.ThrowsAsync<AutofocusFailedException>(() => RunAutofocus(host));

        var error = log.Single(LogLevel.Error, "Autofocus failed for rig rig.main");
        var failure = Assert.IsType<AutofocusFailedException>(error.Exception);
        Assert.Equal("Autofocus failed: only 3 usable stars were detected; at least 5 are required.", failure.Message);
        var warning = log.Where(LogLevel.Warning, "Only 3 of 3 stars are usable").First();
        Assert.Equal("FrameAnalyzer", warning.Category.Split('.').Last());
        Assert.Equal(5, warning.Properties["MinimumUsableStars"]);
        Assert.DoesNotContain(log.Entries, e => e.Message.StartsWith("Autofocus completed"));
    }

    [Fact]
    public async Task AFrameWithoutStars_IsAWarning()
    {
        var (host, _, log) = await AutofocusRig(19600, new SimulatedSkyOptions(StarCount: 0));
        await using var _ = host;

        await Assert.ThrowsAsync<AutofocusFailedException>(() => RunAutofocus(host));

        var warning = log.Single(LogLevel.Warning, "No stars were detected");
        Assert.Equal(800, warning.Properties["Width"]);
        Assert.Equal(600, warning.Properties["Height"]);
        Assert.Equal("Autofocus failed: no stars were detected.", log.Single(LogLevel.Error, "Autofocus failed").Exception!.Message);
    }

    [Fact]
    public async Task ACancelledAutofocus_IsInformation_NotAnError()
    {
        var (host, _, log) = await AutofocusRig(19600);
        await using var _ = host;
        using var cts = new CancellationTokenSource();
        var cancelWhenMeasuring = log.WaitForAsync(e => e.Message.StartsWith("Autofocus sample 2 ", StringComparison.Ordinal));

        var run = RunAutofocus(host, cts.Token);
        await cancelWhenMeasuring;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        log.Single(LogLevel.Information, "Autofocus cancelled for rig rig.main");
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task AnAnalysedFrame_IsLoggedAtDebug_WithItsMetricsAndNoStarOrPixel()
    {
        var log = new LogCapture();
        await using var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        var frame = new SimulatedSky(1).Render(2.0, TimeSpan.FromSeconds(1), 1);

        var result = host.FrameAnalyzer.Analyze(frame);
        host.FrameAnalyzer.Analyze(frame); // the second one is a cache hit

        var entry = Assert.Single(log.Entries, e => e.Level == LogLevel.Debug && e.Message.StartsWith("Frame 800x600 analyzed"));
        Assert.Equal(result.Metrics.UsableStarCount, entry.Properties["UsableStarCount"]);
        Assert.Equal(result.Metrics.StarCount, entry.Properties["StarCount"]);
        Assert.Equal(result.Metrics.SaturatedStarCount, entry.Properties["SaturatedStarCount"]);
        Assert.Equal(result.Metrics.MedianHfr!.Value, (double)entry.Properties["MedianHfr"]!, 9);
        Assert.Equal(result.Metrics.Background, (double)entry.Properties["Background"]!, 9);
        Assert.Equal(result.Metrics.BackgroundSigma, (double)entry.Properties["Noise"]!, 9);
        Assert.IsType<double>(entry.Properties["DurationMs"]);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Information);
        Assert.True(entry.Message.Length < 300); // an aggregate, not a dump
    }
}

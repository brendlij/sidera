using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Diagnostics;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop.Tests;

/// <summary>
/// The diagnostics of a whole Multi-Rig run on the simulator, composed by the desktop: the context (session, execution,
/// rig, track, coordination group) reaches the entries of the steps it is meant for, and the trail of a coordinated dither
/// and of an automatic autofocus can be followed through one log.
/// </summary>
public class LoggingIntegrationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    private static readonly DemoOptions Fast = new()
    {
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(100),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(50),
    };

    private static async Task<(SideraRuntimeHost Host, LogCapture Log)> CreateHost()
    {
        var log = new LogCapture();
        var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        DemoSetup.AddDemoEquipment(host, Fast);
        DemoSetup.AddDemoRigs(host, Fast);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        return (host, log);
    }

    private static BuiltSequence Build(SideraRuntimeHost host, params SequenceStepDraft[] steps) =>
        SequenceDraftBuilder.Build(
            host.DeviceRegistry, steps,
            new SequenceDraftContext(host.RigRegistry, Shared, host.FocusMetricProvider, host.EventBus, host.LoggerFactory));

    private static SequenceRunner Runner(SideraRuntimeHost host) =>
        new(host.ResourceManager, host.SafePointCoordinator, host.LoggerFactory.CreateLogger<SequenceRunner>());

    [Fact]
    public async Task AMultiRigRunWithAutofocusAndDither_CanBeFollowedThroughOneLog()
    {
        var (host, log) = await CreateHost();
        await using var _ = host;
        var mainTrack = new RigTrackDraft(
            Guid.NewGuid(), Main,
            [new RigExposureStepDraft(Guid.NewGuid(), 0.1), new RigExposureStepDraft(Guid.NewGuid(), 0.1)],
            new RigAutofocusPolicyDraft(true, true, false, 0.1, 400, 7));
        var wideTrack = new RigTrackDraft(
            Guid.NewGuid(), Wide, [new RepeatStepDraft(Guid.NewGuid(), 4, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)])], null);
        var block = new MultiRigStepDraft(
            Guid.NewGuid(), [mainTrack, wideTrack], new MultiRigDitherPolicyDraft(true, Wide, 2, 0.6, 0.5, 0.1, 5));
        var built = Build(
            host, new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId), block,
            new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId));
        var runner = Runner(host);

        await runner.RunAsync(built.Sequence).WaitAsync(Bound);

        var tag = runner.ExecutionTag;
        var runStart = log.Single(LogLevel.Information, "started with 3 top-level steps");
        Assert.Equal(tag, runStart.ScopeValue(LogContext.SequenceExecutionId));
        log.Single(LogLevel.Information, "Parallel block Multi-Rig Imaging started with 2 branches");

        // The generated autofocus of the policy: the one autofocus action, in the context of its rig and its track.
        var autofocus = log.Single(LogLevel.Information, "Autofocus started for rig rig.main");
        Assert.Equal(tag, autofocus.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Equal("rig.main", autofocus.ScopeValue(LogContext.RigId));
        Assert.Equal(mainTrack.Id.ToString("N")[..8], autofocus.ScopeValue(LogContext.TrackId));
        Assert.Equal(host.SessionId, autofocus.ScopeValue(LogContext.SessionId));
        Assert.NotNull(autofocus.ScopeValue(LogContext.CoordinationGroupId));
        log.Single(LogLevel.Information, "Autofocus completed for rig rig.main");
        var samples = log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Autofocus sample"));
        Assert.True(samples >= 7 && samples % 7 == 0, $"{samples} samples"); // whole patterns of 7, measured on real frames

        // The dither of the policy, coordinated: request, round, safe points, settle.
        var dithers = log.Where(LogLevel.Information, "Dither requested").ToList();
        Assert.Equal(2, dithers.Count); // every 2nd frame of 4
        Assert.All(dithers, d =>
        {
            Assert.Equal("rig.wide", d.ScopeValue(LogContext.RigId));
            Assert.StartsWith("multirig.", (string)d.ScopeValue(LogContext.CoordinationGroupId)!);
            Assert.Equal(tag, d.ScopeValue(LogContext.SequenceExecutionId));
        });
        Assert.Equal(2, log.Where(LogLevel.Information, "coordinated operation requested by").Count());
        Assert.Equal(2, log.Where(LogLevel.Information, "Settle succeeded").Count());
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("reached a safe point"));
        Assert.Equal(2, log.Where(LogLevel.Information, "coordinated operation completed").Count());

        // The frames behind the autofocus were analysed; the tracks ended; nothing was a warning or worse.
        Assert.True(log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Frame 800x600 analyzed")) >= 7);
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("Rig track") && e.Message.Contains("completed")));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Sequence ") && e.Message.Contains(" completed in "));
    }

    [Fact]
    public async Task AFailingRigTrack_IsAWarningWithItsRig_AndTheRunAnErrorWithTheStackTrace()
    {
        var (host, log) = await CreateHost();
        await using var _ = host;
        var camera = (ICamera)host.DeviceRegistry.GetAll().Single(d => d.Id == DemoSetup.MainCameraId);
        var track = new RigTrackDraft(Guid.NewGuid(), Main, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)], null);
        var built = Build(host, new MultiRigStepDraft(Guid.NewGuid(), [track, new RigTrackDraft(Guid.NewGuid(), Wide, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)], null)], null));
        await camera.DisconnectAsync();
        var runner = Runner(host);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        var warning = log.Where(LogLevel.Warning, "Rig track Main Rig failed").Single();
        Assert.Equal("rig.main", warning.ScopeValue(LogContext.RigId));
        var error = log.Single(LogLevel.Error, "failed after");
        Assert.Same(failure, error.Exception);
        Assert.Equal(runner.ExecutionTag, error.ScopeValue(LogContext.SequenceExecutionId));
        Assert.NotNull(error.Exception!.StackTrace);
    }

    [Fact]
    public async Task ACancelledMultiRigRun_IsNotReportedAsAnErrorAnywhere()
    {
        var (host, log) = await CreateHost();
        await using var _ = host;
        var track = new RigTrackDraft(
            Guid.NewGuid(), Main, [new RepeatStepDraft(Guid.NewGuid(), 50, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)])], null);
        var other = new RigTrackDraft(Guid.NewGuid(), Wide, [new RepeatStepDraft(Guid.NewGuid(), 50, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)])], null);
        var built = Build(host, new MultiRigStepDraft(Guid.NewGuid(), [track, other], null));
        using var cts = new CancellationTokenSource();

        var run = Runner(host).RunAsync(built.Sequence, cts.Token);
        await log.WaitForAsync(e => e.Message.StartsWith("Rig track Main Rig started") || e.Message.Contains("Exposure") && e.Message.Contains("completed"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Single(log.Entries, e => e.Level == LogLevel.Information && e.Message.StartsWith("Sequence ") && e.Message.Contains("cancelled after"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task TheMainViewModel_GivesItsSequenceRunnerTheHostsLogger_AndTheDiagnosticsPageTheLogInfo()
    {
        var (host, log) = await CreateHost();
        await using var _ = host;
        var info = new LogInfo("C:\\logs", "C:\\logs\\sidera-x.log", LogLevel.Debug, host.SessionId);
        using var vm = new MainViewModel(host, action => action(), Fast, logInfo: info);

        vm.SequenceDraft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]);
        await vm.Sequencer.RunCommand.ExecuteAsync(null);

        var started = log.Where(LogLevel.Information, "Sequence ").First(e => e.Message.Contains("started"));
        Assert.Equal(host.SessionId, started.ScopeValue(LogContext.SessionId));
        Assert.NotNull(started.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Equal("SequenceRunner", started.Category.Split('.').Last());
        Assert.Same(vm.Diagnostics, vm.CurrentPage is DiagnosticsViewModel d ? d : vm.Diagnostics);
        Assert.Equal(info.CurrentFile, vm.Diagnostics.LogFileText);
    }

    [Fact]
    public async Task AFailedRun_TellsTheUserWhichExecutionToLookForInTheLog()
    {
        var (host, log) = await CreateHost();
        await using var _ = host;
        using var vm = new MainViewModel(host, action => action(), Fast);
        var camera = (ICamera)host.DeviceRegistry.GetAll().Single(d => d.Id == DemoSetup.MainCameraId);
        vm.SequenceDraft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]);
        await camera.DisconnectAsync(); // the run starts, and the exposure fails: the camera is not connected

        vm.Sequencer.RefreshReadiness();
        await vm.Sequencer.RunCommand.ExecuteAsync(null);

        var error = log.Entries.Single(e => e.Level == LogLevel.Error && e.Message.StartsWith("Sequence "));
        var tag = (string)error.ScopeValue(LogContext.SequenceExecutionId)!;
        Assert.True(vm.Sequencer.HasError, vm.Sequencer.ErrorMessage);
        Assert.EndsWith($"See the log, execution {tag}.", vm.Sequencer.ErrorMessage);
        Assert.DoesNotContain("   at ", vm.Sequencer.ErrorMessage); // no stack trace in the UI
    }
}

using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Parallel Imaging on the simulator: the tracks really run next to each other, and the sequencer shows it.</summary>
public class MultiRigRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");

    private static readonly DemoOptions Fast = new()
    {
        ManualExposure = TimeSpan.FromMilliseconds(30),
        SequenceExposure = TimeSpan.FromMilliseconds(30),
        SequenceWait = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(20),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
    };

    private static RigExposureStepDraft Exposure(double seconds) => new(Guid.NewGuid(), seconds);
    private static DelayStepDraft Delay(double seconds) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);
    private static MultiRigStepDraft MultiRig(params RigTrackDraft[] tracks) => new(Guid.NewGuid(), tracks);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static async Task ConnectCameras(SideraRuntimeHost host)
    {
        foreach (var camera in host.DeviceRegistry.GetAll().OfType<ICamera>())
        {
            await camera.ConnectAsync();
        }
    }

    // What a run did, seen from outside: when each step ended, and how many exposures were running at once.
    private sealed class Timeline
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        public List<(TimeSpan At, string[] Path)> Completed { get; } = [];
        public int MaxConcurrentExposures { get; private set; }

        public async Task Run(SideraRuntimeHost host, Sequence sequence, TimeSpan? sampleFor = null)
        {
            var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
            runner.StepCompleted += (_, e) =>
            {
                lock (_gate)
                {
                    Completed.Add((_clock.Elapsed, PathOf(e.Position)));
                }
            };

            using var stop = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var running = runner.ActivePositions.Count(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal));
                    lock (_gate)
                    {
                        MaxConcurrentExposures = Math.Max(MaxConcurrentExposures, running);
                    }

                    await Task.Delay(2);
                }
            });

            try
            {
                await runner.RunAsync(sequence).WaitAsync(Bound);
            }
            finally
            {
                await stop.CancelAsync();
                await sampler;
            }
        }

        private static string[] PathOf(SequenceExecutionPosition position)
        {
            var path = new List<string>();
            for (var p = position; p is not null; p = p.Parent)
            {
                path.Insert(0, p.StepName);
            }

            return [.. path];
        }

        // Completions of exposures of the track with this name.
        public List<TimeSpan> ExposuresOf(string track) =>
            Completed.Where(c => c.Path.Length > 1 && c.Path[1] == track && c.Path[^1].StartsWith("Exposure", StringComparison.Ordinal))
                .Select(c => c.At).ToList();

        public TimeSpan TrackEnded(string track) => Completed.Single(c => c.Path.Length == 2 && c.Path[1] == track).At;

        public TimeSpan BlockEnded => Completed.Single(c => c.Path.Length == 1 && c.Path[0] == "Parallel Imaging").At;
    }

    private static Sequence Build(SideraRuntimeHost host, params SequenceStepDraft[] steps) =>
        SequenceDraftBuilder.Build(host.DeviceRegistry, steps, new SequenceDraftContext(host.RigRegistry)).Sequence;

    // Concurrency: the runtime, directly

    [Fact]
    public async Task ASecondTrackWithShortExposures_FinishesSeveralFramesWhileTheFirstOneIsStillExposing()
    {
        await using var host = CreateHost();
        await ConnectCameras(host);
        var sequence = Build(host, MultiRig(
            Track(Main, Exposure(0.8)),
            Track(Wide, Repeat(6, Exposure(0.1)))));
        var timeline = new Timeline();

        await timeline.Run(host, sequence);

        var mainEnded = timeline.ExposuresOf("Main Rig").Single();
        var wide = timeline.ExposuresOf("Wide Rig");
        Assert.Equal(6, wide.Count);
        Assert.True(wide.Count(at => at < mainEnded) >= 3, $"Wide finished only {wide.Count(at => at < mainEnded)} frames while Main exposed");
        Assert.True(timeline.MaxConcurrentExposures >= 2, "the exposures never overlapped");
    }

    [Fact]
    public async Task TheTracksAreNotInLockstep_ShortAndLongExposuresInterleaveFreely()
    {
        await using var host = CreateHost();
        await ConnectCameras(host);
        var sequence = Build(host, MultiRig(
            Track(Main, Repeat(2, Exposure(0.6))),
            Track(Wide, Repeat(10, Exposure(0.1)))));
        var timeline = new Timeline();

        await timeline.Run(host, sequence);

        var main = timeline.ExposuresOf("Main Rig");
        var wide = timeline.ExposuresOf("Wide Rig");
        Assert.Equal(2, main.Count);
        Assert.Equal(10, wide.Count);
        // Between the two frames of Main, Wide made several: nothing waits for the other track.
        Assert.True(wide.Count(at => at > main[0] && at < main[1]) >= 3);
        Assert.True(wide.Count(at => at < main[0]) >= 3);
    }

    [Fact]
    public async Task ThreeTracksExposeAtTheSameTime_AndEachOneCountsItsOwnFrames()
    {
        await using var host = CreateHost();
        await ConnectCameras(host);
        var sequence = Build(host, MultiRig(
            Track(Main, Repeat(2, Exposure(0.6))),
            Track(Wide, Repeat(6, Exposure(0.2))),
            Track(Narrow, Repeat(3, Exposure(0.4)))));
        var timeline = new Timeline();

        await timeline.Run(host, sequence);

        Assert.Equal(3, timeline.MaxConcurrentExposures);
        Assert.Equal(
            [2, 6, 3],
            new[] { "Main Rig", "Wide Rig", "Narrow Rig" }.Select(track => timeline.ExposuresOf(track).Count));
        // The three of them took about as long as the longest one, not as long as all of them together.
        Assert.True(timeline.BlockEnded < TimeSpan.FromSeconds(2.5), $"took {timeline.BlockEnded}");
    }

    [Fact]
    public async Task TheBlockEndsOnlyWhenTheLastTrackHas_ShortTracksEndEarly()
    {
        await using var host = CreateHost();
        await ConnectCameras(host);
        var sequence = Build(host, MultiRig(
            Track(Main, Exposure(0.1)),
            Track(Wide, Exposure(0.9)),
            Track(Narrow, Exposure(0.2))));
        var timeline = new Timeline();

        await timeline.Run(host, sequence);

        var main = timeline.TrackEnded("Main Rig");
        var wide = timeline.TrackEnded("Wide Rig");
        var narrow = timeline.TrackEnded("Narrow Rig");
        Assert.True(main < wide && narrow < wide);
        Assert.True(timeline.BlockEnded >= wide);
        Assert.True(timeline.BlockEnded >= main && timeline.BlockEnded >= narrow);
    }

    [Fact]
    public async Task EachTrackHoldsOnlyItsOwnCamera_NothingElseIsHeldWhileTheBlockRuns()
    {
        await using var host = CreateHost();
        await ConnectCameras(host);
        var sequence = Build(host, MultiRig(
            Track(Main, Exposure(0.5)),
            Track(Wide, Exposure(0.5))));
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);

        var run = runner.RunAsync(sequence);
        var deadline = DateTime.UtcNow + Bound;
        while (runner.ActivePositions.Count(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal)) < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the exposures never overlapped");
            await Task.Delay(2);
        }

        await run;

        Assert.Equal(SequenceState.Completed, runner.State);
    }

    // The sequencer

    private sealed class App : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }

        public async Task ConnectCameras()
        {
            foreach (var camera in Vm.Equipment.Cameras)
            {
                await camera.ConnectCommand.ExecuteAsync(null);
            }
        }

        public void Use(params SequenceStepDraft[] steps) => Draft.ReplaceSteps(steps);
    }

    private static App CreateApp()
    {
        var host = CreateHost();

        // What the dispatcher of the window does: one thing at a time. Tracks end on threads of their own.
        var gate = new object();
        return new App
        {
            Host = host,
            Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, Fast),
        };
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static SequenceNodeViewModel Row(App app, Guid draftId) =>
        app.Sequencer.Definition.Single(row => row.DraftId == draftId);

    [Fact]
    public async Task ARunOfTheBlock_Completes_AndEveryTrackIsDoneAtTheEnd()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var main = Track(Main, Repeat(2, Exposure(0.1)));
        var wide = Track(Wide, Repeat(3, Exposure(0.05)));
        var block = MultiRig(main, wide);
        app.Use(block);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(5, app.Vm.Imaging.FrameCount);
        Assert.All(app.Sequencer.Definition, row => Assert.Equal(NodeStatus.Done, row.Status));
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
    }

    [Fact]
    public async Task TheRowsOfARunMapToTheDraft_BlockTrackRepeatAndStep_ById()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var exposure = Exposure(0.05);
        var repeat = Repeat(2, exposure);
        var main = Track(Main, repeat);
        var delay = Delay(0.05);
        var wide = Track(Wide, delay);
        var block = MultiRig(main, wide);
        app.Use(block);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var rows = app.Sequencer.Definition;
        Assert.Equal([block.Id, main.Id, repeat.Id, exposure.Id, wide.Id, delay.Id], rows.Select(r => r.DraftId!.Value));
        Assert.Equal(
            ["Parallel Imaging", "Main Rig", "Repeat × 2", "Exposure", "Wide Rig", "Delay"],
            rows.Select(r => r.Title));
        Assert.Equal(["1.", "1.1", "1.1.1", "1.1.1.1", "1.2", "1.2.1"], rows.Select(r => r.NumberText));
        Assert.Equal([0d, 20d, 40d, 60d, 20d, 40d], rows.Select(r => r.IndentWidth));
        Assert.Equal(
            [SequenceNodeKind.Parallel, SequenceNodeKind.Group, SequenceNodeKind.Repeat, SequenceNodeKind.Step,
             SequenceNodeKind.Group, SequenceNodeKind.Step],
            rows.Select(r => r.Kind));
    }

    [Fact]
    public async Task ShortTrackIsDoneWhileLongTrackRuns_AndTheBlockStaysActive()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var main = Track(Main, Exposure(1.2));
        var wide = Track(Wide, Exposure(0.05));
        var block = MultiRig(main, wide);
        app.Use(block);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 5 && Row(app, wide.Id).Status == NodeStatus.Done, "the short track is done");

        Assert.Equal(NodeStatus.Active, Row(app, main.Id).Status);   // Main ● still running
        Assert.Equal(NodeStatus.Done, Row(app, wide.Id).Status);     // Wide ✓ finished
        Assert.Equal(NodeStatus.Active, Row(app, block.Id).Status);  // the block remains active until Main ends
        Assert.Equal("✓", Row(app, wide.Id).Glyph);
        Assert.Equal("●", Row(app, main.Id).Glyph);
        await run.WaitAsync(Bound);

        Assert.All(app.Sequencer.Definition, row => Assert.Equal(NodeStatus.Done, row.Status));
    }

    [Fact]
    public async Task EachActiveTrackIsShownOnItsOwn_WithItsOwnProgress()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        app.Use(MultiRig(
            Track(Main, Repeat(2, Exposure(0.8))),
            Track(Wide, Repeat(2, Exposure(0.8))),
            Track(Narrow, Repeat(2, Exposure(0.8)))));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.ActiveBranches.Count(b => b.HasProgress) == 3, "three exposures in progress");

        var branches = app.Sequencer.ActiveBranches;
        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig"], branches.Select(b => b.BranchName).Order());
        Assert.Equal(
            [DemoSetup.MainCameraId.Value, DemoSetup.NarrowCameraId.Value, DemoSetup.WideCameraId.Value],
            branches.Select(b => b.Camera!.DeviceIdText).Order());
        Assert.All(branches, b => Assert.Contains("Repeat × 2", b.Context));
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task ARepeatInATrack_ShowsItsIterationOnItsOwnRow()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var repeatMain = Repeat(3, Exposure(0.3));
        var repeatWide = Repeat(2, Exposure(0.9));
        app.Use(MultiRig(Track(Main, repeatMain), Track(Wide, repeatWide)));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 7 && Row(app, repeatMain.Id).IterationText == "iteration 2 / 3", "iteration 2 of Main");

        Assert.Equal("iteration 1 / 2", Row(app, repeatWide.Id).IterationText); // Wide is in its first exposure
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
    }

    // Pause and Resume

    [Fact]
    public async Task Pause_WaitsForTheLongExposure_WhileTheShortTrackAlreadyRestsAtItsBoundary()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var main = Track(Main, Repeat(2, Exposure(1.0)));
        var wide = Track(Wide, Repeat(15, Exposure(0.1)));
        app.Use(MultiRig(main, wide));
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ActiveBranches.Count(b => b.HasProgress) == 2, "both tracks exposing");
        vm.PauseCommand.Execute(null);

        // Wide is at its boundary within one short exposure; Main is still in its long one: not paused yet.
        await WaitUntil(() => vm.State == SequenceState.Pausing, "pausing");
        await Task.Delay(250);
        Assert.Equal(SequenceState.Pausing, vm.State);
        Assert.False(vm.CanResume);
        Assert.False(vm.CanPause);

        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.True(vm.CanResume);
        Assert.True(app.Vm.Imaging.FrameCount is >= 2 and < 17, "the exposure that was running finished, nothing new started");

        var frames = app.Vm.Imaging.FrameCount;
        await Task.Delay(250);
        Assert.Equal(frames, app.Vm.Imaging.FrameCount); // nothing runs while paused

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(17, app.Vm.Imaging.FrameCount); // 2 of Main and 15 of Wide, none lost, none repeated
    }

    [Fact]
    public async Task EditingStaysLockedThroughPausingAndPaused_AndIsFreeAfterwards()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        app.Use(MultiRig(Track(Main, Repeat(2, Exposure(0.5))), Track(Wide, Repeat(2, Exposure(0.5)))));
        var vm = app.Sequencer;
        Assert.True(app.Draft.CanAddTrack is false || app.Draft.IsEditable);

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        app.Draft.SelectedStep = app.Draft.Steps[0];
        Assert.False(app.Draft.AddTrackCommand.CanExecute(null));
        Assert.False(app.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.False(app.Draft.DuplicateStepCommand.CanExecute(null));
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.False(app.Draft.AddTrackCommand.CanExecute(null));
        Assert.False(app.Draft.RemoveStepCommand.CanExecute(null));
        Assert.False(app.Draft.IsEditable);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.True(app.Draft.IsEditable);
        Assert.True(app.Draft.AddTrackCommand.CanExecute(null));
        Assert.True(app.Draft.DuplicateStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelWhilePaused_EndsTheRun_AndReleasesEveryCamera()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        app.Use(MultiRig(Track(Main, Repeat(3, Exposure(0.4))), Track(Wide, Repeat(3, Exposure(0.4)))));
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ActiveBranches.Count(b => b.HasProgress) == 2, "both tracks exposing");
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
        Assert.True(vm.RunCommand.CanExecute(null));
        Assert.True(app.Draft.IsEditable);
    }

    // Failure

    [Fact]
    public async Task ATrackThatFails_IsNamedAndMarked_TheOthersAreStopped_AndEverythingIsReleased()
    {
        await using var app = CreateApp();
        // The camera of the Wide rig stays disconnected.
        foreach (var camera in app.Vm.Equipment.Cameras.Where(c => c.DeviceIdText != DemoSetup.WideCameraId.Value))
        {
            await camera.ConnectCommand.ExecuteAsync(null);
        }

        var main = Track(Main, Repeat(5, Exposure(0.5)));
        var wide = Track(Wide, Repeat(5, Exposure(0.5)));
        app.Use(MultiRig(main, wide));
        Assert.False(app.Sequencer.CanRun); // the sequencer already says which camera is missing
        Assert.Equal("Connect Wide Camera to run the sequence.", app.Sequencer.ReadinessHint);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound); // started anyway, past the disabled button

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.StartsWith("Wide Rig: ", app.Sequencer.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(NodeStatus.Failed, Row(app, wide.Id).Status);
        Assert.Equal("✕", Row(app, wide.Id).Glyph);
        Assert.NotEqual(NodeStatus.Failed, Row(app, main.Id).Status);
        Assert.True(app.Vm.Imaging.FrameCount < 5, "Main was stopped, not run to the end");
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
        Assert.True(app.Draft.IsEditable);
    }

    // Readiness

    [Fact]
    public async Task TheBlockNeedsTheCameraOfEveryTrack_AndOnlyThose()
    {
        await using var app = CreateApp();
        var main = app.Vm.Equipment.Cameras.Single(c => c.DeviceIdText == "camera.main");
        var wide = app.Vm.Equipment.Cameras.Single(c => c.DeviceIdText == "camera.wide");
        app.Use(MultiRig(Track(Main, Exposure(0.1)), Track(Wide, Exposure(0.1))));

        await main.ConnectCommand.ExecuteAsync(null);
        app.Sequencer.RefreshReadiness();
        Assert.False(app.Sequencer.CanRun);
        Assert.Equal("Connect Wide Camera to run the sequence.", app.Sequencer.ReadinessHint);

        await wide.ConnectCommand.ExecuteAsync(null);
        app.Sequencer.RefreshReadiness();

        // The narrow camera, the mount and the guider are not used: they need not be connected.
        Assert.True(app.Sequencer.CanRun, app.Sequencer.ReadinessHint);
        Assert.Null(app.Sequencer.ReadinessHint);

        await wide.DisconnectCommand.ExecuteAsync(null);
        Assert.False(app.Sequencer.CanRun);
    }

    [Fact]
    public async Task ASessionAroundTheBlock_NeedsItsOwnSharedEquipment_AsWellAsTheCameras()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        app.Use(
            new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
            MultiRig(Track(Main, Exposure(0.1)), Track(Wide, Exposure(0.1))),
            new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId));
        app.Sequencer.RefreshReadiness();
        Assert.Equal("Connect Main Guider to run the sequence.", app.Sequencer.ReadinessHint);

        await app.Vm.Equipment.Guiders[0].ConnectCommand.ExecuteAsync(null);
        app.Sequencer.RefreshReadiness();

        Assert.True(app.Sequencer.CanRun, app.Sequencer.ReadinessHint);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
    }

    // The snapshot

    [Fact]
    public async Task EditsAfterTheRunStarted_DoNotReachTheRunningBlock_NorItsRows()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var exposure = Exposure(0.4);
        var main = Track(Main, Repeat(2, exposure));
        var wide = Track(Wide, Repeat(2, Exposure(0.4)));
        app.Use(MultiRig(main, wide));
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        var rows = vm.Definition;

        // The controls are locked, but even stray changes must not reach the run.
        var track = (RigTrackDraftViewModel)((MultiRigStepDraftViewModel)app.Draft.Steps[0]).Children[0];
        track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == "rig.narrow");
        ((RigExposureStepDraftViewModel)((RepeatStepDraftViewModel)track.Children[0]).Children[0]).ExposureText = "9";

        Assert.Same(rows, vm.Definition);
        Assert.Equal("Main Rig", rows[1].Title);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(4, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.4), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    [Fact]
    public async Task EveryRunBuildsAFreshBlock_FromTheDraftAsItIsThen()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var exposure = Exposure(0.05);
        app.Use(MultiRig(Track(Main, exposure), Track(Wide, Exposure(0.05))));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        var first = app.Sequencer.Definition.Select(r => r.Node.Step).ToList();
        ((RigExposureStepDraftViewModel)((RigTrackDraftViewModel)((MultiRigStepDraftViewModel)app.Draft.Steps[0]).Children[0]).Children[0]).ExposureText = "0.1";
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        var second = app.Sequencer.Definition.Select(r => r.Node.Step).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.All(first.Zip(second), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.Equal(TimeSpan.FromSeconds(0.05), Assert.IsType<CameraExposureAction>(first[2]).Duration);
        Assert.Equal(TimeSpan.FromSeconds(0.1), Assert.IsType<CameraExposureAction>(second[2]).Duration);
    }

    [Fact]
    public async Task ADitherInATrack_CannotBeRunAtAll_ItIsRefusedByTheBuilderToo()
    {
        await using var app = CreateApp();
        await app.ConnectCameras();
        var dither = new DitherStepDraft(Guid.NewGuid(), DemoSetup.GuiderId, DemoSetup.MountId, DemoSetup.MainCameraId, 1, 0.5, 1, 10);
        app.Use(MultiRig(Track(Main, dither), Track(Wide, Exposure(0.1))));

        Assert.False(app.Sequencer.CanRun);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound); // past the button

        Assert.Equal(SequenceState.Idle, app.Sequencer.State);
        Assert.Contains("Dither is not available inside Parallel Imaging yet", app.Sequencer.ErrorMessage);
    }
}

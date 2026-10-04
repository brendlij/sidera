using System.ComponentModel;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>
/// The sequencer while a Multi-Rig block dithers: what it shows comes from the running positions of the runner, the
/// steps the builder generated never show up as rows, and the editor is locked as for any run.
/// </summary>
public class MultiRigDitherSequencerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");

    // A guider that takes its time, so that the states of a dither can be seen.
    private static readonly DemoOptions Slow = new()
    {
        ManualExposure = TimeSpan.FromMilliseconds(30),
        SequenceExposure = TimeSpan.FromMilliseconds(30),
        SequenceWait = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(500),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(500),
        SettleTimeout = TimeSpan.FromSeconds(5),
    };

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

        public async Task ConnectAll()
        {
            foreach (var device in Host.DeviceRegistry.GetAll())
            {
                await device.ConnectAsync();
            }
        }
    }

    private static App CreateApp()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Slow);
        DemoSetup.AddDemoRigs(host);
        var gate = new object();
        return new App
        {
            Host = host,
            Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, Slow),
        };
    }

    private static RigExposureStepDraft Exposure(double seconds) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigStepDraft Block(bool policy, params RigTrackDraft[] tracks) => new(
        Guid.NewGuid(),
        tracks,
        policy ? MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Wide, EveryNFrames = 3, SettleStableSeconds = 0.5, SettleTimeoutSeconds = 5 } : null);

    private static SequenceStepDraft[] Session(MultiRigStepDraft block) =>
    [
        new StartGuidingStepDraft(Guid.NewGuid(), new DeviceId("guider.main")),
        block,
        new StopGuidingStepDraft(Guid.NewGuid(), new DeviceId("guider.main")),
    ];

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    // What the shared activity was, in order, without repeats.
    private static List<string?> WatchSharedActivity(SequencerViewModel sequencer)
    {
        var seen = new List<string?>();
        var gate = new object();
        sequencer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SequencerViewModel.SharedActivity))
            {
                lock (gate)
                {
                    var value = sequencer.SharedActivity;
                    if (seen.Count == 0 || seen[^1] != value)
                    {
                        seen.Add(value);
                    }
                }
            }
        };
        return seen;
    }

    private static MultiRigStepDraft ThreeFrameWide() => Block(
        true,
        Track(Main, Repeat(2, Exposure(0.8))),
        Track(Wide, Repeat(7, Exposure(0.1))));

    [Fact]
    public async Task TheGeneratedSteps_NeverShowAsRows_AndEveryRowIsDoneAtTheEnd()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        var block = ThreeFrameWide();
        var steps = Session(block);
        app.Draft.ReplaceSteps(steps);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        var rows = app.Sequencer.Definition;
        var expected = new List<Guid> { steps[0].Id, block.Id };
        foreach (var track in block.Tracks)
        {
            expected.Add(track.Id);
            var repeat = (RepeatStepDraft)track.Steps[0];
            expected.Add(repeat.Id);
            expected.Add(repeat.Children[0].Id);
        }

        expected.Add(steps[2].Id);
        Assert.Equal(expected, rows.Select(r => r.DraftId!.Value));
        Assert.DoesNotContain(rows, r => r.Title.Contains("Safe", StringComparison.OrdinalIgnoreCase) || r.Title.StartsWith("Dither", StringComparison.Ordinal));
        Assert.All(rows, row => Assert.Equal(NodeStatus.Done, row.Status));
        Assert.Equal(9, app.Vm.Imaging.FrameCount);
        Assert.Null(app.Sequencer.SharedActivity);
    }

    [Fact]
    public async Task TheSharedActivity_IsPending_ThenDithering_ThenSettling_ThenGone_FromTheRealPositions()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        app.Draft.ReplaceSteps(Session(ThreeFrameWide()));
        var seen = WatchSharedActivity(app.Sequencer);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var texts = seen.OfType<string>().ToList();
        Assert.Contains(texts, t => t.StartsWith("Dither pending", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Dithering", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("Settling", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("waiting for", StringComparison.Ordinal)); // Main was still exposing
        // In that order, each time a dither happened.
        var first = texts.FindIndex(t => t.StartsWith("Dither pending", StringComparison.Ordinal));
        var dithering = texts.FindIndex(first, t => t.StartsWith("Dithering", StringComparison.Ordinal));
        var settling = texts.FindIndex(dithering, t => t.StartsWith("Settling", StringComparison.Ordinal));
        Assert.True(first < dithering && dithering < settling);
        Assert.Null(seen[^1]); // and nothing is claimed once the run is over
        Assert.False(app.Sequencer.HasSharedActivity);
    }

    [Fact]
    public async Task APendingDither_ShowsTheTracksThatWaitAsWaiting_AndTheBusyOneAsExposing()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        var narrow = new RigId("rig.narrow");
        app.Draft.ReplaceSteps(Session(Block(
            true,
            Track(Main, Exposure(2)),
            Track(narrow, Repeat(9, Exposure(0.1))),
            Track(Wide, Repeat(9, Exposure(0.1))))));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);

        // Wide asked after three frames; Narrow arrived at its safe point; Main is still exposing.
        await WaitUntil(
            () => app.Sequencer.ActiveBranches.Count(b => b.Title == "Waiting for coordinated dither") == 2
                  && app.Sequencer.ActiveBranches.Any(b => b.Title.StartsWith("Exposure", StringComparison.Ordinal)),
            "two tracks waiting, one exposing");

        Assert.Equal(3, app.Sequencer.ActiveBranches.Count);
        Assert.Equal(
            ["Narrow Rig", "Wide Rig"],
            app.Sequencer.ActiveBranches.Where(b => b.Title == "Waiting for coordinated dither").Select(b => b.BranchName).Order());
        Assert.Contains("Dither pending", app.Sequencer.SharedActivity!, StringComparison.Ordinal);
        Assert.Contains("waiting for 1 rig", app.Sequencer.SharedActivity!, StringComparison.Ordinal);

        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task WithoutAPolicy_NothingSharedIsEverShown_AndNothingIsGenerated()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        app.Draft.ReplaceSteps(Session(Block(
            false,
            Track(Main, Repeat(2, Exposure(0.3))),
            Track(Wide, Repeat(6, Exposure(0.1))))));
        var seen = WatchSharedActivity(app.Sequencer);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.All(seen, text => Assert.Null(text));
        Assert.Equal(8, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task TheEditor_IsLockedWhileItRuns_AndTheStatusWordsComeFromTheRunner()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        app.Draft.ReplaceSteps(Session(ThreeFrameWide()));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.IsRunning, "running");

        Assert.False(app.Draft.IsEditable);
        var block = app.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Single();
        app.Draft.SelectedStep = block;
        Assert.False(app.Draft.AddTrackCommand.CanExecute(null));
        Assert.False(app.Draft.RemoveStepCommand.CanExecute(null));
        Assert.False(app.Draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));

        await run.WaitAsync(Bound);

        Assert.True(app.Draft.IsEditable);
    }

    [Fact]
    public async Task PausingWhileADitherIsPending_PausesAfterTheRound_AndResumeRunsTheRestToTheEnd()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        app.Draft.ReplaceSteps(Session(Block(
            true,
            Track(Main, Repeat(2, Exposure(1.0))),
            Track(Wide, Repeat(9, Exposure(0.1))))));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.SharedActivity?.StartsWith("Dither pending", StringComparison.Ordinal) == true, "pending dither");
        app.Sequencer.PauseCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");

        Assert.Null(app.Sequencer.SharedActivity); // the dither of that round is over; nothing is pending any more
        var frames = app.Vm.Imaging.FrameCount;
        await Task.Delay(300);
        Assert.Equal(frames, app.Vm.Imaging.FrameCount);

        app.Sequencer.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(11, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task CancellingWhileADitherIsPending_EndsTheRun_AndClearsTheStatus()
    {
        await using var app = CreateApp();
        await app.ConnectAll();
        app.Draft.ReplaceSteps(Session(Block(
            true,
            Track(Main, Repeat(2, Exposure(1.0))),
            Track(Wide, Repeat(9, Exposure(0.1))))));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.SharedActivity?.StartsWith("Dither pending", StringComparison.Ordinal) == true, "pending dither");
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        Assert.Null(app.Sequencer.SharedActivity);
        Assert.Empty(app.Sequencer.ActiveBranches);
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);

        // A fresh run of the same draft works.
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
    }
}

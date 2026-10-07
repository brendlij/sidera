using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The sequencer while focuser moves and filter changes run: rows map to the draft, and the status is the runner's.</summary>
public class HardwareSequencerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly DemoOptions Slow = new()
    {
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        FocuserStepsPerSecond = 1000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(50),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(500),
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
    }

    private static async Task<App> CreateApp()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Slow);
        DemoSetup.AddDemoRigs(host, Slow);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var gate = new object();
        return new App { Host = host, Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, Slow) };
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

    private static RigTrackDraft Track(string rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), new RigId(rig), steps);

    [Fact]
    public async Task TheRowsOfARunMapToTheDraft_ForTopLevelAndRigLocalSteps_AndEveryRowIsDone()
    {
        await using var app = await CreateApp();
        var move = new MoveFocuserStepDraft(Guid.NewGuid(), DemoSetup.MainFocuserId, 19000);
        var change = new ChangeFilterStepDraft(Guid.NewGuid(), DemoSetup.MainFilterWheelId, 4);
        var rigChange = new RigChangeFilterStepDraft(Guid.NewGuid(), 2);
        var rigMove = new RigMoveFocuserStepDraft(Guid.NewGuid(), 19500);
        var exposure = new RigExposureStepDraft(Guid.NewGuid(), 0.1);
        var main = Track("rig.main", rigChange, rigMove, exposure);
        var wide = Track("rig.wide", new RigExposureStepDraft(Guid.NewGuid(), 0.1));
        var block = new MultiRigStepDraft(Guid.NewGuid(), [main, wide]);
        app.Draft.ReplaceSteps([move, change, block]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        var rows = app.Sequencer.Definition;
        Assert.Equal(
            [move.Id, change.Id, block.Id, main.Id, rigChange.Id, rigMove.Id, exposure.Id, wide.Id, wide.Steps[0].Id],
            rows.Select(r => r.DraftId!.Value));
        Assert.Equal(
            ["Move Focuser", "Change Filter", "Parallel Imaging", "Main Rig", "Change Filter", "Move Focuser", "Exposure", "Wide Rig", "Exposure"],
            rows.Select(r => r.Title));
        Assert.All(rows, row => Assert.Equal(NodeStatus.Done, row.Status));
    }

    [Fact]
    public async Task WhileARigMovesItsFocuser_TheSequencerShowsThatTrackMoving_AndTheOtherTrackExposing()
    {
        await using var app = await CreateApp();
        var main = Track("rig.main", new RigMoveFocuserStepDraft(Guid.NewGuid(), 19200), new RigExposureStepDraft(Guid.NewGuid(), 0.1));
        var wide = Track("rig.wide", new RepeatStepDraft(Guid.NewGuid(), 20, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)]));
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [main, wide])]);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(
            () => app.Sequencer.ActiveBranches.Any(b => b.BranchName == "Main Rig" && b.Title == "Move focuser to 19200")
                  && app.Sequencer.ActiveBranches.Any(b => b.BranchName == "Wide Rig" && b.Title.StartsWith("Exposure", StringComparison.Ordinal)),
            "Main moving its focuser while Wide exposes");

        Assert.Null(app.Sequencer.SharedActivity); // no dither policy: nothing is shared
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task APendingDither_ShowsAWaitingTrack_WhileAnotherStillChangesItsFilter()
    {
        await using var app = await CreateApp();
        var policy = MultiRigDitherPolicyDraft.Default with
        {
            Enabled = true, TriggerRigId = new RigId("rig.wide"), EveryNFrames = 2, SettleStableSeconds = 0.1, SettleTimeoutSeconds = 5,
        };
        var main = Track("rig.main", new RigChangeFilterStepDraft(Guid.NewGuid(), 4), new RigExposureStepDraft(Guid.NewGuid(), 0.2));
        var wide = Track("rig.wide", new RepeatStepDraft(Guid.NewGuid(), 6, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)]));
        app.Draft.ReplaceSteps(
        [
            new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
            new MultiRigStepDraft(Guid.NewGuid(), [main, wide], policy),
            new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
        ]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(
            () => app.Sequencer.SharedActivity?.StartsWith("Dither pending", StringComparison.Ordinal) == true
                  && app.Sequencer.SharedActivity.Contains("waiting for 1 imaging setup", StringComparison.Ordinal)
                  && app.Sequencer.ActiveBranches.Any(b => b.BranchName == "Main Rig" && b.Title == "Change filter to slot 4")
                  && app.Sequencer.ActiveBranches.Any(b => b.BranchName == "Wide Rig" && b.Title == "Waiting for coordinated dither"),
            "Wide waiting for Main's filter change");

        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Null(app.Sequencer.SharedActivity);
    }
}

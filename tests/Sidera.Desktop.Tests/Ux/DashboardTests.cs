using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>The dashboard answers one question, "what is Sidera doing right now?", with what the runtime really reports.</summary>
public class DashboardTests
{
    private static MultiRigStepDraft Block(int wideFrames, double seconds, bool dither = false)
    {
        var main = new RigTrackDraft(
            Guid.NewGuid(), new RigId("rig.main"),
            [new RepeatStepDraft(Guid.NewGuid(), 3, [new RigExposureStepDraft(Guid.NewGuid(), seconds)])],
            new RigAutofocusPolicyDraft(false, false, false, 0.1, 400, 7));
        var wide = new RigTrackDraft(
            Guid.NewGuid(), new RigId("rig.wide"),
            [new RepeatStepDraft(Guid.NewGuid(), wideFrames, [new RigExposureStepDraft(Guid.NewGuid(), seconds)])]);
        return new MultiRigStepDraft(
            Guid.NewGuid(), [main, wide],
            dither ? new MultiRigDitherPolicyDraft(true, new RigId("rig.wide"), 2, 0.6, 0.5, 0.1, 5) : null);
    }

    // No session

    [Fact]
    public async Task WithoutASession_TheDashboardIsIdle_AndSaysThereIsNothingToShow()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        app.Vm.SequenceDraft.ReplaceSteps([]);
        var dashboard = app.Vm.Dashboard;

        Assert.False(dashboard.HasSession);
        Assert.True(dashboard.IsIdle);
        Assert.False(dashboard.IsRunning);
        Assert.False(dashboard.ShowLanes);
        Assert.False(dashboard.ShowActivity);
        Assert.Equal("Idle", dashboard.StateText);
        Assert.Equal(StatusKind.Neutral, dashboard.StateKind);
        Assert.Equal("Untitled session", dashboard.Title);
        Assert.False(dashboard.HasElapsed);
        Assert.Equal("No sequence is running", dashboard.IdleTitle);
    }

    [Fact]
    public async Task ASessionThatIsLoadedButNotRunning_IsSummarisedAsItsStepCount()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        Assert.True(app.Vm.Dashboard.HasSession);
        Assert.Equal("8 steps", app.Vm.Dashboard.SessionSummary);
    }

    [Fact]
    public async Task TheSharedEquipment_IsTheMountAndTheGuider_WithTheirState()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);

        Assert.Equal(["Mount", "Guider"], app.Vm.Dashboard.SharedItems.Select(i => i.Role));
        Assert.All(app.Vm.Dashboard.SharedItems, i => Assert.Equal("Disconnected", i.StatusText));

        foreach (var device in app.Host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        await UxApp.WaitUntil(() => app.Vm.Dashboard.SharedItems.All(i => i.Kind == StatusKind.Ok), "shared equipment connected");
        Assert.Equal(["Idle", "Idle"], app.Vm.Dashboard.SharedItems.Select(i => i.StatusText));
    }

    // A single camera

    [Fact]
    public async Task ASimpleSequenceThatRuns_ShowsItsStep_NotLanes_AndCountsTime()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps(
        [
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.6),
            new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1),
        ]);
        var dashboard = app.Vm.Dashboard;

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => dashboard.Execution.HasActivity && dashboard.Execution.HasActivityCamera, "the exposure on the dashboard");

        Assert.True(dashboard.IsRunning);
        Assert.True(dashboard.ShowActivity);
        Assert.False(dashboard.ShowLanes);
        Assert.False(dashboard.IsIdle);
        Assert.Equal("Running", dashboard.StateText);
        Assert.Equal(StatusKind.Active, dashboard.StateKind);
        Assert.StartsWith("Exposure", dashboard.Execution.ActivityTitle);
        Assert.Equal("Step 1 / 2", dashboard.Execution.StepCounterText);
        Assert.True(dashboard.HasElapsed);
        Assert.Matches(@"^\d+:\d\d$", dashboard.ElapsedText);

        await run;
        Assert.Equal("Completed", dashboard.StateText);
        Assert.Equal(StatusKind.Ok, dashboard.StateKind);
        Assert.Equal("The last run completed", dashboard.IdleTitle);
        Assert.False(dashboard.IsRunning);
        Assert.True(dashboard.HasElapsed); // the time of the run stays after it
    }

    // Multi-Rig

    [Fact]
    public async Task AMultiRigRun_ShowsALanePerTrack_WithItsFramesAndItsCamera()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([Block(wideFrames: 6, seconds: 0.25)]);
        Assert.True(app.Vm.SequenceDraft.IsValid, string.Join(" ", app.Vm.SequenceDraft.ValidationErrors));
        var dashboard = app.Vm.Dashboard;

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => dashboard.Execution.Lanes.Count == 2 && dashboard.Execution.Lanes.All(l => l.HasFrames), "the frames of both lanes");

        Assert.True(dashboard.ShowLanes);
        Assert.False(dashboard.ShowActivity);
        Assert.False(dashboard.ShowUnits); // the lanes already say what each rig does
        Assert.Equal(["Main Rig", "Wide Rig"], dashboard.Execution.Lanes.Select(l => l.Name));
        Assert.Equal([new RigId("rig.main"), new RigId("rig.wide")], dashboard.Execution.Lanes.Select(l => l.RigId!.Value));
        var main = dashboard.Execution.Lanes[0];
        var wide = dashboard.Execution.Lanes[1];
        Assert.Matches(@"^Frame \d / 3$", main.FrameText);
        Assert.Matches(@"^Frame \d / 6$", wide.FrameText);
        Assert.All(dashboard.Execution.Lanes, l => Assert.Equal("Running", l.StatusText));
        Assert.All(dashboard.Execution.Lanes, l => Assert.NotNull(l.Rig));
        Assert.Contains("Main Camera", main.DescriptionText);

        await run;

        await UxApp.WaitUntil(() => dashboard.Execution.Lanes.All(l => l.IsDone), "both lanes done");
        Assert.Equal(["3 / 3 frames", "6 / 6 frames"], dashboard.Execution.Lanes.Select(l => l.FrameText));
        Assert.All(dashboard.Execution.Lanes, l => Assert.Equal(1.0, l.FrameProgress));
        Assert.True(dashboard.ShowLanes); // the result of the run stays until the session is edited
    }

    [Fact]
    public async Task TheFramesOfALane_AreOnlyThoseThatHaveEnded()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([Block(wideFrames: 8, seconds: 0.3)]);
        var wide = (TrackLaneViewModel?)null;

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Execution.Lanes.Count == 2 && app.Vm.Execution.Lanes[1].FrameProgress > 0, "the second frame of the wide lane");
        wide = app.Vm.Execution.Lanes[1];

        // "Frame 3 / 8" means two frames have ended: the bar is two eighths, not three.
        var shown = int.Parse(wide.FrameText.Split(' ')[1]);
        Assert.InRange(wide.FrameProgress, (shown - 1) / 8.0 - 0.001, (shown - 1) / 8.0 + 0.125);
        Assert.True(wide.FrameProgress < 1);

        await run;
    }

    [Fact]
    public async Task ACancelledMultiRigRun_SaysWhichTracksWereStopped()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([Block(wideFrames: 100, seconds: 0.2)]);
        var execution = app.Vm.Execution;

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => execution.Lanes.Count == 2 && execution.Lanes.All(l => l.IsActive), "both lanes running");
        app.Vm.Sequencer.CancelCommand.Execute(null);
        await run;

        Assert.Equal("Cancelled", app.Vm.Dashboard.StateText);
        Assert.Equal(StatusKind.Warning, app.Vm.Dashboard.StateKind);
        Assert.All(execution.Lanes, l => Assert.Equal("Stopped", l.StatusText));
        Assert.Equal("The last run was cancelled", app.Vm.Dashboard.IdleTitle);
    }

    [Fact]
    public async Task AFailedRun_ShowsItsErrorOnTheDashboard_WhereTheRunIs()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]);
        var camera = app.Vm.Equipment.Cameras[0];
        await camera.DisconnectCommand.ExecuteAsync(null); // the run starts anyway: readiness is a hint, the runtime decides

        app.Vm.Sequencer.RefreshReadiness();
        if (app.Vm.Sequencer.RunCommand.CanExecute(null))
        {
            await app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
            Assert.True(app.Vm.Dashboard.HasRunError);
            Assert.Contains("See the log, execution", app.Vm.Dashboard.RunError);
            Assert.Equal(StatusKind.Error, app.Vm.Dashboard.StateKind);
        }
        else
        {
            Assert.False(app.Vm.Dashboard.IsRunning); // a disconnected camera keeps the run from starting at all
        }
    }

    [Fact]
    public async Task ADither_IsSharedActivity_NotAnythingOfOneRig()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps(
        [
            new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
            Block(wideFrames: 6, seconds: 0.15, dither: true),
            new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId),
        ]);
        Assert.True(app.Vm.SequenceDraft.IsValid, string.Join(" ", app.Vm.SequenceDraft.ValidationErrors));

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Dashboard.SharedItems.Any(i => i.Role == "Dither"), "a dither on the dashboard");

        var dither = app.Vm.Dashboard.SharedItems.Single(i => i.Role == "Dither");
        Assert.Contains("dither", dither.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StatusKind.Active, dither.Kind);

        await run;
        await UxApp.WaitUntil(() => app.Vm.Dashboard.SharedItems.All(i => i.Role != "Dither"), "the dither gone");
    }

    [Fact]
    public async Task EditingTheSessionAfterARun_TakesTheLanesAwayAndShowsTheEquipmentAgain()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([Block(wideFrames: 2, seconds: 0.1)]);
        await app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        Assert.True(app.Vm.Dashboard.ShowLanes);

        app.Vm.SequenceDraft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 1)]);

        Assert.False(app.Vm.Dashboard.ShowLanes);
        Assert.True(app.Vm.Dashboard.ShowUnits);
    }

    [Fact]
    public async Task TheElapsedTime_IsReadAgainByRefreshElapsed()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        app.Vm.SequenceDraft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 0.3)]);

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Sequencer.RunStartedAt is not null, "the run started");
        app.Vm.Dashboard.RefreshElapsed();
        Assert.True(app.Vm.Dashboard.HasElapsed);
        await run;

        Assert.NotNull(app.Vm.Sequencer.RunEndedAt);
        Assert.True(app.Vm.Sequencer.Elapsed >= TimeSpan.FromMilliseconds(250));
    }
}

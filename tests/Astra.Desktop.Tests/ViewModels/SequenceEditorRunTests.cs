using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;
using Astra.Runtime;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop.Tests.ViewModels;

/// <summary>The editor together with the sequencer: runs built from the draft, the editing lock, the snapshot.</summary>
public class SequenceEditorRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>Short timings, so that whole runs of the demo take about a second.</summary>
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

    private sealed class App : IAsyncDisposable
    {
        public required AstraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }

        public async Task ConnectEverything()
        {
            await Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Mounts[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Guiders[0].ConnectCommand.ExecuteAsync(null);
        }

        public T Add<T>(SequenceStepKind kind) where T : StepDraftViewModel
        {
            Draft.AddStepCommand.Execute(kind);
            return Assert.IsType<T>(Draft.SelectedStep);
        }

        /// <summary>Replaces the draft by exposures of the given length (in seconds), one per entry; null is a delay.</summary>
        public void ReplaceDraftWith(params string[] kinds)
        {
            while (Draft.Steps.Count > 0)
            {
                Draft.SelectedStep = Draft.Steps[0];
                Draft.RemoveStepCommand.Execute(null);
            }

            foreach (var kind in kinds)
            {
                Draft.AddStepCommand.Execute(Enum.Parse<SequenceStepKind>(kind));
            }
        }
    }

    private static App Create(DemoOptions? options = null)
    {
        var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, options ?? Fast);
        return new App { Host = host, Vm = new MainViewModel(host, action => action(), options ?? Fast) };
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

    private static (bool Run, bool Pause, bool Resume, bool Cancel) Buttons(SequencerViewModel vm) =>
        (vm.RunCommand.CanExecute(null), vm.PauseCommand.CanExecute(null),
         vm.ResumeCommand.CanExecute(null), vm.CancelCommand.CanExecute(null));

    // The editing controls: add, remove, move up, move down (remove and move need a selected step).
    private static (bool Add, bool Remove, bool Up, bool Down) EditControls(SequenceDraftViewModel draft) =>
        (draft.AddStepCommand.CanExecute(SequenceStepKind.Delay), draft.RemoveStepCommand.CanExecute(null),
         draft.MoveStepUpCommand.CanExecute(null), draft.MoveStepDownCommand.CanExecute(null));

    // Running the draft

    [Fact]
    public async Task TheDemoDraft_RunsToCompletion_OnTheSimulatedEquipment_IncludingDithersInALinearSequence()
    {
        await using var app = Create();
        await app.ConnectEverything();

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Null(app.Sequencer.ErrorMessage);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task ACustomSequence_RunsTheStepsInTheOrderOfTheDraft()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Delay", "Exposure");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "0.04";
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().Last().ExposureText = "0.07";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.07), app.Vm.Imaging.LatestFrame!.ExposureDuration); // the last one shown
    }

    [Fact]
    public async Task Run_IsUnavailable_ForAnEmptyDraft_AndTheEmptyStateIsShown()
    {
        await using var app = Create();
        await app.ConnectEverything();
        Assert.True(app.Sequencer.CanRun);

        app.ReplaceDraftWith();

        Assert.True(app.Draft.IsEmpty);
        Assert.False(app.Sequencer.CanRun);
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));
        Assert.True(app.Sequencer.IsEmpty);
        Assert.Empty(app.Sequencer.Definition);
    }

    [Fact]
    public async Task Run_OfAnEmptyDraftBypassingTheButton_DoesNotStart_AndDoesNotCrash()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith();

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Idle, app.Sequencer.State);
        Assert.Equal("The sequence has no steps.", app.Sequencer.ErrorMessage);
        Assert.True(app.Draft.IsEditable);
    }

    [Fact]
    public async Task Run_IsUnavailable_WhileAnyStepIsInvalid_AndAvailableAgainOnceFixed()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();

        exposure.ExposureText = "abc";
        Assert.False(app.Sequencer.RunCommand.CanExecute(null));

        exposure.ExposureText = "0.03";
        Assert.True(app.Sequencer.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task Run_WithAnInvalidDraft_BypassingTheButton_DoesNotStartTheRunner_AndStaysEditable()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Add<DelayStepDraftViewModel>(SequenceStepKind.Delay).DurationText = "0";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Idle, app.Sequencer.State);
        Assert.Equal("Step 9 (Delay): Delay must be greater than 0 s.", app.Sequencer.ErrorMessage);
        Assert.True(app.Draft.IsEditable);
        Assert.Equal(0, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task Run_NeedsEveryDeviceOfTheDraftToBeConnected_AndSaysWhichOne()
    {
        await using var app = Create();
        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
        await app.Vm.Equipment.Mounts[0].ConnectCommand.ExecuteAsync(null);

        Assert.False(app.Sequencer.CanRun);
        Assert.Equal("Connect Main Guider to run the sequence.", app.Sequencer.ReadinessHint);

        // A draft that does not use the guider does not need it.
        app.ReplaceDraftWith("Slew", "Exposure");
        app.Sequencer.RefreshReadiness();

        Assert.True(app.Sequencer.CanRun);
        Assert.Null(app.Sequencer.ReadinessHint);
    }

    [Fact]
    public async Task APickedDeviceThatIsNotRegisteredAnyMore_DisablesRun_AndIsMarkedOnItsRow()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();
        Assert.True(app.Sequencer.CanRun);

        app.Host.DeviceRegistry.Unregister(new DeviceId("camera.main"));
        app.Sequencer.RefreshReadiness();

        Assert.False(app.Sequencer.CanRun);
        Assert.True(exposure.HasProblems);
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        var row = app.Sequencer.Definition.First(n => n.DraftId == exposure.Id);
        Assert.True(row.IsProblem);
        Assert.Equal("The camera 'camera.main' is not available.", row.SubText);
    }

    // The outline while idle: the draft as rows

    [Fact]
    public async Task WhileIdle_TheOutlineIsTheDraft_AndFollowsEveryEdit()
    {
        await using var app = Create(new DemoOptions());
        Assert.Equal(
            new[] { "Start Guiding", "Slew", "Exposure", "Dither", "Exposure", "Dither", "Exposure", "Stop Guiding" },
            app.Sequencer.Definition.Select(n => n.Title));
        Assert.Equal(Enumerable.Range(1, 8).Select(n => $"{n}."), app.Sequencer.Definition.Select(n => n.NumberText));
        Assert.Equal(app.Draft.Steps.Select(s => s.Id), app.Sequencer.Definition.Select(n => n.DraftId!.Value));

        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();
        exposure.ExposureText = "300";
        Assert.Equal("Main Camera · 300 s · Camera defaults", app.Sequencer.Definition[2].Detail);

        app.Draft.SelectedStep = app.Draft.Steps[1];
        app.Draft.MoveStepDownCommand.Execute(null);
        Assert.Equal("Slew", app.Sequencer.Definition[2].Title);
        Assert.Equal("Exposure", app.Sequencer.Definition[1].Title);
    }

    [Fact]
    public async Task AnInvalidRow_ShowsItsProblemAsSubTextOfTheOutline()
    {
        await using var app = Create(new DemoOptions());
        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();

        exposure.ExposureText = "abc";

        var row = app.Sequencer.Definition[2];
        Assert.True(row.IsProblem);
        Assert.Equal("Exposure must be a number of seconds.", row.SubText);
        Assert.Equal(string.Empty, row.Detail);
    }

    // The editing lock

    [Fact]
    public async Task EditingControls_AreAvailableWhileIdle_LockedWhileRunningPausingAndPaused_AndFreeAfterwards()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Delay", "Exposure");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        app.Draft.SelectedStep = app.Draft.Steps[1];
        Assert.Equal((true, true, true, true), EditControls(app.Draft));
        Assert.True(app.Draft.IsEditable);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        Assert.Equal((false, false, false, false), EditControls(app.Draft));
        Assert.False(app.Draft.IsEditable);

        app.Sequencer.PauseCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Pausing, "pausing");
        Assert.Equal((false, false, false, false), EditControls(app.Draft));
        Assert.False(app.Draft.IsEditable);

        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, false, false), EditControls(app.Draft));
        Assert.False(app.Draft.IsEditable);

        app.Sequencer.ResumeCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running again");
        Assert.False(app.Draft.IsEditable);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.True(app.Draft.IsEditable);
        Assert.Equal((true, true, true, true), EditControls(app.Draft));
    }

    [Fact]
    public async Task EditingControls_AreFreeAgainAfterACancelledRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().Single().ExposureText = "0.3";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        Assert.False(app.Draft.IsEditable);
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        Assert.True(app.Draft.IsEditable);
        Assert.True(app.Draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
    }

    [Fact]
    public async Task EditingControls_AreFreeAgainAfterAFailedRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        // The guide error can never get below this threshold within the timeout: the settle wait fails.
        app.ReplaceDraftWith("StartGuiding", "Dither");
        var dither = app.Draft.Steps.OfType<DitherStepDraftViewModel>().Single();
        dither.SettleThresholdText = "0.31";
        dither.SettleStableText = "0.1";
        dither.SettleTimeoutText = "0.3";
        dither.AmplitudeText = "5";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.StartsWith("Guiding did not settle within the time limit. See the log, execution ", app.Sequencer.ErrorMessage);
        Assert.True(app.Draft.IsEditable);
        Assert.True(app.Draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
    }

    // The snapshot: draft against active sequence

    [Fact]
    public async Task EditsAfterTheRunStarted_DoNotReachTheRunningSequence_NorItsLabels()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure");
        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().Single();
        exposure.ExposureText = "0.4";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        var running = app.Sequencer.Definition;

        // The controls are locked, but even stray changes must not reach the run.
        exposure.ExposureText = "9";
        app.Draft.Steps.Add(new DelayStepDraftViewModel(new DelayStepDraft(Guid.NewGuid(), 5)));

        Assert.Same(running, app.Sequencer.Definition);
        var row = Assert.Single(app.Sequencer.Definition);
        Assert.Equal("Exposure", row.Title);
        Assert.Equal("Main Camera · 0.4 s · Camera defaults", row.Detail);
        Assert.Equal(exposure.Id, row.DraftId);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.4), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    [Fact]
    public async Task EachRun_BuildsAFreshRuntimeSequence_FromTheDraftAsItIsThen()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Delay");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().Single().ExposureText = "0.03";
        app.Draft.Steps.OfType<DelayStepDraftViewModel>().Single().DurationText = "0.03";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        var first = app.Sequencer.Definition.Select(n => n.Node.Step).ToList();
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);

        app.Draft.Steps.OfType<DelayStepDraftViewModel>().Single().DurationText = "0.05";
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        var second = app.Sequencer.Definition.Select(n => n.Node.Step).ToList();

        Assert.Equal(2, first.Count);
        Assert.All(first.Zip(second), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.Equal(TimeSpan.FromSeconds(0.03), Assert.IsType<DelayAction>(first[1]).Duration);
        Assert.Equal(TimeSpan.FromSeconds(0.05), Assert.IsType<DelayAction>(second[1]).Duration);
    }

    [Fact]
    public async Task WhileTheRunIsPaused_AStrayEditStillDoesNotChangeWhatResumes()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Exposure");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        app.Sequencer.PauseCommand.Execute(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");

        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().Last().ExposureText = "7";
        app.Sequencer.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(TimeSpan.FromSeconds(0.3), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    // Execution status mapped back to the draft

    [Fact]
    public async Task ExecutionStatus_IsShownOnTheRowOfTheDraftStepThatRuns()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Delay", "Exposure");
        var steps = app.Draft.Steps.ToList();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.03");
        app.Draft.Steps.OfType<DelayStepDraftViewModel>().Single().DurationText = "0.5";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 3 && app.Sequencer.Definition[1].IsActive, "the delay runs");

        var rows = app.Sequencer.Definition;
        Assert.Equal(steps.Select(s => s.Id), rows.Select(r => r.DraftId!.Value));
        Assert.Equal(
            new[] { NodeStatus.Done, NodeStatus.Active, NodeStatus.Pending },
            rows.Select(r => r.Status));
        Assert.Equal(new[] { "✓", "●", "○" }, rows.Select(r => r.Glyph));
        await run.WaitAsync(Bound);

        Assert.All(app.Sequencer.Definition, r => Assert.Equal(NodeStatus.Done, r.Status));
    }

    [Fact]
    public async Task ADitherInALinearSequence_ShowsItsOwnSettleStepAsANoteOnItsRow()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("StartGuiding", "Dither");
        var dither = app.Draft.Steps.OfType<DitherStepDraftViewModel>().Single();
        dither.AmplitudeText = "0.6";
        dither.SettleStableText = "0.3";
        dither.SettleTimeoutText = "5";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 2 && app.Sequencer.Definition[1].IsActive, "the dither runs");
        await WaitUntil(() => app.Sequencer.Definition[1].HasNote || app.Sequencer.State != SequenceState.Running, "a note");
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(dither.Id, app.Sequencer.Definition[1].DraftId);
    }

    // Regression of the buttons

    [Fact]
    public async Task PauseResumeAndCancel_StillFollowTheState_WithTheEditorInPlace()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.ReplaceDraftWith("Exposure", "Exposure");
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        var vm = app.Sequencer;
        Assert.Equal((true, false, false, false), Buttons(vm));

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.Equal((false, true, false, true), Buttons(vm));

        vm.PauseCommand.Execute(null);
        Assert.Equal((false, false, false, true), Buttons(vm));
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, true, true), Buttons(vm));

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.False(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeviceControlsOfTheEquipmentPage_StillWorkNextToTheEditor()
    {
        await using var app = Create();

        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);

        Assert.True(app.Vm.Equipment.Cameras[0].IsConnected);
        Assert.True(app.Vm.Equipment.Cameras[0].StartExposureCommand.CanExecute(null));
        Assert.True(app.Draft.IsEditable);
    }
}

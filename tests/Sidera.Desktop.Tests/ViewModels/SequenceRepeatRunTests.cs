using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Repeat together with the sequencer: readiness, execution mapping, pause, the editing lock and the snapshot.</summary>
public class SequenceRepeatRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

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
        public required SideraRuntimeHost Host { get; init; }
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

        public void Clear()
        {
            while (Draft.Steps.Count > 0)
            {
                Draft.SelectedStep = Draft.Steps[0];
                Draft.RemoveStepCommand.Execute(null);
            }
        }

        public T Top<T>(SequenceStepKind kind) where T : StepDraftViewModel
        {
            Draft.AddStepCommand.Execute(kind);
            return Assert.IsType<T>(Draft.SelectedStep);
        }

        public T Child<T>(SequenceStepKind kind) where T : StepDraftViewModel
        {
            Draft.AddChildCommand.Execute(kind);
            return Assert.IsType<T>(Draft.SelectedStep);
        }

        /// <summary>The draft becomes one Repeat of the given count with the given children (seconds; null = delay).</summary>
        public (RepeatStepDraftViewModel Repeat, ExposureStepDraftViewModel Exposure, DelayStepDraftViewModel? Delay) OneRepeat(
            int count, double exposureSeconds, double? delaySeconds)
        {
            Clear();
            var repeat = Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
            repeat.CountText = count.ToString();
            var exposure = Child<ExposureStepDraftViewModel>(SequenceStepKind.Exposure);
            exposure.ExposureText = exposureSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DelayStepDraftViewModel? delay = null;
            if (delaySeconds is { } d)
            {
                delay = Child<DelayStepDraftViewModel>(SequenceStepKind.Delay);
                delay.DurationText = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return (repeat, exposure, delay);
        }
    }

    private static App Create()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        return new App { Host = host, Vm = new MainViewModel(host, action => action(), Fast) };
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

    private static SequenceNodeViewModel Row(App app, StepDraftViewModel step) =>
        app.Sequencer.Definition.Single(row => row.DraftId == step.Id);

    // Readiness: the devices inside a Repeat count

    [Fact]
    public async Task ADeviceInsideARepeat_MustBeConnected_ForTheSequenceToBeRunnable()
    {
        await using var app = Create();
        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
        await app.Vm.Equipment.Mounts[0].ConnectCommand.ExecuteAsync(null);
        app.Clear();
        app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        app.Child<ExposureStepDraftViewModel>(SequenceStepKind.Exposure);
        app.Sequencer.RefreshReadiness();
        Assert.True(app.Sequencer.CanRun);

        // The guider is used only inside the Repeat, and is not connected.
        app.Child<DitherStepDraftViewModel>(SequenceStepKind.Dither);
        app.Sequencer.RefreshReadiness();

        Assert.False(app.Sequencer.CanRun);
        Assert.Equal("Connect Main Guider to run the sequence.", app.Sequencer.ReadinessHint);
    }

    [Fact]
    public async Task ADeviceInsideARepeat_ThatIsDisconnectedLater_TakesTheRunAway()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.OneRepeat(2, 0.03, 0.03);
        app.Sequencer.RefreshReadiness();
        Assert.True(app.Sequencer.CanRun);

        await app.Vm.Equipment.Cameras[0].DisconnectCommand.ExecuteAsync(null);

        Assert.False(app.Sequencer.CanRun);
        Assert.Equal("Connect Main Camera to run the sequence.", app.Sequencer.ReadinessHint);
    }

    [Fact]
    public async Task ADeviceThatOnlyAnotherPartOfTheDraftUses_DoesNotMatter()
    {
        await using var app = Create();
        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
        app.OneRepeat(2, 0.03, 0.03); // camera only; mount and guider stay disconnected
        app.Sequencer.RefreshReadiness();

        Assert.True(app.Sequencer.CanRun);
    }

    // Running a Repeat

    [Fact]
    public async Task ARepeat_RunsItsStepsInOrder_AsOftenAsItsCount()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Clear();
        app.Top<StartGuidingStepDraftViewModel>(SequenceStepKind.StartGuiding);
        var repeat = app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        repeat.CountText = "3";
        app.Child<ExposureStepDraftViewModel>(SequenceStepKind.Exposure).ExposureText = "0.03";
        app.Child<DitherStepDraftViewModel>(SequenceStepKind.Dither);
        app.Top<StopGuidingStepDraftViewModel>(SequenceStepKind.StopGuiding);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Null(app.Sequencer.ErrorMessage);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task TheRunTree_IsARuntimeRepeatOfAGroup_AndTheRowsFollowTheDraftStructure()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, exposure, delay) = app.OneRepeat(2, 0.03, 0.03);
        app.Draft.SelectedStep = repeat;

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var rows = app.Sequencer.Definition;
        Assert.Equal(3, rows.Count); // the group around the steps is not listed
        Assert.IsType<RepeatStep>(rows[0].Node.Step);
        Assert.Equal(new[] { repeat.Id, exposure.Id, delay!.Id }, rows.Select(r => r.DraftId!.Value));
        Assert.Equal(["Repeat × 2", "Exposure", "Delay"], rows.Select(r => r.Title));
        Assert.Equal(["1.", "1.1", "1.2"], rows.Select(r => r.NumberText));
        Assert.True(rows[1].IndentWidth > rows[0].IndentWidth);
        Assert.Equal(rows[1].IndentWidth, rows[2].IndentWidth);
    }

    // Execution mapping and iteration status

    [Fact]
    public async Task TheRunningRepeatAndTheRunningChildAreFound_ByDraftId_WithTheIterationShown()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, exposure, delay) = app.OneRepeat(2, 0.03, 0.5);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 3 && Row(app, delay!).IsActive, "the delay of the first repetition");

        Assert.Equal(NodeStatus.Active, Row(app, repeat).Status);
        Assert.Equal("iteration 1 / 2", Row(app, repeat).IterationText);
        Assert.Equal(NodeStatus.Done, Row(app, exposure).Status);
        Assert.Equal(NodeStatus.Active, Row(app, delay!).Status);

        // The second repetition starts again with the first step; what was done before is pending again.
        await WaitUntil(() => Row(app, exposure).IsActive || Row(app, repeat).IterationText == "iteration 2 / 2", "the second repetition");
        await WaitUntil(() => Row(app, repeat).IterationText == "iteration 2 / 2", "iteration 2");
        Assert.Equal(NodeStatus.Active, Row(app, repeat).Status);
        Assert.NotEqual(NodeStatus.Done, Row(app, delay!).Status);

        await run.WaitAsync(Bound);
        Assert.All(app.Sequencer.Definition, r => Assert.Equal(NodeStatus.Done, r.Status));
        Assert.All(app.Sequencer.Definition, r => Assert.Null(r.IterationText));
    }

    [Fact]
    public async Task TheMapping_DoesNotDependOnTheLabels_TwoIdenticalStepsAreToldApart()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Clear();
        var repeat = app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        repeat.CountText = "1";
        var first = app.Child<DelayStepDraftViewModel>(SequenceStepKind.Delay);
        var second = app.Child<DelayStepDraftViewModel>(SequenceStepKind.Delay);
        first.DurationText = "0.4";
        second.DurationText = "0.4"; // the same title and the same summary

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 3 && Row(app, first).IsActive, "the first delay");
        Assert.Equal(NodeStatus.Pending, Row(app, second).Status);
        await WaitUntil(() => Row(app, second).IsActive, "the second delay");
        Assert.Equal(NodeStatus.Done, Row(app, first).Status);
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task ADitherInsideARepeat_IsFoundByDraftId_AndRunsToCompletionWithoutCoordination()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Clear();
        app.Top<StartGuidingStepDraftViewModel>(SequenceStepKind.StartGuiding);
        app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        var dither = app.Child<DitherStepDraftViewModel>(SequenceStepKind.Dither);
        dither.SettleStableText = "0.3";
        dither.SettleTimeoutText = "5";

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.Definition.Count == 3 && Row(app, dither).IsActive, "the dither");
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Null(app.Sequencer.ErrorMessage);
    }

    // Pause and resume

    [Fact]
    public async Task Pause_DuringAChild_LetsItFinish_StartsNoNextChild_AndResumeContinuesWithIt()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (_, exposure, delay) = app.OneRepeat(2, 0.4, 0.05);
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Definition.Count == 3 && Row(app, exposure).IsActive, "the first exposure");
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        Assert.Equal(1, app.Vm.Imaging.FrameCount);                 // the exposure finished
        Assert.NotEqual(NodeStatus.Active, Row(app, delay!).Status); // the delay did not start
        Assert.Equal(NodeStatus.Done, Row(app, exposure).Status);
        await Task.Delay(150);
        Assert.Equal(SequenceState.Paused, vm.State);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task Pause_DuringTheLastChildOfARepetition_StartsNoNextRepetition_AndResumeContinuesWithIt()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, exposure, delay) = app.OneRepeat(2, 0.03, 0.5);
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Definition.Count == 3 && Row(app, delay!).IsActive, "the first delay");
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        Assert.Equal(1, app.Vm.Imaging.FrameCount);                  // no second exposure yet
        Assert.Equal("iteration 1 / 2", Row(app, repeat).IterationText);
        await Task.Delay(150);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
        _ = exposure;
    }

    [Fact]
    public async Task Pause_BetweenTwoRepetitionsOfASingleStep_ResumesWithTheNextRepetition()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (_, exposure, _) = app.OneRepeat(3, 0.4, null);
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Definition.Count == 2 && Row(app, exposure).IsActive, "the first exposure");
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal(1, app.Vm.Imaging.FrameCount);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task CancelWhilePausedInARepeat_EndsTheRun_AndReleasesEverything()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (_, exposure, _) = app.OneRepeat(3, 0.4, null);
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Definition.Count == 2 && Row(app, exposure).IsActive, "the first exposure");
        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.Equal(1, app.Vm.Imaging.FrameCount);
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
        Assert.True(app.Draft.IsEditable);
        Assert.True(vm.RunCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelDuringALaterRepetition_EndsTheRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, _, _) = app.OneRepeat(3, 0.3, null);
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Definition.Count == 2 && Row(app, repeat).IterationText == "iteration 2 / 3", "iteration 2");
        vm.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, vm.State);
        Assert.True(app.Vm.Imaging.FrameCount < 3);
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
    }

    // The editing lock

    [Fact]
    public async Task NestedEditing_IsLockedWhileRunningPausingAndPaused_AndFreeAfterwards()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, exposure, delay) = app.OneRepeat(2, 0.3, 0.05);
        app.Draft.SelectedStep = delay;
        var vm = app.Sequencer;

        (bool Add, bool AddChild, bool Remove, bool Up, bool Down) Controls() =>
            (app.Draft.AddStepCommand.CanExecute(SequenceStepKind.Repeat),
             app.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay),
             app.Draft.RemoveStepCommand.CanExecute(null),
             app.Draft.MoveStepUpCommand.CanExecute(null),
             app.Draft.MoveStepDownCommand.CanExecute(null));

        Assert.Equal((true, true, true, true, false), Controls()); // the delay is the last child

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.Equal((false, false, false, false, false), Controls());
        Assert.False(app.Draft.IsEditable);
        Assert.False(app.Draft.CanAddChildHere);

        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Pausing, "pausing");
        Assert.Equal((false, false, false, false, false), Controls());
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, false, false, false), Controls());
        Assert.False(app.Draft.IsEditable);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.True(app.Draft.IsEditable);
        Assert.Equal((true, true, true, true, false), Controls());
        _ = (repeat, exposure);
    }

    [Fact]
    public async Task NestedEditing_IsFreeAgainAfterACancelledRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (_, exposure, _) = app.OneRepeat(2, 0.4, null);
        app.Draft.SelectedStep = exposure;

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        Assert.False(app.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        Assert.True(app.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));
        Assert.True(app.Draft.RemoveStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task NestedEditing_IsFreeAgainAfterAFailedRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Clear();
        app.Top<StartGuidingStepDraftViewModel>(SequenceStepKind.StartGuiding);
        app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        var dither = app.Child<DitherStepDraftViewModel>(SequenceStepKind.Dither);
        // The guide error can never get below this threshold within the timeout: the settle wait fails.
        dither.SettleThresholdText = "0.31";
        dither.SettleStableText = "0.1";
        dither.SettleTimeoutText = "0.3";
        dither.AmplitudeText = "5";

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.True(app.Draft.IsEditable);
        Assert.True(app.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));
    }

    // Snapshot

    [Fact]
    public async Task EditsAfterTheRunStarted_DoNotReachTheRunningRepeat_NorItsRows()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, exposure, _) = app.OneRepeat(2, 0.3, null);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        var rows = app.Sequencer.Definition;

        repeat.CountText = "9";          // the controls are locked, but stray changes must not reach the run
        exposure.ExposureText = "7";
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Delay);

        Assert.Same(rows, app.Sequencer.Definition);
        Assert.Equal("Repeat × 2", rows[0].Title);
        Assert.Equal("Main Camera · 0.3 s · Camera defaults", rows[1].Detail);
        Assert.Equal(2, rows.Count);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(0.3), app.Vm.Imaging.LatestFrame!.ExposureDuration);
    }

    [Fact]
    public async Task ChangingTheCount_AndRunningAgain_BuildsANewRuntimeSequenceWithTheNewCount()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var (repeat, _, _) = app.OneRepeat(2, 0.03, null);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        var first = Assert.IsType<RepeatStep>(app.Sequencer.Definition[0].Node.Step);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);

        repeat.CountText = "4";
        Assert.Equal("Repeat × 4", app.Sequencer.Definition[0].Title);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var second = Assert.IsType<RepeatStep>(app.Sequencer.Definition[0].Node.Step);
        Assert.NotSame(first, second);
        Assert.Equal(4, second.Count);
        Assert.Equal(2 + 4, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task Run_WithAnEmptyRepeat_IsUnavailable_AndBypassingTheButtonDoesNotStart()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Clear();
        app.Top<RepeatStepDraftViewModel>(SequenceStepKind.Repeat);
        app.Sequencer.RefreshReadiness();

        Assert.False(app.Sequencer.CanRun);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Idle, app.Sequencer.State);
        Assert.Equal("Step 1 (Repeat): Repeat must contain at least one step.", app.Sequencer.ErrorMessage);
    }

    [Fact]
    public async Task ARunWithoutRepeat_StillBehavesAsBefore_TheLinearDemoCompletes()
    {
        await using var app = Create();
        await app.ConnectEverything();

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
        Assert.Equal(8, app.Sequencer.Definition.Count);
        Assert.All(app.Sequencer.Definition, r => Assert.Null(r.IterationText));
    }
}

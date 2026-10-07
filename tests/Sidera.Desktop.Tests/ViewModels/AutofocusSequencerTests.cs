using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The sequencer while an autofocus runs: what it reports, what it shows afterwards, and what stays locked.</summary>
public class AutofocusSequencerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly RigId Main = new("rig.main");

    private static readonly DemoOptions Fast = new()
    {
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(50),
    };

    private sealed class App : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public IFocuser Focuser(string id) => (IFocuser)Host.DeviceRegistry.GetAll().Single(d => d.Id.Value == id);

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static async Task<App> CreateApp()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        DemoSetup.AddDemoRigs(host, Fast);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var gate = new object();
        return new App { Host = host, Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, Fast) };
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

    private static AutofocusStepDraft Autofocus(string rig = "rig.main", double seconds = 0.1) =>
        new(Guid.NewGuid(), new RigId(rig), seconds, 400, 7);

    private static AutofocusStatusViewModel? StatusOf(App app, string rig) =>
        app.Sequencer.AutofocusStatuses.FirstOrDefault(s => s.RigId.Value == rig);

    // The status on its own

    [Fact]
    public void TheStatus_ShowsOnlyWhatTheRunReported_InTheWordsOfTheSpec()
    {
        var status = new AutofocusStatusViewModel(Main, "Main Rig");

        Assert.Equal("AUTOFOCUS · MAIN", status.Title);
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 1, 0, 7));
        Assert.Equal(["Sampling 7 focus positions"], status.Lines);
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 1, 4, 7, 20100, 2.14));
        Assert.Equal(["Sample 4 / 7", "Position 20100", "HFR 2.14 px"], status.Lines);
        Assert.True(status.IsActive);
        status.Apply(new AutofocusProgress(AutofocusPhase.Fitting, 1, 7, 7));
        Assert.Equal(["Fitting focus curve"], status.Lines);
        status.Apply(new AutofocusProgress(AutofocusPhase.Moving, 1, 0, 0, BestPosition: 19984));
        Assert.Equal(["Moving to best focus", "19984 steps"], status.Lines);
        status.Apply(new AutofocusProgress(AutofocusPhase.Completed, 1, 0, 0, 19984, 1.82, 19984, 1.82));
        Assert.Equal(["Best focus", "19984 steps", "HFR", "1.82 px"], status.Lines);
        Assert.Equal((true, false, 19984, 1.82), (status.IsCompleted, status.IsActive, status.BestPosition, status.BestHfr));
    }

    [Fact]
    public void ASecondPattern_SaysSo_AndAllSamplesAreKept()
    {
        var status = new AutofocusStatusViewModel(Main, "Main Rig");
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 1, 0, 7));
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 1, 1, 7, 100, 3.0));
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 2, 0, 7));
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 2, 1, 7, 500, 2.0));

        Assert.Equal(["Sample 1 / 7", "Position 500", "HFR 2.00 px", "Pass 2"], status.Lines);
        Assert.Equal([(100, 3.0), (500, 2.0)], status.Measurements.Select(m => (m.FocuserPosition, m.Hfr)));
    }

    [Fact]
    public void AStoppedRun_IsShownAsStopped_AndKeepsItsSamples()
    {
        var status = new AutofocusStatusViewModel(Main, "Main Rig");
        status.Apply(new AutofocusProgress(AutofocusPhase.Measuring, 1, 1, 7, 100, 3.0));

        status.Apply(new AutofocusProgress(AutofocusPhase.Stopped, 0, 0, 0));

        Assert.Equal(["Autofocus stopped"], status.Lines);
        Assert.False(status.IsActive);
        Assert.False(status.IsCompleted);
        Assert.Single(status.Measurements);
    }

    // In a run

    [Fact]
    public async Task ARunShowsTheSamplesAsTheyAreTaken_AndTheBestFocusAfterwards()
    {
        await using var app = await CreateApp();
        app.Draft.ReplaceSteps([Autofocus(seconds: 0.15)]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var lines = new List<string>();

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        while (!run.IsCompleted)
        {
            if (StatusOf(app, "rig.main") is { } status)
            {
                foreach (var line in status.Lines.Where(l => l.StartsWith("Sample ", StringComparison.Ordinal) || l.StartsWith("Position ", StringComparison.Ordinal) || l.StartsWith("HFR ", StringComparison.Ordinal)))
                {
                    if (!lines.Contains(line))
                    {
                        lines.Add(line);
                    }
                }
            }

            await Task.Delay(5);
        }

        await run;
        var done = StatusOf(app, "rig.main")!;
        Assert.True(done.IsCompleted);
        Assert.InRange(done.BestPosition!.Value, DemoSetup.MainBestFocus - 100, DemoSetup.MainBestFocus + 100);
        Assert.InRange(done.BestHfr!.Value, 1.6, 2.0);
        Assert.Equal([AutofocusStatusViewModel.ManualOrigin, "Best focus", $"{done.BestPosition} steps", "HFR", $"{done.BestHfr!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} px"], done.Lines);
        Assert.Equal(14, done.Measurements.Count); // two patterns from 1800 steps away
        Assert.Contains(lines, l => l.StartsWith("Sample ", StringComparison.Ordinal)); // seen while running
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.True(app.Sequencer.HasAutofocus);
    }

    [Fact]
    public async Task InAMultiRigRun_OnlyTheRigThatFocusesHasAStatus_AndItIsNamedAfterTheRig()
    {
        await using var app = await CreateApp();
        var main = new RigTrackDraft(Guid.NewGuid(), Main, [new RigAutofocusStepDraft(Guid.NewGuid(), 0.1, 400, 7), new RigExposureStepDraft(Guid.NewGuid(), 0.1)]);
        var wide = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.wide"), [new RepeatStepDraft(Guid.NewGuid(), 15, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)])]);
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [main, wide])]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var status = Assert.Single(app.Sequencer.AutofocusStatuses);
        Assert.Equal("AUTOFOCUS · MAIN", status.Title);
        Assert.True(status.IsCompleted);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
    }

    [Fact]
    public async Task TheRowsOfARunWithAnAutofocus_MapToTheDraft_AndAreAllDone()
    {
        await using var app = await CreateApp();
        var focus = new RigAutofocusStepDraft(Guid.NewGuid(), 0.1, 400, 7);
        var exposure = new RigExposureStepDraft(Guid.NewGuid(), 0.1);
        var main = new RigTrackDraft(Guid.NewGuid(), Main, [focus, exposure]);
        var wide = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.wide"), [new RigExposureStepDraft(Guid.NewGuid(), 0.1)]);
        var policy = MultiRigDitherPolicyDraft.Default with
        {
            Enabled = true, TriggerRigId = new RigId("rig.wide"), SettleStableSeconds = 0.1, SettleTimeoutSeconds = 5,
        };
        var block = new MultiRigStepDraft(Guid.NewGuid(), [main, wide], policy);
        var top = Autofocus("rig.narrow");
        app.Draft.ReplaceSteps([top, new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId), block]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var rows = app.Sequencer.Definition;
        Assert.Equal(
            ["Autofocus", "Start Guiding", "Parallel Imaging", "Main Rig", "Autofocus", "Exposure", "Wide Rig", "Exposure"],
            rows.Select(r => r.Title)); // the generated safe points are no rows
        Assert.Equal(top.Id, rows[0].DraftId);
        Assert.Equal(focus.Id, rows[4].DraftId);
        Assert.All(rows, row => Assert.Equal(NodeStatus.Done, row.Status));
    }

    [Fact]
    public async Task TheEditorIsLocked_WhileRunning_Pausing_AndPaused_AndTheAutofocusFinishesBeforeItPauses()
    {
        await using var app = await CreateApp();
        app.Draft.ReplaceSteps([Autofocus(seconds: 0.15), new DelayStepDraft(Guid.NewGuid(), 0.2)]);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => StatusOf(app, "rig.main") is { IsActive: true }, "the autofocus to run");
        Assert.False(app.Draft.IsEditable);

        app.Sequencer.PauseCommand.Execute(null);
        Assert.Equal(SequenceState.Pausing, app.Sequencer.State);
        Assert.False(app.Draft.IsEditable);
        Assert.False(app.Draft.AddStepCommand.CanExecute(SequenceStepKind.Autofocus));
        Assert.False(app.Draft.RemoveStepCommand.CanExecute(null));

        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");
        Assert.False(app.Draft.IsEditable);
        Assert.True(StatusOf(app, "rig.main")!.IsCompleted); // the whole curve was done before the pause
        Assert.InRange(app.Focuser("focuser.main").Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);

        app.Sequencer.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);
        Assert.True(app.Draft.IsEditable);
        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
    }

    [Fact]
    public async Task ACancelledAutofocus_IsShownAsStopped_AndTheEditorIsFreeAgain()
    {
        await using var app = await CreateApp();
        app.Draft.ReplaceSteps([Autofocus(seconds: 30)]);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => StatusOf(app, "rig.main") is { IsActive: true }, "the autofocus to run");
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        var status = StatusOf(app, "rig.main")!;
        Assert.Equal([AutofocusStatusViewModel.ManualOrigin, "Autofocus stopped"], status.Lines);
        Assert.False(status.IsActive);
        Assert.True(app.Draft.IsEditable);
        Assert.Equal("None in use", app.Vm.Runtime.HeldText);
        Assert.Equal(FocuserMotionState.Idle, app.Focuser("focuser.main").MotionState);
    }

    [Fact]
    public async Task AFailedAutofocus_ShowsTheReasonInOneSentence()
    {
        await using var app = await CreateApp();
        app.Host.AddSimulatedFocusModel(Main, new SimulatedFocusModel(20000, 2.5, 0));
        app.Draft.ReplaceSteps([Autofocus()]);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.StartsWith("Autofocus failed: no reliable focus minimum was found. See the log, execution ", app.Sequencer.ErrorMessage);
        Assert.Equal([AutofocusStatusViewModel.ManualOrigin, "Autofocus stopped"], StatusOf(app, "rig.main")!.Lines);
    }

    [Fact]
    public async Task ANewRun_StartsWithoutTheStatusOfTheLastOne()
    {
        await using var app = await CreateApp();
        app.Draft.ReplaceSteps([Autofocus()]);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.True(app.Sequencer.HasAutofocus);

        app.Draft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 0.05)]);
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.False(app.Sequencer.HasAutofocus);
        Assert.Empty(app.Sequencer.AutofocusStatuses);
    }

    [Fact]
    public async Task RunningAnAutofocus_NeverModifiesTheDocument()
    {
        await using var app = await CreateApp();
        app.Draft.ReplaceSteps([Autofocus()]);
        var modifications = 0;
        app.Draft.Modified += (_, _) => modifications++;

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(0, modifications);
        Assert.False(app.Vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task AnAutofocusThatNeedsADisconnectedDevice_IsNotReadyToRun()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        DemoSetup.AddDemoRigs(host, Fast);
        using var vm = new MainViewModel(host, action => action(), Fast);
        vm.SequenceDraft.ReplaceSteps([Autofocus()]);

        Assert.False(vm.Sequencer.CanRun);
        Assert.NotNull(vm.Sequencer.ReadinessHint);
        await host.DisposeAsync();
    }
}

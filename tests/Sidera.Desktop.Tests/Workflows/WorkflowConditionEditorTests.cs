using System.Diagnostics;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>The workflow editor and the conditions: the switches of a block, a wait and the target write conditions into the workflow, rows say what they are and where they stand, and a conversion to Advanced keeps them.</summary>
public sealed class WorkflowConditionEditorTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Afternoon = new(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId A = new("rig.a");
    private static readonly WorkflowTarget Target = new("M31", 0.712, 41.27);

    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = Afternoon;

        public DateTime Now
        {
            get { lock (_gate) { return _now; } }
            set { lock (_gate) { _now = value; } }
        }

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }
    }

    private async Task<(MainViewModel Vm, WorkflowEditorViewModel Editor, Clock Clock)> EditorAsync(WorkflowDefinition? workflow = null)
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new("camera.a"), "Camera a", 1);
        host.AddSimulatedFocuser(new("focuser.a"), "Focuser a", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedMount(new("mount.1"), "Mount 1", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddRig(new Rig(A, "Main", new("camera.a"), Optics, new("focuser.a"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddSimulatedFocusModel(A, new SimulatedFocusModel(2600));
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var clock = new Clock();
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));
        vm.SequenceDraft.Clock = clock;
        vm.SequenceDraft.SiteProvider = () => Site;
        vm.SequenceDraft.PollInterval = TimeSpan.FromMilliseconds(5);
        vm.Workflow.Load(workflow ?? Workflow());
        return (vm, vm.Workflow, clock);
    }

    private static WorkflowDefinition Workflow() =>
        WorkflowDefinition.Empty with
        {
            Target = Target,
            Prepare = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StartGuiding), new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.Wait, Seconds: 0.05)],
            Imaging = [new ImagingBlock(Guid.NewGuid(), A, null, 0.2, 5)],
            Finish = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StopGuiding)],
        };

    private static WorkflowRowViewModel Block(WorkflowEditorViewModel editor) => editor.ImagingRows.Single();

    private static WorkflowRowViewModel Wait(WorkflowEditorViewModel editor) => editor.PrepareRows.Single(r => r.Kind == WorkflowStepKind.Wait);

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    // ---- a block

    [Fact]
    public async Task TheSwitchesOfABlock_WriteStartAndStopConditionsIntoTheWorkflow()
    {
        var (vm, editor, _) = await EditorAsync();
        var row = Block(editor);

        row.Start.AltitudeAboveOn = true;
        row.Start.AltitudeAboveText = "35";
        row.Start.DarknessOn = true;
        row.Stop.AltitudeBelowOn = true;
        row.Stop.DawnOn = true;
        row.Stop.TimeOn = true;
        row.Stop.TimeText = "04:30";
        row.Stop.DurationOn = true;
        row.Stop.DurationText = "4";

        var block = editor.Definition!.Imaging.Single();
        Assert.Equal([new TargetAltitudeCondition(35, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], block.StartAll);
        Assert.Equal(
            [
                new TargetAltitudeCondition(25, ThresholdDirection.Below), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn),
                TimeCondition.AtLocalTime(new TimeOnly(4, 30), TimeZoneInfo.Local.Id), new DurationCondition(TimeSpan.FromHours(4)),
            ],
            block.StopAny);
        var imaging = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        Assert.IsType<WaitUntilStepDraft>(imaging.Tracks.Single().Steps[0]);
        Assert.Equal(4, ((RepeatStepDraft)imaging.Tracks.Single().Steps[^1]).Stop!.Any.Count);
    }

    [Fact]
    public async Task TheRowSaysWhatStartsAndStopsIt_InAFewWords()
    {
        var (_, editor, _) = await EditorAsync();
        var row = Block(editor);
        Assert.Equal(string.Empty, row.StartText);
        Assert.Equal(string.Empty, row.StopText); // only the frames end it: nothing to add

        row.Start.AltitudeAboveOn = true;
        row.Start.DarknessOn = true;
        row.Stop.DawnOn = true;
        row.Stop.AltitudeBelowOn = true;

        Assert.Equal("Start: altitude ≥ 30° and astronomical darkness", row.StartText);
        Assert.Equal("Stop: 5 frames or altitude < 25° or astronomical dawn", row.StopText);
        Assert.True(row.HasConditionText);
    }

    [Fact]
    public async Task ATimeThatIsNoTime_IsToldAndTheLastGoodOneStays()
    {
        var (_, editor, _) = await EditorAsync();
        var row = Block(editor);
        row.Stop.TimeOn = true;
        row.Stop.TimeText = "05:15";

        row.Stop.TimeText = "soon";

        Assert.True(editor.HasUnreadableFields);
        Assert.Contains(editor.Problems, p => p.Contains("hours and minutes", StringComparison.Ordinal));
        Assert.Equal(new TimeOnly(5, 15), ((TimeCondition)editor.Definition!.Imaging.Single().StopAny.Single()).TimeOfDay);
        row.Stop.TimeText = "05:20";
        Assert.False(editor.HasUnreadableFields);
    }

    [Fact]
    public async Task ASavedConditionWithoutASwitch_IsKept_WhenAnotherSwitchChanges()
    {
        var workflow = Workflow();
        workflow = workflow with { Imaging = [workflow.Imaging[0] with { StopWhen = [new SunAltitudeCondition(-15, ThresholdDirection.Above), TimeCondition.AtUtc(new DateTime(2026, 3, 2, 3, 0, 0, DateTimeKind.Utc))] }] };
        var (_, editor, _) = await EditorAsync(workflow);
        var row = Block(editor);
        Assert.True(row.Stop.HasExtras);

        row.Stop.DurationOn = true;

        var stop = editor.Definition!.Imaging.Single().StopAny;
        Assert.Contains(new SunAltitudeCondition(-15, ThresholdDirection.Above), stop);
        Assert.Contains(stop, c => c is TimeCondition { AbsoluteUtc: not null });
        Assert.Contains(stop, c => c is DurationCondition);
    }

    // ---- the target

    [Fact]
    public async Task TheTargetStopSwitches_WriteIntoTheWorkflow_AndSayWhatStopsTheTarget()
    {
        var (vm, editor, _) = await EditorAsync();
        Assert.Equal(string.Empty, editor.TargetStopSummary);

        editor.TargetStop.DawnOn = true;
        editor.TargetStop.AltitudeBelowOn = true;
        editor.TargetStop.AltitudeBelowText = "20";

        Assert.Equal([new TargetAltitudeCondition(20, ThresholdDirection.Below), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)], editor.Definition!.TargetStopAny);
        Assert.Equal("Target stops: altitude < 20° or astronomical dawn", editor.TargetStopSummary);
        Assert.Equal(2, ((MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft)).TargetStop!.Any.Count);
    }

    [Fact]
    public async Task TheTargetStopOfALoadedWorkflow_IsShownInTheSwitches()
    {
        var workflow = Workflow() with { StopTargetWhen = [new TwilightCondition(Twilight.Nautical, TwilightEvent.Dawn), new DurationCondition(TimeSpan.FromHours(6))] };

        var (_, editor, _) = await EditorAsync(workflow);

        Assert.True(editor.TargetStop.DawnOn);
        Assert.Equal("Nautical", editor.TargetStop.DawnKind);
        Assert.True(editor.TargetStop.DurationOn);
        Assert.Equal("6", editor.TargetStop.DurationText);
        Assert.False(editor.TargetStop.TimeOn);
    }

    // ---- a wait

    [Fact]
    public async Task AWaitCanBeForADuration_UntilATime_OrUntilTheSkyIsRight()
    {
        var (vm, editor, _) = await EditorAsync();
        var wait = Wait(editor);
        Assert.True(wait.WaitIsDuration);

        wait.SelectedWaitMode = wait.WaitModes[1];
        wait.WaitConditions.TimeText = "22:30";

        var untilTime = editor.Definition!.Prepare.Single(s => s.Kind == WorkflowStepKind.Wait);
        Assert.Equal(WorkflowWaitMode.UntilTime, untilTime.WaitMode);
        Assert.Equal(new TimeOnly(22, 30), ((TimeCondition)untilTime.UntilAll.Single()).TimeOfDay);
        Assert.Equal("Until 22:30", wait.Summary);
        Assert.Contains(vm.SequenceDraft.Snapshot(), s => s is WaitUntilStepDraft);

        wait.SelectedWaitMode = wait.WaitModes[2];
        wait.WaitConditions.AltitudeAboveOn = true;
        wait.WaitConditions.DarknessOn = true;

        var untilSky = editor.Definition.Prepare.Single(s => s.Kind == WorkflowStepKind.Wait);
        Assert.Equal(WorkflowWaitMode.UntilCondition, untilSky.WaitMode);
        Assert.Equal([new TargetAltitudeCondition(30, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], untilSky.UntilAll);
        Assert.Equal("Until altitude ≥ 30° and astronomical darkness", wait.Summary);

        wait.SelectedWaitMode = wait.WaitModes[0];
        var duration = editor.Definition.Prepare.Single(s => s.Kind == WorkflowStepKind.Wait);
        Assert.Equal(WorkflowWaitMode.Duration, duration.WaitMode);
        Assert.Null(duration.Until);
        Assert.Equal(0.05, duration.Seconds);
        Assert.Contains(vm.SequenceDraft.Snapshot(), s => s is DelayStepDraft);
    }

    [Fact]
    public async Task AWaitUntilNothing_IsAProblemOfTheRow()
    {
        var (_, editor, _) = await EditorAsync();
        var wait = Wait(editor);

        wait.SelectedWaitMode = wait.WaitModes[2]; // nothing is switched on yet

        Assert.Contains("Choose what to wait for", wait.ProblemText, StringComparison.Ordinal);
    }

    // ---- while it runs

    [Fact]
    public async Task ABlockThatWaitsToStart_SaysWhy_AndAReachedStopIsSaid()
    {
        var (vm, editor, clock) = await EditorAsync();
        var row = Block(editor);
        // It is afternoon: no darkness. The block waits for it, and stops at astronomical dawn.
        row.Start.DarknessOn = true;
        row.Stop.DawnOn = true;
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));

        vm.Sequencer.RunCommand.Execute(null);
        await WaitAsync(() => { editor.RefreshConditions(); return row.ConditionStatusText.StartsWith("Waiting", StringComparison.Ordinal); }, "the block to wait for darkness");

        Assert.Contains("astronomical darkness", row.ConditionStatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sun altitude", row.ConditionStatusDetail, StringComparison.Ordinal);

        var dusk = SunCrossings.NextDusk(Site, Afternoon, Twilight.Astronomical).Utc!.Value;
        clock.Now = dusk.AddMinutes(1); // dark: the block begins
        await WaitAsync(() => { editor.RefreshConditions(); return row.ConditionStatusText.StartsWith("Imaging", StringComparison.Ordinal); }, "the block to image");
        var dawn = SunCrossings.NextDawn(Site, dusk, Twilight.Astronomical).Utc!.Value;
        clock.Now = dawn.AddMinutes(1);
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Completed or SequenceState.Failed, "the sequence to end");
        editor.RefreshConditions();

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.StartsWith("Stopped", row.ConditionStatusText, StringComparison.Ordinal);
        Assert.Contains("Stop: Astronomical dawn", row.ConditionStatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitUntilDarkness_InPrepare_ShowsItsStatusOnItsRow()
    {
        var (vm, editor, _) = await EditorAsync();
        var wait = Wait(editor);
        wait.SelectedWaitMode = wait.WaitModes[2];
        wait.WaitConditions.DarknessOn = true;

        vm.Sequencer.RunCommand.Execute(null);
        await WaitAsync(() => { editor.RefreshConditions(); return wait.HasConditionStatus; }, "the status of the wait");

        Assert.StartsWith("Waiting", wait.ConditionStatusText, StringComparison.Ordinal);
        vm.Sequencer.CancelCommand.Execute(null);
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Cancelled or SequenceState.Completed or SequenceState.Failed, "the cancel");
    }

    // ---- Advanced

    [Fact]
    public async Task ConvertingToAdvanced_KeepsEveryCondition()
    {
        var (vm, editor, _) = await EditorAsync();
        Block(editor).Start.AltitudeAboveOn = true;
        Block(editor).Stop.DurationOn = true;
        editor.TargetStop.DawnOn = true;
        var before = Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(vm.SequenceDraft.Snapshot()));

        editor.ConvertToAdvancedCommand.Execute(null);
        editor.ConvertToAdvancedCommand.Execute(null);

        Assert.True(editor.IsAdvancedMode);
        var after = Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(vm.SequenceDraft.Snapshot()));
        Assert.Equal(before, after);
        var imaging = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        Assert.NotNull(imaging.TargetStop);
        Assert.Contains(imaging.Tracks.Single().Steps, s => s is WaitUntilStepDraft);
        Assert.Contains("The target stops when any", vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig).Summary, StringComparison.Ordinal);
        Assert.True(editor.CanReturnToWorkflow); // untouched: it is still exactly what the workflow compiled to
    }
}

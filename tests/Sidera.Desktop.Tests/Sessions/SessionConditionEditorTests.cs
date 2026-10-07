using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>The editor and the conditions: limits, repeat rules and waits write conditions into the session, cards say what they are and where they stand, and the tree keeps every condition.</summary>
public sealed class SessionConditionEditorTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Afternoon = new(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId A = new("rig.a");

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

    private async Task<(MainViewModel Vm, SessionEditorViewModel Editor, Clock Clock)> EditorAsync(SessionDefinition? session = null)
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
        vm.SessionEditor.Load(session ?? Plain());
        return (vm, vm.SessionEditor, clock);
    }

    private static SessionDefinition Plain() => SessionDefinition.Empty with
    {
        Targets =
        [
            new SessionTarget(
                Guid.NewGuid(), "M31", 0.712, 41.27, null, true,
                [new StartGuidingAction(Guid.NewGuid()), new WaitAction(Guid.NewGuid(), 0.05)], [Lane(null, SequenceBlock.Imaging(null, null, 0.2, 5))], []),
        ],
        End = [new StopGuidingAction(Guid.NewGuid())],
    };

    private static SequenceBlock BlockOf(SessionEditorViewModel editor) => editor.Session!.Targets[0].Lanes[0].Blocks[0];

    private static BlockDrawerViewModel OpenBlock(SessionEditorViewModel editor)
    {
        editor.SelectBlock(BlockOf(editor).Id);
        return Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
    }

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
    public async Task TheLimitsAndTheRepeatOfABlock_WriteConditionsIntoTheSession()
    {
        var (vm, editor, _) = await EditorAsync();
        var drawer = OpenBlock(editor);

        drawer.Until.DurationOn = true;
        drawer.Until.DurationText = "3";
        drawer.Limits.AltitudeBelowOn = true;
        drawer.Limits.DawnOn = true;
        drawer.Limits.TimeOn = true;
        drawer.Limits.TimeText = "04:30";
        drawer.Limits.DurationOn = true;
        drawer.Limits.DurationText = "4";

        var block = BlockOf(editor);
        Assert.Equal([new DurationCondition(TimeSpan.FromHours(3))], block.Repeat.Until);
        Assert.Equal(
            [
                new TargetAltitudeCondition(25, ThresholdDirection.Below), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn),
                TimeCondition.AtLocalTime(new TimeOnly(4, 30), TimeZoneInfo.Local.Id), new DurationCondition(TimeSpan.FromHours(4)),
            ],
            block.Limits);
        var imaging = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        Assert.Equal(5, ((RepeatStepDraft)imaging.Tracks.Single().Steps[^1]).Stop!.Any.Count); // what ends the repeat, and the limits of the block
    }

    [Fact]
    public async Task TheCard_SaysWhatRepeatsAndStopsTheBlock_InAFewWords()
    {
        var (_, editor, _) = await EditorAsync();
        var drawer = OpenBlock(editor);
        Assert.Equal("0.2 s × 5", editor.Targets[0].Lanes[0].Blocks[0].Line); // only the frames end it: nothing to add

        drawer.Limits.DawnOn = true;
        drawer.Limits.AltitudeBelowOn = true;
        drawer.RepeatCountOn = false;
        drawer.Until.DurationOn = true;

        Assert.Equal("0.2 s until 4 h · stops: alt<25°, dawn", editor.Targets[0].Lanes[0].Blocks[0].Line);
    }

    [Fact]
    public async Task ATimeThatIsNoTime_IsToldAndTheLastGoodOneStays()
    {
        var (_, editor, _) = await EditorAsync();
        var drawer = OpenBlock(editor);
        drawer.Limits.TimeOn = true;
        drawer.Limits.TimeText = "05:15";

        drawer.Limits.TimeText = "soon";

        Assert.True(editor.HasUnreadableFields);
        Assert.Contains("hours and minutes", drawer.ProblemText, StringComparison.Ordinal);
        Assert.Equal(new TimeOnly(5, 15), ((TimeCondition)BlockOf(editor).Limits.Single()).TimeOfDay);
        drawer.Limits.TimeText = "05:20";
        Assert.False(editor.HasUnreadableFields);
    }

    [Fact]
    public async Task ASavedConditionWithoutASwitch_IsKept_WhenAnotherSwitchChanges()
    {
        var session = Plain();
        var block = session.Targets[0].Lanes[0].Blocks[0] with
        {
            Limits = [new SunAltitudeCondition(-15, ThresholdDirection.Above), TimeCondition.AtUtc(new DateTime(2026, 3, 2, 3, 0, 0, DateTimeKind.Utc))],
        };
        session = session with { Targets = [session.Targets[0] with { Lanes = [session.Targets[0].Lanes[0] with { Blocks = [block] }] }] };
        var (_, editor, _) = await EditorAsync(session);
        var drawer = OpenBlock(editor);
        Assert.True(drawer.Limits.HasExtras);

        drawer.Limits.DurationOn = true;

        var limits = BlockOf(editor).Limits;
        Assert.Contains(new SunAltitudeCondition(-15, ThresholdDirection.Above), limits);
        Assert.Contains(limits, c => c is TimeCondition { AbsoluteUtc: not null });
        Assert.Contains(limits, c => c is DurationCondition);
    }

    // ---- the target

    [Fact]
    public async Task TheLimitsOfATarget_WriteIntoTheSession_AndTheCardSaysWhatEndsTheTarget()
    {
        var (vm, editor, _) = await EditorAsync();
        Assert.Equal("No limits besides the blocks", editor.Targets[0].LimitsText);
        editor.SelectTarget(editor.Session!.Targets[0].Id);
        var drawer = Assert.IsType<TargetDrawerViewModel>(editor.Drawer);

        drawer.Limits.DawnOn = true;
        drawer.Limits.AltitudeBelowOn = true;
        drawer.Limits.AltitudeBelowText = "20";

        Assert.Equal([new TargetAltitudeCondition(20, ThresholdDirection.Below), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)], editor.Session.Targets[0].Limits);
        Assert.Equal("Limits · alt<20°, dawn", editor.Targets[0].LimitsText);
        Assert.Equal(2, ((MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft)).TargetStop!.Any.Count);
    }

    [Fact]
    public async Task TheLimitsOfALoadedTarget_AreShownInTheSwitches()
    {
        var session = Plain();
        session = session with { Targets = [session.Targets[0] with { Limits = [new TwilightCondition(Twilight.Nautical, TwilightEvent.Dawn), new DurationCondition(TimeSpan.FromHours(6))] }] };
        var (_, editor, _) = await EditorAsync(session);

        editor.SelectTarget(session.Targets[0].Id);
        var drawer = Assert.IsType<TargetDrawerViewModel>(editor.Drawer);

        Assert.True(drawer.Limits.DawnOn);
        Assert.Equal("Nautical", drawer.Limits.DawnKind);
        Assert.True(drawer.Limits.DurationOn);
        Assert.Equal("6", drawer.Limits.DurationText);
        Assert.False(drawer.Limits.TimeOn);
    }

    // ---- a wait

    [Fact]
    public async Task AWaitCanBeForADuration_UntilATime_OrUntilTheSkyIsRight()
    {
        var (vm, editor, _) = await EditorAsync();
        var wait = editor.Targets[0].Preparation.Rows.Single(r => r.Kind == SessionActionKind.Wait);
        Assert.Equal("0.05 s", wait.Summary);

        // Until a time of the day, or until the sky is right: a Wait Until in its place.
        editor.Targets[0].Preparation.AddCommand.Execute(null);
        editor.Library.Search = "until";
        editor.Library.ChooseSelectedCommand.Execute(null);
        var action = Assert.IsType<ActionEditorViewModel>(editor.Drawer);
        action.Conditions.TimeOn = true;
        action.Conditions.TimeText = "22:30";

        var untilTime = Assert.Single(editor.Session!.Targets[0].Preparation.OfType<WaitUntilAction>());
        Assert.Equal(new TimeOnly(22, 30), ((TimeCondition)untilTime.Conditions.Single()).TimeOfDay);
        Assert.Equal("22:30", editor.Targets[0].Preparation.Rows.Single(r => r.Kind == SessionActionKind.WaitUntil).Summary);
        Assert.Contains(vm.SequenceDraft.Snapshot(), s => s is WaitUntilStepDraft);

        action.Conditions.TimeOn = false;
        action.Conditions.AltitudeAboveOn = true;
        action.Conditions.DarknessOn = true;

        untilTime = Assert.Single(editor.Session.Targets[0].Preparation.OfType<WaitUntilAction>());
        Assert.Equal([new TargetAltitudeCondition(30, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], untilTime.Conditions);
        Assert.Equal("alt>30° and dusk", editor.Targets[0].Preparation.Rows.Single(r => r.Kind == SessionActionKind.WaitUntil).Summary);
        Assert.Contains(vm.SequenceDraft.Snapshot(), s => s is DelayStepDraft); // the plain wait is still there
    }

    [Fact]
    public async Task AWaitUntilNothing_IsAProblemOfTheAction()
    {
        var (_, editor, _) = await EditorAsync();

        editor.Targets[0].Preparation.AddCommand.Execute(null);
        editor.Library.Search = "until";
        editor.Library.ChooseSelectedCommand.Execute(null); // nothing is switched on yet

        var row = editor.Targets[0].Preparation.Rows.Single(r => r.Kind == SessionActionKind.WaitUntil);
        Assert.Contains("Choose what to wait for", row.ProblemText, StringComparison.Ordinal);
    }

    // ---- while it runs

    [Fact]
    public async Task ABlockThatWaitsToStart_SaysWhy_AndAReachedStopIsSaid()
    {
        var (vm, editor, clock) = await EditorAsync();
        // It is afternoon: no darkness. The block waits for it (a Wait Until in front of it), and stops at astronomical dawn.
        var drawer = OpenBlock(editor);
        drawer.Limits.DawnOn = true;
        editor.Library.Open(ActionOwner.BlockOf(BlockOf(editor).Id));
        editor.Library.Search = "until";
        editor.Library.ChooseSelectedCommand.Execute(null);
        var wait = Assert.IsType<ActionEditorViewModel>(editor.Drawer);
        wait.Conditions.DarknessOn = true;
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));

        vm.Sequencer.RunCommand.Execute(null);
        var card = () => editor.Targets[0].Lanes[0].Blocks[0];
        await WaitAsync(() => { editor.RefreshConditions(); return card().StatusText.StartsWith("Waiting", StringComparison.Ordinal); }, "the block to wait for darkness");

        Assert.Contains("astronomical darkness", card().StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sun altitude", card().StatusDetail, StringComparison.Ordinal);

        var dusk = SunCrossings.NextDusk(Site, Afternoon, Twilight.Astronomical).Utc!.Value;
        clock.Now = dusk.AddMinutes(1); // dark: the block begins
        await WaitAsync(() => { editor.RefreshConditions(); return card().StatusText.StartsWith("Imaging", StringComparison.Ordinal); }, "the block to image");
        var dawn = SunCrossings.NextDawn(Site, dusk, Twilight.Astronomical).Utc!.Value;
        clock.Now = dawn.AddMinutes(1);
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Completed or SequenceState.Failed, "the sequence to end");
        editor.RefreshConditions();

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.StartsWith("Stopped", card().StatusText, StringComparison.Ordinal);
        Assert.Contains("Stop: Astronomical dawn", card().StatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWaitUntilDarkness_InThePreparation_ShowsItsStatusOnItsRow()
    {
        var (vm, editor, _) = await EditorAsync();
        editor.Targets[0].Preparation.AddCommand.Execute(null);
        editor.Library.Search = "until";
        editor.Library.ChooseSelectedCommand.Execute(null);
        Assert.IsType<ActionEditorViewModel>(editor.Drawer).Conditions.DarknessOn = true;

        vm.Sequencer.RunCommand.Execute(null);
        var row = () => editor.Targets[0].Preparation.Rows.Single(r => r.Kind == SessionActionKind.WaitUntil);
        await WaitAsync(() => { editor.RefreshConditions(); return row().HasStatus; }, "the status of the wait");

        Assert.StartsWith("Waiting", row().StatusText, StringComparison.Ordinal);
        vm.Sequencer.CancelCommand.Execute(null);
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Cancelled or SequenceState.Completed or SequenceState.Failed, "the cancel");
    }

    // ---- the tree

    [Fact]
    public async Task OpeningTheTree_KeepsEveryCondition()
    {
        var (vm, editor, _) = await EditorAsync();
        var drawer = OpenBlock(editor);
        drawer.Limits.DurationOn = true;
        editor.SelectTarget(editor.Session!.Targets[0].Id);
        Assert.IsType<TargetDrawerViewModel>(editor.Drawer).Limits.DawnOn = true;
        var before = Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(vm.SequenceDraft.Snapshot()));

        editor.ShowTreeCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null);

        Assert.True(editor.IsTree);
        var after = Sidera.Desktop.Documents.SequenceDocumentStore.Fingerprint(Sidera.Desktop.Documents.SequenceDocumentMapper.ToDocument(vm.SequenceDraft.Snapshot()));
        Assert.Equal(before, after);
        var imaging = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        Assert.NotNull(imaging.TargetStop);
        Assert.Contains("The target stops when any", vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig).Summary, StringComparison.Ordinal);
        Assert.True(editor.CanReturnToStructured); // untouched: it is still exactly what the session compiled to
    }
}

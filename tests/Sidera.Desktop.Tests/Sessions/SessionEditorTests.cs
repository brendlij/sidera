using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>The editor of the session on the demo equipment: targets, blocks and actions, the drawer, what it compiles to, the document it is saved in, and the tree of steps.</summary>
public sealed class SessionEditorTests : IAsyncLifetime
{
    private sealed class Picker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult(SavePath);
    }

    // Solves where the mount points: the plate solver of a simulated sky.
    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-session-editor-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private async Task<(MainViewModel Vm, SessionEditorViewModel Editor, Picker Picker)> CreateAsync(bool withTarget = true)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        host.ConfigurePlateSolver(new Solver(host));
        var picker = new Picker();
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), SequenceDocumentStore.CreateDefault(), picker);
        _apps.Add((host, vm));
        await vm.SequenceDocument.NewCommand.ExecuteAsync(null);
        if (vm.SequenceDocument.IsConfirmingDiscard)
        {
            await vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        if (withTarget)
        {
            vm.SessionEditor.AddTargetCommand.Execute(null);
        }

        return (vm, vm.SessionEditor, picker);
    }

    private static SessionTarget FirstTarget(SessionEditorViewModel editor) => editor.Session!.Targets[0];

    private static SequenceBlock FirstBlock(SessionEditorViewModel editor) => FirstTarget(editor).Lanes[0].Blocks[0];

    // ---- the start

    [Fact]
    public async Task ANewSession_IsAnEmptySession_AndTheDemoSequenceIsATreeOfSteps()
    {
        var (vm, editor, _) = await CreateAsync(withTarget: false);

        Assert.True(editor.IsStructured);
        Assert.True(editor.IsEmpty);
        Assert.True(vm.SequenceDraft.IsEmpty);
        Assert.False(vm.SessionPage.ShowsTree);
        Assert.Equal("Add a target to begin.", editor.CanRunText);

        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        _apps.Add((host, new MainViewModel(host, a => a(), new DemoOptions())));
        Assert.True(_apps[^1].Vm.SessionEditor.IsTree); // the sequence a first start has is explicit steps: it is not guessed into a session
        Assert.False(_apps[^1].Vm.SequenceDraft.IsEmpty);
    }

    [Fact]
    public async Task TheFirstTarget_ComesWithWhatItsSetupCanDo_AndCompilesToASequenceThatCanRun()
    {
        var (vm, editor, _) = await CreateAsync();

        var target = FirstTarget(editor);
        Assert.Equal([SessionActionKind.SlewAndCenter, SessionActionKind.Autofocus, SessionActionKind.StartGuiding], target.Preparation.Select(a => a.Kind));
        var lane = Assert.Single(target.Lanes);
        Assert.Single(lane.Blocks);
        Assert.Equal([SessionActionKind.StopGuiding], editor.Session!.End.Select(a => a.Kind)); // the guiding that was started is stopped
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));
        Assert.Equal(5, vm.SequenceDraft.Steps.Count); // slew, autofocus, start guiding, imaging, stop guiding
        Assert.Equal("The session is complete.", editor.CanRunText);
        Assert.IsType<TargetDrawerViewModel>(editor.Drawer); // the new target is open, to be set
    }

    [Fact]
    public async Task AddingASecondSequence_GivesAnotherSetupItsOwnBlocks_AndTheSetupsImageSideBySide_WithoutBuildingAParallel()
    {
        var (vm, editor, _) = await CreateAsync();
        Assert.True(editor.IsMultiSetup);

        editor.Targets[0].AddLaneCommand.Execute(null);

        var target = FirstTarget(editor);
        Assert.Equal(2, target.Lanes.Count);
        Assert.NotEqual(target.Lanes[0].Setup, target.Lanes[1].Setup);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig));
        Assert.Equal(2, block.Children.Count);
        Assert.True(editor.Targets[0].ShowLaneTabs);
        Assert.StartsWith("Parallel imaging · ", editor.Targets[0].ParallelText, StringComparison.Ordinal);
    }

    // ---- the drawer

    [Fact]
    public async Task EditingTheFieldsOfABlock_ChangesTheSequence_AndTheCard()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);

        drawer.ExposureText = "300";
        drawer.RepeatCountText = "40";

        Assert.Equal("300 s × 40", editor.Targets[0].Lanes[0].Blocks[0].Line);
        var block = (MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig);
        var repeat = (RepeatStepDraftViewModel)((RigTrackDraftViewModel)block.Children[0]).Children[0];
        Assert.Equal("40", repeat.CountText);
        Assert.True(vm.SequenceDocument.IsDirty);
        Assert.Same(drawer, editor.Drawer); // typing does not rebuild what is being typed in
    }

    [Fact]
    public async Task ATextThatIsNoNumber_IsToldInTheDrawer_KeepsTheLastGoodValue_AndStopsSaving()
    {
        var (vm, editor, picker) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.RepeatCountText = "12";

        drawer.RepeatCountText = "many";

        Assert.True(editor.HasUnreadableFields);
        Assert.Contains("number of frames", drawer.ProblemText, StringComparison.Ordinal);
        Assert.Equal(12, FirstBlock(editor).Repeat.Count);
        picker.SavePath = Path.Combine(_directory, "Bad");
        await vm.SequenceDocument.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(_directory, "Bad.astraseq"))); // not saved with a value that was not read

        drawer.RepeatCountText = "13";
        Assert.False(editor.HasUnreadableFields);
        Assert.True(vm.SequenceDraft.IsValid);
    }

    [Fact]
    public async Task Dither_IsAutomationOfTheBlock_NeedsNoGuiderChoice_AndIsCountedOnOneSetup()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.Targets[0].AddLaneCommand.Execute(null);
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);

        drawer.DitherOn = true;
        drawer.DitherEveryText = "2";

        Assert.Contains("Dither 2", editor.Targets[0].Lanes[0].Blocks[0].Line, StringComparison.Ordinal);
        var block = (MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig);
        Assert.True(block.DitherEnabled);
        Assert.Equal("2", block.DitherEveryText);
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.DoesNotContain(vm.SequenceDraft.Rows, r => r.Kind == SequenceStepKind.Dither); // no explicit Dither step
    }

    [Fact]
    public async Task Autofocus_IsAutomationOfTheBlock_AndShownOnItsCard_AndTheIntervalIsTheClockOfTheSetup()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        Assert.True(drawer.CanFocus);

        drawer.FocusOn = true;
        drawer.FocusAtStart = true;
        drawer.FocusEveryOn = true;
        drawer.FocusEveryText = "60";
        drawer.FocusAfterFilter = false;

        Assert.Contains("AF 60 m/start", editor.Targets[0].Lanes[0].Blocks[0].Line, StringComparison.Ordinal);
        var track = (RigTrackDraftViewModel)((MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig)).Children[0];
        Assert.True(track.AutofocusEnabled);
        Assert.Equal("60", track.AutofocusIntervalText);
    }

    [Fact]
    public async Task AutofocusWithNoTrigger_IsAProblemOfTheBlock()
    {
        var (_, editor, _) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.FocusOn = true;

        drawer.FocusAtStart = false;

        Assert.Contains(editor.Problems, p => p.Contains("no trigger", StringComparison.Ordinal));
    }

    // ---- actions

    [Fact]
    public async Task AnActionIsAddedFromTheLibrary_WhereItCanGo_AndIsOpenedToBeSet()
    {
        var (_, editor, _) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var library = editor.Library;

        library.Open(ActionOwner.BlockOf(FirstBlock(editor).Id));

        Assert.True(library.IsOpen);
        Assert.Contains(library.Items, i => i.Kind == SessionActionKind.SetFilter);
        Assert.DoesNotContain(library.Items, i => i.Kind is SessionActionKind.Park or SessionActionKind.SlewAndCenter); // not in a block
        library.Search = "wait";
        Assert.Equal([SessionActionKind.Wait, SessionActionKind.WaitUntil], library.Items.Select(i => i.Kind).Order());
        library.Search = "until";
        library.ChooseSelectedCommand.Execute(null);

        Assert.False(library.IsOpen);
        var added = FirstBlock(editor).Actions.OfType<WaitUntilAction>().Single();
        Assert.True(FirstBlock(editor).Actions.ToList().IndexOf(added) < FirstBlock(editor).BodyStart); // before the first exposure: it runs once
        var editorOfAction = Assert.IsType<ActionEditorViewModel>(editor.Drawer);
        Assert.Equal(SessionActionKind.WaitUntil, editorOfAction.Kind);
    }

    [Fact]
    public async Task TheStartAndTheEnd_TakeTheActionsOfTheSession_NotTheOnesOfABlock()
    {
        var (_, editor, _) = await CreateAsync();

        editor.Library.Open(ActionOwner.Start);
        Assert.Contains(editor.Library.Items, i => i.Kind == SessionActionKind.Unpark);
        Assert.DoesNotContain(editor.Library.Items, i => i.Kind == SessionActionKind.Exposure);
        editor.Library.Search = "unpark";
        editor.Library.ChooseSelectedCommand.Execute(null);

        Assert.Equal([SessionActionKind.Unpark], editor.Session!.Start.Select(a => a.Kind));
        Assert.Equal(["Unpark"], editor.StartList.Rows.Select(r => r.Title));
    }

    [Fact]
    public async Task AnActionMovesWithinItsList_IsSwitchedOff_AndIsRemoved_AndTheSequenceFollows()
    {
        var (vm, editor, _) = await CreateAsync();
        var preparation = editor.Targets[0].Preparation;
        Assert.False(preparation.Rows[0].CanMoveUp);
        Assert.True(preparation.Rows[0].CanMoveDown);

        preparation.Rows[0].MoveDownCommand.Execute(null);
        Assert.Equal([SessionActionKind.Autofocus, SessionActionKind.SlewAndCenter, SessionActionKind.StartGuiding], FirstTarget(editor).Preparation.Select(a => a.Kind));

        editor.Targets[0].Preparation.Rows[0].IsOn = false;
        Assert.DoesNotContain(vm.SequenceDraft.Steps, s => s.Kind == SequenceStepKind.Autofocus);

        editor.Targets[0].Preparation.Rows[0].RemoveCommand.Execute(null);
        Assert.Equal([SessionActionKind.SlewAndCenter, SessionActionKind.StartGuiding], FirstTarget(editor).Preparation.Select(a => a.Kind));
    }

    [Fact]
    public async Task ABlockIsDuplicated_ReorderedSwitchedOffAndRemoved()
    {
        var (vm, editor, _) = await CreateAsync();
        var original = FirstBlock(editor);
        editor.SelectBlock(original.Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.NameText = "Ha";
        drawer.DitherOn = true;

        editor.Targets[0].Lanes[0].Blocks[0].DuplicateCommand.Execute(null);

        var blocks = FirstTarget(editor).Lanes[0].Blocks;
        Assert.Equal(2, blocks.Count);
        Assert.Equal("Ha", blocks[1].Name); // the copy keeps what the block had
        Assert.NotNull(blocks[1].Automation.Dither);
        Assert.NotEqual(blocks[0].Id, blocks[1].Id);
        Assert.NotEqual(blocks[0].Actions[0].Id, blocks[1].Actions[0].Id);

        editor.Targets[0].Lanes[0].Blocks[1].MoveUpCommand.Execute(null);
        Assert.Equal(blocks[1].Id, FirstTarget(editor).Lanes[0].Blocks[0].Id);

        editor.Targets[0].Lanes[0].Blocks[0].IsOn = false;
        Assert.False(FirstTarget(editor).Lanes[0].Blocks[0].Enabled);
        editor.Targets[0].Lanes[0].Blocks[1].RemoveCommand.Execute(null);
        Assert.Single(FirstTarget(editor).Lanes[0].Blocks);
        Assert.True(vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task WhileASequenceRuns_TheSessionCannotBeChanged()
    {
        var (vm, editor, _) = await CreateAsync();

        vm.SequenceDraft.IsEditable = false;

        Assert.False(editor.IsEditable);
        Assert.False(editor.AddTargetCommand.CanExecute(null));
        Assert.False(editor.Targets[0].Preparation.Rows[0].RemoveCommand.CanExecute(null));
        Assert.False(editor.Targets[0].Lanes[0].AddBlockCommand.CanExecute(null));
    }

    // ---- the tree of steps

    [Fact]
    public async Task OpeningTheTree_NeedsASecondClick_AndKeepsTheSteps()
    {
        var (vm, editor, _) = await CreateAsync();
        var steps = vm.SequenceDraft.Steps.Count;

        editor.ShowTreeCommand.Execute(null);
        Assert.True(editor.IsConfirmingTree);
        Assert.True(editor.IsStructured);

        editor.ShowTreeCommand.Execute(null);

        Assert.True(editor.IsTree);
        Assert.Null(editor.Session);
        Assert.Equal(steps, vm.SequenceDraft.Steps.Count);
        Assert.True(vm.SequenceDocument.IsDirty);
        Assert.True(editor.CanReturnToStructured); // the steps are still exactly what the session compiled to
    }

    [Fact]
    public async Task AStepAddedBehindTheSessionsBack_MakesTheSequenceATree_WithoutLosingAnything()
    {
        var (vm, editor, _) = await CreateAsync();
        var steps = vm.SequenceDraft.Steps.Count;

        vm.SequenceDraft.AddStepCommand.Execute(SequenceStepKind.Delay);

        Assert.True(editor.IsTree);
        Assert.Equal(steps + 1, vm.SequenceDraft.Steps.Count);
        Assert.False(editor.CanReturnToStructured); // no longer what a session compiles to
        Assert.Contains("cannot be shown as blocks", editor.WhyNotStructuredText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTreeGoesBackToTheBlocks_WhereItIsExact()
    {
        var (_, editor, _) = await CreateAsync();
        var session = editor.Session;
        editor.ShowTreeCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null);

        editor.SwitchToStructuredCommand.Execute(null);

        Assert.True(editor.IsStructured);
        Assert.Equal(session!.Targets[0].Id, editor.Session!.Targets[0].Id);
    }

    // ---- the document

    [Fact]
    public async Task ASession_IsSavedWithTheFile_AndOpensAsTheSameSession()
    {
        var (vm, editor, picker) = await CreateAsync();
        editor.SelectBlock(FirstBlock(editor).Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.ExposureText = "120";
        drawer.DitherOn = true;
        drawer.DitherEveryText = "4";
        drawer.FocusOn = true;
        drawer.FocusEveryOn = true;
        drawer.FocusEveryText = "45";
        var saved = editor.Session!;
        picker.SavePath = Path.Combine(_directory, "Session");
        await vm.SequenceDocument.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.SequenceDocument.IsDirty);
        var text = await File.ReadAllTextAsync(Path.Combine(_directory, "Session.astraseq"));
        Assert.Contains("\"session\"", text, StringComparison.Ordinal);
        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workflow\"", text, StringComparison.Ordinal);

        // Another session opens it.
        var (other, otherEditor, otherPicker) = await CreateAsync(withTarget: false);
        otherPicker.OpenPath = Path.Combine(_directory, "Session.astraseq");
        await other.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (other.SequenceDocument.IsConfirmingDiscard)
        {
            await other.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        Assert.True(otherEditor.IsStructured);
        Assert.False(other.SequenceDocument.IsDirty);
        Assert.Equal(saved.Targets[0].Id, otherEditor.Session!.Targets[0].Id);
        Assert.Equal(saved.Targets[0].Lanes[0].Blocks[0].Automation.Dither, otherEditor.Session.Targets[0].Lanes[0].Blocks[0].Automation.Dither);
        Assert.Equal(saved.Targets[0].Lanes[0].Blocks[0].Automation.Focus, otherEditor.Session.Targets[0].Lanes[0].Blocks[0].Automation.Focus);
        Assert.Equal(editor.Targets[0].Lanes[0].Blocks[0].Line, otherEditor.Targets[0].Lanes[0].Blocks[0].Line);
        Assert.True(other.SequenceDraft.IsValid, string.Join(" ", other.SequenceDraft.ValidationErrors));
    }

    [Fact]
    public async Task AVersion8FileWithAWorkflow_OpensAsASession_IsNotDirty_AndIsNotRewrittenByOpeningIt()
    {
        var (vm, editor, picker) = await CreateAsync(withTarget: false);
        var path = Path.Combine(_directory, "Old.astraseq");
        var workflow = Sidera.Desktop.Workflows.WorkflowDefinition.Empty with
        {
            Target = new Sidera.Desktop.Workflows.WorkflowTarget("M31", 0.7123, 41.269),
            Prepare = [new Sidera.Desktop.Workflows.WorkflowStep(Guid.NewGuid(), Sidera.Desktop.Workflows.WorkflowStepKind.SlewAndCenter)],
            Imaging = [new Sidera.Desktop.Workflows.ImagingBlock(Guid.NewGuid(), new RigId("rig.main"), null, 60, 5)],
        };
        using (var stream = new MemoryStream())
        {
            await new JsonSequenceDocumentSerializer().SaveAsync(stream, new SequenceDocument("Old", [], null, workflow), CancellationToken.None);
            var text = System.Text.Encoding.UTF8.GetString(stream.ToArray()).Replace("\"version\": 9", "\"version\": 8", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, text);
        }

        var before = await File.ReadAllBytesAsync(path);
        picker.OpenPath = path;

        await vm.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (vm.SequenceDocument.IsConfirmingDiscard)
        {
            await vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        Assert.True(editor.IsStructured);
        Assert.Equal("M31", editor.Session!.Targets[0].Name);
        Assert.Equal([SessionActionKind.SlewAndCenter], editor.Session.Targets[0].Preparation.Select(a => a.Kind));
        Assert.False(vm.SequenceDocument.IsDirty);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ALegacySequenceFile_OpensAsATree_AsItWas()
    {
        var (vm, editor, picker) = await CreateAsync();
        var path = Path.Combine(_directory, "Legacy.astraseq");
        await File.WriteAllTextAsync(path, "{\"format\":\"astra-sequence\",\"version\":7,\"steps\":[{\"type\":\"delay\",\"id\":\"11111111-1111-4111-8111-111111111111\",\"durationSeconds\":3}]}");
        picker.OpenPath = path;

        await vm.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (vm.SequenceDocument.IsConfirmingDiscard)
        {
            await vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        Assert.True(editor.IsTree);
        Assert.Equal([SequenceStepKind.Delay], vm.SequenceDraft.Steps.Select(s => s.Kind));
        Assert.False(vm.SequenceDocument.IsDirty);
    }

    // ---- framing

    [Fact]
    public async Task ATargetFromFraming_IsAddedAfterTheOnesThereAre_WithTheStepThatCentersIt_AndNoSync()
    {
        var (vm, editor, _) = await CreateAsync(withTarget: false);
        Assert.True(editor.IsEmpty);

        var message = vm.SequenceDraft.TargetSink!(new SessionTargetRequest("NGC 7000", 20.9, 44.3, 81.5, new RigId("rig.wide")));
        var second = vm.SequenceDraft.TargetSink!(new SessionTargetRequest("M31", 0.7, 41.3, null, null));

        Assert.NotNull(message);
        Assert.NotNull(second);
        Assert.Equal(["NGC 7000", "M31"], editor.Session!.Targets.Select(t => t.Name));
        var first = editor.Session.Targets[0];
        Assert.Equal((20.9, 44.3, 81.5), (first.RightAscensionHours, first.DeclinationDegrees, first.RotationDegrees!.Value));
        Assert.Equal(ImagingBindingId.For(new Sidera.Core.Devices.DeviceId("camera.wide")), first.Lanes[0].Setup); // the setup of the framing, as the path of its camera
        Assert.Contains(first.Preparation, a => a is SlewAndCenterAction or CenterAndRotateAction);
        Assert.DoesNotContain(editor.Session.Targets.SelectMany(t => t.Preparation), a => a.Kind == SessionActionKind.SyncMount);
        Assert.Equal(2, editor.Targets.Count);
    }

    [Fact]
    public async Task ASequenceOfExplicitSteps_IsLeftToTheFraming()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));

        var handled = vm.SequenceDraft.TargetSink!(new SessionTargetRequest("M31", 0.7, 41.3, null, null));

        Assert.Null(handled); // a tree with steps gets the steps of the framing, as it always did
    }
}

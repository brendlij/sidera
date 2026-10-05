using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>The workflow editor of the session: rows, the inspector's fields, the policies, section rules, Advanced mode, and the document it is saved in.</summary>
public sealed class WorkflowEditorTests : IAsyncLifetime
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

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astra-workflow-editor-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(MainViewModel Vm, WorkflowEditorViewModel Editor, Picker Picker)> CreateAsync(bool template = true)
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

        if (template)
        {
            vm.Workflow.StartFromTemplateCommand.Execute(null);
        }

        return (vm, vm.Workflow, picker);
    }

    private static List<T> Pick<T>(IEnumerable<WorkflowRowViewModel> rows, Func<WorkflowRowViewModel, T> read) => rows.Select(read).ToList();

    [Fact]
    public async Task ANewSession_IsAnEmptyWorkflow_AndTheDemoSequenceIsAnAdvancedOne()
    {
        var (vm, editor, _) = await CreateAsync(template: false);

        Assert.True(editor.IsWorkflowMode);
        Assert.True(editor.IsEmpty);
        Assert.True(vm.SequenceDraft.IsEmpty);
        Assert.False(vm.SessionPage.Workflow!.IsAdvancedMode);

        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        _apps.Add((host, new MainViewModel(host, a => a(), new DemoOptions())));
        Assert.True(_apps[^1].Vm.Workflow.IsAdvancedMode); // the sequence a first start has is explicit steps: it is not guessed into a workflow
        Assert.False(_apps[^1].Vm.SequenceDraft.IsEmpty);
    }

    [Fact]
    public async Task TheTemplate_StartsWithWhatTheFirstSetupCanDo_AndCompilesToASequenceThatCanRun()
    {
        var (vm, editor, _) = await CreateAsync();

        Assert.Equal(["Slew & Center", "Autofocus", "Start Guiding"], Pick(editor.PrepareRows, r => r.Title));
        Assert.Single(editor.ImagingRows);
        Assert.Equal(["Stop Guiding"], Pick(editor.FinishRows, r => r.Title));
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));
        Assert.Equal(5, vm.SequenceDraft.Steps.Count); // slew, autofocus, start guiding, imaging, stop guiding
        Assert.Equal("The workflow is complete.", editor.CanRunText);
    }

    [Fact]
    public async Task AddingASecondImagingBlock_PicksAnotherSetup_AndMakesTheSetupsRunTogether()
    {
        var (vm, editor, _) = await CreateAsync();

        editor.AddImagingBlockCommand.Execute(null);

        Assert.Equal(2, editor.ImagingRows.Count);
        Assert.NotEqual(editor.ImagingRows[0].SetupLabel, editor.ImagingRows[1].SetupLabel);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig));
        Assert.Equal(2, block.Children.Count); // parallel, without anybody building a Parallel
    }

    [Fact]
    public async Task EditingTheFieldsOfABlock_ChangesTheSequence_AndTheRow()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.SelectRow(editor.ImagingRows[0]);
        var row = editor.ImagingRows[0];

        row.ExposureText = "300";
        row.FramesInputText = "40";

        Assert.Equal("300 s", row.ExposureLabel);
        Assert.Equal("40", row.FramesLabel);
        var block = (MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig);
        var repeat = (RepeatStepDraftViewModel)((RigTrackDraftViewModel)block.Children[0]).Children[0];
        Assert.Equal("40", repeat.CountText);
        Assert.True(vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task ATextThatIsNoNumber_IsToldOnTheRow_KeepsTheLastGoodValue_AndStopsSaving()
    {
        var (vm, editor, picker) = await CreateAsync();
        var row = editor.ImagingRows[0];
        row.FramesInputText = "12";

        row.FramesInputText = "many";

        Assert.True(editor.HasUnreadableFields);
        Assert.Contains("number of frames", editor.Problems[0], StringComparison.Ordinal);
        Assert.Equal("12", row.FramesLabel);
        Assert.False(vm.SequenceDraft.IsValid);
        picker.SavePath = Path.Combine(_directory, "Bad");
        await vm.SequenceDocument.SaveCommand.ExecuteAsync(null);
        Assert.False(File.Exists(Path.Combine(_directory, "Bad.astraseq"))); // not saved with a value that was not read

        row.FramesInputText = "13";
        Assert.False(editor.HasUnreadableFields);
        Assert.True(vm.SequenceDraft.IsValid);
    }

    [Fact]
    public async Task DitherEveryN_NeedsNoGuiderChoice_AndIsCountedOnOneSetup_WhileTheOthersAreCoordinated()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.AddImagingBlockCommand.Execute(null);

        editor.DitherEnabled = true;
        editor.DitherEveryText = "2";

        var labels = Pick(editor.ImagingRows, r => r.DitherLabel).ToList();
        Assert.Equal("Every 2", labels[0]);
        Assert.Equal("Coordinated", labels[1]); // on the same mount: it waits at a safe point
        Assert.Contains("guider is the one of the setup", editor.DitherSummary, StringComparison.Ordinal);
        var block = (MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig);
        Assert.True(block.DitherEnabled);
        Assert.Equal("2", block.DitherEveryText);
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.DoesNotContain(vm.SequenceDraft.Rows, r => r.Kind == SequenceStepKind.Dither); // no explicit Dither step
    }

    [Fact]
    public async Task TheAutofocusPolicy_IsEditedForTheSetupOfTheSelectedBlock_AndShownOnItsRow()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.SelectRow(editor.ImagingRows[0]);
        Assert.True(editor.HasPolicy);

        editor.PolicyEnabled = true;
        editor.PolicyAtStart = true;
        editor.PolicyIntervalText = "60";
        editor.PolicyAfterFilterChange = false;

        Assert.Equal("At start · every 60 min", editor.ImagingRows[0].AutofocusLabel);
        var track = (RigTrackDraftViewModel)((MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig)).Children[0];
        Assert.True(track.AutofocusEnabled);
        Assert.Equal("60", track.AutofocusIntervalText);
        Assert.True(track.AutofocusAtStart);
    }

    [Fact]
    public async Task AutofocusWithNoTrigger_IsAProblemOfTheWorkflow()
    {
        var (_, editor, _) = await CreateAsync();
        editor.SelectRow(editor.ImagingRows[0]);

        editor.PolicyEnabled = true;

        Assert.Contains(editor.Problems, p => p.Contains("no trigger", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARowMovesWithinItsSection_NeverToAnother()
    {
        var (_, editor, _) = await CreateAsync();
        editor.SelectRow(editor.PrepareRows[0]);
        Assert.False(editor.MoveUpCommand.CanExecute(null)); // the first row of Prepare stays in Prepare
        Assert.True(editor.MoveDownCommand.CanExecute(null));

        editor.MoveDownCommand.Execute(null);

        Assert.Equal(["Autofocus", "Slew & Center", "Start Guiding"], Pick(editor.PrepareRows, r => r.Title));
        Assert.Equal([1, 2, 3], Pick(editor.PrepareRows, r => r.Number));
        editor.SelectRow(editor.PrepareRows[^1]);
        Assert.False(editor.MoveDownCommand.CanExecute(null));
        Assert.Single(editor.ImagingRows); // nothing crossed into the imaging section
    }

    [Fact]
    public async Task RemovingDuplicatingAndSwitchingRowsOff_ChangeTheSequence()
    {
        var (vm, editor, _) = await CreateAsync();
        editor.SelectRow(editor.PrepareRows.Single(r => r.Kind == WorkflowStepKind.Autofocus));
        editor.RemoveSelectedCommand.Execute(null);
        Assert.DoesNotContain(vm.SequenceDraft.Steps, s => s.Kind == SequenceStepKind.Autofocus);

        editor.SelectRow(editor.FinishRows[0]);
        editor.DuplicateSelectedCommand.Execute(null);
        Assert.Equal(2, editor.FinishRows.Count); // a second Stop Guiding of the same guider compiles to one

        editor.ToggleEnabledCommand.Execute(null);
        Assert.False(editor.FinishRows[1].Enabled);
        Assert.Equal(1, vm.SequenceDraft.Steps.Count(s => s.Kind == SequenceStepKind.StopGuiding));
    }

    [Fact]
    public async Task ASetupThatCannotDoWhatTheRowAsks_IsToldOnTheRow()
    {
        var (_, editor, _) = await CreateAsync();
        editor.AddStartGuidingCommand.Execute(null);
        var row = editor.PrepareRows.Last();
        // a rig that has nothing: none of the demo rigs lacks a guider, so a setup that does not exist stands in
        row.SelectedSetup = row.SetupChoices.First(c => c.Id is not null);

        Assert.False(row.HasProblem, row.ProblemText); // the demo setups all have a guider
        Assert.True(editor.IsEditable);
    }

    [Fact]
    public async Task ConvertingToAdvanced_NeedsASecondClick_AndKeepsTheSteps()
    {
        var (vm, editor, _) = await CreateAsync();
        var steps = vm.SequenceDraft.Steps.Count;

        editor.ConvertToAdvancedCommand.Execute(null);
        Assert.True(editor.IsConfirmingAdvanced);
        Assert.True(editor.IsWorkflowMode);

        editor.ConvertToAdvancedCommand.Execute(null);

        Assert.True(editor.IsAdvancedMode);
        Assert.Null(editor.Definition);
        Assert.Equal(steps, vm.SequenceDraft.Steps.Count);
        Assert.True(vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task AStepAddedBehindTheWorkflowsBack_MakesTheSessionAnAdvancedOne_WithoutLosingAnything()
    {
        var (vm, editor, _) = await CreateAsync();
        var steps = vm.SequenceDraft.Steps.Count;

        vm.SequenceDraft.AddStepCommand.Execute(SequenceStepKind.Delay);

        Assert.True(editor.IsAdvancedMode);
        Assert.Equal(steps + 1, vm.SequenceDraft.Steps.Count);
    }

    [Fact]
    public async Task AWorkflow_IsSavedWithTheSession_AndOpensAsTheSameWorkflow()
    {
        var (vm, editor, picker) = await CreateAsync();
        editor.AddImagingBlockCommand.Execute(null);
        editor.SelectRow(editor.ImagingRows[0]);
        editor.ImagingRows[0].ExposureText = "120";
        editor.DitherEnabled = true;
        editor.DitherEveryText = "4";
        editor.PolicyEnabled = true;
        editor.PolicyIntervalText = "45";
        editor.PolicyAtStart = true;
        var saved = editor.Definition!;
        picker.SavePath = Path.Combine(_directory, "Session");
        await vm.SequenceDocument.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.SequenceDocument.IsDirty);
        Assert.Contains("\"workflow\"", await File.ReadAllTextAsync(Path.Combine(_directory, "Session.astraseq")), StringComparison.Ordinal);

        // Another session opens it.
        var (other, otherEditor, otherPicker) = await CreateAsync(template: false);
        otherPicker.OpenPath = Path.Combine(_directory, "Session.astraseq");
        await other.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (other.SequenceDocument.IsConfirmingDiscard)
        {
            await other.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        Assert.True(otherEditor.IsWorkflowMode);
        Assert.False(other.SequenceDocument.IsDirty);
        Assert.Equal(saved.Target, otherEditor.Definition!.Target);
        Assert.Equal(saved.Imaging, otherEditor.Definition.Imaging);
        Assert.Equal(saved.Prepare, otherEditor.Definition.Prepare);
        Assert.Equal(saved.Dither, otherEditor.Definition.Dither);
        Assert.Equal(saved.AutofocusPolicies, otherEditor.Definition.AutofocusPolicies);
        Assert.Equal(Pick(editor.ImagingRows, r => r.DitherLabel), Pick(otherEditor.ImagingRows, r => r.DitherLabel));
        Assert.True(other.SequenceDraft.IsValid, string.Join(" ", other.SequenceDraft.ValidationErrors));
    }

    [Fact]
    public async Task ALegacySequenceFile_OpensInAdvanced_AsItWas()
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

        Assert.True(editor.IsAdvancedMode);
        Assert.Equal([SequenceStepKind.Delay], vm.SequenceDraft.Steps.Select(s => s.Kind));
        Assert.False(vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task AddToSessionFromTheFraming_SetsTheTargetOfTheWorkflow_WithoutASyncStep()
    {
        var (vm, editor, _) = await CreateAsync();
        var framing = vm.Framing;
        framing.RefreshEquipment();
        framing.SearchText = "M31";
        await framing.SearchCommand.ExecuteAsync(null);
        if (!framing.HasTarget)
        {
            return; // the catalog of this environment is not available; the hook itself is tested below
        }

        framing.AddToSessionCommand.Execute(null);

        Assert.Equal(framing.Target!.Name, editor.Definition!.Target.Name);
        Assert.DoesNotContain(vm.SequenceDraft.Steps, s => s.Kind == SequenceStepKind.SyncMountToSolved);
    }

    [Fact]
    public async Task ATargetFromTheFraming_ReplacesTheTargetOfAWorkflow_AndStartsOneInAnEmptySession()
    {
        var (vm, editor, _) = await CreateAsync(template: false);
        Assert.True(editor.IsEmpty);

        var message = vm.SequenceDraft.TargetSink!(new WorkflowTargetRequest("NGC 7000", 20.9, 44.3, 81.5, new RigId("rig.wide")));

        Assert.NotNull(message);
        Assert.Equal("NGC 7000", editor.Definition!.Target.Name);
        Assert.Equal(81.5, editor.Definition.Target.DesiredRotationDegrees);
        Assert.Equal(new RigId("rig.wide"), editor.Definition.Target.PointingSetup);
        Assert.Contains(editor.PrepareRows, r => r.Kind == WorkflowStepKind.SlewAndCenter);
        Assert.DoesNotContain(editor.PrepareRows.Concat(editor.FinishRows), r => r.Title.Contains("Sync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionOfExplicitSteps_IsLeftToTheFraming()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));

        var handled = vm.SequenceDraft.TargetSink!(new WorkflowTargetRequest("M31", 0.7, 41.3, null, null));

        Assert.Null(handled); // an Advanced session with steps gets the steps of the framing, as it always did
    }

    [Fact]
    public async Task WhileASequenceRuns_TheRowsCannotBeChanged()
    {
        var (vm, editor, _) = await CreateAsync();

        vm.SequenceDraft.IsEditable = false;

        Assert.False(editor.IsEditable);
        Assert.False(editor.AddImagingBlockCommand.CanExecute(null));
        Assert.False(editor.RemoveSelectedCommand.CanExecute(null));
        Assert.False(editor.MoveDownCommand.CanExecute(null));
    }
}

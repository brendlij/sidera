using System.ComponentModel;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;

namespace Astra.Desktop.Tests.Ux;

/// <summary>The session page: document, run, workflow, inspector and the Multi-Rig lanes of the editor.</summary>
public class SessionPageTests
{
    private static MultiRigStepDraft Block(bool autofocus = true, bool dither = true) => new(
        Guid.NewGuid(),
        [
            new RigTrackDraft(
                Guid.NewGuid(), new RigId("rig.main"),
                [
                    new RigChangeFilterStepDraft(Guid.NewGuid(), 4),
                    new RepeatStepDraft(Guid.NewGuid(), 40, [new RigExposureStepDraft(Guid.NewGuid(), 300)]),
                ],
                autofocus ? new RigAutofocusPolicyDraft(true, true, true, 1, 400, 7) : null),
            new RigTrackDraft(
                Guid.NewGuid(), new RigId("rig.wide"),
                [new RepeatStepDraft(Guid.NewGuid(), 120, [new RigExposureStepDraft(Guid.NewGuid(), 60)])]),
        ],
        dither ? new MultiRigDitherPolicyDraft(true, new RigId("rig.wide"), 3, 1.5, 0.5, 1, 10) : null);

    // The document and the run

    [Fact]
    public async Task TheDocumentControls_AreNewOpenSaveAndSaveAs_AndTheyAreNotTheRunControls()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var page = app.Vm.SessionPage;

        Assert.Equal("Untitled Sequence", page.Document.DisplayName);
        Assert.NotNull(page.Document.NewCommand);
        Assert.NotNull(page.Document.OpenCommand);
        Assert.NotNull(page.Document.SaveCommand);
        Assert.NotNull(page.Document.SaveAsCommand);
        Assert.NotSame(page.Document, page.Sequencer);
        Assert.True(page.Document.NewCommand.CanExecute(null));
        Assert.True(page.Document.OpenCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnEdit_MarksTheDocumentModified()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var page = app.Vm.SessionPage;
        Assert.False(page.Document.IsDirty);

        page.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);

        Assert.True(page.Document.IsDirty);
    }

    [Fact]
    public async Task TheRunControls_FollowTheStateOfTheRun()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var page = app.Vm.SessionPage;
        page.Draft.ReplaceSteps(
        [
            new DelayStepDraft(Guid.NewGuid(), 0.4),
            new DelayStepDraft(Guid.NewGuid(), 0.4),
            new DelayStepDraft(Guid.NewGuid(), 0.4),
        ]);
        var sequencer = page.Sequencer;
        sequencer.RefreshReadiness();

        Assert.True(sequencer.RunCommand.CanExecute(null));
        Assert.False(sequencer.PauseCommand.CanExecute(null));
        Assert.False(sequencer.ResumeCommand.CanExecute(null));
        Assert.False(sequencer.CancelCommand.CanExecute(null));
        Assert.Equal(StatusKind.Neutral, sequencer.StateKind);

        var run = sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => sequencer.IsRunningState, "running");

        Assert.False(sequencer.RunCommand.CanExecute(null));
        Assert.True(sequencer.PauseCommand.CanExecute(null));
        Assert.True(sequencer.CancelCommand.CanExecute(null));
        Assert.Equal(StatusKind.Active, sequencer.StateKind);
        Assert.False(sequencer.IsPauseState);

        sequencer.PauseCommand.Execute(null);
        await UxApp.WaitUntil(() => sequencer.IsPaused, "paused");

        Assert.True(sequencer.IsPauseState); // the Resume button takes the place of the Pause button
        Assert.True(sequencer.ResumeCommand.CanExecute(null));
        Assert.False(sequencer.PauseCommand.CanExecute(null));
        Assert.Equal(StatusKind.Warning, sequencer.StateKind);

        sequencer.ResumeCommand.Execute(null);
        sequencer.CancelCommand.Execute(null);
        await run;

        Assert.Equal(StatusKind.Warning, sequencer.StateKind); // cancelled
        Assert.False(sequencer.IsRunning);
    }

    // The workflow

    [Fact]
    public async Task EveryEditorCommand_RemainsAvailableOnAnySelectedStep()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var draft = app.Vm.SessionPage.Draft;
        draft.SelectedStep = draft.Rows[2]; // the first exposure

        Assert.True(draft.DuplicateStepCommand.CanExecute(null));
        Assert.True(draft.CopyStepCommand.CanExecute(null));
        Assert.True(draft.RemoveStepCommand.CanExecute(null));
        Assert.True(draft.MoveStepUpCommand.CanExecute(null));
        Assert.True(draft.MoveStepDownCommand.CanExecute(null));
        Assert.False(draft.PasteStepCommand.CanExecute(null)); // nothing was copied yet
        foreach (var kind in new[]
                 {
                     SequenceStepKind.Exposure, SequenceStepKind.Delay, SequenceStepKind.Slew, SequenceStepKind.StartGuiding,
                     SequenceStepKind.StopGuiding, SequenceStepKind.Dither, SequenceStepKind.MoveFocuser, SequenceStepKind.ChangeFilter,
                     SequenceStepKind.Autofocus, SequenceStepKind.Repeat, SequenceStepKind.MultiRig,
                 })
        {
            Assert.True(draft.AddStepCommand.CanExecute(kind), kind.ToString());
        }

        draft.CopyStepCommand.Execute(null);
        Assert.True(draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheEditorCommands_AreOffWhileASequenceRuns()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 1)]);
        draft.SelectedStep = draft.Rows[0];

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Sequencer.IsRunning, "running");

        Assert.False(draft.IsEditable);
        Assert.False(draft.RemoveStepCommand.CanExecute(null));
        Assert.False(draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.False(app.Vm.SessionPage.Document.NewCommand.CanExecute(null));

        app.Vm.Sequencer.CancelCommand.Execute(null);
        await run;
        Assert.True(draft.IsEditable);
    }

    [Fact]
    public async Task TheInspector_ShowsTheEditorOfTheSelectedStep_AndNothingWhenNothingIsSelected()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var draft = app.Vm.SessionPage.Draft;
        draft.SelectedStep = null;
        Assert.Null(draft.SelectedStep);

        draft.SelectedStep = draft.Rows.First(r => r.Kind == SequenceStepKind.Slew);
        Assert.IsType<SlewStepDraftViewModel>(draft.SelectedStep);

        draft.SelectedStep = draft.Rows.First(r => r.Kind == SequenceStepKind.Dither);
        Assert.IsType<DitherStepDraftViewModel>(draft.SelectedStep);
    }

    [Fact]
    public async Task ASimpleSessionHasNoLanes_AndNoRigTrackAppearsInTheWorkflow()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var draft = app.Vm.SessionPage.Draft;

        Assert.All(draft.Rows, row => Assert.False(row.IsMultiRig || row.IsTrack || row.InTrack));
        Assert.False(app.Vm.Execution.HasLanes);
        Assert.Empty(app.Vm.Execution.Lanes);
    }

    // Multi-Rig in the editor

    [Fact]
    public async Task AMultiRigBlock_IsShownAsLanes_OneForEachRigWithWhatItDoes()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;

        draft.ReplaceSteps([Block()]);

        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.Steps[0]);
        Assert.True(block.HasLanes);
        Assert.Equal(["Main Rig", "Wide Rig"], block.Lanes.Select(l => l.Name));

        var main = block.Lanes[0];
        Assert.Equal("Autofocus: track start + filter change", main.Autofocus);
        Assert.True(main.HasAutofocus);
        Assert.Equal(
            [("Change Filter · Main Filter Wheel · Ha", false), ("Repeat × 40", false), ("Exposure · 300 s", true)],
            main.Lines.Select(l => (l.Text, l.IsNested)));

        var wide = block.Lanes[1];
        Assert.Null(wide.Autofocus);
        Assert.Equal([("Repeat × 120", false), ("Exposure · 60 s", true)], wide.Lines.Select(l => (l.Text, l.IsNested)));
    }

    [Fact]
    public async Task TheDitherPolicyOfTheBlock_IsOneLine_OrOff()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;

        draft.ReplaceSteps([Block(dither: true)]);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.Steps[0]);
        Assert.Equal("Every 3 Wide Rig frames · 1.5 px · settle ≤ 0.5 px for 1 s", block.DitherSummary);

        block.DitherEnabled = false;
        Assert.Equal("Off", block.DitherSummary);
    }

    [Fact]
    public async Task TheLanes_FollowEdits()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps([Block()]);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.Steps[0]);
        var wideTrack = block.Children.OfType<RigTrackDraftViewModel>().Last();
        var raised = new List<string?>();
        block.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        wideTrack.AutofocusEnabled = true;
        wideTrack.AutofocusAtStart = true;

        Assert.Contains(nameof(MultiRigStepDraftViewModel.Lanes), raised);
        Assert.Equal("Autofocus: track start", block.Lanes[1].Autofocus);
    }

    [Fact]
    public async Task ARowKnowsWhetherItIsTheHeadOfTheBlock_ALane_OrInsideALane()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;

        draft.ReplaceSteps([Block()]);

        var rows = draft.Rows.ToList();
        Assert.True(rows[0].IsMultiRig);
        Assert.False(rows[0].InTrack);
        Assert.True(rows[1].IsTrack);
        Assert.False(rows[1].InTrack);
        Assert.All(rows.Skip(2).Where(r => !r.IsTrack), row => Assert.True(row.InTrack, row.Title));
    }

    [Fact]
    public async Task DescribingTheStepsAgain_RaisesNoEditOfAnyStep_SoItCannotLoopForever()
    {
        // What a step derives from its children is read again after every edit. A property raised for it that looks like
        // an input field would count as an edit, and the edit would describe the step again, without end.
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps([Block()]);
        var all = Flatten(draft.Steps).ToList();
        var inputsRaised = new List<string>();
        foreach (var step in all)
        {
            step.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is { } name && name.EndsWith("Text", StringComparison.Ordinal))
                {
                    inputsRaised.Add($"{step.Title}.{name}");
                }
            };
        }

        draft.Revalidate();

        Assert.Empty(inputsRaised);
    }

    private static IEnumerable<StepDraftViewModel> Flatten(IEnumerable<StepDraftViewModel> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step is ContainerStepDraftViewModel container)
            {
                foreach (var child in Flatten(container.Children))
                {
                    yield return child;
                }
            }
        }
    }

    // The sequence of the workflow while it runs

    [Fact]
    public async Task WhileItRuns_TheWorkflowIsTheSnapshotOfTheRun_WithWhatIsDoneRunningAndWaiting()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        app.Vm.SessionPage.Draft.ReplaceSteps(
        [
            new DelayStepDraft(Guid.NewGuid(), 0.05),
            new DelayStepDraft(Guid.NewGuid(), 0.6),
            new DelayStepDraft(Guid.NewGuid(), 0.05),
        ]);
        var sequencer = app.Vm.Sequencer;

        var run = sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => sequencer.Definition.Count == 3 && sequencer.Definition[1].IsActive, "the second step running");

        Assert.True(sequencer.ShowsRun);
        Assert.Equal(
            [NodeStatus.Done, NodeStatus.Active, NodeStatus.Pending], sequencer.Definition.Select(n => n.Status));

        await run;
        Assert.All(sequencer.Definition, n => Assert.True(n.IsDone));
    }

    [Fact]
    public async Task APausedMultiRigRun_ShowsItsLanesAsPaused()
    {
        await using var app = await UxApp.Create(UxSetup.Demo, connect: true);
        app.Vm.SessionPage.Draft.ReplaceSteps([Block(autofocus: false, dither: false)]);
        // short exposures, many frames
        var track = (MultiRigStepDraftViewModel)app.Vm.SessionPage.Draft.Steps[0];
        foreach (var repeat in Flatten(track.Children).OfType<RepeatStepDraftViewModel>())
        {
            repeat.CountText = "50";
        }

        foreach (var exposure in Flatten(track.Children).OfType<RigExposureStepDraftViewModel>())
        {
            exposure.ExposureText = "0.1";
        }

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Execution.Lanes.Count == 2 && app.Vm.Execution.Lanes.All(l => l.IsActive), "both lanes");
        app.Vm.Sequencer.PauseCommand.Execute(null);
        await UxApp.WaitUntil(() => app.Vm.Sequencer.IsPaused, "paused");

        Assert.All(app.Vm.Execution.Lanes, l => Assert.Equal("Paused", l.StatusText));
        Assert.All(app.Vm.Execution.Lanes, l => Assert.Equal(StatusKind.Warning, l.StatusKind));

        app.Vm.Sequencer.CancelCommand.Execute(null);
        await run;
    }
}

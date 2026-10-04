using Astra.Core.Devices;
using Astra.Desktop.ViewModels;
using Astra.Runtime;

namespace Astra.Desktop.Tests.ViewModels;

/// <summary>Repeat in the editor: structure, selection, validation and the preview rows.</summary>
public class SequenceRepeatEditingTests
{
    private static AstraRuntimeHost CreateHost()
    {
        var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(AstraRuntimeHost host)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults);
    }

    private static T Add<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddStepCommand.Execute(kind);
        return Assert.IsType<T>(draft.SelectedStep);
    }

    private static T AddChild<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        Assert.True(draft.AddChildCommand.CanExecute(kind));
        draft.AddChildCommand.Execute(kind);
        return Assert.IsType<T>(draft.SelectedStep);
    }

    // A draft of: Start Guiding, Repeat × 3 [Exposure, Delay, Dither], Stop Guiding.
    private sealed record Fixture(
        SequenceDraftViewModel Draft,
        StartGuidingStepDraftViewModel Start,
        RepeatStepDraftViewModel Repeat,
        ExposureStepDraftViewModel Exposure,
        DelayStepDraftViewModel Delay,
        DitherStepDraftViewModel Dither,
        StopGuidingStepDraftViewModel Stop);

    private static Fixture Build(AstraRuntimeHost host)
    {
        var draft = CreateDraft(host);
        var start = Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);
        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);
        repeat.CountText = "3";
        var exposure = AddChild<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var delay = AddChild<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var dither = AddChild<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);
        var stop = Add<StopGuidingStepDraftViewModel>(draft, SequenceStepKind.StopGuiding);
        return new Fixture(draft, start, repeat, exposure, delay, dither, stop);
    }

    // Structure

    [Fact]
    public async Task AddRepeat_AppendsAnEmptyRepeatAtTheTopLevel_AndSelectsIt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);

        Assert.Same(repeat, draft.Steps[^1]);
        Assert.Same(repeat, draft.SelectedStep);
        Assert.Empty(repeat.Children);
        Assert.Equal("2", repeat.CountText);
        Assert.Equal(("Repeat × 2", "no steps"), (repeat.Title, repeat.Summary));
        Assert.True(repeat.IsContainer);
        Assert.True(repeat.IsTopLevel);
        Assert.Null(repeat.Parent);
    }

    [Fact]
    public async Task AnEmptyRepeat_IsInvalid_AndMakesTheDraftInvalid_AndCannotBeBuilt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);

        Assert.Equal(["Repeat must contain at least one step."], repeat.Problems);
        Assert.False(draft.IsValid);
        Assert.Equal(["Step 1 (Repeat): Repeat must contain at least one step."], draft.ValidationErrors);
        Assert.Throws<SequenceConfigurationException>(() => draft.Build());
    }

    [Theory]
    [InlineData(SequenceStepKind.Exposure, typeof(ExposureStepDraftViewModel))]
    [InlineData(SequenceStepKind.Delay, typeof(DelayStepDraftViewModel))]
    [InlineData(SequenceStepKind.Slew, typeof(SlewStepDraftViewModel))]
    [InlineData(SequenceStepKind.StartGuiding, typeof(StartGuidingStepDraftViewModel))]
    [InlineData(SequenceStepKind.StopGuiding, typeof(StopGuidingStepDraftViewModel))]
    [InlineData(SequenceStepKind.Dither, typeof(DitherStepDraftViewModel))]
    public async Task AddChild_AddsEveryLeafKindToTheSelectedRepeat_AndSelectsIt(SequenceStepKind kind, Type viewModel)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);

        draft.AddChildCommand.Execute(kind);

        var child = Assert.Single(repeat.Children);
        Assert.IsType(viewModel, child);
        Assert.Equal(kind, child.Kind);
        Assert.Same(repeat, child.Parent);
        Assert.True(child.IsChild);
        Assert.False(child.IsTopLevel);
        Assert.Same(child, draft.SelectedStep);
        Assert.Single(draft.Steps); // not added at the top level
        Assert.Equal("1.1", child.NumberLabel);
    }

    [Fact]
    public async Task AChild_GetsTheSameDefaultsAsATopLevelStep()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var top = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);

        var child = AddChild<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var dither = AddChild<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        Assert.Equal(top.Camera.SelectedId, child.Camera.SelectedId);
        Assert.Equal(top.ExposureText, child.ExposureText);
        Assert.Equal(top.Summary, child.Summary);
        Assert.Equal(new DeviceId("guider.main"), dither.Guider.SelectedId);
        Assert.Equal(new DeviceId("mount.eq6"), dither.Mount.SelectedId);
        Assert.Equal(new DeviceId("camera.main"), dither.Camera.SelectedId);
        Assert.Equal("1.5 px · settle ≤ 0.5 px for 1 s", dither.Summary);
    }

    [Fact]
    public async Task AddChild_WhileAChildIsSelected_GoesIntoThatChildsRepeat_AtTheEnd()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Exposure;

        var added = AddChild<SlewStepDraftViewModel>(f.Draft, SequenceStepKind.Slew);

        Assert.Equal([f.Exposure, f.Delay, f.Dither, added], f.Repeat.Children);
        Assert.Equal("2.4", added.NumberLabel);
    }

    [Fact]
    public async Task AddChild_IsOnlyAvailable_WhenARepeatOrAStepInsideOneIsSelected()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Draft.SelectedStep = f.Start;
        Assert.False(f.Draft.CanAddChildHere);
        Assert.False(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));

        f.Draft.SelectedStep = f.Repeat;
        Assert.True(f.Draft.CanAddChildHere);
        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));

        f.Draft.SelectedStep = f.Delay;
        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));

        f.Draft.SelectedStep = null;
        Assert.False(f.Draft.CanAddChildHere);
    }

    [Fact]
    public async Task ARepeatCannotBeAddedInsideARepeat()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;

        Assert.False(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Repeat));

        // Adding a Repeat always goes to the top level, also while a step inside a Repeat is selected.
        f.Draft.SelectedStep = f.Delay;
        f.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        Assert.Equal(4, f.Draft.Steps.Count);
        Assert.IsType<RepeatStepDraftViewModel>(f.Draft.Steps[^1]);
        Assert.DoesNotContain(f.Repeat.Children, c => c is RepeatStepDraftViewModel);
    }

    [Fact]
    public async Task TheListShowsEachRepeatFollowedByItsChildren_AndNumbersThemByLevel()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.Equal([f.Start, f.Repeat, f.Exposure, f.Delay, f.Dither, f.Stop], f.Draft.Rows);
        Assert.Equal(["1", "2", "2.1", "2.2", "2.3", "3"], f.Draft.Rows.Select(r => r.NumberLabel));
        Assert.Equal([0d, 0d, 28d, 28d, 28d, 0d], f.Draft.Rows.Select(r => r.IndentWidth));
        Assert.Equal(3, f.Draft.Steps.Count - 0);
        Assert.Equal([1, 1, 2, 3], new[] { f.Start.Number, f.Exposure.Number, f.Delay.Number, f.Dither.Number });
    }

    [Fact]
    public async Task ChildrenAreEditedWithTheSameEditorsAsTopLevelSteps_AndTheRepeatShowsHowManyAreInside()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Assert.Equal("3 steps", f.Repeat.Summary);

        f.Exposure.ExposureText = "300";
        f.Delay.DurationText = "10";
        f.Dither.AmplitudeText = "2";

        Assert.Equal("Main Camera · 300 s · Camera defaults", f.Exposure.Summary);
        Assert.Equal("10 s", f.Delay.Summary);
        Assert.Equal("2 px · settle ≤ 0.5 px for 1 s", f.Dither.Summary);
        var repeat = Assert.IsType<RepeatStepDraft>(f.Draft.Snapshot()[1]);
        Assert.Equal(300, Assert.IsType<ExposureStepDraft>(repeat.Children[0]).Seconds);
        Assert.Equal(10, Assert.IsType<DelayStepDraft>(repeat.Children[1]).Seconds);
        Assert.Equal(2, Assert.IsType<DitherStepDraft>(repeat.Children[2]).AmplitudePixels);
    }

    // Removing

    [Fact]
    public async Task RemoveChild_RemovesOnlyThatChild_AndSelectsTheNextOneInsideTheRepeat()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Delay;

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Equal([f.Exposure, f.Dither], f.Repeat.Children);
        Assert.Same(f.Dither, f.Draft.SelectedStep);
        Assert.Equal(["2.1", "2.2"], f.Repeat.Children.Select(c => c.NumberLabel));
        Assert.Equal(3, f.Draft.Steps.Count);
    }

    [Fact]
    public async Task RemovingTheLastChild_LeavesTheRepeatInvalid_SelectsIt_AndDoesNotDeleteIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        foreach (var child in new StepDraftViewModel[] { f.Exposure, f.Delay, f.Dither })
        {
            f.Draft.SelectedStep = child;
            f.Draft.RemoveStepCommand.Execute(null);
        }

        Assert.Contains(f.Repeat, f.Draft.Steps);
        Assert.Empty(f.Repeat.Children);
        Assert.Same(f.Repeat, f.Draft.SelectedStep);
        Assert.Equal(["Repeat must contain at least one step."], f.Repeat.Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task RemoveRepeat_RemovesItsChildrenWithIt_AndSelectsTheNextStep()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Equal([f.Start, f.Stop], f.Draft.Steps);
        Assert.Equal([f.Start, f.Stop], f.Draft.Rows);
        Assert.Same(f.Stop, f.Draft.SelectedStep);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    // Moving

    [Fact]
    public async Task MovingAChild_ReordersItAmongItsSiblings_AndKeepsItSelected()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Dither;

        f.Draft.MoveStepUpCommand.Execute(null);
        Assert.Equal([f.Exposure, f.Dither, f.Delay], f.Repeat.Children);
        Assert.Same(f.Dither, f.Draft.SelectedStep);
        Assert.Equal([f.Start, f.Repeat, f.Exposure, f.Dither, f.Delay, f.Stop], f.Draft.Rows);
        Assert.Equal("2.2", f.Dither.NumberLabel);

        f.Draft.MoveStepUpCommand.Execute(null);
        f.Draft.MoveStepDownCommand.Execute(null);
        f.Draft.MoveStepDownCommand.Execute(null);
        Assert.Equal([f.Exposure, f.Delay, f.Dither], f.Repeat.Children);
    }

    [Fact]
    public async Task AChildCannotLeaveItsRepeat_OrEnterAnother_ByMovingUpOrDown()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Draft.SelectedStep = f.Exposure; // first inside; a step precedes the Repeat
        Assert.False(f.Draft.MoveStepUpCommand.CanExecute(null));
        Assert.True(f.Draft.MoveStepDownCommand.CanExecute(null));

        f.Draft.SelectedStep = f.Dither;   // last inside; a step follows the Repeat
        Assert.True(f.Draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(f.Draft.MoveStepDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task ATopLevelStepNextToARepeat_MovesPastItAsAWhole_NeverIntoIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Start;

        f.Draft.MoveStepDownCommand.Execute(null);

        Assert.Equal([f.Repeat, f.Start, f.Stop], f.Draft.Steps);
        Assert.Equal([f.Repeat, f.Exposure, f.Delay, f.Dither, f.Start, f.Stop], f.Draft.Rows);
        Assert.Equal(3, f.Repeat.Children.Count);
        Assert.Null(f.Start.Parent);
    }

    [Fact]
    public async Task MovingARepeat_MovesItWithItsChildren()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;

        f.Draft.MoveStepDownCommand.Execute(null);
        Assert.Equal([f.Start, f.Stop, f.Repeat], f.Draft.Steps);
        Assert.Equal([f.Start, f.Stop, f.Repeat, f.Exposure, f.Delay, f.Dither], f.Draft.Rows);
        Assert.False(f.Draft.MoveStepDownCommand.CanExecute(null));
        Assert.Same(f.Repeat, f.Draft.SelectedStep);

        f.Draft.MoveStepUpCommand.Execute(null);
        f.Draft.MoveStepUpCommand.Execute(null);
        Assert.Equal([f.Repeat, f.Start, f.Stop], f.Draft.Steps);
        Assert.Equal(["1", "1.1", "1.2", "1.3", "2", "3"], f.Draft.Rows.Select(r => r.NumberLabel));
    }

    [Fact]
    public async Task IdsStayTheSame_WhenStepsAreMoved_AddedOrRemoved()
    {
        await using var host = CreateHost();
        var f = Build(host);
        var before = f.Draft.Rows.ToDictionary(r => r, r => r.Id);

        f.Draft.SelectedStep = f.Dither;
        f.Draft.MoveStepUpCommand.Execute(null);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.MoveStepDownCommand.Execute(null);
        AddChild<DelayStepDraftViewModel>(f.Draft, SequenceStepKind.Delay);
        f.Draft.SelectedStep = f.Stop;
        f.Draft.RemoveStepCommand.Execute(null);

        Assert.All(f.Draft.Rows.Where(before.ContainsKey), row => Assert.Equal(before[row], row.Id));
        var snapshot = f.Draft.Snapshot();
        var repeat = Assert.IsType<RepeatStepDraft>(snapshot.Single(s => s is RepeatStepDraft));
        Assert.Equal(f.Repeat.Id, repeat.Id);
        Assert.Equal(f.Repeat.Children.Select(c => c.Id), repeat.Children.Select(c => c.Id));
        Assert.Equal(f.Draft.Rows.Select(r => r.Id).Distinct().Count(), f.Draft.Rows.Count);
    }

    // Selection

    [Fact]
    public async Task TheSelectedStepKnowsWhetherItIsTopLevel_AndWhichRepeatItBelongsTo()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Draft.SelectedStep = f.Repeat;
        Assert.True(f.Draft.SelectedStep!.IsTopLevel);
        Assert.Null(f.Draft.SelectedStep.Parent);
        Assert.Same(f.Repeat, f.Draft.ChildTarget);

        f.Draft.SelectedStep = f.Dither;
        Assert.True(f.Draft.SelectedStep!.IsChild);
        Assert.Same(f.Repeat, f.Draft.SelectedStep.Parent);
        Assert.Same(f.Repeat, f.Draft.ChildTarget);

        f.Draft.SelectedStep = f.Stop;
        Assert.True(f.Draft.SelectedStep!.IsTopLevel);
        Assert.Null(f.Draft.ChildTarget);
    }

    [Fact]
    public async Task TheSelection_SurvivesMovesAndAdditionsElsewhere()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Delay;

        f.Draft.AddStepCommand.Execute(SequenceStepKind.Delay); // selects the new one
        f.Draft.SelectedStep = f.Delay;
        f.Draft.MoveStepUpCommand.Execute(null);

        Assert.Same(f.Delay, f.Draft.SelectedStep);
        Assert.Contains(f.Delay, f.Draft.Rows);
    }

    // Validation and preview rows

    [Theory]
    [InlineData("0", "Repeat count must be at least 1.")]
    [InlineData("-1", "Repeat count must be at least 1.")]
    [InlineData("2.5", "Repeat count must be a whole number.")]
    [InlineData("abc", "Repeat count must be a whole number.")]
    [InlineData("", "Repeat count must be a whole number.")]
    public async Task AnInvalidCount_MarksTheRepeat_AndDisablesTheDraft(string text, string expected)
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Repeat.CountText = text;

        Assert.Equal([expected], f.Repeat.Problems);
        Assert.False(f.Draft.IsValid);
        Assert.Equal(text, f.Repeat.CountText); // never replaced by a number
    }

    [Fact]
    public async Task AnInvalidChild_ShowsItsProblemOnItsOwnRow_AndMarksTheRepeat_AndTheDraft()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Delay.DurationText = "abc";

        Assert.Equal(["Delay must be a number of seconds."], f.Delay.Problems);
        Assert.Equal(["A step inside has a problem."], f.Repeat.Problems);
        Assert.False(f.Draft.IsValid);
        Assert.Equal(["Step 2.2 (Delay): Delay must be a number of seconds."], f.Draft.ValidationErrors);
        Assert.False(f.Exposure.HasProblems);
        Assert.False(f.Start.HasProblems);
    }

    [Fact]
    public async Task AMissingDeviceInsideARepeat_InvalidatesTheRepeatAndTheDraft_AndKeepsTheSelectionVisible()
    {
        await using var host = CreateHost();
        var f = Build(host);

        host.DeviceRegistry.Unregister(new DeviceId("camera.main"));
        f.Draft.RefreshDevices();

        Assert.Equal(["The camera 'camera.main' is not available."], f.Exposure.Problems.Take(1));
        Assert.Equal(new DeviceId("camera.main"), f.Exposure.Camera.SelectedId);
        Assert.True(f.Exposure.Camera.Selected!.IsMissing);
        Assert.True(f.Dither.HasProblems);
        Assert.True(f.Repeat.HasProblems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task ADitherInsideARepeat_IsInvalidIfItsSettleValuesAre()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Dither.SettleStableText = "20";   // timeout is 10 s

        Assert.Equal(["Settle timeout must be longer than the stable time."], f.Dither.Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task TheGuidingOrder_IsCheckedAcrossAndInsideARepeat_AndTheProblemIsOnTheStepThatIsAffected()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));

        // Start Guiding inside a Repeat of 3: the second repetition would start it again.
        f.Draft.SelectedStep = f.Repeat;
        var start = AddChild<StartGuidingStepDraftViewModel>(f.Draft, SequenceStepKind.StartGuiding);

        Assert.Equal(
            ["Guiding was already started by step 1.", "Guiding was already started by step 2.4 in the previous repetition."],
            start.Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task APreviewOfANestedSequence_ShowsTheRepeatAndItsStepsIndented_AndFollowsEdits()
    {
        await using var host = CreateHost();
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        var draft = vm.SequenceDraft;
        while (draft.Steps.Count > 0)
        {
            draft.SelectedStep = draft.Steps[0];
            draft.RemoveStepCommand.Execute(null);
        }

        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        var repeat = Assert.IsType<RepeatStepDraftViewModel>(draft.SelectedStep);
        repeat.CountText = "10";
        draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        var exposure = Assert.IsType<ExposureStepDraftViewModel>(draft.SelectedStep);
        draft.AddChildCommand.Execute(SequenceStepKind.Delay);
        draft.AddChildCommand.Execute(SequenceStepKind.Dither);
        exposure.ExposureText = "300";

        var rows = vm.Sequencer.Definition;
        Assert.Equal(["Start Guiding", "Repeat × 10", "Exposure", "Delay", "Dither"], rows.Select(r => r.Title));
        Assert.Equal(["1.", "2.", "2.1", "2.2", "2.3"], rows.Select(r => r.NumberText));
        Assert.Equal(SequenceNodeKind.Repeat, rows[1].Kind);
        Assert.Equal("3 steps", rows[1].Detail);
        Assert.Equal(0, rows[1].IndentWidth);
        Assert.All(rows.Skip(2), row => Assert.True(row.IndentWidth > rows[1].IndentWidth));
        Assert.Equal("Main Camera · 300 s · Camera defaults", rows[2].Detail);
        Assert.Equal(new[] { repeat.Id, exposure.Id }, new[] { rows[1].DraftId!.Value, rows[2].DraftId!.Value });

        repeat.CountText = "20";
        exposure.ExposureText = "60";

        Assert.Equal("Repeat × 20", vm.Sequencer.Definition[1].Title);
        Assert.Equal("Main Camera · 60 s · Camera defaults", vm.Sequencer.Definition[2].Detail);
        vm.Dispose();
    }

    [Fact]
    public async Task APreviewShowsTheProblemOfAChildOnTheChild_AndMarksTheRepeat()
    {
        await using var host = CreateHost();
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        var draft = vm.SequenceDraft;
        draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        var exposure = Assert.IsType<ExposureStepDraftViewModel>(draft.SelectedStep);
        var repeat = exposure.Parent!;

        exposure.ExposureText = "x";

        var rows = vm.Sequencer.Definition;
        var repeatRow = rows.Single(r => r.DraftId == repeat.Id);
        var childRow = rows.Single(r => r.DraftId == exposure.Id);
        Assert.True(childRow.IsProblem);
        Assert.Equal("Exposure must be a number of seconds.", childRow.SubText);
        Assert.True(repeatRow.IsProblem);
        Assert.Equal("A step inside has a problem.", repeatRow.SubText);
        vm.Dispose();
    }
}

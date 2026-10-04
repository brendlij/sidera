using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Tests.Ux;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>
/// Reordering steps by drag and drop: what the draft allows, what it refuses (and says why), that the order, the ids and the
/// selection are what the user expects, and that a drop is a change of the document only when something moved.
/// </summary>
public class SequenceDragDropTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host, params SequenceStepDraft[] steps)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var draft = new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, null, host.RigRegistry);
        draft.ReplaceSteps(steps);
        return draft;
    }

    private static ExposureStepDraft Exposure() => new(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1);
    private static DelayStepDraft Delay() => new(Guid.NewGuid(), 1);
    private static SlewStepDraft Slew() => new(Guid.NewGuid(), DemoSetup.MountId, 5.5, -5);
    private static DitherStepDraft Dither() => new(Guid.NewGuid(), DemoSetup.GuiderId, DemoSetup.MountId, DemoSetup.MainCameraId, 0.6, 0.5, 0.1, 5);
    private static RigExposureStepDraft RigExposure() => new(Guid.NewGuid(), 0.1);
    private static RepeatStepDraft Repeat(params LeafStepDraft[] children) => new(Guid.NewGuid(), 3, children);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);
    private static MultiRigStepDraft MultiRig(params RigTrackDraft[] tracks) => new(Guid.NewGuid(), tracks);

    private static List<Guid> Order(SequenceDraftViewModel draft) => draft.Steps.Select(s => s.Id).ToList();

    private static List<Guid> ChildrenOf(SequenceDraftViewModel draft, Guid container) =>
        ((ContainerStepDraftViewModel)draft.Rows.Single(r => r.Id == container)).Children.Select(c => c.Id).ToList();

    private static int Modifications(SequenceDraftViewModel draft, Action act)
    {
        var count = 0;
        draft.Modified += (_, _) => count++;
        act();
        return count;
    }

    // The top level

    [Fact]
    public async Task ATopLevelStepMovesFromFirstToLast_AndKeepsItsIdentity()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay(), Exposure()];
        var draft = CreateDraft(host, steps);
        var ids = steps.Select(s => s.Id).ToList();

        Assert.True(draft.CanMoveStep(ids[0], null, 4));
        var modified = Modifications(draft, () => Assert.True(draft.MoveStep(ids[0], null, 4)));

        Assert.Equal([ids[1], ids[2], ids[3], ids[0]], Order(draft));
        Assert.Equal(["1", "2", "3", "4"], draft.Steps.Select(s => s.NumberLabel)); // the numbers follow the order
        Assert.Equal(ids.Order(), Order(draft).Order()); // nothing was created or lost
        Assert.Equal(1, modified);
    }

    [Fact]
    public async Task ATopLevelStepMovesFromLastToFirst()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay(), Exposure()];
        var draft = CreateDraft(host, steps);
        var ids = steps.Select(s => s.Id).ToList();

        Assert.True(draft.MoveStep(ids[3], null, 0));

        Assert.Equal([ids[3], ids[0], ids[1], ids[2]], Order(draft));
    }

    [Fact]
    public async Task DroppingBeforeOrAfterARowInTheMiddle_PutsTheStepNextToThatRow()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay(), Exposure()];
        var draft = CreateDraft(host, steps);
        var (a, b, c, d) = (steps[0].Id, steps[1].Id, steps[2].Id, steps[3].Id);

        Assert.True(draft.Drop(a, c, DropPlacement.After));
        Assert.Equal([b, c, a, d], Order(draft));

        Assert.True(draft.Drop(d, b, DropPlacement.Before));
        Assert.Equal([d, b, c, a], Order(draft));

        Assert.True(draft.Drop(d, c, DropPlacement.Before));
        Assert.Equal([b, d, c, a], Order(draft));
    }

    [Fact]
    public async Task TheMovedStepIsTheSelectedOne_AlsoWhenAnotherWasSelectedBefore()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        draft.SelectedStep = draft.Steps[2];

        draft.MoveStep(steps[0].Id, null, 3);

        Assert.Equal(steps[0].Id, draft.SelectedStep!.Id);
        Assert.Contains(draft.SelectedStep, draft.Rows);
    }

    [Fact]
    public async Task DroppingAStepWhereItIs_ChangesNothingAndModifiesNothing()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        var before = Order(draft);

        var modified = Modifications(draft, () =>
        {
            Assert.False(draft.MoveStep(steps[1].Id, null, 1)); // before itself
            Assert.False(draft.MoveStep(steps[1].Id, null, 2)); // after itself
            Assert.False(draft.Drop(steps[1].Id, steps[0].Id, DropPlacement.After)); // right where it is
            Assert.False(draft.Drop(steps[1].Id, steps[2].Id, DropPlacement.Before));
        });

        Assert.Equal(0, modified);
        Assert.Equal(before, Order(draft));
        Assert.Equal(StepDropOutcome.Unchanged, draft.PlanDrop(steps[1].Id, steps[1].Id, DropPlacement.Before).Outcome);
    }

    [Fact]
    public async Task ARepeatMovesAsAWhole_WithItsSteps()
    {
        await using var host = CreateHost();
        LeafStepDraft[] inner = [Exposure(), Delay()];
        var repeat = Repeat(inner);
        var start = Slew();
        var draft = CreateDraft(host, repeat, start);

        Assert.True(draft.Drop(repeat.Id, start.Id, DropPlacement.After));

        Assert.Equal([start.Id, repeat.Id], Order(draft));
        Assert.Equal(inner.Select(s => s.Id), ChildrenOf(draft, repeat.Id));
        Assert.Equal([start.Id, repeat.Id, inner[0].Id, inner[1].Id], draft.Rows.Select(r => r.Id)); // its rows follow it
    }

    [Fact]
    public async Task ADropAfterARepeat_IsAfterAllOfItsSteps_AndTheLineIsDrawnAfterTheLastOne()
    {
        await using var host = CreateHost();
        LeafStepDraft[] inner = [Exposure(), Delay()];
        var repeat = Repeat(inner);
        var other = Slew();
        var draft = CreateDraft(host, other, repeat);

        var plan = draft.PlanDrop(other.Id, repeat.Id, DropPlacement.After);

        Assert.True(plan.IsMove);
        Assert.Equal(2, plan.Index);
        Assert.Equal(inner[1].Id, plan.Marker!.Id); // the line is under the last step of the Repeat ...
        Assert.False(plan.MarkerBefore);
        Assert.Equal(0, plan.Over!.Depth); // ... but at the indent of the Repeat itself
    }

    // Inside a Repeat

    [Fact]
    public async Task TheStepsOfARepeat_ReorderWithinIt_AndTheRepeatStaysWhereItIs()
    {
        await using var host = CreateHost();
        var inner = new LeafStepDraft[] { Exposure(), Delay(), Slew() };
        var repeat = Repeat(inner);
        var after = Delay();
        var draft = CreateDraft(host, repeat, after);
        var (e, d, s) = (inner[0].Id, inner[1].Id, inner[2].Id);

        Assert.True(draft.Drop(s, e, DropPlacement.Before));

        Assert.Equal([s, e, d], ChildrenOf(draft, repeat.Id));
        Assert.Equal([repeat.Id, after.Id], Order(draft));
        Assert.Equal(["1.1", "1.2", "1.3"], ((ContainerStepDraftViewModel)draft.Steps[0]).Children.Select(c => c.NumberLabel));
        Assert.Equal(s, draft.SelectedStep!.Id);
    }

    [Fact]
    public async Task AStepOfARepeatCannotLeaveIt_AndATopLevelStepCannotEnterIt()
    {
        await using var host = CreateHost();
        var inner = new LeafStepDraft[] { Exposure(), Delay() };
        var repeat = Repeat(inner);
        var other = Repeat(Exposure());
        var loose = Exposure();
        var draft = CreateDraft(host, repeat, other, loose);

        Assert.False(draft.CanMoveStep(inner[0].Id, null, 0)); // out of its Repeat
        Assert.False(draft.CanMoveStep(inner[0].Id, other.Id, 0)); // into another Repeat
        Assert.False(draft.CanMoveStep(loose.Id, repeat.Id, 1)); // from the top level in
        Assert.Equal("Steps are reordered within their own list.", draft.WhyNotMoveStep(loose.Id, repeat.Id, 1));

        Assert.Equal(inner.Select(s => s.Id), ChildrenOf(draft, repeat.Id));
        Assert.Equal([repeat.Id, other.Id, loose.Id], Order(draft));
    }

    // What the structure forbids

    [Fact]
    public async Task ARepeatCannotBePutIntoARepeat_NotEvenByTheStepsOfItsOwn()
    {
        await using var host = CreateHost();
        var outer = Repeat(Exposure());
        var second = Repeat(Delay());
        var draft = CreateDraft(host, outer, second);
        var before = draft.Rows.Select(r => r.Id).ToList();

        var modified = Modifications(draft, () =>
        {
            Assert.False(draft.MoveStep(second.Id, outer.Id, 0));
            Assert.False(draft.MoveStep(outer.Id, outer.Id, 0));
        });

        Assert.Equal("A Repeat cannot be put into another Repeat.", draft.WhyNotMoveStep(second.Id, outer.Id, 0));
        Assert.Equal("A step cannot be put into itself.", draft.WhyNotMoveStep(outer.Id, outer.Id, 0));
        Assert.Equal(0, modified);
        Assert.Equal(before, draft.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task AMultiRigBlockStaysAtTheTopLevel_AndMovesThereLikeAnyStep()
    {
        await using var host = CreateHost();
        var block = MultiRig(Track(Main, RigExposure()), Track(Wide, RigExposure()));
        var repeat = Repeat(Exposure());
        var slew = Slew();
        var draft = CreateDraft(host, slew, block, repeat);

        Assert.False(draft.CanMoveStep(block.Id, repeat.Id, 0));
        Assert.Equal("A Multi-Rig block belongs at the top level of the sequence.", draft.WhyNotMoveStep(block.Id, repeat.Id, 0));
        Assert.False(draft.CanMoveStep(block.Id, block.Tracks[0].Id, 0));

        Assert.True(draft.Drop(block.Id, slew.Id, DropPlacement.Before));
        Assert.Equal([block.Id, slew.Id, repeat.Id], Order(draft));
        Assert.Equal(block.Tracks.Select(t => t.Id), ChildrenOf(draft, block.Id)); // the tracks went with it
    }

    [Fact]
    public async Task AStepOfARigTrack_CannotBeDroppedAtTheTopLevel_OrIntoAPlainRepeat()
    {
        await using var host = CreateHost();
        var trackStep = RigExposure();
        var block = MultiRig(Track(Main, trackStep, Delay()), Track(Wide, RigExposure()));
        var repeat = Repeat(Exposure());
        var draft = CreateDraft(host, block, repeat);
        var before = draft.Rows.Select(r => r.Id).ToList();

        Assert.False(draft.CanMoveStep(trackStep.Id, null, 0));
        Assert.False(draft.CanMoveStep(trackStep.Id, repeat.Id, 0));
        Assert.Equal("A rig step only exists inside a Rig Track.", draft.WhyNotMoveStep(trackStep.Id, null, 0));
        Assert.Equal(StepDropOutcome.Rejected, draft.PlanDrop(trackStep.Id, repeat.Id, DropPlacement.After).Outcome);
        Assert.False(draft.Drop(trackStep.Id, repeat.Id, DropPlacement.After));

        Assert.Equal(before, draft.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task StepsOfTheSessionCannotBeDroppedIntoARigTrack()
    {
        await using var host = CreateHost();
        var track = Track(Main, RigExposure());
        var block = MultiRig(track, Track(Wide, RigExposure()));
        SequenceStepDraft[] session = [Exposure(), Dither(), Slew(), Delay()];
        var draft = CreateDraft(host, [.. session, block]);
        var before = draft.Rows.Select(r => r.Id).ToList();

        foreach (var step in session) // the Delay may be a step of a track, but it is not moved there: lists keep their steps
        {
            Assert.False(draft.CanMoveStep(step.Id, track.Id, 0), step.GetType().Name);
        }

        Assert.Contains("cannot be part of a Rig Track", draft.WhyNotMoveStep(session[1].Id, track.Id, 0));
        Assert.False(draft.CanMoveStep(session[0].Id, block.Id, 0)); // nor into the block between the tracks
        Assert.Equal("A Multi-Rig block holds Rig Tracks only.", draft.WhyNotMoveStep(session[0].Id, block.Id, 0));
        Assert.Equal(before, draft.Rows.Select(r => r.Id));
    }

    [Fact]
    public async Task ATargetThatIsNotAContainer_OrNotThere_IsRefused()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure()];
        var draft = CreateDraft(host, steps);

        Assert.False(draft.CanMoveStep(steps[0].Id, steps[1].Id, 0));
        Assert.False(draft.CanMoveStep(steps[0].Id, Guid.NewGuid(), 0));
        Assert.False(draft.CanMoveStep(Guid.NewGuid(), null, 0));
        Assert.False(draft.CanMoveStep(steps[0].Id, null, 3)); // beyond the end of the list
        Assert.False(draft.CanMoveStep(steps[0].Id, null, -1));
        Assert.Equal(StepDropOutcome.Rejected, draft.PlanDrop(steps[0].Id, Guid.NewGuid(), DropPlacement.After).Outcome);
    }

    // Rig tracks

    [Fact]
    public async Task TheStepsOfARigTrack_ReorderWithinIt_AndSoDoTheStepsOfARepeatInsideIt()
    {
        await using var host = CreateHost();
        var inner = new LeafStepDraft[] { RigExposure(), new DelayStepDraft(Guid.NewGuid(), 1), RigExposure() };
        var repeat = Repeat(inner);
        SequenceStepDraft[] trackSteps = [RigExposure(), new DelayStepDraft(Guid.NewGuid(), 2), repeat];
        var track = Track(Main, trackSteps);
        var draft = CreateDraft(host, MultiRig(track, Track(Wide, RigExposure())));

        Assert.True(draft.Drop(trackSteps[2].Id, trackSteps[0].Id, DropPlacement.Before)); // the Repeat to the head of the track
        Assert.Equal([trackSteps[2].Id, trackSteps[0].Id, trackSteps[1].Id], ChildrenOf(draft, track.Id));

        Assert.True(draft.Drop(inner[2].Id, inner[0].Id, DropPlacement.Before)); // inside the Repeat of the track
        Assert.Equal([inner[2].Id, inner[0].Id, inner[1].Id], ChildrenOf(draft, repeat.Id));
        Assert.Equal(inner[2].Id, draft.SelectedStep!.Id);
    }

    [Fact]
    public async Task AStepOfARigTrack_CannotMoveToAnotherTrack_OrOutOfItsRepeat()
    {
        await using var host = CreateHost();
        var inner = new LeafStepDraft[] { RigExposure(), RigExposure() };
        var repeat = Repeat(inner);
        var first = Track(Main, RigExposure(), repeat);
        var second = Track(Wide, RigExposure());
        var draft = CreateDraft(host, MultiRig(first, second));

        Assert.False(draft.CanMoveStep(first.Steps[0].Id, second.Id, 0));
        Assert.False(draft.CanMoveStep(inner[0].Id, first.Id, 0));
        Assert.False(draft.CanMoveStep(inner[0].Id, second.Id, 0));
    }

    [Fact]
    public async Task TheTracksOfABlock_CanBeReordered_AndOnlyAmongThemselves()
    {
        await using var host = CreateHost();
        var (main, wide) = (Track(Main, RigExposure()), Track(Wide, RigExposure()));
        var block = MultiRig(main, wide);
        var after = Delay();
        var draft = CreateDraft(host, block, after);

        Assert.True(draft.Drop(wide.Id, main.Id, DropPlacement.Before));
        Assert.Equal([wide.Id, main.Id], ChildrenOf(draft, block.Id));

        Assert.False(draft.CanMoveStep(wide.Id, null, 2)); // a track does not leave its block
        Assert.Equal("A Rig Track belongs in a Multi-Rig block.", draft.WhyNotMoveStep(wide.Id, null, 2));
    }

    // While the sequence runs

    [Fact]
    public async Task WhenTheDraftIsNotEditable_NothingMoves_NothingIsDragged_AndTheReasonIsSaid()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        draft.IsEditable = false;
        var before = Order(draft);

        Assert.False(draft.CanMoveStep(steps[0].Id, null, 3));
        Assert.False(draft.MoveStep(steps[0].Id, null, 3));
        Assert.False(draft.Drop(steps[0].Id, steps[2].Id, DropPlacement.After));
        Assert.False(draft.BeginDrag(steps[0].Id));
        Assert.Null(draft.DraggedStep);
        Assert.Equal("The sequence cannot be changed while it runs.", draft.WhyNotMoveStep(steps[0].Id, null, 3));
        Assert.Equal(before, Order(draft));
    }

    [Fact]
    public async Task ADragInProgress_EndsWhenTheSequenceStartsRunning()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        Assert.True(draft.BeginDrag(steps[0].Id));
        draft.ShowDrop(draft.PlanDrop(steps[0].Id, steps[2].Id, DropPlacement.After));

        draft.IsEditable = false;

        Assert.Null(draft.DraggedStep);
        Assert.All(draft.Rows, row => Assert.False(row.IsDragSource || row.ShowsDropAfter || row.ShowsDropBefore || row.IsDropRejected));
    }

    [Fact]
    public async Task TheDraftCannotBeReordered_WhileTheSequenceRunsPausesAndIsPaused_AndCanAfterwards()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, connect: true);
        SequenceStepDraft[] steps = [new DelayStepDraft(Guid.NewGuid(), 0.4), new DelayStepDraft(Guid.NewGuid(), 0.4), new DelayStepDraft(Guid.NewGuid(), 0.4)];
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps(steps);
        var before = Order(draft);

        var run = app.Vm.Sequencer.RunCommand.ExecuteAsync(null);
        await UxApp.WaitUntil(() => app.Vm.Sequencer.State == SequenceState.Running, "running");
        Assert.False(draft.MoveStep(steps[0].Id, null, 3));
        Assert.False(draft.BeginDrag(steps[0].Id));

        app.Vm.Sequencer.PauseCommand.Execute(null);
        Assert.Equal(SequenceState.Pausing, app.Vm.Sequencer.State);
        Assert.False(draft.MoveStep(steps[0].Id, null, 3));

        await UxApp.WaitUntil(() => app.Vm.Sequencer.State == SequenceState.Paused, "paused");
        Assert.False(draft.MoveStep(steps[0].Id, null, 3));
        Assert.False(draft.CanMoveStep(steps[0].Id, null, 3));
        Assert.Equal(before, Order(draft));

        app.Vm.Sequencer.CancelCommand.Execute(null);
        await run;

        Assert.True(draft.CanMoveStep(steps[0].Id, null, 3));
        Assert.True(draft.MoveStep(steps[0].Id, null, 3));
        Assert.Equal([before[1], before[2], before[0]], Order(draft));
    }

    // The marks the view draws

    [Fact]
    public async Task ADragMarksItsSource_AValidPlaceWithALine_AndARefusedOneWithoutAnyLine()
    {
        await using var host = CreateHost();
        var inner = Exposure();
        var repeat = Repeat(inner);
        var slew = Slew();
        var delay = Delay();
        var draft = CreateDraft(host, slew, repeat, delay);

        Assert.True(draft.BeginDrag(slew.Id));
        Assert.True(draft.Rows.Single(r => r.Id == slew.Id).IsDragSource);
        Assert.Equal(slew.Id, draft.SelectedStep!.Id); // the dragged step is the selected one

        draft.ShowDrop(draft.PlanDrop(slew.Id, delay.Id, DropPlacement.After));
        var last = draft.Rows.Single(r => r.Id == delay.Id);
        Assert.True(last.ShowsDropAfter);
        Assert.False(last.ShowsDropBefore);

        draft.ShowDrop(draft.PlanDrop(slew.Id, inner.Id, DropPlacement.Before)); // into the Repeat: refused
        Assert.False(last.ShowsDropAfter); // the line of the place before is gone
        var refused = draft.Rows.Single(r => r.Id == inner.Id);
        Assert.True(refused.IsDropRejected);
        Assert.DoesNotContain(draft.Rows, row => row.ShowsDropAfter || row.ShowsDropBefore);

        draft.EndDrag();
        Assert.All(draft.Rows, row => Assert.False(row.IsDragSource || row.IsDropRejected || row.ShowsDropAfter || row.ShowsDropBefore));
        Assert.Null(draft.DraggedStep);
        Assert.Equal([slew.Id, repeat.Id, delay.Id], Order(draft)); // marking changed nothing
    }

    [Fact]
    public async Task ACancelledDrag_ChangesNothing_AndModifiesNothing()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        var before = Order(draft);

        var modified = Modifications(draft, () =>
        {
            Assert.True(draft.BeginDrag(steps[0].Id));
            draft.ShowDrop(draft.PlanDrop(steps[0].Id, steps[2].Id, DropPlacement.After));
            draft.EndDrag(); // the drag ended outside of the list
        });

        Assert.Equal(0, modified);
        Assert.Equal(before, Order(draft));
    }

    [Fact]
    public async Task MoveUpAndMoveDown_StillWork_AfterADrop()
    {
        await using var host = CreateHost();
        SequenceStepDraft[] steps = [Slew(), Exposure(), Delay()];
        var draft = CreateDraft(host, steps);
        draft.Drop(steps[0].Id, steps[2].Id, DropPlacement.After); // Exposure, Delay, Slew; the Slew is selected

        draft.MoveStepUpCommand.Execute(null);
        Assert.Equal([steps[1].Id, steps[0].Id, steps[2].Id], Order(draft));

        draft.MoveStepDownCommand.Execute(null);
        Assert.Equal([steps[1].Id, steps[2].Id, steps[0].Id], Order(draft));
    }
}

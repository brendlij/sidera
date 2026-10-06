using Sidera.Core.Sequencing;
using Sidera.Desktop;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>A step can be dropped into a container, also an empty one: the middle of its row.</summary>
public class DropIntoContainerTests
{
    private static SequenceDraftViewModel Draft(UxApp app)
    {
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps([]);
        draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        draft.AddStepCommand.Execute(SequenceStepKind.Exposure);
        return draft;
    }

    [Fact]
    public async Task AnExposure_CanBeDroppedIntoAnEmptyRepeat()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var draft = Draft(app);
        var repeat = (ContainerStepDraftViewModel)draft.Steps[0];
        var exposure = draft.Steps[1];

        var plan = draft.PlanDrop(exposure.Id, repeat.Id, DropPlacement.Into);

        Assert.True(plan.IsMove, plan.Reason);
        Assert.Equal(repeat.Id, plan.ParentId);
        Assert.True(draft.Drop(exposure.Id, repeat.Id, DropPlacement.Into));
        Assert.Single(repeat.Children);
        Assert.Same(exposure, repeat.Children[0]);
        Assert.Single(draft.Steps);
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task IntoARepeatThatHasSteps_PutsTheStepAfterThem()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var draft = Draft(app);
        var repeat = (ContainerStepDraftViewModel)draft.Steps[0];
        draft.Drop(draft.Steps[1].Id, repeat.Id, DropPlacement.Into);
        draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        var delay = draft.Steps[^1];

        Assert.True(draft.Drop(delay.Id, repeat.Id, DropPlacement.Into));

        Assert.Equal([SequenceStepKind.Exposure, SequenceStepKind.Delay], repeat.Children.Select(c => c.Kind));
    }

    [Fact]
    public async Task IntoAStepThatCannotHoldSteps_OrARepeatIntoARepeat_IsRefused()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var draft = Draft(app);
        var exposure = draft.Steps[1];
        draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        var second = draft.Steps[^1];

        Assert.False(draft.PlanDrop(second.Id, exposure.Id, DropPlacement.Into).IsMove);
        Assert.False(draft.PlanDrop(second.Id, draft.Steps[0].Id, DropPlacement.Into).IsMove); // a Repeat holds no Repeat
    }
}

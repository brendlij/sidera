using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>The session by rig: Overview, one tab for each rig and Shared are filters over the one sequence; nothing is copied, and with one rig there are no tabs.</summary>
public class SessionScopeViewTests
{
    private static RigTrackDraftViewModel AddTrack(SequenceDraftViewModel draft, MultiRigStepDraftViewModel block, string rig)
    {
        draft.SelectedStep = block;
        draft.AddTrackCommand.Execute(null);
        var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
        track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
        return track;
    }

    private static MultiRigStepDraftViewModel Build(SequenceDraftViewModel draft)
    {
        draft.ReplaceSteps([]);
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        AddTrack(draft, block, "rig.main");
        AddTrack(draft, block, "rig.wide");
        return block;
    }

    [Fact]
    public async Task TheTabs_AreOverviewEachRigAndShared_AndTheOverviewShowsEverything()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        Build(draft);

        Assert.True(draft.HasScopeTabs);
        Assert.Equal(["Overview", "Main Rig", "Narrow Rig", "Wide Rig", "Shared"], draft.ScopeTabs.Select(t => t.Title));
        Assert.Equal("overview", draft.SelectedScopeKey);
        Assert.All(draft.Rows, row => Assert.True(row.IsInScopeView));
    }

    [Fact]
    public async Task ARigTab_ShowsItsStepsAndTheContainersAroundThem_NotTheOtherRigs()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        var block = Build(draft);
        var main = block.Children[0];
        var wide = block.Children[1];

        draft.SelectScope("rig.wide");

        Assert.Equal("rig.wide", draft.SelectedScopeKey);
        Assert.True(block.IsInScopeView); // the block is shown while a track inside it is
        Assert.True(wide.IsInScopeView);
        Assert.All(((ContainerStepDraftViewModel)wide).Children, c => Assert.True(c.IsInScopeView));
        Assert.False(main.IsInScopeView);
        Assert.All(((ContainerStepDraftViewModel)main).Children, c => Assert.False(c.IsInScopeView));
        Assert.False(draft.Rows.First(r => r.Kind == SequenceStepKind.StartGuiding).IsInScopeView);
        Assert.Equal(1, draft.ScopeTabs.Single(t => t.Key == "rig.wide").Count);
    }

    [Fact]
    public async Task TheSharedTab_ShowsTheStepsOfTheMountAndTheGuider_AndEveryRowSaysWhoseItIs()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        var block = Build(draft);

        draft.SelectScope("shared");

        var guiding = draft.Rows.First(r => r.Kind == SequenceStepKind.StartGuiding);
        Assert.True(guiding.IsInScopeView);
        Assert.Equal("Shared", guiding.ScopeLabel);
        Assert.False(block.Children[0].IsInScopeView);
        Assert.Equal("Main Rig", block.Children[0].ScopeLabel);
        Assert.Equal("Main Rig", ((ContainerStepDraftViewModel)block.Children[0]).Children[0].ScopeLabel); // a step in a track is the rig of the track
        Assert.Equal("Parallel", block.ScopeLabel);
    }

    [Fact]
    public async Task TheTabsAreOnlyAProjection_TheSequenceIsTheSameInEveryView()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        Build(draft);
        var before = draft.Snapshot().Select(step => step.Id).ToList();
        var rows = draft.Rows.ToList();

        foreach (var key in new[] { "rig.main", "shared", "rig.wide", "overview" })
        {
            draft.SelectScope(key);
        }

        Assert.Equal(before, draft.Snapshot().Select(step => step.Id));
        Assert.Equal(rows.Count, draft.Rows.Count);
        Assert.Equal(rows, draft.Rows);
    }

    [Fact]
    public async Task WithOneRig_ThereAreNoTabs_AndEveryStepIsShown()
    {
        await using var app = await UxApp.Create(UxSetup.OneRig);
        var draft = app.Vm.SessionPage.Draft;
        draft.ReplaceSteps([]);
        draft.AddStepCommand.Execute(SequenceStepKind.Delay);

        Assert.False(draft.HasScopeTabs);
        Assert.All(draft.Rows, row => Assert.True(row.IsInScopeView));
    }

    [Fact]
    public async Task ARigThatIsGone_FallsBackToTheOverview()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        Build(draft);

        draft.SelectScope("rig.that.does.not.exist");

        Assert.Equal("overview", draft.SelectedScopeKey);
    }

    [Fact]
    public async Task ChoosingATab_TheWayTheTabButtonDoes_ShowsThatView()
    {
        await using var app = await UxApp.Create(UxSetup.Demo);
        var draft = app.Vm.SessionPage.Draft;
        Build(draft);

        draft.ScopeTabs.Single(t => t.Key == "rig.main").IsSelected = true;

        Assert.Equal("rig.main", draft.SelectedScopeKey);
        Assert.Equal(["Main Rig"], draft.ScopeTabs.Where(t => t.IsSelected).Select(t => t.Title));
    }
}

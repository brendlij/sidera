using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Autofocus in the editor: outside a track with a rig to pick, inside one with the rig of the track.</summary>
public class AutofocusEditingTests
{
    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var shared = new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId);
        return new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, null, clipboard, host.RigRegistry, shared, host.FocusMetrics, host.EventBus);
    }

    private static T Add<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddStepCommand.Execute(kind);
        return Assert.IsType<T>(draft.SelectedStep);
    }

    private static (MultiRigStepDraftViewModel Block, RigTrackDraftViewModel Main, RigTrackDraftViewModel Wide) AddBlock(
        SequenceDraftViewModel draft)
    {
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        var tracks = new List<RigTrackDraftViewModel>();
        foreach (var rig in new[] { "rig.main", "rig.wide" })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            tracks.Add(track);
        }

        draft.SelectedStep = tracks[0];
        return (block, tracks[0], tracks[1]);
    }

    // Outside a track

    [Fact]
    public async Task AddingAnAutofocus_AppendsAStepForARigWithAFocuser_WithTheDefaultSettings()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);

        Assert.Equal(new RigId("rig.main"), step.Rig.SelectedId);
        Assert.Equal(("1", "400", "7"), (step.ExposureText, step.StepSizeText, step.SamplesText));
        Assert.Equal(("Autofocus", "Main Rig · 1 s · step 400 · 7 samples"), (step.Title, step.Summary));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task TheRigPicker_OffersTheRigsByName_AndChoosingAnotherChangesThePreview()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);

        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig"], step.Rig.Options.Select(o => o.Name).Order());

        step.Rig.Selected = step.Rig.Options.Single(o => o.IdText == "rig.narrow");

        Assert.Equal("Narrow Rig · 1 s · step 400 · 7 samples", step.Summary);
        Assert.Equal(new RigId("rig.narrow"), Assert.IsType<AutofocusStepDraft>(draft.Snapshot().Single()).RigId);
    }

    [Fact]
    public async Task NoCameraOrFocuserIsSelectable_TheRigDecides()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);

        Assert.NotNull(step.Rig);
        Assert.Null(typeof(AutofocusStepDraftViewModel).GetProperty("Camera"));
        Assert.Null(typeof(AutofocusStepDraftViewModel).GetProperty("Focuser"));
    }

    [Theory]
    [InlineData("0", "400", "7", "Autofocus exposure must be greater than 0 s.")]
    [InlineData("abc", "400", "7", "Autofocus exposure must be a number of seconds.")]
    [InlineData("1", "0", "7", "Autofocus step size must be greater than 0.")]
    [InlineData("1", "x", "7", "Autofocus step size must be a whole number.")]
    [InlineData("1", "400", "6", "Autofocus samples must be an odd number between 5 and 21.")]
    [InlineData("1", "400", "3", "Autofocus samples must be an odd number between 5 and 21.")]
    [InlineData("1", "400", "7.5", "Autofocus samples must be a whole number.")]
    public async Task AWrongSetting_IsReportedOnTheStep_AndTheDraftIsNotRunnable(string exposure, string step, string samples, string expected)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var autofocus = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);

        autofocus.ExposureText = exposure;
        autofocus.StepSizeText = step;
        autofocus.SamplesText = samples;

        Assert.Contains(expected, autofocus.Problems);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public async Task ARigWithoutAFocuser_IsReportedOnTheStep_NotReplacedByAnotherRig()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176)));
        var draft = CreateDraft(host);
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);

        step.Rig.Selected = step.Rig.Options.Single(o => o.IdText == "rig.bare");

        Assert.Equal(["The rig 'rig.bare' has no focuser."], step.Problems);
        Assert.Equal(new RigId("rig.bare"), step.Rig.SelectedId);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public async Task ARigThatIsNoLongerThere_StaysSelected_AndIsReported()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        draft.ReplaceSteps([new AutofocusStepDraft(Guid.NewGuid(), new RigId("rig.gone"), 1, 400, 7)]);

        var step = Assert.IsType<AutofocusStepDraftViewModel>(draft.Rows.Single());

        Assert.True(step.Rig.Selected!.IsMissing);
        Assert.Equal(["The rig 'rig.gone' is not available."], step.Problems);
    }

    [Fact]
    public async Task TheFocuserRangeLimitsTheStepSize_OnTheStep()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);
        step.Rig.Selected = step.Rig.Options.Single(o => o.IdText == "rig.wide"); // 0 to 12000

        step.StepSizeText = "3500";

        Assert.Contains(step.Problems, p => p.Contains("does not have enough travel", StringComparison.Ordinal));
    }

    // Inside a track

    [Fact]
    public async Task InATrack_TheMenuMakesAnAutofocusOfTheRig_WithNoRigToSelect()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);

        Assert.True(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Autofocus));
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        var step = Assert.IsType<RigAutofocusStepDraftViewModel>(draft.SelectedStep);

        Assert.Same(main, step.Parent);
        Assert.Equal(("1", "400", "7"), (step.ExposureText, step.StepSizeText, step.SamplesText));
        Assert.Equal(("Autofocus", "1 s · step 400 · 7 samples"), (step.Title, step.Summary));
        Assert.Null(typeof(RigAutofocusStepDraftViewModel).GetProperty("Rig")); // the rig is the rig of the track
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigAutofocus_CanBeAddedInsideARepeatOfATrack()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        AddBlock(draft);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Repeat);
        var repeat = Assert.IsType<RepeatStepDraftViewModel>(draft.SelectedStep);

        draft.AddChildCommand.Execute(SequenceStepKind.Autofocus);

        var step = Assert.IsType<RigAutofocusStepDraftViewModel>(draft.SelectedStep);
        Assert.Same(repeat, step.Parent);
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task TheRigKind_CannotBeAddedToTheSequenceItself()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        draft.AddStepCommand.Execute(SequenceStepKind.RigAutofocus);

        Assert.Empty(draft.Steps);
    }

    [Fact]
    public async Task ATrackOfARigWithoutAFocuser_ShowsTheProblemOnTheAutofocus_AndItIsGoneWhenTheTrackGetsAnotherRig()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176)));
        var draft = CreateDraft(host);
        var (_, _, wide) = AddBlock(draft);
        draft.SelectedStep = wide;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        var step = Assert.IsType<RigAutofocusStepDraftViewModel>(draft.SelectedStep);
        Assert.Empty(step.Problems);

        wide.Rig.Selected = wide.Rig.Options.Single(o => o.IdText == "rig.bare");

        Assert.Equal(["The rig 'rig.bare' has no focuser."], step.Problems);
        Assert.Equal("the rig has no focuser", step.Summary);
        Assert.False(draft.IsValid);

        wide.Rig.Selected = wide.Rig.Options.Single(o => o.IdText == "rig.narrow");

        Assert.Empty(step.Problems);
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    // Copy, duplicate, paste

    [Fact]
    public async Task DuplicatingAnAutofocus_MakesACopyWithANewId_RightAfterIt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);
        step.StepSizeText = "250";

        draft.DuplicateStepCommand.Execute(null);

        var steps = draft.Snapshot().Cast<AutofocusStepDraft>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.NotEqual(steps[0].Id, steps[1].Id);
        Assert.Equal((steps[0].RigId, steps[0].StepSize), (steps[1].RigId, steps[1].StepSize));
    }

    [Fact]
    public async Task ACopiedAutofocus_IsASnapshot_LaterEditsOfTheOriginalDoNotReachIt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);
        step.SamplesText = "9";
        draft.CopyStepCommand.Execute(null);
        step.SamplesText = "5";
        draft.SelectedStep = null;

        draft.PasteStepCommand.Execute(null);

        var steps = draft.Snapshot().Cast<AutofocusStepDraft>().ToList();
        Assert.Equal([5, 9], steps.Select(s => s.SampleCount));
        Assert.NotEqual(steps[0].Id, steps[1].Id);
    }

    [Fact]
    public async Task ARigAutofocus_IsPastedIntoATrack_ButNotIntoTheSequence_AndATopLevelOneNotIntoATrack()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var top = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);
        var (block, main, wide) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        draft.CopyStepCommand.Execute(null);

        draft.SelectedStep = wide;
        Assert.True(draft.PasteStepCommand.CanExecute(null));
        draft.PasteStepCommand.Execute(null);
        Assert.IsType<RigAutofocusStepDraftViewModel>(draft.SelectedStep);
        Assert.Same(wide, draft.SelectedStep!.Parent);
        draft.SelectedStep = null;
        Assert.False(draft.PasteStepCommand.CanExecute(null));
        draft.SelectedStep = block;
        Assert.False(draft.PasteStepCommand.CanExecute(null));

        draft.SelectedStep = top;
        draft.CopyStepCommand.Execute(null);
        draft.SelectedStep = main;
        Assert.False(draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADuplicatedBlock_KeepsItsAutofocusOnTheSameRig_WithNewIds()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (block, main, _) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        draft.SelectedStep = block;

        draft.DuplicateStepCommand.Execute(null);

        var blocks = draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        var steps = blocks.Select(b => b.Tracks[0].Steps.OfType<RigAutofocusStepDraft>().Single()).ToList();
        Assert.NotEqual(steps[0].Id, steps[1].Id);
        Assert.Equal(steps[0] with { Id = default }, steps[1] with { Id = default });
    }

    // Dirty state and the lock

    [Fact]
    public async Task EditingTheSettings_IsAModification_EachOfThem()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<AutofocusStepDraftViewModel>(draft, SequenceStepKind.Autofocus);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        step.Rig.Selected = step.Rig.Options.Single(o => o.IdText == "rig.narrow");
        step.ExposureText = "2";
        step.StepSizeText = "300";
        step.SamplesText = "9";

        Assert.Equal(4, modifications);
    }

    [Fact]
    public async Task EditingARigAutofocus_IsAModification_ButReadingTheDevicesAgainIsNot()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        var step = Assert.IsType<RigAutofocusStepDraftViewModel>(draft.SelectedStep);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        draft.RefreshDevices();
        Assert.Equal(0, modifications);

        step.StepSizeText = "500";
        Assert.Equal(1, modifications);
    }

    [Fact]
    public async Task WhileNotEditable_AnAutofocusCannotBeAdded()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;

        draft.IsEditable = false;

        Assert.False(draft.AddStepCommand.CanExecute(SequenceStepKind.Autofocus));
        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Autofocus));
        Assert.False(draft.AddChildCommand.CanExecute(SequenceStepKind.Autofocus));
    }

    [Fact]
    public async Task OutsideAMultiRigBlock_NoTrackAutofocusCanBeAdded()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Autofocus));
    }
}

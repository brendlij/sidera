using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Move Focuser and Change Filter in the editor: outside a track with a device, inside one with the rig's.</summary>
public class HardwareStepEditingTests
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
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, clipboard, host.RigRegistry, shared);
    }

    private static T Add<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddStepCommand.Execute(kind);
        return Assert.IsType<T>(draft.SelectedStep);
    }

    // A block of two tracks (Main and Wide) with an exposure each; the main track is selected.
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

    // Top level

    [Fact]
    public async Task AddingMoveFocuser_AppendsAStepWithTheDefaultFocuser_AtItsCurrentPosition()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var step = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);

        Assert.Equal(DemoSetup.MainFocuserId, step.Focuser.SelectedId);
        Assert.Equal("18200", step.PositionText);
        Assert.Equal("0 to 50000", step.RangeLabel);
        Assert.Equal(("Move Focuser", "Main Focuser · 18200"), (step.Title, step.Summary));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task TheFocuserPicker_OffersTheFocusersOnly_ByName()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var step = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);

        Assert.Equal(["Main Focuser", "Narrow Focuser", "Wide Focuser"], step.Focuser.Options.Select(o => o.Name).Order());
    }

    [Fact]
    public async Task AddingChangeFilter_OffersTheFiltersOfTheWheelByName_AndKeepsTheSlotIndex()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var step = Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);

        Assert.Equal(DemoSetup.MainFilterWheelId, step.Wheel.SelectedId);
        Assert.Equal(["L", "R", "G", "B", "Ha", "OIII", "SII"], step.Filter.Options.Select(o => o.Name));
        Assert.Equal("L", step.Filter.Selected!.Name);

        step.Filter.Selected = step.Filter.Options.Single(o => o.Name == "Ha");

        var saved = Assert.IsType<ChangeFilterStepDraft>(draft.Snapshot().Single());
        Assert.Equal(4, saved.SlotIndex);
        Assert.Equal(("Change Filter", "Main Filter Wheel · Ha"), (step.Title, step.Summary));
    }

    [Fact]
    public async Task ChoosingAnotherWheel_ShowsItsFilters_AndKeepsTheSlot_ReportedWhenTheWheelHasNone()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);
        step.Filter.Selected = step.Filter.Options.Single(o => o.Name == "SII"); // slot 6

        step.Wheel.Selected = step.Wheel.Options.Single(o => o.IdText == "filterwheel.narrow"); // four slots

        Assert.Equal(["L", "Ha", "OIII", "SII"], step.Filter.Options.Where(o => !o.IsMissing).Select(o => o.Name));
        Assert.True(step.Filter.Selected!.IsMissing); // slot 6 is kept, not replaced by another filter
        Assert.Equal(6, step.Filter.SelectedIndex);
        Assert.Equal(["The filter wheel 'filterwheel.narrow' has no slot 6."], step.Problems);
        Assert.False(draft.IsValid);

        step.Filter.Selected = step.Filter.Options.Single(o => o.Name == "SII");
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal(3, step.Filter.SelectedIndex);
    }

    [Theory]
    [InlineData("abc", "Focuser position must be a whole number.")]
    [InlineData("1.5", "Focuser position must be a whole number.")]
    [InlineData("", "Focuser position must be a whole number.")]
    [InlineData("-3", "Focuser position must be between 0 and 50000.")]
    [InlineData("50001", "Focuser position must be between 0 and 50000.")]
    public async Task AWrongPosition_IsReportedOnTheStep(string text, string expected)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var step = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);

        step.PositionText = text;

        Assert.Contains(expected, step.Problems);
        Assert.False(draft.IsValid);
        Assert.False(draft.CanDuplicate && text is "abc" or "1.5" or ""); // an unreadable field cannot be copied faithfully
    }

    [Fact]
    public async Task ADeviceThatIsNoLongerThere_StaysSelected_AndIsReported()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        draft.ReplaceSteps([new MoveFocuserStepDraft(Guid.NewGuid(), new DeviceId("focuser.gone"), 100)]);

        var step = Assert.IsType<MoveFocuserStepDraftViewModel>(draft.Rows.Single());

        Assert.True(step.Focuser.Selected!.IsMissing);
        Assert.Equal(["The focuser 'focuser.gone' is not available."], step.Problems);
        Assert.False(draft.IsValid);
    }

    // Inside a track

    [Fact]
    public async Task InATrack_TheMenuEntriesMakeTheStepsOfTheRig_NoDeviceToSelect()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);

        Assert.True(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.MoveFocuser));
        draft.AddTrackStepCommand.Execute(SequenceStepKind.MoveFocuser);
        var move = Assert.IsType<RigMoveFocuserStepDraftViewModel>(draft.SelectedStep);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<RigChangeFilterStepDraftViewModel>(draft.SelectedStep);

        Assert.Same(main, move.Parent);
        Assert.Equal(("Move Focuser", "Main Focuser · 18200"), (move.Title, move.Summary));
        Assert.Equal(["L", "R", "G", "B", "Ha", "OIII", "SII"], change.Filter.Options.Select(o => o.Name)); // the main rig's wheel
        Assert.Equal(("Change Filter", "Main Filter Wheel · L"), (change.Title, change.Summary));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigStepOfAnotherRigsTrack_ShowsThatRigsFilters()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (block, _, _) = AddBlock(draft);
        draft.SelectedStep = block;
        draft.AddTrackCommand.Execute(null);
        var narrow = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
        narrow.Rig.Selected = narrow.Rig.Options.Single(o => o.IdText == "rig.narrow");

        draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<RigChangeFilterStepDraftViewModel>(draft.SelectedStep);

        Assert.Equal(["L", "Ha", "OIII", "SII"], change.Filter.Options.Select(o => o.Name));
    }

    [Fact]
    public async Task ARigWithoutAFocuserOrWheel_MakesTheRigStepsInvalid_ThenValidAgainWhenTheTrackGetsAnotherRig()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, wide) = AddBlock(draft);
        draft.SelectedStep = wide; // the wide rig has a focuser but no filter wheel
        draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<RigChangeFilterStepDraftViewModel>(draft.SelectedStep);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.MoveFocuser);
        var move = Assert.IsType<RigMoveFocuserStepDraftViewModel>(draft.SelectedStep);

        Assert.Equal(["The rig 'rig.wide' has no filter wheel."], change.Problems);
        Assert.Empty(move.Problems);
        Assert.Equal("the rig has no filter wheel", change.Summary);
        Assert.False(draft.IsValid);
        Assert.True(change.Filter.Selected!.IsMissing); // no wheel: no names, the slot is kept

        wide.Rig.Selected = wide.Rig.Options.Single(o => o.IdText == "rig.narrow");

        Assert.Empty(change.Problems);
        Assert.Equal(["L", "Ha", "OIII", "SII"], change.Filter.Options.Where(o => !o.IsMissing).Select(o => o.Name));
        Assert.Equal("Narrow Filter Wheel · L", change.Summary);
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        _ = main;
    }

    [Fact]
    public async Task RigStepsCanBeAddedInsideARepeatOfATrack_AndTopLevelStepsCannot()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Repeat);
        var repeat = Assert.IsType<RepeatStepDraftViewModel>(draft.SelectedStep);
        Assert.True(repeat.IsInTrack);

        draft.AddChildCommand.Execute(SequenceStepKind.MoveFocuser);
        var move = Assert.IsType<RigMoveFocuserStepDraftViewModel>(draft.SelectedStep);
        draft.AddChildCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<RigChangeFilterStepDraftViewModel>(draft.SelectedStep);

        Assert.Same(repeat, move.Parent);
        Assert.Same(repeat, change.Parent);
        Assert.Equal("Main Focuser · 18200", move.Summary);
        Assert.False(draft.AddChildCommand.CanExecute(SequenceStepKind.Slew));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        _ = main;
    }

    [Fact]
    public async Task TheRigKindsCannotBeAddedToTheSequenceItself_AndNotThroughTheTopLevelMenu()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        draft.AddStepCommand.Execute(SequenceStepKind.RigMoveFocuser);
        draft.AddStepCommand.Execute(SequenceStepKind.RigChangeFilter);

        Assert.Empty(draft.Steps);
    }

    [Fact]
    public async Task OutsideAMultiRigBlock_NoTrackStepCanBeAdded()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.MoveFocuser));
        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.ChangeFilter));
    }

    // Copy, duplicate, paste

    [Fact]
    public async Task DuplicatingAHardwareStep_MakesACopyWithANewId_RightAfterIt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var move = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);
        move.PositionText = "19000";

        draft.DuplicateStepCommand.Execute(null);

        var steps = draft.Snapshot().Cast<MoveFocuserStepDraft>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.NotEqual(steps[0].Id, steps[1].Id);
        Assert.Equal((steps[0].FocuserId, steps[0].Position), (steps[1].FocuserId, steps[1].Position));
    }

    [Fact]
    public async Task ACopiedFilterChange_IsPastedAtTheEndWithANewId()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var change = Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);
        change.Filter.Selected = change.Filter.Options.Single(o => o.Name == "OIII");
        draft.CopyStepCommand.Execute(null);
        draft.SelectedStep = null;

        draft.PasteStepCommand.Execute(null);

        var steps = draft.Snapshot().Cast<ChangeFilterStepDraft>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.NotEqual(steps[0].Id, steps[1].Id);
        Assert.Equal(5, steps[1].SlotIndex);
    }

    [Fact]
    public async Task ARigStep_CanBePastedIntoATrack_ButNotIntoTheSequenceOrOntoATopLevelRepeat()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var (block, main, wide) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.MoveFocuser);
        draft.CopyStepCommand.Execute(null);

        draft.SelectedStep = wide;
        Assert.True(draft.PasteStepCommand.CanExecute(null));
        draft.PasteStepCommand.Execute(null);
        Assert.IsType<RigMoveFocuserStepDraftViewModel>(draft.SelectedStep);
        Assert.Same(wide, draft.SelectedStep!.Parent);

        draft.SelectedStep = null;
        Assert.False(draft.PasteStepCommand.CanExecute(null)); // the sequence itself
        draft.SelectedStep = block;
        Assert.False(draft.PasteStepCommand.CanExecute(null)); // next to the block, in the sequence
    }

    [Fact]
    public async Task ATopLevelHardwareStep_CannotBePastedIntoATrack()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var move = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);
        draft.CopyStepCommand.Execute(null);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;

        Assert.False(draft.PasteStepCommand.CanExecute(null));
        _ = move;
    }

    [Fact]
    public async Task ACopiedRepeatOfRigSteps_GoesIntoATrack_ButNotIntoTheSequence()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var (_, main, wide) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Repeat);
        draft.AddChildCommand.Execute(SequenceStepKind.ChangeFilter);
        draft.SelectedStep = main.Children.OfType<RepeatStepDraftViewModel>().Single();
        draft.CopyStepCommand.Execute(null);

        draft.SelectedStep = wide;
        Assert.True(draft.PasteStepCommand.CanExecute(null));
        draft.SelectedStep = null;
        Assert.False(draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task ADuplicatedBlock_KeepsItsRigStepsOnTheSameRigs_WithNewIds()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (block, main, _) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.MoveFocuser);
        draft.SelectedStep = block;

        draft.DuplicateStepCommand.Execute(null);

        var blocks = draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal(2, blocks.Count);
        var steps = blocks.Select(b => b.Tracks[0].Steps.OfType<RigMoveFocuserStepDraft>().Single()).ToList();
        Assert.NotEqual(steps[0].Id, steps[1].Id);
        Assert.Equal(steps[0].Position, steps[1].Position);
        Assert.Equal(blocks[0].Tracks.Select(t => t.RigId), blocks[1].Tracks.Select(t => t.RigId));
    }

    // Dirty state and the lock

    [Fact]
    public async Task EditingTheStepsParameters_IsAModification()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var move = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);
        var change = Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        move.PositionText = "19000";                                                       // the target
        move.Focuser.Selected = move.Focuser.Options.Single(o => o.IdText == "focuser.narrow"); // the focuser
        change.Wheel.Selected = change.Wheel.Options.Single(o => o.IdText == "filterwheel.narrow"); // the wheel
        change.Filter.Selected = change.Filter.Options.Single(o => o.Name == "Ha");        // the filter

        Assert.Equal(4, modifications);
    }

    [Fact]
    public async Task EditingARigStep_IsAModification_ButAFilterListLookedUpAgain_IsNot()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;
        draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<RigChangeFilterStepDraftViewModel>(draft.SelectedStep);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        draft.RefreshDevices();
        Assert.Equal(0, modifications);

        change.Filter.Selected = change.Filter.Options.Single(o => o.Name == "Ha");
        Assert.Equal(1, modifications);
    }

    [Fact]
    public async Task DeviceStateChangesAtRuntime_AreNotModifications()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);
        Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        await host.DeviceOperations.MoveFocuserToAsync(DemoSetup.MainFocuserId, 19000);
        await host.DeviceOperations.MoveFilterWheelToAsync(DemoSetup.MainFilterWheelId, 3);
        draft.Revalidate();

        Assert.Equal(0, modifications);
    }

    [Fact]
    public async Task WhileNotEditable_TheNewStepsCannotBeAdded()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;

        draft.IsEditable = false;

        Assert.False(draft.AddStepCommand.CanExecute(SequenceStepKind.MoveFocuser));
        Assert.False(draft.AddStepCommand.CanExecute(SequenceStepKind.ChangeFilter));
        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.MoveFocuser));
        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.ChangeFilter));
    }

    [Fact]
    public async Task TheSequenceRunsTheStepsOnTheRealDevices_FromTheEditor()
    {
        await using var host = CreateHost();
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var draft = CreateDraft(host);
        var move = Add<MoveFocuserStepDraftViewModel>(draft, SequenceStepKind.MoveFocuser);
        move.PositionText = "19000";
        var change = Add<ChangeFilterStepDraftViewModel>(draft, SequenceStepKind.ChangeFilter);
        change.Filter.Selected = change.Filter.Options.Single(o => o.Name == "Ha");

        var built = draft.Build();
        var runner = new Sidera.Runtime.Sequencing.SequenceRunner(host.ResourceManager);
        await runner.RunAsync(built.Sequence);

        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == DemoSetup.MainFocuserId);
        Assert.Equal(19000, focuser.Position);
        Assert.Equal("Ha", ((Sidera.Core.FilterWheels.IFilterWheel)host.DeviceRegistry.GetAll().Single(d => d.Id == DemoSetup.MainFilterWheelId)).CurrentSlot.Name);
    }
}

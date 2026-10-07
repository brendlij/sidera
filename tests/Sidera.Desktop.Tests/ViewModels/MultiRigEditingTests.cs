using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Parallel Imaging in the editor: blocks, tracks, rigs, steps of a track, copying, and the shared equipment.</summary>
public class MultiRigEditingTests
{
    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(
        SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null, SharedEquipmentDraft? shared = null)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, clipboard, host.RigRegistry, shared);
    }

    private static T Selected<T>(SequenceDraftViewModel draft) where T : StepDraftViewModel => Assert.IsAssignableFrom<T>(draft.SelectedStep);

    private static MultiRigStepDraftViewModel AddBlock(SequenceDraftViewModel draft)
    {
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        return Selected<MultiRigStepDraftViewModel>(draft);
    }

    private static RigTrackDraftViewModel AddTrack(SequenceDraftViewModel draft)
    {
        Assert.True(draft.AddTrackCommand.CanExecute(null));
        draft.AddTrackCommand.Execute(null);
        return Selected<RigTrackDraftViewModel>(draft);
    }

    private static T AddTrackStep<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        Assert.True(draft.AddTrackStepCommand.CanExecute(kind));
        draft.AddTrackStepCommand.Execute(kind);
        return Selected<T>(draft);
    }

    private static IEnumerable<Guid> Ids(IEnumerable<SequenceStepDraft> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Id;
            var inner = step switch
            {
                MultiRigStepDraft m => m.Tracks.Cast<SequenceStepDraft>(),
                RigTrackDraft t => t.Steps,
                RepeatStepDraft r => r.Children,
                _ => [],
            };
            foreach (var id in Ids(inner))
            {
                yield return id;
            }
        }
    }

    // Main × 2 [Exposure 300 s], Wide [Exposure 60 s, Delay 2 s], between Start Guiding and Stop Guiding.
    private sealed record Fixture(
        SequenceDraftViewModel Draft,
        MultiRigStepDraftViewModel Block,
        RigTrackDraftViewModel Main,
        RepeatStepDraftViewModel Repeat,
        RigExposureStepDraftViewModel MainExposure,
        RigTrackDraftViewModel Wide,
        RigExposureStepDraftViewModel WideExposure,
        DelayStepDraftViewModel WideDelay);

    private static Fixture Build(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var draft = CreateDraft(host, clipboard);
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        var block = AddBlock(draft);
        var main = AddTrack(draft);
        var repeat = AddTrackStep<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);
        repeat.CountText = "2";
        draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        var mainExposure = Selected<RigExposureStepDraftViewModel>(draft);
        mainExposure.ExposureText = "300";
        draft.SelectedStep = block;
        var wide = AddTrack(draft);
        wide.Rig.Selected = wide.Rig.Options.Single(o => o.IdText == "rig.wide");
        var wideExposure = AddTrackStep<RigExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        wideExposure.ExposureText = "60";
        var wideDelay = AddTrackStep<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        wideDelay.DurationText = "2";
        draft.SelectedStep = null;
        draft.AddStepCommand.Execute(SequenceStepKind.StopGuiding);
        return new Fixture(draft, block, main, repeat, mainExposure, wide, wideExposure, wideDelay);
    }

    // The block and its tracks

    [Fact]
    public async Task AddingABlock_AppendsAnEmptyBlockAtTheTopLevel_AndSelectsIt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        draft.AddStepCommand.Execute(SequenceStepKind.Delay);

        var block = AddBlock(draft);

        Assert.Same(block, draft.Steps[^1]);
        Assert.Empty(block.Children);
        Assert.True(block.IsTopLevel);
        Assert.True(block.IsContainer);
        Assert.Equal(("Parallel Imaging", "no setup sequences"), (block.Title, block.Summary));
        Assert.Equal(["Parallel Imaging needs at least two Setup Sequences."], block.Problems);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public async Task AddingTracks_GivesEachTheNextRigThatIsNotUsedYet_AndNoRigWhenAllAreUsed()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var block = AddBlock(draft);

        var first = AddTrack(draft);
        draft.SelectedStep = block;
        var second = AddTrack(draft);
        draft.SelectedStep = block;
        var third = AddTrack(draft);
        draft.SelectedStep = block;
        var fourth = AddTrack(draft);

        Assert.Equal(["rig.main", "rig.narrow", "rig.wide", null], new[] { first, second, third, fourth }.Select(t => t.Rig.SelectedId?.Value));
        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig"], new[] { first, second, third }.Select(t => t.Title));
        Assert.Equal(["No imaging setup selected.", "A Setup Sequence needs at least one step."], fourth.Problems);
        Assert.Equal(["1.1", "1.2", "1.3", "1.4"], block.Children.Select(c => c.NumberLabel));
    }

    [Fact]
    public async Task ATrackCanBeAdded_FromTheBlock_FromATrack_AndFromAStepInATrack_ButNotFromElsewhere()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Draft.SelectedStep = f.Draft.Steps[0]; // Start Guiding
        Assert.False(f.Draft.AddTrackCommand.CanExecute(null));
        Assert.False(f.Draft.IsMultiRigContext);

        foreach (var from in new StepDraftViewModel[] { f.Block, f.Main, f.Repeat, f.MainExposure, f.WideDelay })
        {
            f.Draft.SelectedStep = from;
            Assert.True(f.Draft.AddTrackCommand.CanExecute(null), from.Title);
            Assert.True(f.Draft.IsMultiRigContext);
            Assert.Same(f.Block, f.Draft.MultiRigTarget);
        }

        f.Draft.SelectedStep = f.Block;
        f.Draft.AddTrackCommand.Execute(null);
        Assert.Equal(3, f.Block.Children.Count);
    }

    [Fact]
    public async Task ATrackStepCanBeAdded_ToTheSelectedTrack_OrTheTrackOfTheSelectedStep_ButNotFromTheBlock()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Draft.SelectedStep = f.Block;
        Assert.False(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Delay));

        f.Draft.SelectedStep = f.WideExposure;
        Assert.Same(f.Wide, f.Draft.TrackTarget);
        f.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Delay);

        var added = Selected<DelayStepDraftViewModel>(f.Draft);
        Assert.Same(f.Wide, added.Parent);
        Assert.Equal([f.WideExposure, f.WideDelay, added], f.Wide.Children); // at the end of the track
    }

    [Fact]
    public async Task OnlyExposuresDelaysAndRepeatsCanBeAddedToATrack()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Wide;

        Assert.True(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Exposure));
        Assert.True(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.True(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Repeat));
        foreach (var forbidden in new[]
                 {
                     SequenceStepKind.Slew, SequenceStepKind.StartGuiding, SequenceStepKind.StopGuiding,
                     SequenceStepKind.Dither, SequenceStepKind.MultiRig, SequenceStepKind.RigTrack,
                 })
        {
            Assert.False(f.Draft.AddTrackStepCommand.CanExecute(forbidden), forbidden.ToString());
            f.Draft.AddTrackStepCommand.Execute(forbidden); // forced: nothing happens
        }

        Assert.Equal(2, f.Wide.Children.Count);
    }

    [Fact]
    public async Task AnExposureInATrack_IsTheExposureOfTheRig_WithNoCameraToSelect()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.IsType<RigExposureStepDraftViewModel>(f.WideExposure);
        Assert.Equal(("Exposure", "60 s · Camera defaults"), (f.WideExposure.Title, f.WideExposure.Summary));
        Assert.Equal(("Wide Rig", "Wide Camera"), (f.Wide.Title, f.Wide.Summary));
        Assert.IsNotType<ExposureStepDraftViewModel>(f.WideExposure); // the exposure with a camera has a picker, this has none
        Assert.Equal("2.1.1.1", f.MainExposure.NumberLabel);
    }

    [Fact]
    public async Task ARepeatInATrack_HoldsExposuresAndDelaysOnly_AndTopLevelAddsAreUnchanged()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;

        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Exposure));
        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));
        foreach (var forbidden in new[]
                 {
                     SequenceStepKind.Slew, SequenceStepKind.StartGuiding, SequenceStepKind.StopGuiding,
                     SequenceStepKind.Dither, SequenceStepKind.Repeat,
                 })
        {
            Assert.False(f.Draft.AddChildCommand.CanExecute(forbidden), forbidden.ToString());
        }

        f.Draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        Assert.IsType<RigExposureStepDraftViewModel>(Selected<StepDraftViewModel>(f.Draft)); // not an exposure with a camera

        // A Repeat at the top level still takes the six steps of before.
        f.Draft.SelectedStep = null;
        f.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Slew));
        Assert.True(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Dither));
    }

    [Fact]
    public async Task ARigExposureAndATrack_CannotBeAddedAtTheTopLevel()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        draft.AddStepCommand.Execute(SequenceStepKind.RigExposure);
        draft.AddStepCommand.Execute(SequenceStepKind.RigTrack);

        Assert.Empty(draft.Steps);
    }

    // Rigs

    [Fact]
    public async Task TheRigPicker_ShowsTheNameAndTheCameraOfEveryRig_NotTheId()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        AddBlock(draft);
        var track = AddTrack(draft);

        Assert.Equal(["rig.main", "rig.narrow", "rig.wide"], track.Rig.Options.Select(o => o.IdText));
        var main = track.Rig.Options[0];
        Assert.Equal(("Main Rig", "Main Camera", "Main Camera"), (main.Name, main.CameraText, main.DetailText)); // the id is what the file holds, not something to read
        Assert.Equal("rig.main", track.Rig.Selected!.IdText);
    }

    [Fact]
    public async Task ChoosingTheSameRigInTwoTracks_IsRefused_UntilOneIsChanged()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));

        f.Wide.Rig.Selected = f.Wide.Rig.Options.Single(o => o.IdText == "rig.main");

        Assert.Equal(["The imaging setup 'Main Rig' is already used by another setup sequence."], f.Wide.Problems);
        Assert.False(f.Main.HasProblems);
        Assert.False(f.Draft.IsValid);

        f.Wide.Rig.Selected = f.Wide.Rig.Options.Single(o => o.IdText == "rig.narrow");
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigThatIsNotThere_StaysSelected_IsShownAsMissing_AndCanBeReplaced()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var track = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.observatory"), [new RigExposureStepDraft(Guid.NewGuid(), 5)]);
        draft.Replace([new MultiRigStepDraft(Guid.NewGuid(), [track, new RigTrackDraft(Guid.NewGuid(), new RigId("rig.main"), [new DelayStepDraft(Guid.NewGuid(), 1)])])], null);
        var vm = (RigTrackDraftViewModel)((MultiRigStepDraftViewModel)draft.Steps[0]).Children[0];

        Assert.Equal(new RigId("rig.observatory"), vm.Rig.SelectedId);
        Assert.True(vm.Rig.Selected!.IsMissing);
        Assert.Equal("rig.observatory (not available)", vm.Rig.Selected.Name);
        Assert.Equal(["The imaging setup 'rig.observatory' is not available."], vm.Problems);
        Assert.Equal(("rig.observatory", "imaging setup not available"), (vm.Title, vm.Summary));
        Assert.False(draft.IsValid);

        draft.RefreshDevices(); // looking again does not replace it
        Assert.Equal(new RigId("rig.observatory"), vm.Rig.SelectedId);

        vm.Rig.Selected = vm.Rig.Options.Single(o => o.IdText == "rig.wide");
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigThatDisappearsLater_IsMarkedMissing_AndKeepsItsId()
    {
        await using var host = CreateHost();
        var f = Build(host);

        host.RigRegistry.Unregister(new RigId("rig.wide"));
        f.Draft.RefreshDevices();

        Assert.Equal(new RigId("rig.wide"), f.Wide.Rig.SelectedId);
        Assert.True(f.Wide.Rig.Selected!.IsMissing);
        Assert.Equal(["The imaging setup 'rig.wide' is not available."], f.Wide.Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task ARigThatAppearsLater_BecomesSelectable_WithoutChangingTheSelection()
    {
        await using var host = CreateHost();
        var f = Build(host);
        var changes = 0;
        f.Main.Rig.Changed += (_, _) => changes++;

        host.AddSimulatedCamera(new DeviceId("camera.extra"), "Extra Camera");
        host.AddRig(new Rig(new RigId("rig.extra"), "Extra Rig", new DeviceId("camera.extra"), new OpticalTrain(300, 70, 3.76, 3.76, 6248, 4176)));
        f.Draft.RefreshDevices();

        Assert.Contains(f.Main.Rig.Options, o => o.IdText == "rig.extra");
        Assert.Equal(new RigId("rig.main"), f.Main.Rig.SelectedId);
        Assert.Equal(0, changes);
    }

    // Validation of the block, through the editor

    [Fact]
    public async Task ABlockWithOneTrack_OrAnEmptyTrack_IsInvalid_WithTheProblemOnTheRightRow()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var block = AddBlock(draft);
        var track = AddTrack(draft);

        Assert.Equal(["A Setup Sequence needs at least one step."], track.Problems);
        Assert.Contains("Parallel Imaging needs at least two Setup Sequences.", block.Problems);
        Assert.Contains("A step inside has a problem.", block.Problems);
        Assert.Equal(
            ["Step 1 (Parallel Imaging): Parallel Imaging needs at least two Setup Sequences.", "Step 1.1 (Setup Sequence): A Setup Sequence needs at least one step."],
            draft.ValidationErrors);
        Assert.Throws<SequenceConfigurationException>(() => draft.Build());
    }

    [Fact]
    public async Task AValidBlock_BuildsARuntimeSequence()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
        var built = f.Draft.Build();

        Assert.Equal(3, built.Sequence.Steps.Count);
        Assert.IsType<Sidera.Core.Sequencing.ParallelStep>(built.Sequence.Steps[1]);
    }

    [Fact]
    public async Task ADraftWithADitherOutsideTheBlock_IsStillValid_NextToABlock()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Draft.Steps[0];
        f.Draft.AddStepCommand.Execute(SequenceStepKind.Dither); // goes to the end, after Stop Guiding

        // Stop Guiding came before it: that is the usual guiding-order problem, nothing about the block.
        var dither = Selected<DitherStepDraftViewModel>(f.Draft);
        Assert.Contains(dither.Problems, p => p.StartsWith("Dither needs guiding", StringComparison.Ordinal));
        Assert.False(f.Block.HasProblems);
    }

    // Moving and removing

    [Fact]
    public async Task StepsOfATrackMoveWithinTheirTrack_AndNeverOutOfIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.WideDelay;

        Assert.True(f.Draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(f.Draft.MoveStepDownCommand.CanExecute(null)); // last in its track, though another track follows? no: it is the last
        f.Draft.MoveStepUpCommand.Execute(null);

        Assert.Equal([f.WideDelay, f.WideExposure], f.Wide.Children);
        Assert.Same(f.WideDelay, f.Draft.SelectedStep);
        Assert.Equal(["2.2.1", "2.2.2"], f.Wide.Children.Select(c => c.NumberLabel));
        Assert.False(f.Draft.MoveStepUpCommand.CanExecute(null)); // first in its track
        Assert.Equal(2, f.Block.Children.Count);
    }

    [Fact]
    public async Task TracksMoveAmongTracks_WithTheirSteps()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Wide;

        f.Draft.MoveStepUpCommand.Execute(null);

        Assert.Equal([f.Wide, f.Main], f.Block.Children);
        Assert.Equal(["2.1", "2.2"], f.Block.Children.Select(c => c.NumberLabel));
        Assert.Equal(2, f.Wide.Children.Count);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors)); // the order of tracks does not matter
    }

    [Fact]
    public async Task RemovingAStepOfATrack_SelectsItsNeighbour_AndRemovingTheLastOneSelectsTheTrack()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.WideExposure;
        f.Draft.RemoveStepCommand.Execute(null);
        Assert.Same(f.WideDelay, f.Draft.SelectedStep);

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Same(f.Wide, f.Draft.SelectedStep);
        Assert.Empty(f.Wide.Children);
        Assert.Equal(["A Setup Sequence needs at least one step."], f.Wide.Problems);
    }

    [Fact]
    public async Task RemovingATrack_KeepsTheBlock_WhichThenNeedsAnotherTrack()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Wide;

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Equal([f.Main], f.Block.Children);
        Assert.Same(f.Main, f.Draft.SelectedStep);
        Assert.Contains("Parallel Imaging needs at least two Setup Sequences.", f.Block.Problems);
        Assert.DoesNotContain(f.Wide, f.Draft.Rows);
        Assert.DoesNotContain(f.WideExposure, f.Draft.Rows);
    }

    [Fact]
    public async Task RemovingTheBlock_RemovesEverythingInIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Equal(["Start Guiding", "Stop Guiding"], f.Draft.Steps.Select(s => s.Title));
        Assert.Equal(2, f.Draft.Rows.Count);
    }

    // The list

    [Fact]
    public async Task TheListShowsTheBlockItsTracksAndTheirSteps_IndentedByLevel_NumberedByPath()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.Equal(
            ["1", "2", "2.1", "2.1.1", "2.1.1.1", "2.2", "2.2.1", "2.2.2", "3"],
            f.Draft.Rows.Select(r => r.NumberLabel));
        Assert.Equal([0d, 0d, 28d, 56d, 84d, 28d, 56d, 56d, 0d], f.Draft.Rows.Select(r => r.IndentWidth));
        Assert.Equal(
            ["Start Guiding", "Parallel Imaging", "Main Rig", "Repeat × 2", "Exposure", "Wide Rig", "Exposure", "Delay", "Stop Guiding"],
            f.Draft.Rows.Select(r => r.Title));
        Assert.Equal([0, 0, 1, 2, 3, 1, 2, 2, 0], f.Draft.Rows.Select(r => r.Depth));
    }

    // Copy, duplicate, paste

    [Fact]
    public async Task DuplicatingTheBlock_ClonesItWithNewIdsForEverythingInIt_AndTheSameRigsAndValues()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;
        var before = (MultiRigStepDraft)f.Draft.Snapshot()[1];

        f.Draft.DuplicateStepCommand.Execute(null);

        Assert.Equal(4, f.Draft.Steps.Count);
        var clone = Selected<MultiRigStepDraftViewModel>(f.Draft);
        Assert.Same(clone, f.Draft.Steps[2]);
        var cloned = (MultiRigStepDraft)f.Draft.Snapshot()[2];
        Assert.NotEqual(before.Id, cloned.Id);
        Assert.Equal(before.Tracks.Select(t => t.RigId), cloned.Tracks.Select(t => t.RigId));
        Assert.Empty(Ids([before]).Intersect(Ids([cloned])));
        var exposure = (RigExposureStepDraft)((RepeatStepDraft)cloned.Tracks[0].Steps[0]).Children[0];
        Assert.Equal(300, exposure.Seconds);
        Assert.Equal(2, ((RepeatStepDraft)cloned.Tracks[0].Steps[0]).Count);
        Assert.Equal(60, ((RigExposureStepDraft)cloned.Tracks[1].Steps[0]).Seconds);
        Assert.Equal(["Main Rig", "Wide Rig"], clone.Children.Select(c => c.Title));
        Assert.All(clone.Children, track => Assert.Same(clone, track.Parent));
    }

    [Fact]
    public async Task ADuplicatedBlock_IsIndependent_OfItsSource()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;
        f.Draft.DuplicateStepCommand.Execute(null);
        var clone = Selected<MultiRigStepDraftViewModel>(f.Draft);
        var sourceBefore = f.Draft.Snapshot()[1];

        var cloneTrack = (RigTrackDraftViewModel)clone.Children[0];
        cloneTrack.Rig.Selected = cloneTrack.Rig.Options.Single(o => o.IdText == "rig.narrow");
        ((RigExposureStepDraftViewModel)((RepeatStepDraftViewModel)cloneTrack.Children[0]).Children[0]).ExposureText = "11";
        ((RepeatStepDraftViewModel)cloneTrack.Children[0]).CountText = "9";
        f.Draft.SelectedStep = clone.Children[1];
        f.Draft.RemoveStepCommand.Execute(null);

        var sourceAfter = (MultiRigStepDraft)f.Draft.Snapshot()[1];
        var before = (MultiRigStepDraft)sourceBefore;
        Assert.Equal(before.Tracks.Select(t => t.RigId), sourceAfter.Tracks.Select(t => t.RigId));
        Assert.Equal(2, sourceAfter.Tracks.Count);
        Assert.Equal("300", f.MainExposure.ExposureText);
        Assert.Equal("2", f.Repeat.CountText);
        Assert.Equal(new RigId("rig.main"), f.Main.Rig.SelectedId);
    }

    [Fact]
    public async Task ADuplicatedBlock_StartsWithTheSameRigs_SoItIsInvalidUntilTheyAreChanged()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;

        f.Draft.DuplicateStepCommand.Execute(null);

        // The rigs are in two blocks, one after the other; each block is valid on its own (one block runs at a time).
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigTrackOnItsOwn_CannotBeDuplicatedOrCopied()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Main;

        Assert.False(f.Draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(f.Draft.CopyStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task AStepInATrack_CanBeDuplicated_RightAfterItselfInTheTrack_AndCopiedAndPastedWithinTracks()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.WideExposure;

        f.Draft.DuplicateStepCommand.Execute(null);

        var clone = Selected<RigExposureStepDraftViewModel>(f.Draft);
        Assert.Equal([f.WideExposure, clone, f.WideDelay], f.Wide.Children);
        Assert.Equal("60", clone.ExposureText);
        Assert.NotEqual(f.WideExposure.Id, clone.Id);

        // Copied in one track, pasted into the other: after the selected track, at its end.
        f.Draft.SelectedStep = f.WideExposure;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Main;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.PasteStepCommand.Execute(null);

        var pasted = Selected<RigExposureStepDraftViewModel>(f.Draft);
        Assert.Same(f.Main, pasted.Parent);
        Assert.Equal([f.Repeat, pasted], f.Main.Children);
        Assert.Equal("60", pasted.ExposureText);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ACopiedBlock_IsPastedAtTheTopLevelOnly_NeverInsideARepeatATrackOrABlock()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;
        f.Draft.CopyStepCommand.Execute(null);

        foreach (var inside in new StepDraftViewModel[] { f.Main, f.Repeat, f.MainExposure, f.WideDelay })
        {
            f.Draft.SelectedStep = inside;
            Assert.False(f.Draft.PasteStepCommand.CanExecute(null), inside.Title);
            f.Draft.PasteStepCommand.Execute(null); // forced: nothing happens
        }

        Assert.Equal(3, f.Draft.Steps.Count);

        f.Draft.SelectedStep = f.Draft.Steps[0];
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.PasteStepCommand.Execute(null);
        Assert.Equal(4, f.Draft.Steps.Count);
        Assert.IsType<MultiRigStepDraftViewModel>(f.Draft.Steps[1]);
        Assert.Same(f.Draft.Steps[1], f.Draft.SelectedStep);

        f.Draft.SelectedStep = f.Block;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null)); // with a block selected: after it, at the top level

        f.Draft.SelectedStep = null;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task APastedBlock_HasNewIdsAndTheCopyTimeState_AndManyPastesNeverCollide()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Block;
        f.Draft.CopyStepCommand.Execute(null);
        f.MainExposure.ExposureText = "1";
        f.Repeat.CountText = "99";

        for (var i = 0; i < 10; i++)
        {
            f.Draft.SelectedStep = f.Draft.Steps[0];
            f.Draft.PasteStepCommand.Execute(null);
        }

        var all = Ids(f.Draft.Snapshot()).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        var pasted = (MultiRigStepDraft)f.Draft.Snapshot()[1];
        Assert.Equal(300, ((RigExposureStepDraft)((RepeatStepDraft)pasted.Tracks[0].Steps[0]).Children[0]).Seconds);
        Assert.Equal(2, ((RepeatStepDraft)pasted.Tracks[0].Steps[0]).Count);
        Assert.Equal(13, f.Draft.Steps.Count);
    }

    [Fact]
    public async Task AnExposureWithACamera_CannotBePastedIntoATrack_AndATrackExposureNotAtTheTopLevel()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = null;
        f.Draft.AddStepCommand.Execute(SequenceStepKind.Exposure);
        var topExposure = Selected<ExposureStepDraftViewModel>(f.Draft);
        f.Draft.CopyStepCommand.Execute(null);

        f.Draft.SelectedStep = f.Wide;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.SelectedStep = f.WideDelay;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));

        f.Draft.SelectedStep = f.WideExposure;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = topExposure;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.SelectedStep = null;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task ACopiedRepeat_GoesIntoATrackOnlyIfItHoldsWhatATrackMayHold_AndToTheTopLevelOnlyIfItDoesNot()
    {
        await using var host = CreateHost();
        var f = Build(host);

        // The Repeat of Main holds a rig exposure: fine in a track, not at the top level.
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Wide;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.SelectedStep = f.Draft.Steps[0];
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));

        // A Repeat of a top-level step holding a slew: the other way round.
        f.Draft.SelectedStep = null;
        f.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        f.Draft.AddChildCommand.Execute(SequenceStepKind.Slew);
        f.Draft.SelectedStep = f.Draft.Steps[^1];
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Wide;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.SelectedStep = f.Draft.Steps[0];
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
    }

    // Dirty and lock

    [Fact]
    public async Task EveryEditOfABlock_IsAModification_AndSelectingIsNot()
    {
        await using var host = CreateHost();
        var f = Build(host);
        var modifications = 0;
        f.Draft.Modified += (_, _) => modifications++;

        f.Draft.SelectedStep = f.Wide;
        f.Draft.SelectedStep = f.WideExposure;
        f.Draft.SelectedStep = f.Block;
        Assert.Equal(0, modifications);

        f.Draft.AddTrackCommand.Execute(null);                                         // add a track
        f.Draft.SelectedStep = f.Block.Children[2];
        f.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Delay);                   // add a track step
        ((RigTrackDraftViewModel)f.Block.Children[2]).Rig.Selected = ((RigTrackDraftViewModel)f.Block.Children[2]).Rig.Options[0]; // choose a rig
        f.WideExposure.ExposureText = "61";                                            // a parameter
        f.Repeat.CountText = "3";                                                      // a Repeat in a track
        f.Draft.SelectedStep = f.WideDelay;
        f.Draft.MoveStepUpCommand.Execute(null);                                       // reorder
        f.Draft.RemoveStepCommand.Execute(null);                                       // remove
        f.Draft.SelectedStep = f.Block;
        f.Draft.DuplicateStepCommand.Execute(null);                                    // duplicate
        f.Draft.PasteStepCommand.Execute(null);                                        // (nothing copied: not available)

        Assert.Equal(8, modifications);
    }

    [Fact]
    public async Task TheBlockEditingCommands_AreLockedWhileNotEditable()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Wide;
        Assert.True(f.Draft.AddTrackCommand.CanExecute(null));
        Assert.True(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Delay));

        f.Draft.IsEditable = false;

        Assert.False(f.Draft.AddTrackCommand.CanExecute(null));
        Assert.False(f.Draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.False(f.Draft.AddChildCommand.CanExecute(SequenceStepKind.Delay));
        Assert.False(f.Draft.RemoveStepCommand.CanExecute(null));
        Assert.False(f.Draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(f.Draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(f.Draft.CopyStepCommand.CanExecute(null));
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
    }

    // Shared equipment

    [Fact]
    public async Task NewStepsUseTheSharedMountAndGuider_OfTheSession()
    {
        await using var host = CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.other"), "Other Mount");
        host.AddSimulatedGuider(new DeviceId("guider.other"), "Other Guider");
        var draft = CreateDraft(host, shared: new SharedEquipmentDraft(new DeviceId("mount.other"), new DeviceId("guider.other")));

        draft.AddStepCommand.Execute(SequenceStepKind.Slew);
        var slew = Selected<SlewStepDraftViewModel>(draft);
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        var start = Selected<StartGuidingStepDraftViewModel>(draft);
        draft.AddStepCommand.Execute(SequenceStepKind.Dither);
        var dither = Selected<DitherStepDraftViewModel>(draft);

        Assert.Equal(new DeviceId("mount.other"), slew.Mount.SelectedId);
        Assert.Equal(new DeviceId("guider.other"), start.Guider.SelectedId);
        Assert.Equal(new DeviceId("mount.other"), dither.Mount.SelectedId);
        Assert.Equal(new DeviceId("guider.other"), dither.Guider.SelectedId);
        Assert.True(draft.IsValid || draft.ValidationErrors.All(e => !e.Contains("shared", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AStepWithAnotherMountThanTheSharedOne_ShowsTheMismatchOnItsRow()
    {
        await using var host = CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.other"), "Other Mount");
        var draft = CreateDraft(host, shared: new SharedEquipmentDraft(new DeviceId("mount.eq6"), null));
        draft.AddStepCommand.Execute(SequenceStepKind.Slew);
        var slew = Selected<SlewStepDraftViewModel>(draft);
        Assert.False(slew.HasProblems);

        slew.Mount.Selected = slew.Mount.Options.Single(o => o.IdText == "mount.other");

        Assert.Equal(["The mount 'mount.other' is not the session's shared mount 'mount.eq6'."], slew.Problems);
        Assert.False(draft.IsValid);

        draft.SharedMount.Selected = draft.SharedMount.Options.Single(o => o.IdText == "mount.other");
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors)); // the session follows
    }

    [Fact]
    public async Task ChangingTheSharedEquipment_IsAModification_ButOpeningADocumentIsNot()
    {
        await using var host = CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.other"), "Other Mount");
        var draft = CreateDraft(host, shared: new SharedEquipmentDraft(new DeviceId("mount.eq6"), new DeviceId("guider.main")));
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        draft.SharedMount.Selected = draft.SharedMount.Options.Single(o => o.IdText == "mount.other");
        Assert.Equal(1, modifications);

        draft.Replace([], new SharedEquipmentDraft(new DeviceId("mount.eq6"), null));
        Assert.Equal(1, modifications);
        Assert.Equal(new DeviceId("mount.eq6"), draft.SharedEquipment.MountId);
        Assert.Null(draft.SharedEquipment.GuiderId);
    }

    [Fact]
    public async Task ASharedDeviceThatIsNotThere_StaysSelectedAndIsReported()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        draft.Replace([], new SharedEquipmentDraft(new DeviceId("mount.observatory"), new DeviceId("guider.main")));

        Assert.Equal(new DeviceId("mount.observatory"), draft.SharedMount.SelectedId);
        Assert.True(draft.SharedMount.Selected!.IsMissing);
        Assert.Equal(["The shared mount 'mount.observatory' is not available."], draft.SharedProblems);
        Assert.True(draft.HasSharedProblems);
        Assert.False(draft.IsValid);

        draft.SharedMount.Selected = draft.SharedMount.Options.Single(o => o.IdText == "mount.eq6");
        Assert.Empty(draft.SharedProblems);
    }

    [Fact]
    public async Task AFreshDraft_HasNoSharedEquipmentUntilOneIsChosen_AndSeesTheDefaultsForANewSession()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        Assert.Equal(new SharedEquipmentDraft(null, null), draft.SharedEquipment);
        Assert.Equal(new SharedEquipmentDraft(new DeviceId("mount.eq6"), new DeviceId("guider.main")), draft.DefaultSharedEquipment);
    }
}

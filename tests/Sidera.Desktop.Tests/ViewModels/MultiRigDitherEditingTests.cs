using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The Dither Policy section of a Multi-Rig block in the editor.</summary>
public class MultiRigDitherEditingTests
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
        var shared = new SharedEquipmentDraft(new DeviceId("mount.eq6"), new DeviceId("guider.main"));
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, clipboard, host.RigRegistry, shared);
    }

    private sealed record Fixture(
        SequenceDraftViewModel Draft, MultiRigStepDraftViewModel Block, RigTrackDraftViewModel Main, RigTrackDraftViewModel Wide);

    // Start Guiding, Multi-Rig [Main: Exposure 3 s, Wide: Exposure 0.6 s], Stop Guiding. The policy is off.
    private static Fixture Build(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var draft = CreateDraft(host, clipboard);
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        var tracks = new List<RigTrackDraftViewModel>();
        foreach (var (rig, seconds) in new[] { ("rig.main", "3"), ("rig.wide", "0.6") })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            Assert.IsType<RigExposureStepDraftViewModel>(draft.SelectedStep).ExposureText = seconds;
            tracks.Add(track);
        }

        draft.SelectedStep = null;
        draft.AddStepCommand.Execute(SequenceStepKind.StopGuiding);
        return new Fixture(draft, block, tracks[0], tracks[1]);
    }

    private static void Enable(Fixture f, string rig = "rig.wide")
    {
        f.Block.DitherEnabled = true;
        f.Block.TriggerRig.Selected = f.Block.TriggerRig.Options.Single(o => o.IdText == rig);
    }

    private static MultiRigDitherPolicyDraft PolicyOf(SequenceDraftViewModel draft) =>
        draft.Snapshot().OfType<MultiRigStepDraft>().Single().DitherPolicy!;

    // The section

    [Fact]
    public async Task ANewBlock_HasThePolicyOff_WithTheDefaultValuesAndNoTrigger()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.False(f.Block.DitherEnabled);
        Assert.Null(f.Block.TriggerRig.SelectedId);
        Assert.Equal(["3", "1.5", "0.5", "1", "10"],
            [f.Block.DitherEveryText, f.Block.DitherAmplitudeText, f.Block.DitherSettleThresholdText, f.Block.DitherSettleStableText, f.Block.DitherSettleTimeoutText]);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
        Assert.Equal(["2 rig tracks"], [f.Block.Summary]);
    }

    [Fact]
    public async Task EnablingIt_PicksTheFirstTrackAsTrigger_WhichIsEasyToChange()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Block.DitherEnabled = true;

        Assert.Equal(new RigId("rig.main"), f.Block.TriggerRig.SelectedId);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));

        f.Block.TriggerRig.Selected = f.Block.TriggerRig.Options.Single(o => o.IdText == "rig.wide");
        Assert.Equal(new RigId("rig.wide"), PolicyOf(f.Draft).TriggerRigId);
    }

    [Fact]
    public async Task OnlyTheRigsOfTheTracks_AreOfferedAsTrigger()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.Equal(["rig.main", "rig.wide"], f.Block.TriggerRig.Options.Select(o => o.IdText).Order());

        // Another track makes its rig a candidate; a track without a rig does not.
        f.Draft.SelectedStep = f.Block;
        f.Draft.AddTrackCommand.Execute(null);
        var third = Assert.IsType<RigTrackDraftViewModel>(f.Draft.SelectedStep);
        third.Rig.Selected = third.Rig.Options.Single(o => o.IdText == "rig.narrow");
        f.Draft.Revalidate();

        Assert.Equal(["rig.main", "rig.narrow", "rig.wide"], f.Block.TriggerRig.Options.Select(o => o.IdText).Order());
    }

    [Fact]
    public async Task ThePolicyIsReadFromTheFields_AsADraft()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        f.Block.DitherEveryText = "5";
        f.Block.DitherAmplitudeText = "2";
        f.Block.DitherSettleThresholdText = "0.4";
        f.Block.DitherSettleStableText = "2";
        f.Block.DitherSettleTimeoutText = "30";

        Assert.Equal(new MultiRigDitherPolicyDraft(true, new RigId("rig.wide"), 5, 2, 0.4, 2, 30), PolicyOf(f.Draft));
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task TheBlockSummary_SaysWhenAndHowItDithers_OnlyWhileItIsOn()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Enable(f);

        Assert.Equal("2 rig tracks\nDither every 3 Wide Rig frames · 1.5 px · settle ≤ 0.5 px for 1 s", f.Block.Summary);

        f.Block.DitherEveryText = "1";
        Assert.Contains("after every Wide Rig frame", f.Block.Summary, StringComparison.Ordinal);

        f.Block.DitherEnabled = false;
        Assert.Equal("2 rig tracks", f.Block.Summary);
    }

    // Validation in the editor

    [Theory]
    [InlineData("every", "0", "Dither interval must be at least 1 frame.")]
    [InlineData("every", "-2", "Dither interval must be at least 1 frame.")]
    [InlineData("every", "x", "Dither interval must be a whole number of frames.")]
    [InlineData("every", "2.5", "Dither interval must be a whole number of frames.")]
    [InlineData("every", "", "Dither interval must be a whole number of frames.")]
    [InlineData("amplitude", "0", "Dither amplitude must be greater than 0 px.")]
    [InlineData("amplitude", "abc", "Dither amplitude must be a number of pixels.")]
    [InlineData("threshold", "-1", "Settle threshold must be greater than 0 px.")]
    [InlineData("threshold", "NaN", "Settle threshold must be a number of pixels.")]
    [InlineData("stable", "0", "Settle stable time must be greater than 0 s.")]
    [InlineData("timeout", "0", "Settle timeout must be greater than 0 s.")]
    [InlineData("timeout", "1", "Settle timeout must be longer than the stable time.")]
    public async Task AWrongField_IsReportedOnTheBlock_AndTheDraftIsNotRunnable(string field, string text, string expected)
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);

        switch (field)
        {
            case "every": f.Block.DitherEveryText = text; break;
            case "amplitude": f.Block.DitherAmplitudeText = text; break;
            case "threshold": f.Block.DitherSettleThresholdText = text; break;
            case "stable": f.Block.DitherSettleStableText = text; break;
            default: f.Block.DitherSettleTimeoutText = text; break;
        }

        Assert.Contains(expected, f.Block.Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task AFieldOfAPolicyThatIsOff_IsNotReported()
    {
        await using var host = CreateHost();
        var f = Build(host);

        f.Block.DitherEveryText = "x";
        f.Block.DitherAmplitudeText = "-4";

        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ThePolicyComesBackWhenSwitchedOnAgain_WithTheValuesStillInTheFields()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        f.Block.DitherEveryText = "7";

        f.Block.DitherEnabled = false;
        f.Block.DitherEnabled = true;

        Assert.Equal("7", f.Block.DitherEveryText);
        Assert.Equal(new RigId("rig.wide"), f.Block.TriggerRig.SelectedId);
    }

    // The trigger rig and the tracks

    [Fact]
    public async Task ARemovedTriggerTrack_LeavesTheTriggerSelected_AsMissing_AndInvalid()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        f.Draft.SelectedStep = f.Wide;

        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Equal(new RigId("rig.wide"), f.Block.TriggerRig.SelectedId); // kept, never silently replaced
        Assert.True(f.Block.TriggerRig.Selected!.IsMissing);
        Assert.False(f.Draft.IsValid);
        Assert.Contains(f.Block.Problems, p => p.Contains("trigger rig 'rig.wide'", StringComparison.Ordinal));

        // Putting the track back (another one with that rig) repairs it.
        f.Draft.SelectedStep = f.Block;
        f.Draft.AddTrackCommand.Execute(null);
        var track = Assert.IsType<RigTrackDraftViewModel>(f.Draft.SelectedStep);
        track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == "rig.wide");
        f.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);

        Assert.DoesNotContain(f.Block.Problems, p => p.Contains("trigger rig", StringComparison.Ordinal));
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ChangingTheRigOfTheTriggerTrack_MakesTheTriggerInvalid_NotAnotherRig()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);

        f.Wide.Rig.Selected = f.Wide.Rig.Options.Single(o => o.IdText == "rig.narrow");

        Assert.Equal(new RigId("rig.wide"), f.Block.TriggerRig.SelectedId);
        Assert.False(f.Draft.IsValid);
        Assert.Contains(f.Block.Problems, p => p.Contains("not a track of this block", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATriggerTrackWithoutAnExposure_IsReported_AndAddingOneRepairsIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        f.Draft.SelectedStep = f.Wide.Children.Single();
        f.Draft.RemoveStepCommand.Execute(null);

        Assert.Contains(f.Block.Problems, p => p.Contains("has no exposure to count", StringComparison.Ordinal));
        Assert.False(f.Draft.IsValid);

        f.Draft.SelectedStep = f.Wide;
        f.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);

        Assert.DoesNotContain(f.Block.Problems, p => p.Contains("no exposure", StringComparison.Ordinal));
    }

    // Modifications, copying and the lock

    [Fact]
    public async Task EveryPolicyEdit_IsAModification()
    {
        await using var host = CreateHost();
        var f = Build(host);
        var modifications = 0;
        f.Draft.Modified += (_, _) => modifications++;

        f.Block.DitherEnabled = true; // also picks a trigger: still one edit
        Assert.True(modifications >= 1);
        var after = modifications;
        f.Block.TriggerRig.Selected = f.Block.TriggerRig.Options.Single(o => o.IdText == "rig.wide");
        f.Block.DitherEveryText = "4";
        f.Block.DitherAmplitudeText = "2";
        f.Block.DitherSettleThresholdText = "0.4";
        f.Block.DitherSettleStableText = "2";
        f.Block.DitherSettleTimeoutText = "20";

        Assert.Equal(after + 6, modifications);
    }

    [Fact]
    public async Task ReplacingTheSteps_IsNotAModification_AndRestoresThePolicyIncludingAMissingTrigger()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        var snapshot = f.Draft.Snapshot();
        var modifications = 0;
        f.Draft.Modified += (_, _) => modifications++;

        var policy = ((MultiRigStepDraft)snapshot[1]).DitherPolicy! with { TriggerRigId = new RigId("rig.observatory") };
        var changed = snapshot.Select(step => step is MultiRigStepDraft m ? m with { DitherPolicy = policy } : step).ToList();
        f.Draft.ReplaceSteps(changed);

        Assert.Equal(0, modifications);
        var block = f.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Single();
        Assert.True(block.DitherEnabled);
        Assert.Equal(new RigId("rig.observatory"), block.TriggerRig.SelectedId);
        Assert.True(block.TriggerRig.Selected!.IsMissing);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task ADuplicatedBlock_KeepsThePolicy_WithTheSameRigs_AndNewIds()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Enable(f);
        f.Block.DitherEveryText = "5";
        f.Draft.SelectedStep = f.Block;

        f.Draft.DuplicateStepCommand.Execute(null);

        var blocks = f.Draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal(2, blocks.Count);
        Assert.NotEqual(blocks[0].Id, blocks[1].Id);
        Assert.Equal(blocks[0].DitherPolicy, blocks[1].DitherPolicy); // it refers to rigs, which a copy shares
        Assert.Equal(5, blocks[1].DitherPolicy!.EveryNFrames);
        Assert.Equal(new RigId("rig.wide"), blocks[1].DitherPolicy!.TriggerRigId);
        Assert.Empty(blocks[0].Tracks.Select(t => t.Id).Intersect(blocks[1].Tracks.Select(t => t.Id)));
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ACopiedAndPastedBlock_KeepsThePolicy_AndEditingOneDoesNotChangeTheOther()
    {
        await using var host = CreateHost();
        var f = Build(host, new SequenceStepClipboard());
        Enable(f);
        f.Draft.SelectedStep = f.Block;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = null;

        f.Draft.PasteStepCommand.Execute(null);

        var blocks = f.Draft.Rows.OfType<MultiRigStepDraftViewModel>().ToList();
        Assert.Equal(2, blocks.Count);
        Assert.True(blocks[1].DitherEnabled);
        Assert.Equal(new RigId("rig.wide"), blocks[1].TriggerRig.SelectedId);

        blocks[1].DitherEveryText = "9";

        Assert.Equal("3", blocks[0].DitherEveryText);
        var drafts = f.Draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal(3, drafts[0].DitherPolicy!.EveryNFrames);
        Assert.Equal(9, drafts[1].DitherPolicy!.EveryNFrames);
    }

    [Fact]
    public async Task ARunWithAnEnabledPolicy_NeedsTheSharedDevicesConnected()
    {
        await using var host = CreateHost();
        var app = new MainViewModel(host, a => a(), new DemoOptions());
        try
        {
            var f = Build(host);
            Enable(f);
            app.SequenceDraft.ReplaceSteps(f.Draft.Snapshot());

            Assert.False(app.Sequencer.CanRun);
            Assert.Contains("Connect", app.Sequencer.ReadinessHint ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            app.Dispose();
        }
    }
}

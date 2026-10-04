using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>Multi-Rig Imaging: what a draft of it compiles to, and what is refused.</summary>
public class MultiRigBuilderTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");
    private static readonly DeviceId Mount = new("mount.eq6");
    private static readonly DeviceId Guider = new("guider.main");

    private static SideraRuntimeHost CreateHost(bool withRigs = true)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        if (withRigs)
        {
            DemoSetup.AddDemoRigs(host);
        }

        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host, SharedEquipmentDraft? shared = null) => new(host.RigRegistry, shared);

    private static RigExposureStepDraft RigExposure(double seconds = 1) => new(Guid.NewGuid(), seconds);
    private static DelayStepDraft Delay(double seconds = 1) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId? rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);
    private static MultiRigStepDraft MultiRig(params RigTrackDraft[] tracks) => new(Guid.NewGuid(), tracks);

    // A session of the three demo rigs: Main × 40 [300 s], Wide × 120 [60 s], Narrow × 60 [180 s].
    private static MultiRigStepDraft ThreeRigs() => MultiRig(
        Track(Main, Repeat(40, RigExposure(300))),
        Track(Wide, Repeat(120, RigExposure(60))),
        Track(Narrow, Repeat(60, RigExposure(180))));

    // Compilation

    [Fact]
    public async Task AMultiRigBlock_BecomesAParallelStepWithOneBranchPerRigTrack_InTheOrderOfTheTracks()
    {
        await using var host = CreateHost();
        var block = ThreeRigs();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var parallel = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps));
        Assert.Equal(3, parallel.Children.Count);
        Assert.All(parallel.Children, child => Assert.IsType<RigTrackStep>(child));
        Assert.Equal(
            ["Main Rig", "Wide Rig", "Narrow Rig"],
            parallel.Children.Select(child => child.Name));
        Assert.Equal(block.Tracks.Select(t => t.Id), parallel.Children.Cast<RigTrackStep>().Select(t => t.TrackId));
        Assert.Null(parallel.CoordinationGroup); // nothing in a track needs the branches to wait for each other
    }

    [Fact]
    public async Task ATracksStepsKeepTheirOrder_AndRepeatCompilesInsideTheBranch()
    {
        await using var host = CreateHost();
        var block = MultiRig(
            Track(Main, RigExposure(5), Delay(2), Repeat(3, RigExposure(7), Delay(1))),
            Track(Wide, RigExposure(1)));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var parallel = Assert.IsType<ParallelStep>(built.Sequence.Steps[0]);
        var main = Assert.IsType<RigTrackStep>(parallel.Children[0]);
        Assert.Collection(
            main.Steps,
            step => Assert.Equal(TimeSpan.FromSeconds(5), Assert.IsType<CameraExposureAction>(step).Duration),
            step => Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<DelayAction>(step).Duration),
            step =>
            {
                var repeat = Assert.IsType<RepeatStep>(step);
                Assert.Equal(3, repeat.Count);
                var body = Assert.IsType<SequenceGroup>(repeat.Child);
                Assert.Collection(
                    body.Children,
                    child => Assert.Equal(TimeSpan.FromSeconds(7), Assert.IsType<CameraExposureAction>(child).Duration),
                    child => Assert.Equal(TimeSpan.FromSeconds(1), Assert.IsType<DelayAction>(child).Duration));
            });
    }

    [Fact]
    public async Task EveryExposureOfATrack_UsesTheCameraOfItsRig_AndNoStepIsSharedBetweenTracks()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [ThreeRigs()], Context(host));

        var parallel = Assert.IsType<ParallelStep>(built.Sequence.Steps[0]);
        var exposures = parallel.Children.Cast<RigTrackStep>().Select(track =>
            Assert.IsType<CameraExposureAction>(
                Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(Assert.Single(track.Steps)).Child).Children[0])).ToList();
        Assert.Equal(
            [DemoSetup.MainCameraId, DemoSetup.WideCameraId, DemoSetup.NarrowCameraId],
            exposures.Select(e => e.CameraId));
        Assert.Equal(3, exposures.Distinct().Count()); // three action objects
    }

    [Fact]
    public async Task EveryBuildMakesAFreshRuntimeTree()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { ThreeRigs() };

        var first = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));
        var second = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));

        var a = Assert.IsType<ParallelStep>(first.Sequence.Steps[0]);
        var b = Assert.IsType<ParallelStep>(second.Sequence.Steps[0]);
        Assert.NotSame(a, b);
        for (var i = 0; i < 3; i++)
        {
            var trackA = Assert.IsType<RigTrackStep>(a.Children[i]);
            var trackB = Assert.IsType<RigTrackStep>(b.Children[i]);
            Assert.NotSame(trackA, trackB);
            Assert.NotSame(trackA.Steps[0], trackB.Steps[0]);
        }
    }

    [Fact]
    public async Task TheBuiltStepsMapEveryRuntimeStepBackToItsDraftId_ThroughBlockTrackRepeatAndStep()
    {
        await using var host = CreateHost();
        var exposure = RigExposure(3);
        var repeat = Repeat(2, exposure);
        var trackA = Track(Main, repeat);
        var trackB = Track(Wide, RigExposure(1));
        var block = MultiRig(trackA, trackB);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var top = Assert.Single(built.Steps);
        Assert.Equal(block.Id, top.DraftId);
        Assert.Equal([trackA.Id, trackB.Id], top.Children!.Select(c => c.DraftId));
        var builtRepeat = Assert.Single(top.Children![0].Children!);
        Assert.Equal(repeat.Id, builtRepeat.DraftId);
        Assert.Equal(exposure.Id, Assert.Single(builtRepeat.Children!).DraftId);
        Assert.Equal(("Multi-Rig Imaging", "2 rig tracks"), (top.Description.Title, top.Description.Summary));
        Assert.Equal(("Main Rig", "Main Camera"), (top.Children![0].Description.Title, top.Children![0].Description.Summary));
        Assert.Equal(("Exposure", "3 s · Camera defaults"), (builtRepeat.Children![0].Description.Title, builtRepeat.Children![0].Description.Summary));
    }

    [Fact]
    public async Task ATopLevelSequenceWithAMultiRigBlockInTheMiddle_KeepsTheOrderAroundIt()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[]
        {
            new StartGuidingStepDraft(Guid.NewGuid(), Guider),
            new SlewStepDraft(Guid.NewGuid(), Mount, 5.588, -5.39),
            ThreeRigs(),
            new StopGuidingStepDraft(Guid.NewGuid(), Guider),
        };

        var built = SequenceDraftBuilder.Build(
            host.DeviceRegistry, steps, Context(host, new SharedEquipmentDraft(Mount, Guider)));

        Assert.Collection(
            built.Sequence.Steps,
            step => Assert.IsType<StartGuidingAction>(step),
            step => Assert.IsType<SlewAction>(step),
            step => Assert.IsType<ParallelStep>(step),
            step => Assert.IsType<StopGuidingAction>(step));
    }

    // Validation: the block

    private static IReadOnlyList<string> Problems(DraftValidation validation, Guid id) => validation.ProblemsOf(id);

    [Fact]
    public async Task AValidBlock_IsValid()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [ThreeRigs()], Context(host));

        Assert.True(validation.IsValid, string.Join(" ", SequenceDraftBuilder.Sentences([ThreeRigs()], validation)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ABlockNeedsAtLeastTwoRigTracks(int tracks)
    {
        await using var host = CreateHost();
        var block = MultiRig(new[] { Wide, Main }.Take(tracks).Select(rig => Track(rig, RigExposure())).ToArray());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));

        Assert.Equal(["Multi-Rig Imaging needs at least two Rig Tracks."], Problems(validation, block.Id));
        Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host)));
    }

    [Fact]
    public async Task EveryTrackNeedsARig_ThatIsThere()
    {
        await using var host = CreateHost();
        var none = Track(null, RigExposure());
        var gone = Track(new RigId("rig.observatory"), RigExposure());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(none, gone)], Context(host));

        Assert.Equal(["No rig selected."], Problems(validation, none.Id));
        Assert.Equal(["The rig 'rig.observatory' is not available."], Problems(validation, gone.Id));
    }

    [Fact]
    public async Task WithoutARigRegistry_NoRigIsAvailable_ButNothingBreaks()
    {
        await using var host = CreateHost();
        var track = Track(Main, RigExposure());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(track, Track(Wide, RigExposure()))]);

        Assert.Equal(["The rig 'rig.main' is not available."], Problems(validation, track.Id));
    }

    [Fact]
    public async Task TheSameRigInTwoTracks_IsRefused_OnTheSecondTrack()
    {
        await using var host = CreateHost();
        var first = Track(Main, RigExposure());
        var second = Track(Main, RigExposure());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(first, second)], Context(host));

        Assert.Empty(Problems(validation, first.Id));
        Assert.Equal(["The rig 'rig.main' is already used by another track."], Problems(validation, second.Id));
    }

    [Fact]
    public async Task TwoRigsThatNameTheSameCamera_WouldExposeItTwiceAtOnce_AndAreRefused()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.twin"), "Twin Rig", DemoSetup.MainCameraId,
            new OpticalTrain(500, 100, 3.76, 3.76, 6248, 4176)));
        var first = Track(Main, RigExposure());
        var second = Track(new RigId("rig.twin"), RigExposure());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(first, second)], Context(host));

        Assert.Equal(["The camera 'camera.main' is already used by another track."], Problems(validation, second.Id));
    }

    [Fact]
    public async Task ARigWhoseCameraIsGone_IsRefused_WithTheCameraNamed()
    {
        await using var host = CreateHost();
        host.DeviceRegistry.Unregister(DemoSetup.WideCameraId);
        var wide = Track(Wide, RigExposure());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(Track(Main, RigExposure()), wide)], Context(host));

        Assert.Equal(["The camera 'camera.wide' of rig 'rig.wide' is not available."], Problems(validation, wide.Id));
    }

    [Fact]
    public async Task ATrackNeedsAtLeastOneStep()
    {
        await using var host = CreateHost();
        var empty = Track(Wide);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [MultiRig(Track(Main, RigExposure()), empty)], Context(host));

        Assert.Equal(["A Rig Track needs at least one step."], Problems(validation, empty.Id));
    }

    [Fact]
    public async Task ARepeatInATrackIsValidatedLikeAnyOther()
    {
        await using var host = CreateHost();
        var emptyRepeat = Repeat(2);
        var zero = Repeat(0, RigExposure());

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [MultiRig(Track(Main, emptyRepeat), Track(Wide, zero))], Context(host));

        Assert.Equal(["Repeat must contain at least one step."], Problems(validation, emptyRepeat.Id));
        Assert.Equal(["Repeat count must be at least 1."], Problems(validation, zero.Id));
    }

    [Fact]
    public async Task ADurationInATrackMustBePositive()
    {
        await using var host = CreateHost();
        var exposure = RigExposure(0);
        var delay = Delay(-1);

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [MultiRig(Track(Main, exposure), Track(Wide, delay))], Context(host));

        Assert.Equal(["Exposure must be greater than 0 s."], Problems(validation, exposure.Id));
        Assert.Equal(["Delay must be greater than 0 s."], Problems(validation, delay.Id));
    }

    // Validation: what must not be in a track

    [Fact]
    public async Task WhatBelongsToTheWholeSession_IsRefusedInsideATrack_ForEveryKind()
    {
        await using var host = CreateHost();
        var slew = new SlewStepDraft(Guid.NewGuid(), Mount, 1, 1);
        var start = new StartGuidingStepDraft(Guid.NewGuid(), Guider);
        var stop = new StopGuidingStepDraft(Guid.NewGuid(), Guider);
        var dither = new DitherStepDraft(Guid.NewGuid(), Guider, Mount, DemoSetup.MainCameraId, 1, 0.5, 1, 10);
        var camera = new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 1);
        var nested = MultiRig();
        var block = MultiRig(Track(Main, slew, start, stop, dither, camera, nested), Track(Wide, RigExposure()));

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));

        Assert.Equal(["Slewing moves the shared mount and cannot be done inside a Rig Track."], Problems(validation, slew.Id));
        var guiding = "Guiding is shared by the whole session and cannot be started or stopped inside a Rig Track.";
        Assert.Equal([guiding], Problems(validation, start.Id));
        Assert.Equal([guiding], Problems(validation, stop.Id));
        Assert.Contains("Dither is not available inside Multi-Rig Imaging yet", Assert.Single(Problems(validation, dither.Id)));
        Assert.Equal(["Use an exposure of the track here: its camera is the camera of the rig."], Problems(validation, camera.Id));
        Assert.Equal(["Multi-Rig Imaging cannot be placed inside a Rig Track."], Problems(validation, nested.Id));
        Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host)));
    }

    [Fact]
    public async Task WhatARepeatInATrackMayHold_IsWhatATrackMayHold()
    {
        await using var host = CreateHost();
        var slew = new SlewStepDraft(Guid.NewGuid(), Mount, 1, 1);
        var block = MultiRig(Track(Main, Repeat(2, slew)), Track(Wide, RigExposure()));

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));

        Assert.Equal(["Slewing moves the shared mount and cannot be done inside a Rig Track."], Problems(validation, slew.Id));
    }

    [Fact]
    public async Task ARigExposureOutsideATrack_IsRefused_AtTheTopLevelAndInsideARepeat()
    {
        await using var host = CreateHost();
        var top = RigExposure();
        var inside = RigExposure();
        var repeat = Repeat(2, inside);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [top, repeat], Context(host));

        var message = "An exposure with the camera of a rig can only be used inside a Rig Track.";
        Assert.Equal([message], Problems(validation, top.Id));
        Assert.Equal([message], Problems(validation, inside.Id));
    }

    [Fact]
    public async Task AMultiRigBlockInsideARepeat_IsRefused()
    {
        await using var host = CreateHost();
        var draft = new RepeatStepDraft(Guid.NewGuid(), 2, []);
        var block = MultiRig();
        var tracks = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));
        Assert.NotEmpty(Problems(tracks, block.Id)); // the block is invalid without tracks, and cannot be a Repeat child by type

        Assert.False(typeof(LeafStepDraft).IsAssignableFrom(typeof(MultiRigStepDraft)));
        Assert.NotNull(draft);
    }

    // Shared equipment

    [Fact]
    public async Task ASharedEquipmentThatIsNotThere_OrOfTheWrongKind_IsReported()
    {
        await using var host = CreateHost();
        var step = new DelayStepDraft(Guid.NewGuid(), 1);

        var gone = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [step], Context(host, new SharedEquipmentDraft(new DeviceId("mount.gone"), new DeviceId("guider.gone"))));
        var wrong = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [step], Context(host, new SharedEquipmentDraft(DemoSetup.MainCameraId, Mount)));

        Assert.Equal(["The shared mount 'mount.gone' is not available.", "The shared guider 'guider.gone' is not available."], gone.SharedProblems);
        Assert.Equal(["'camera.main' is not a mount.", "'mount.eq6' is not a guider."], wrong.SharedProblems);
        Assert.False(gone.IsValid);
    }

    [Fact]
    public async Task AStepThatUsesAnotherMountOrGuiderThanTheSessionShares_IsRefusedClearly()
    {
        await using var host = CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.other"), "Other Mount");
        host.AddSimulatedGuider(new DeviceId("guider.other"), "Other Guider");
        var slew = new SlewStepDraft(Guid.NewGuid(), new DeviceId("mount.other"), 1, 1);
        var start = new StartGuidingStepDraft(Guid.NewGuid(), new DeviceId("guider.other"));
        var dither = new DitherStepDraft(
            Guid.NewGuid(), new DeviceId("guider.other"), new DeviceId("mount.other"), DemoSetup.MainCameraId, 1, 0.5, 1, 10);
        var shared = new SharedEquipmentDraft(Mount, Guider);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [slew, start, dither], Context(host, shared));

        Assert.Equal(["The mount 'mount.other' is not the session's shared mount 'mount.eq6'."], Problems(validation, slew.Id));
        Assert.Equal(["The guider 'guider.other' is not the session's shared guider 'guider.main'."], Problems(validation, start.Id));
        Assert.Equal(
            ["The guider 'guider.other' is not the session's shared guider 'guider.main'.",
             "The mount 'mount.other' is not the session's shared mount 'mount.eq6'."],
            Problems(validation, dither.Id));
    }

    [Fact]
    public async Task WithoutASelectedSharedEquipment_NothingIsComparedAndEveryStepKeepsItsDevice()
    {
        await using var host = CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.other"), "Other Mount");
        var slew = new SlewStepDraft(Guid.NewGuid(), new DeviceId("mount.other"), 1, 1);

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [slew], Context(host, new SharedEquipmentDraft(null, null)));

        Assert.True(validation.IsValid);
        Assert.True(SequenceDraftBuilder.Validate(host.DeviceRegistry, [slew], Context(host)).IsValid);
    }

    // Devices a draft needs

    [Fact]
    public async Task TheDevicesOfABlock_AreTheCamerasOfItsRigs_NotTheSharedEquipment()
    {
        await using var host = CreateHost();
        var block = MultiRig(Track(Main, RigExposure()), Track(Narrow, Delay()));

        var ids = SequenceDraftBuilder.RequiredDeviceIds([block], Context(host, new SharedEquipmentDraft(Mount, Guider)));

        Assert.Equivalent(new[] { DemoSetup.MainCameraId, DemoSetup.NarrowCameraId }, ids);
    }

    [Fact]
    public async Task ARigThatIsNotThere_NeedsNoDevice_ItIsReportedByTheValidationInstead()
    {
        await using var host = CreateHost();

        var ids = SequenceDraftBuilder.RequiredDeviceIds(
            [MultiRig(Track(new RigId("rig.gone"), RigExposure()), Track(null, RigExposure()))], Context(host));

        Assert.Empty(ids);
    }

    [Fact]
    public async Task OtherStepsOfTheDraftKeepTheirDevices_NextToABlock()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[]
        {
            new StartGuidingStepDraft(Guid.NewGuid(), Guider),
            MultiRig(Track(Wide, RigExposure()), Track(Main, RigExposure())),
        };

        var ids = SequenceDraftBuilder.RequiredDeviceIds(steps, Context(host));

        Assert.Equivalent(new[] { Guider, DemoSetup.WideCameraId, DemoSetup.MainCameraId }, ids);
    }

    // The failure wrapper of a track

    [Fact]
    public async Task ATrackStepThatFails_SaysWhichTrackItWas_AndKeepsTheCause()
    {
        await using var host = CreateHost();
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var track = Track(Wide, RigExposure(0.05));
        var built = SequenceDraftBuilder.Build(
            host.DeviceRegistry, [MultiRig(Track(Main, RigExposure(0.05)), track)], Context(host));

        // Only the camera of the Wide rig is not connected: its first exposure cannot start.
        await host.DeviceRegistry.GetAll().OfType<Sidera.Core.Devices.ICamera>().Single(c => c.Id == DemoSetup.MainCameraId).ConnectAsync();
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(built.Sequence));

        var wide = Assert.IsType<RigTrackFailedException>(failure);
        Assert.Equal(track.Id, wide.TrackId);
        Assert.Equal("Wide Rig", wide.TrackName);
        Assert.IsType<InvalidOperationException>(wide.InnerException);
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task ACancelledTrack_IsNotReportedAsAFailure()
    {
        await using var host = CreateHost();
        foreach (var camera in host.DeviceRegistry.GetAll().OfType<Sidera.Core.Devices.ICamera>())
        {
            await camera.ConnectAsync();
        }

        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var built = SequenceDraftBuilder.Build(
            host.DeviceRegistry, [MultiRig(Track(Main, RigExposure(5)), Track(Wide, RigExposure(5)))], Context(host));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var run = runner.RunAsync(built.Sequence, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public void ARigTrackStep_NeedsAtLeastOneStep()
    {
        Assert.Throws<ArgumentException>(() => new RigTrackStep(Guid.NewGuid(), "Main Rig", []));
    }
}

using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>Move Focuser and Change Filter, outside a track (with a device) and inside one (with the device of the rig).</summary>
public class HardwareStepBuilderTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");
    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host) => new(host.RigRegistry, Shared);

    private static MoveFocuserStepDraft Move(DeviceId? focuser, int position = 19000) => new(Guid.NewGuid(), focuser, position);
    private static ChangeFilterStepDraft Change(DeviceId? wheel, int slot = 4) => new(Guid.NewGuid(), wheel, slot);
    private static RigMoveFocuserStepDraft RigMove(int position = 19000) => new(Guid.NewGuid(), position);
    private static RigChangeFilterStepDraft RigChange(int slot = 4) => new(Guid.NewGuid(), slot);
    private static RigExposureStepDraft Exposure() => new(Guid.NewGuid(), 1);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId? rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigStepDraft Block(params RigTrackDraft[] tracks) => new(Guid.NewGuid(), tracks);

    private static IReadOnlyList<string> Problems(SideraRuntimeHost host, params SequenceStepDraft[] steps) =>
        SequenceDraftBuilder.Sentences(steps, SequenceDraftBuilder.Validate(host.DeviceRegistry, steps, Context(host)));

    private static readonly DeviceId MainFocuser = DemoSetup.MainFocuserId;
    private static readonly DeviceId MainWheel = DemoSetup.MainFilterWheelId;

    // Top level

    [Fact]
    public async Task AMoveFocuserStep_BuildsAMoveFocuserAction_WithTheFocuserAndThePosition()
    {
        await using var host = CreateHost();
        var step = Move(MainFocuser, 19250);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [step], Context(host));

        var action = Assert.IsType<MoveFocuserAction>(Assert.Single(built.Sequence.Steps));
        Assert.Equal((MainFocuser, 19250), (action.FocuserId, action.Target));
        Assert.Equal(step.Id, Assert.Single(built.Steps).DraftId);
    }

    [Fact]
    public async Task AChangeFilterStep_BuildsAChangeFilterAction_WithTheWheelAndTheSlotIndex()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Change(MainWheel, 5)], Context(host));

        var action = Assert.IsType<ChangeFilterAction>(Assert.Single(built.Sequence.Steps));
        Assert.Equal((MainWheel, 5), (action.FilterWheelId, action.SlotIndex));
    }

    [Fact]
    public async Task TheStepsAreDescribedByDeviceNameAndFilterName()
    {
        await using var host = CreateHost();

        var move = SequenceDraftBuilder.Describe(host.DeviceRegistry, Move(MainFocuser, 19250));
        var change = SequenceDraftBuilder.Describe(host.DeviceRegistry, Change(MainWheel, 4));
        var missing = SequenceDraftBuilder.Describe(host.DeviceRegistry, Change(MainWheel, 40));
        var none = SequenceDraftBuilder.Describe(host.DeviceRegistry, Move(null));

        Assert.Equal(new StepDescription("Move Focuser", "Main Focuser · 19250"), move);
        Assert.Equal(new StepDescription("Change Filter", "Main Filter Wheel · Ha"), change);
        Assert.Equal("Main Filter Wheel · slot 40", missing.Summary);
        Assert.Equal("no focuser · 19000", none.Summary);
    }

    [Fact]
    public async Task ValidSteps_AreValid()
    {
        await using var host = CreateHost();

        Assert.Empty(Problems(host, Move(MainFocuser), Change(MainWheel), Repeat(2, Move(MainFocuser, 100), Change(MainWheel, 0))));
    }

    [Fact]
    public async Task NoDevice_IsReported()
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Move Focuser): No focuser selected."], Problems(host, Move(null)));
        Assert.Equal(["Step 1 (Change Filter): No filter wheel selected."], Problems(host, Change(null)));
    }

    [Fact]
    public async Task ADeviceThatIsNotAvailable_OrOfAnotherKind_IsReported()
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Move Focuser): The focuser 'focuser.gone' is not available."], Problems(host, Move(new DeviceId("focuser.gone"))));
        Assert.Equal(["Step 1 (Move Focuser): 'filterwheel.main' is not a focuser."], Problems(host, Move(MainWheel)));
        Assert.Equal(["Step 1 (Change Filter): 'focuser.main' is not a filter wheel."], Problems(host, Change(MainFocuser)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50001)]
    [InlineData(int.MaxValue)]
    public async Task APositionOutsideTheRangeOfTheFocuser_IsReportedWithTheRange(int position)
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Move Focuser): Focuser position must be between 0 and 50000."], Problems(host, Move(MainFocuser, position)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50000)]
    public async Task TheLimitsOfTheRange_AreValid(int position)
    {
        await using var host = CreateHost();

        Assert.Empty(Problems(host, Move(MainFocuser, position)));
    }

    [Fact]
    public async Task ASlotThatTheWheelDoesNotHave_IsReported_AndANegativeOne()
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Change Filter): The filter wheel 'filterwheel.main' has no slot 7."], Problems(host, Change(MainWheel, 7)));
        Assert.Equal(["Step 1 (Change Filter): Filter slot cannot be negative."], Problems(host, Change(MainWheel, -1)));
    }

    [Fact]
    public async Task TheStepsNeedTheirDevicesConnected_ForRunning()
    {
        await using var host = CreateHost();

        var required = SequenceDraftBuilder.RequiredDeviceIds([Move(MainFocuser), Change(MainWheel)], Context(host));

        Assert.Equal(new[] { MainFocuser, MainWheel }, required.OrderBy(d => d.Value, StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public async Task TopLevelStepsAreNotAllowedInATrack_AndRigStepsNotOutsideOne()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Move(MainFocuser), Change(MainWheel), Exposure()), Track(Wide, Exposure()));

        var inTrack = Problems(host, block);
        var outside = Problems(host, RigMove(), RigChange());

        Assert.Contains("Step 1.1.1 (Move Focuser): Use Move Focuser of the track here: its focuser is the focuser of the rig.", inTrack);
        Assert.Contains("Step 1.1.2 (Change Filter): Use Change Filter of the track here: its filter wheel is the filter wheel of the rig.", inTrack);
        Assert.Equal(
            ["Step 1 (Move Focuser): A focuser move of a rig can only be used inside a Rig Track.",
             "Step 2 (Change Filter): A filter change of a rig can only be used inside a Rig Track."],
            outside);
    }

    // Inside a track

    [Fact]
    public async Task ARigStep_ResolvesToTheDeviceOfTheRigOfItsTrack_AtBuildTime()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, RigChange(4), RigMove(19000), Exposure()),
            Track(Narrow, RigChange(2), RigMove(26000), Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var tracks = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children.Cast<RigTrackStep>().ToList();
        var main = tracks[0].Steps;
        Assert.Equal(DemoSetup.MainFilterWheelId, Assert.IsType<ChangeFilterAction>(main[0]).FilterWheelId);
        Assert.Equal(DemoSetup.MainFocuserId, Assert.IsType<MoveFocuserAction>(main[1]).FocuserId);
        var narrow = tracks[1].Steps;
        Assert.Equal(DemoSetup.NarrowFilterWheelId, Assert.IsType<ChangeFilterAction>(narrow[0]).FilterWheelId);
        Assert.Equal(DemoSetup.NarrowFocuserId, Assert.IsType<MoveFocuserAction>(narrow[1]).FocuserId);
        Assert.Equal((4, 19000), (((ChangeFilterAction)main[0]).SlotIndex, ((MoveFocuserAction)main[1]).Target));
    }

    [Fact]
    public async Task TheSameRigStepsOnAnotherRig_ResolveToTheOtherRigsDevices()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { RigMove(7000), Exposure() };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Block(Track(Wide, steps), Track(Main, Exposure()))], Context(host));

        var wide = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.Equal(DemoSetup.WideFocuserId, Assert.IsType<MoveFocuserAction>(wide.Steps[0]).FocuserId);
    }

    [Fact]
    public async Task ARigStepInARepeatOfATrack_Builds()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Repeat(3, RigChange(1), RigMove(18000), Exposure())), Track(Wide, Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(built.Sequence.Steps[0]).Children[0]);
        var body = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(Assert.Single(main.Steps)).Child);
        Assert.Collection(
            body.Children,
            s => Assert.IsType<ChangeFilterAction>(s),
            s => Assert.IsType<MoveFocuserAction>(s),
            s => Assert.IsType<CameraExposureAction>(s));
    }

    [Fact]
    public async Task ARigWithoutAFocuser_CannotMoveOne_ARigWithoutAWheel_CannotChangeFilter()
    {
        await using var host = CreateHost();
        // The wide rig has a focuser but no filter wheel.
        var block = Block(Track(Wide, RigChange(0), Exposure()), Track(Main, Exposure()));
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176)));
        var bare = Block(Track(new RigId("rig.bare"), RigMove(), Exposure()), Track(Main, Exposure()));

        Assert.Equal(["Step 1.1.1 (Change Filter): The rig 'rig.wide' has no filter wheel."], Problems(host, block));
        Assert.Equal(["Step 1.1.1 (Move Focuser): The rig 'rig.bare' has no focuser."], Problems(host, bare));
    }

    [Fact]
    public async Task ARigStepNeverBorrowsTheDeviceOfAnotherRig()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176)));
        var bare = Block(Track(new RigId("rig.bare"), RigMove(), RigChange(), Exposure()), Track(Main, RigMove(), Exposure()));

        var problems = Problems(host, bare);

        Assert.Equal(2, problems.Count);
        Assert.DoesNotContain(problems, p => p.Contains("rig.main", StringComparison.Ordinal));
        Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, [bare], Context(host)));
    }

    [Fact]
    public async Task APositionOrSlotThatTheRigsDeviceDoesNotHave_IsReported()
    {
        await using var host = CreateHost();
        // The wide focuser only goes to 12000; the narrow wheel has 4 slots.
        var block = Block(Track(Wide, RigMove(13000), Exposure()), Track(Narrow, RigChange(4), Exposure()));

        var problems = Problems(host, block);

        Assert.Contains("Step 1.1.1 (Move Focuser): Focuser position must be between 0 and 12000.", problems);
        Assert.Contains("Step 1.2.1 (Change Filter): The filter wheel 'filterwheel.narrow' has no slot 4.", problems);
    }

    [Fact]
    public async Task ATrackWithoutAKnownRig_ReportsTheRig_NotTheHardware()
    {
        await using var host = CreateHost();
        var block = Block(Track(null, RigMove(), Exposure()), Track(Main, Exposure()));

        var problems = Problems(host, block);

        Assert.Equal(["Step 1.1 (Rig Track): No rig selected."], problems);
    }

    [Fact]
    public async Task ADescriptionOfARigStep_NamesTheRigDevice_OrWhatIsMissing()
    {
        await using var host = CreateHost();
        host.RigRegistry.TryGet(Narrow, out var narrow);
        host.RigRegistry.TryGet(Wide, out var wide);

        var move = SequenceDraftBuilder.Describe(host.DeviceRegistry, RigMove(25000), rig: narrow);
        var change = SequenceDraftBuilder.Describe(host.DeviceRegistry, RigChange(1), rig: narrow);
        var noWheel = SequenceDraftBuilder.Describe(host.DeviceRegistry, RigChange(1), rig: wide);
        var unknown = SequenceDraftBuilder.Describe(host.DeviceRegistry, RigChange(1));

        Assert.Equal("Narrow Focuser · 25000", move.Summary);
        Assert.Equal("Narrow Filter Wheel · Ha", change.Summary);
        Assert.Equal("the rig has no filter wheel", noWheel.Summary);
        Assert.Equal("slot 1", unknown.Summary);
    }

    [Fact]
    public async Task ATrackNeedsTheFocuserAndWheelOfItsRigConnected_OnlyWhenItUsesThem()
    {
        await using var host = CreateHost();
        var uses = Block(Track(Main, Repeat(2, RigMove(), RigChange(), Exposure())), Track(Narrow, Exposure()));
        var usesNot = Block(Track(Main, Exposure()), Track(Narrow, Exposure()));

        var required = SequenceDraftBuilder.RequiredDeviceIds([uses], Context(host));
        var notRequired = SequenceDraftBuilder.RequiredDeviceIds([usesNot], Context(host));

        Assert.Contains(MainFocuser, required);
        Assert.Contains(MainWheel, required);
        Assert.DoesNotContain(DemoSetup.NarrowFocuserId, required); // the narrow track uses none of its hardware
        Assert.DoesNotContain(MainFocuser, notRequired);
        Assert.DoesNotContain(MainWheel, notRequired);
    }

    // Orchestration

    [Fact]
    public async Task WithADitherPolicy_ASafePointFollowsEveryFocuserMoveAndFilterChange_AndIsGenerated()
    {
        await using var host = CreateHost();
        var policy = MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Wide };
        var block = new MultiRigStepDraft(
            Guid.NewGuid(),
            [Track(Main, RigChange(4), RigMove(19000), Exposure()), Track(Wide, Exposure())],
            policy);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.Collection(
            main.Steps,
            s => Assert.IsType<ChangeFilterAction>(s),
            s => Assert.IsType<SafePointStep>(s),
            s => Assert.IsType<MoveFocuserAction>(s),
            s => Assert.IsType<SafePointStep>(s),
            s => Assert.IsType<CameraExposureAction>(s),
            s => Assert.IsType<SafePointStep>(s));
        var children = Assert.Single(built.Steps).Children![0].Children!;
        Assert.Equal([false, true, false, true, false, true], children.Select(c => c.IsGenerated));
    }

    [Fact]
    public async Task WithoutADitherPolicy_NoSafePointIsGenerated_AroundHardwareSteps()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, RigChange(4), RigMove(19000), Exposure()), Track(Wide, Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.DoesNotContain(main.Steps, s => s is SafePointStep);
    }
}

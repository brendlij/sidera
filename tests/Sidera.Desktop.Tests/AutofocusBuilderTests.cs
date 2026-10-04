using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>Autofocus, outside a track (with a rig) and inside one (with the rig of the track).</summary>
public class AutofocusBuilderTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");
    private static readonly OpticalTrain Optics = new(250, 60, 3.76, 23.5, 15.7, 6248, 4176);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host, bool metrics = true) =>
        new(host.RigRegistry, new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId), metrics ? host.FocusMetrics : null, host.EventBus);

    private static AutofocusStepDraft Top(RigId? rig, double seconds = 2, int step = 300, int samples = 7) => new(Guid.NewGuid(), rig, seconds, step, samples);
    private static RigAutofocusStepDraft Local(double seconds = 2, int step = 300, int samples = 7) => new(Guid.NewGuid(), seconds, step, samples);
    private static RigExposureStepDraft Exposure() => new(Guid.NewGuid(), 1);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId? rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);
    private static MultiRigStepDraft Block(params RigTrackDraft[] tracks) => new(Guid.NewGuid(), tracks);

    private static IReadOnlyList<string> Problems(SideraRuntimeHost host, SequenceStepDraft[] steps, bool metrics = true) =>
        SequenceDraftBuilder.Sentences(steps, SequenceDraftBuilder.Validate(host.DeviceRegistry, steps, Context(host, metrics)));

    // Top level

    [Fact]
    public async Task ATopLevelAutofocus_BuildsAnAutofocusAction_ForTheRigWithItsCameraAndFocuser()
    {
        await using var host = CreateHost();
        var step = Top(Narrow, 1.5, 250, 9);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [step], Context(host));

        var action = Assert.IsType<AutofocusAction>(Assert.Single(built.Sequence.Steps));
        Assert.Equal((Narrow, DemoSetup.NarrowCameraId, DemoSetup.NarrowFocuserId), (action.RigId, action.CameraId, action.FocuserId));
        Assert.Equal(new AutofocusOptions(TimeSpan.FromSeconds(1.5), 250, 9), action.Options);
        Assert.Equal(step.Id, Assert.Single(built.Steps).DraftId);
    }

    [Fact]
    public async Task TheStepIsDescribedByRigNameAndSettings()
    {
        await using var host = CreateHost();

        var described = SequenceDraftBuilder.Describe(host.DeviceRegistry, Top(Main, 2, 300, 7), Context(host));
        var none = SequenceDraftBuilder.Describe(host.DeviceRegistry, Top(null), Context(host));

        Assert.Equal(new StepDescription("Autofocus", "Main Rig · 2 s · step 300 · 7 samples"), described);
        Assert.StartsWith("no rig ·", none.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValidAutofocus_IsValid_InAndOutOfARepeat()
    {
        await using var host = CreateHost();

        Assert.Empty(Problems(host, [Top(Main), Repeat(2, Top(Wide), Top(Narrow))]));
    }

    [Fact]
    public async Task NoRig_OrARigThatIsNotThere_IsReported()
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Autofocus): No rig selected."], Problems(host, [Top(null)]));
        Assert.Equal(["Step 1 (Autofocus): The rig 'rig.gone' is not available."], Problems(host, [Top(new RigId("rig.gone"))]));
    }

    [Fact]
    public async Task ARigWithoutAFocuser_CannotBeFocused_AndNothingElseIsUsedInstead()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, Optics));

        var problems = Problems(host, [Top(new RigId("rig.bare"))]);

        Assert.Equal(["Step 1 (Autofocus): The rig 'rig.bare' has no focuser."], problems);
    }

    [Fact]
    public async Task ARigThatSharesAFocuserWithAnotherRig_CanBeFocused()
    {
        await using var host = CreateHost();
        // Two rigs may name one focuser; the resource manager keeps their use apart.
        host.AddRig(new Rig(new RigId("rig.odd"), "Odd Rig", DemoSetup.NarrowCameraId, Optics, DemoSetup.MainFocuserId));

        Assert.Empty(Problems(host, [Top(new RigId("rig.odd"))])); // sharing a focuser is allowed
    }

    [Theory]
    [InlineData(0.0, "Autofocus exposure must be greater than 0 s.")]
    [InlineData(-1.0, "Autofocus exposure must be greater than 0 s.")]
    [InlineData(double.NaN, "Autofocus exposure must be greater than 0 s.")]
    public async Task AnExposureThatIsNotPositive_IsReported(double seconds, string expected)
    {
        await using var host = CreateHost();

        Assert.Equal([$"Step 1 (Autofocus): {expected}"], Problems(host, [Top(Main, seconds)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-300)]
    public async Task AStepSizeThatIsNotPositive_IsReported(int step)
    {
        await using var host = CreateHost();

        Assert.Equal(["Step 1 (Autofocus): Autofocus step size must be greater than 0."], Problems(host, [Top(Main, step: step)]));
    }

    [Theory]
    [InlineData(3)]    // fewer than a fit needs
    [InlineData(6)]    // not symmetrical
    [InlineData(8)]
    [InlineData(23)]   // more than offered
    [InlineData(-7)]
    public async Task ASampleCountThatIsNotOddBetween5And21_IsReported(int samples)
    {
        await using var host = CreateHost();

        Assert.Equal(
            ["Step 1 (Autofocus): Autofocus samples must be an odd number between 5 and 21."],
            Problems(host, [Top(Main, samples: samples)]));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(21)]
    public async Task TheLimitsOfTheSampleCount_AreValid(int samples)
    {
        await using var host = CreateHost();

        Assert.Empty(Problems(host, [Top(Main, samples: samples)]));
    }

    [Fact]
    public async Task AStepSizeThatLeavesTooLittleTravelForTheSamples_IsReported()
    {
        await using var host = CreateHost();

        // The wide focuser goes from 0 to 12000: five samples at 3500 steps need 14000.
        var problems = Problems(host, [Top(Wide, step: 3500)]);

        Assert.Equal(
            ["Step 1 (Autofocus): The focuser 'focuser.wide' does not have enough travel for autofocus with a step size of 3500."],
            problems);
        Assert.Empty(Problems(host, [Top(Wide, step: 3000)])); // four steps of 3000 just fit
    }

    [Fact]
    public async Task WithoutAnythingToMeasureFocusWith_AutofocusIsNotAvailable()
    {
        await using var host = CreateHost();

        var problems = Problems(host, [Top(Main)], metrics: false);

        Assert.Equal(["Step 1 (Autofocus): Autofocus is not available: there is nothing to measure focus with."], problems);
        Assert.Throws<SequenceConfigurationException>(() =>
            SequenceDraftBuilder.Build(host.DeviceRegistry, [Top(Main)], Context(host, metrics: false)));
    }

    [Fact]
    public async Task EveryProblemOfOneStep_IsReported_NotOnlyTheFirst()
    {
        await using var host = CreateHost();

        var problems = Problems(host, [Top(null, 0, 0, 4)]);

        Assert.Equal(4, problems.Count);
    }

    [Fact]
    public async Task ARunNeedsTheCameraAndTheFocuserOfTheRig_Connected()
    {
        await using var host = CreateHost();
        var step = Top(Narrow);

        var required = SequenceDraftBuilder.RequiredDeviceIds([step], Context(host));
        var inRepeat = SequenceDraftBuilder.RequiredDeviceIds([Repeat(2, Top(Wide))], Context(host));

        Assert.Equal(
            new[] { DemoSetup.NarrowCameraId, DemoSetup.NarrowFocuserId }.OrderBy(d => d.Value),
            required.OrderBy(d => d.Value));
        Assert.Contains(DemoSetup.WideFocuserId, inRepeat);
        Assert.DoesNotContain(DemoSetup.NarrowFilterWheelId, required); // the filter wheel is left alone
    }

    [Fact]
    public async Task AutofocusNeedsNeitherTheMountNorTheGuider()
    {
        await using var host = CreateHost();

        var required = SequenceDraftBuilder.RequiredDeviceIds([Top(Main)], Context(host));

        Assert.DoesNotContain(DemoSetup.MountId, required);
        Assert.DoesNotContain(DemoSetup.GuiderId, required);
    }

    // Inside a track

    [Fact]
    public async Task ARigAutofocus_FocusesTheRigOfItsTrack_ResolvedAtBuildTime()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Local(1, 400, 9), Exposure()), Track(Narrow, Local(), Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var tracks = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children.Cast<RigTrackStep>().ToList();
        var main = Assert.IsType<AutofocusAction>(tracks[0].Steps[0]);
        var narrow = Assert.IsType<AutofocusAction>(tracks[1].Steps[0]);
        Assert.Equal((Main, DemoSetup.MainCameraId, DemoSetup.MainFocuserId), (main.RigId, main.CameraId, main.FocuserId));
        Assert.Equal((Narrow, DemoSetup.NarrowCameraId, DemoSetup.NarrowFocuserId), (narrow.RigId, narrow.CameraId, narrow.FocuserId));
        Assert.Equal(new AutofocusOptions(TimeSpan.FromSeconds(1), 400, 9), main.Options);
    }

    [Fact]
    public async Task TheSameRigStepOnAnotherRig_FocusesThatRig()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Local(), Exposure() };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Block(Track(Wide, steps), Track(Main, Exposure()))], Context(host));

        var wide = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.Equal(DemoSetup.WideFocuserId, Assert.IsType<AutofocusAction>(wide.Steps[0]).FocuserId);
    }

    [Fact]
    public async Task ARigAutofocusInARepeatOfATrack_Builds()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Repeat(3, Local(), Exposure())), Track(Wide, Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(built.Sequence.Steps[0]).Children[0]);
        var body = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(Assert.Single(main.Steps)).Child);
        Assert.Collection(body.Children, s => Assert.IsType<AutofocusAction>(s), s => Assert.IsType<CameraExposureAction>(s));
    }

    [Fact]
    public async Task ARigWithoutAFocuser_CannotAutofocusInItsTrack_AndNeverBorrowsAnotherFocuser()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, Optics));
        var block = Block(Track(new RigId("rig.bare"), Local(), Exposure()), Track(Main, Exposure()));

        var problems = Problems(host, [block]);

        Assert.Equal(["Step 1.1.1 (Autofocus): The rig 'rig.bare' has no focuser."], problems);
        Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host)));
    }

    [Fact]
    public async Task ATrackWithoutAKnownRig_ReportsTheRig_NotTheAutofocus()
    {
        await using var host = CreateHost();

        var problems = Problems(host, [Block(Track(null, Local(), Exposure()), Track(Main, Exposure()))]);

        Assert.Equal(["Step 1.1 (Rig Track): No rig selected."], problems);
    }

    [Fact]
    public async Task TheSettingsOfARigAutofocus_AreCheckedLikeThoseOfATopLevelOne()
    {
        await using var host = CreateHost();

        var problems = Problems(host, [Block(Track(Main, Local(0, 0, 4), Exposure()), Track(Wide, Local(step: 4000), Exposure()))]);

        Assert.Contains("Step 1.1.1 (Autofocus): Autofocus exposure must be greater than 0 s.", problems);
        Assert.Contains("Step 1.1.1 (Autofocus): Autofocus step size must be greater than 0.", problems);
        Assert.Contains("Step 1.1.1 (Autofocus): Autofocus samples must be an odd number between 5 and 21.", problems);
        Assert.Contains(problems, p => p.StartsWith("Step 1.2.1 (Autofocus): The focuser 'focuser.wide' does not have enough travel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATopLevelAutofocusIsNotAllowedInATrack_AndARigAutofocusNotOutsideOne()
    {
        await using var host = CreateHost();

        var inTrack = Problems(host, [Block(Track(Main, Top(Main), Exposure()), Track(Wide, Exposure()))]);
        var outside = Problems(host, [Local()]);

        Assert.Contains("Step 1.1.1 (Autofocus): Use Autofocus of the track here: its rig is the rig of the track.", inTrack);
        Assert.Equal(["Step 1 (Autofocus): An autofocus of a rig can only be used inside a Rig Track."], outside);
    }

    [Fact]
    public async Task ATrackNeedsTheFocuserOfItsRigConnected_OnlyWhenItFocuses()
    {
        await using var host = CreateHost();
        var focuses = Block(Track(Main, Repeat(2, Local(), Exposure())), Track(Narrow, Exposure()));
        var doesNot = Block(Track(Main, Exposure()), Track(Narrow, Exposure()));

        var required = SequenceDraftBuilder.RequiredDeviceIds([focuses], Context(host));
        var notRequired = SequenceDraftBuilder.RequiredDeviceIds([doesNot], Context(host));

        Assert.Contains(DemoSetup.MainFocuserId, required);
        Assert.DoesNotContain(DemoSetup.NarrowFocuserId, required);
        Assert.DoesNotContain(DemoSetup.MainFocuserId, notRequired);
    }

    // Orchestration

    [Fact]
    public async Task WithADitherPolicy_ASafePointFollowsEveryAutofocus_AndItIsGenerated()
    {
        await using var host = CreateHost();
        var policy = MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Wide };
        var block = new MultiRigStepDraft(Guid.NewGuid(), [Track(Main, Local(), Exposure()), Track(Wide, Exposure())], policy);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.Collection(
            main.Steps,
            s => Assert.IsType<AutofocusAction>(s),
            s => Assert.IsType<SafePointStep>(s),
            s => Assert.IsType<CameraExposureAction>(s),
            s => Assert.IsType<SafePointStep>(s));
        var children = Assert.Single(built.Steps).Children![0].Children!;
        Assert.Equal([false, true, false, true], children.Select(c => c.IsGenerated));
    }

    [Fact]
    public async Task TheFocusExposuresAreNotFrames_NoTriggerStepFollowsAnAutofocus()
    {
        await using var host = CreateHost();
        var policy = MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Main };
        var block = new MultiRigStepDraft(Guid.NewGuid(), [Track(Main, Local(), Exposure()), Track(Wide, Exposure())], policy);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        var autofocusIndex = main.Steps.ToList().FindIndex(s => s is AutofocusAction);
        Assert.IsType<SafePointStep>(main.Steps[autofocusIndex + 1]); // straight to the safe point, not through the counter
    }

    [Fact]
    public async Task WithoutADitherPolicy_NothingIsGenerated_AroundAnAutofocus()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Local(), Exposure()), Track(Wide, Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var main = Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[0]);
        Assert.DoesNotContain(main.Steps, s => s is SafePointStep);
    }

    [Fact]
    public async Task EveryBuild_MakesAFreshAction()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Top(Main) };

        var first = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));
        var second = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));

        Assert.NotSame(first.Sequence.Steps[0], second.Sequence.Steps[0]);
    }
}

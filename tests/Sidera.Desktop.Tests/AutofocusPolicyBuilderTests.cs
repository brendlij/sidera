using Sidera.Core.Focusing;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>
/// The autofocus policy of a Rig Track: what the build puts into the sequence for it, where, and what is refused. The
/// draft stays what the user wrote; the generated autofocus runs are runtime steps with no draft behind them.
/// </summary>
public class AutofocusPolicyBuilderTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");
    private static readonly OpticalTrain Optics = new(250, 60, 3.76, 3.76, 6248, 4176);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host, bool metrics = true) =>
        new(host.RigRegistry, new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId), metrics ? host.FocusMetrics : null, host.EventBus);

    private static RigAutofocusPolicyDraft Policy(
        bool start = false, bool filter = false, bool enabled = true, double seconds = 0.5, int step = 400, int samples = 7) =>
        new(enabled, start, filter, seconds, step, samples);

    private static RigExposureStepDraft Exposure() => new(Guid.NewGuid(), 1);
    private static RigChangeFilterStepDraft ChangeFilter(int slot = 4) => new(Guid.NewGuid(), slot);
    private static RigAutofocusStepDraft Explicit() => new(Guid.NewGuid(), 2, 300, 7);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);

    private static RigTrackDraft Track(RigId rig, RigAutofocusPolicyDraft? policy, params SequenceStepDraft[] steps) =>
        new(Guid.NewGuid(), rig, steps, policy);

    private static MultiRigStepDraft Block(MultiRigDitherPolicyDraft? dither, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, dither);

    private static MultiRigStepDraft Block(params RigTrackDraft[] tracks) => Block(null, tracks);

    private static RigTrackStep MainTrack(BuiltSequence built, int index = 0) =>
        Assert.IsType<RigTrackStep>(Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps)).Children[index]);

    private static BuiltSequence Build(SideraRuntimeHost host, MultiRigStepDraft block) =>
        SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

    private static IReadOnlyList<string> Problems(SideraRuntimeHost host, MultiRigStepDraft block, bool metrics = true)
    {
        SequenceStepDraft[] steps = [block];
        return SequenceDraftBuilder.Sentences(steps, SequenceDraftBuilder.Validate(host.DeviceRegistry, steps, Context(host, metrics)));
    }

    // Describes the steps of a track by what they are: A = autofocus, E = exposure, F = change filter, S = safe point.
    private static string Shape(IEnumerable<ISequenceStep> steps) => string.Concat(steps.Select(Letter));

    private static string Letter(ISequenceStep step) => step switch
    {
        AutofocusAction => "A",
        CameraExposureAction => "E",
        ChangeFilterAction => "F",
        SafePointStep => "S",
        RepeatStep repeat => $"R[{Shape(((SequenceGroup)repeat.Child).Children)}]",
        _ => "?",
    };

    // At the start of the track

    [Fact]
    public async Task AtTrackStart_OneAutofocusIsPutBeforeTheFirstStep_OnlyOnce()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Exposure(), Exposure()), Track(Wide, null, Exposure()));

        var built = Build(host, block);

        Assert.Equal("AEE", Shape(MainTrack(built).Steps));
        Assert.Equal("E", Shape(MainTrack(built, 1).Steps)); // the other track has no policy
    }

    [Fact]
    public async Task ThePolicyDoesNothing_WhenItIsOff_AndTheDraftIsNotChanged()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Exposure(), ChangeFilter(), Exposure() };
        var off = Block(Track(Main, Policy(start: true, filter: true, enabled: false), steps), Track(Wide, null, Exposure()));
        var none = Block(Track(Main, null, steps), Track(Wide, null, Exposure()));

        Assert.Equal("EFE", Shape(MainTrack(Build(host, off)).Steps));
        Assert.Equal("EFE", Shape(MainTrack(Build(host, none)).Steps));
        Assert.Equal(3, off.Tracks[0].Steps.Count); // nothing was written into the draft
    }

    [Fact]
    public async Task AnExplicitAutofocusAsTheFirstStep_IsTheAutofocusAtTheStart_ThereIsNoSecond()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Explicit(), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal("AE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task AnExplicitAutofocusFirstInAFirstRepeat_IsTheAutofocusAtTheStart_Too()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Repeat(2, Explicit(), Exposure())), Track(Wide, null, Exposure()));

        Assert.Equal("R[AE]", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task AnExplicitAutofocusThatIsNotFirst_DoesNotSatisfyTheStart()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Exposure(), Explicit()), Track(Wide, null, Exposure()));

        Assert.Equal("AEA", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task AtTrackStart_AutofocusRunsOnceOutsideARepeat_NotOncePerIteration()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Repeat(10, Exposure())), Track(Wide, null, Exposure()));

        Assert.Equal("AR[E]", Shape(MainTrack(Build(host, block)).Steps));
    }

    // After a filter change

    [Fact]
    public async Task AfterAChangeFilter_AnAutofocusFollows_BeforeTheNextStep()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(filter: true), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal("FAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task EveryChangeFilter_GetsItsOwnAutofocus()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, Policy(filter: true), ChangeFilter(4), Exposure(), ChangeFilter(5), Exposure()),
            Track(Wide, null, Exposure()));

        Assert.Equal("FAEFAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task TheFilterTriggerDoesNotAutofocusAtTheStart_AndTheStartTriggerNotAfterAFilter()
    {
        await using var host = CreateHost();
        var filterOnly = Block(Track(Main, Policy(filter: true), Exposure(), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));
        var startOnly = Block(Track(Main, Policy(start: true), Exposure(), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal("EFAE", Shape(MainTrack(Build(host, filterOnly)).Steps));
        Assert.Equal("AEFE", Shape(MainTrack(Build(host, startOnly)).Steps));
    }

    [Fact]
    public async Task AnExplicitAutofocusRightAfterAChangeFilter_IsTheAutofocusOfThatChange_ThereIsNoSecond()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(filter: true), ChangeFilter(), Explicit(), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal("FAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task AnExplicitAutofocusThatIsNotTheNextStep_DoesNotSatisfyTheChange()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(filter: true), ChangeFilter(), Exposure(), Explicit()), Track(Wide, null, Exposure()));

        Assert.Equal("FAEA", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task ASameFilterChange_IsAChangeFilterStepLikeAnyOther_AndTriggersToo()
    {
        await using var host = CreateHost();
        // The rule is about the step the user wrote, not about whether the wheel had to turn: the same slot twice.
        var block = Block(Track(Main, Policy(filter: true), ChangeFilter(4), ChangeFilter(4), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal("FAFAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task ARepeatedChangeFilter_GetsItsAutofocusInEveryIteration_AtTheStepThatTriggersIt()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, Policy(start: true, filter: true), Repeat(3, ChangeFilter(), Exposure())),
            Track(Wide, null, Exposure()));

        // The start autofocus is outside the Repeat; the filter autofocus is inside, so it runs three times.
        Assert.Equal("AR[FAE]", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task AChangeFilterLastInARepeat_StillGetsItsAutofocus_ThereIsNoLookAcrossTheEndOfTheList()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(filter: true), Repeat(2, Exposure(), ChangeFilter())), Track(Wide, null, Exposure()));

        Assert.Equal("R[EFA]", Shape(MainTrack(Build(host, block)).Steps));
    }

    // Both triggers

    [Fact]
    public async Task BothTriggers_AutofocusAtTheStart_ThenAfterTheFilterChange_InThatOrder()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, Policy(start: true, filter: true), ChangeFilter(4), Exposure(), ChangeFilter(5), Exposure()),
            Track(Wide, null, Exposure()));

        Assert.Equal("AFAEFAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task BothTriggers_AFirstChangeFilterFollowedByAnExplicitAutofocus_HasNoDuplicate()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, Policy(start: true, filter: true), ChangeFilter(), Explicit(), Exposure()),
            Track(Wide, null, Exposure()));

        // One at the start (for the track), then the change filter whose autofocus is the explicit one.
        Assert.Equal("AFAE", Shape(MainTrack(Build(host, block)).Steps));
    }

    [Fact]
    public async Task EachTrackHasItsOwnPolicy()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Main, Policy(start: true, filter: true), ChangeFilter(), Exposure()),
            Track(Wide, Policy(enabled: false), Exposure()),
            Track(Narrow, Policy(start: true), Exposure()));

        var built = Build(host, block);

        Assert.Equal("AFAE", Shape(MainTrack(built, 0).Steps));
        Assert.Equal("E", Shape(MainTrack(built, 1).Steps));
        Assert.Equal("AE", Shape(MainTrack(built, 2).Steps));
    }

    // What is generated

    [Fact]
    public async Task AGeneratedAutofocus_IsTheExistingAction_OfTheRigOfTheTrack_WithThePolicySettings_AndNoDraft()
    {
        await using var host = CreateHost();
        var block = Block(
            Track(Narrow, Policy(start: true, seconds: 1.5, step: 250, samples: 9), Exposure()),
            Track(Main, null, Exposure()));

        var built = Build(host, block);

        var action = Assert.IsType<AutofocusAction>(MainTrack(built).Steps[0]);
        Assert.Equal((Narrow, DemoSetup.NarrowCameraId, DemoSetup.NarrowFocuserId), (action.RigId, action.CameraId, action.FocuserId));
        Assert.Equal(new AutofocusOptions(TimeSpan.FromSeconds(1.5), 250, 9), action.Options);
        var node = Assert.Single(built.Steps).Children![0].Children![0];
        Assert.True(node.IsGenerated);
        Assert.Equal(Guid.Empty, node.DraftId);
        Assert.Equal(AutofocusOrigin.TrackStart, node.AutofocusOrigin);
        Assert.Equal("automatic · track start", node.Description.Summary);
    }

    [Fact]
    public async Task EachGeneratedAutofocus_KnowsWhyItRuns_AndAnExplicitOneDoesNot()
    {
        await using var host = CreateHost();
        var explicitStep = Explicit();
        var block = Block(
            Track(Main, Policy(start: true, filter: true), ChangeFilter(), Exposure(), explicitStep),
            Track(Wide, null, Exposure()));

        var built = Build(host, block);

        var children = Assert.Single(built.Steps).Children![0].Children!;
        Assert.Equal(
            [AutofocusOrigin.TrackStart, null, AutofocusOrigin.AfterFilterChange, null, null],
            children.Select(c => c.AutofocusOrigin));
        Assert.Equal(explicitStep.Id, children[^1].DraftId);
        Assert.False(children[^1].IsGenerated);
    }

    [Fact]
    public async Task EveryGeneratedAutofocus_IsAFreshAction_PerBuild()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Exposure()), Track(Wide, null, Exposure()));

        var first = Build(host, block);
        var second = Build(host, block);

        Assert.NotSame(MainTrack(first).Steps[0], MainTrack(second).Steps[0]);
    }

    [Fact]
    public async Task WithADitherPolicy_ASafePointFollowsEveryGeneratedAutofocus_LikeEveryAtomicStep()
    {
        await using var host = CreateHost();
        var dither = MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Wide };
        var block = Block(dither, Track(Main, Policy(start: true, filter: true), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));

        var built = Build(host, block);

        // start AF, its safe point; change filter, its safe point; filter AF, its safe point; exposure, its safe point
        Assert.Equal("ASFSASES", Shape(MainTrack(built).Steps));
        var children = Assert.Single(built.Steps).Children![0].Children!;
        Assert.Equal([true, true, false, true, true, true, false, true], children.Select(c => c.IsGenerated));
    }

    [Fact]
    public async Task WithoutADitherPolicy_NoSafePointIsGenerated_AroundAGeneratedAutofocus()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true, filter: true), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));

        Assert.DoesNotContain(MainTrack(Build(host, block)).Steps, s => s is SafePointStep);
    }

    // The policy of a track, checked

    [Fact]
    public async Task AValidPolicy_IsValid()
    {
        await using var host = CreateHost();

        var block = Block(Track(Main, Policy(start: true, filter: true), ChangeFilter(), Exposure()), Track(Wide, null, Exposure()));

        Assert.Empty(Problems(host, block));
    }

    [Fact]
    public async Task EnabledWithoutAnyTrigger_IsInvalid()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(), Exposure()), Track(Wide, null, Exposure()));

        var problems = Problems(host, block);

        Assert.Equal(["Step 1.1 (Rig Track): Enable at least one Autofocus trigger."], problems);
        Assert.Throws<SequenceConfigurationException>(() => Build(host, block));
    }

    [Fact]
    public async Task APolicyThatIsOff_IsNotLookedAt_EvenWithNonsense_ButKeepsItsValues()
    {
        await using var host = CreateHost();
        var nonsense = new RigAutofocusPolicyDraft(false, false, true, -1, 0, 4);
        var block = Block(Track(Main, nonsense, Exposure()), Track(Wide, null, Exposure()));

        Assert.Empty(Problems(host, block));
        Assert.Equal(nonsense, block.Tracks[0].AutofocusPolicy);
    }

    [Fact]
    public async Task ATrackStartTriggerForARigWithoutAFocuser_IsInvalid()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, Optics));
        var block = Block(Track(new RigId("rig.bare"), Policy(start: true), Exposure()), Track(Main, null, Exposure()));

        Assert.Equal(["Step 1.1 (Rig Track): The rig 'rig.bare' has no focuser."], Problems(host, block));
    }

    [Fact]
    public async Task AFilterTriggerForARigWithoutAFilterWheel_IsInvalid_ButTheStartTriggerAloneIsNot()
    {
        await using var host = CreateHost();
        // The wide rig has a focuser, but no filter wheel.
        var filter = Block(Track(Wide, Policy(filter: true), Exposure()), Track(Main, null, Exposure()));
        var start = Block(Track(Wide, Policy(start: true), Exposure()), Track(Main, null, Exposure()));

        Assert.Equal(
            ["Step 1.1 (Rig Track): The rig 'rig.wide' has no filter wheel, so Autofocus cannot follow a filter change."],
            Problems(host, filter));
        Assert.Empty(Problems(host, start));
    }

    [Theory]
    [InlineData(0.0, 400, 7, "Autofocus exposure must be greater than 0 s.")]
    [InlineData(-2.0, 400, 7, "Autofocus exposure must be greater than 0 s.")]
    [InlineData(1.0, 0, 7, "Autofocus step size must be greater than 0.")]
    [InlineData(1.0, 400, 6, "Autofocus samples must be an odd number between 5 and 21.")]
    [InlineData(1.0, 400, 3, "Autofocus samples must be an odd number between 5 and 21.")]
    [InlineData(1.0, 400, 23, "Autofocus samples must be an odd number between 5 and 21.")]
    public async Task AnInvalidSetting_IsReportedOnTheTrack(double seconds, int step, int samples, string expected)
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true, seconds: seconds, step: step, samples: samples), Exposure()), Track(Wide, null, Exposure()));

        Assert.Equal([$"Step 1.1 (Rig Track): {expected}"], Problems(host, block));
    }

    [Fact]
    public async Task TooLittleFocuserTravelForTheSamples_IsReported()
    {
        await using var host = CreateHost();
        // The wide focuser goes from 0 to 12000: five samples of 3500 steps need 14000.
        var block = Block(Track(Wide, Policy(start: true, step: 3500), Exposure()), Track(Main, null, Exposure()));

        var problems = Problems(host, block);

        Assert.Single(problems);
        Assert.Contains("The focuser 'focuser.wide' does not have enough travel", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRig_IsReportedByTheTrack_NotByThePolicy()
    {
        await using var host = CreateHost();
        var block = Block(
            new RigTrackDraft(Guid.NewGuid(), new RigId("rig.gone"), [Exposure()], Policy(start: true)),
            Track(Main, null, Exposure()));

        var problems = Problems(host, block);

        Assert.Equal(["Step 1.1 (Rig Track): The rig 'rig.gone' is not available."], problems);
    }

    [Fact]
    public async Task WithoutAnythingToMeasureFocusWith_AnEnabledPolicyIsInvalid()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, Policy(start: true), Exposure()), Track(Wide, null, Exposure()));

        var problems = Problems(host, block, metrics: false);

        Assert.Equal(["Step 1.1 (Rig Track): Autofocus is not available: there is nothing to measure focus with."], problems);
    }

    [Fact]
    public async Task APolicyThatIsOn_NeedsTheFocuserOfItsRigConnected_ForRunning()
    {
        await using var host = CreateHost();
        var on = Block(Track(Main, Policy(start: true), Exposure()), Track(Narrow, null, Exposure()));
        var off = Block(Track(Main, Policy(enabled: false), Exposure()), Track(Narrow, null, Exposure()));
        var noTrigger = Block(Track(Main, Policy(), Exposure()), Track(Narrow, null, Exposure()));

        Assert.Contains(DemoSetup.MainFocuserId, SequenceDraftBuilder.RequiredDeviceIds([on], Context(host)));
        Assert.DoesNotContain(DemoSetup.MainFocuserId, SequenceDraftBuilder.RequiredDeviceIds([off], Context(host)));
        Assert.DoesNotContain(DemoSetup.MainFocuserId, SequenceDraftBuilder.RequiredDeviceIds([noTrigger], Context(host)));
    }

    [Fact]
    public async Task TheTrackSummary_SaysWhenItFocuses_OnlyWhileThePolicyIsOn()
    {
        await using var host = CreateHost();

        string Summary(RigAutofocusPolicyDraft? policy) =>
            SequenceDraftBuilder.DescribeTrack(host.DeviceRegistry, Track(Main, policy, Exposure()), Context(host)).Summary;

        Assert.Equal("Main Camera", Summary(null));
        Assert.Equal("Main Camera", Summary(Policy(start: true, enabled: false)));
        Assert.Equal("Main Camera\nAutofocus: track start", Summary(Policy(start: true)));
        Assert.Equal("Main Camera\nAutofocus: filter change", Summary(Policy(filter: true)));
        Assert.Equal("Main Camera\nAutofocus: track start + filter change", Summary(Policy(start: true, filter: true)));
    }

    [Fact]
    public async Task ADraftThatKnowsNoPolicy_BuildsExactlyAsBefore()
    {
        await using var host = CreateHost();
        var block = Block(Track(Main, null, ChangeFilter(), Exposure(), Explicit()), Track(Wide, null, Exposure()));

        var built = Build(host, block);

        Assert.Equal("FEA", Shape(MainTrack(built).Steps));
        Assert.DoesNotContain(Assert.Single(built.Steps).Children![0].Children!, c => c.IsGenerated);
    }
}

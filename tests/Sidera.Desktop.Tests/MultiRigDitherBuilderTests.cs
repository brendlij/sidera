using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>The Dither Policy of a Multi-Rig block: what is refused, and what it compiles to.</summary>
public class MultiRigDitherBuilderTests
{
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");
    private static readonly SharedEquipmentDraft Shared = new(new DeviceId("mount.eq6"), new DeviceId("guider.main"));

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host, SharedEquipmentDraft? shared = null) =>
        new(host.RigRegistry, shared ?? Shared);

    private static RigExposureStepDraft Exposure(double seconds = 1) => new(Guid.NewGuid(), seconds);
    private static DelayStepDraft Delay(double seconds = 1) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId? rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigDitherPolicyDraft Policy(RigId? trigger = null) => MultiRigDitherPolicyDraft.Default with
    {
        Enabled = true,
        TriggerRigId = trigger ?? Wide,
    };

    private static MultiRigStepDraft Block(MultiRigDitherPolicyDraft? policy, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, policy);

    private static MultiRigStepDraft Session(MultiRigDitherPolicyDraft policy) => SessionOf(policy);

    private static MultiRigStepDraft Session() => SessionOf(Policy());

    // The same three tracks with whatever policy is given, none included.
    private static MultiRigStepDraft SessionOf(MultiRigDitherPolicyDraft? policy) => Block(
        policy,
        Track(Main, Repeat(40, Exposure(300))),
        Track(Wide, Repeat(120, Exposure(60), Delay(1))),
        Track(Narrow, Exposure(180)));

    private static StartGuidingStepDraft Start() => new(Guid.NewGuid(), new DeviceId("guider.main"));
    private static StopGuidingStepDraft Stop() => new(Guid.NewGuid(), new DeviceId("guider.main"));

    private static IReadOnlyList<string> Problems(SideraRuntimeHost host, IReadOnlyList<SequenceStepDraft> steps, SharedEquipmentDraft? shared = null)
    {
        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, steps, Context(host, shared));
        return SequenceDraftBuilder.Sentences(steps, validation);
    }

    // The policy

    [Fact]
    public void TheDefaultPolicy_IsOff_AndHasUsableValues()
    {
        var policy = MultiRigDitherPolicyDraft.Default;

        Assert.False(policy.Enabled);
        Assert.Null(policy.TriggerRigId);
        Assert.Equal((3, 1.5, 0.5, 1, 10), (policy.EveryNFrames, policy.AmplitudePixels, policy.SettleThresholdPixels, policy.SettleStableSeconds, policy.SettleTimeoutSeconds));
    }

    // Validation

    [Fact]
    public async Task AValidPolicy_IsValid()
    {
        await using var host = CreateHost();

        Assert.Empty(Problems(host, [Start(), Session(), Stop()]));
    }

    [Fact]
    public async Task APolicyThatIsOff_IsNotLookedAt_EvenWithNonsense()
    {
        await using var host = CreateHost();
        var nonsense = new MultiRigDitherPolicyDraft(false, new RigId("rig.nowhere"), 0, -1, 0, double.NaN, -5);

        Assert.Empty(Problems(host, [Session(nonsense)], shared: new SharedEquipmentDraft(null, null)));
    }

    [Fact]
    public async Task APolicyThatIsOff_NeedsNoSharedEquipment_AndRequiresNoSharedDevices()
    {
        await using var host = CreateHost();
        var block = SessionOf(null);

        var required = SequenceDraftBuilder.RequiredDeviceIds([block], Context(host));

        Assert.DoesNotContain(new DeviceId("mount.eq6"), required);
        Assert.DoesNotContain(new DeviceId("guider.main"), required);
    }

    [Fact]
    public async Task AnEnabledPolicy_RequiresTheSharedMountAndGuider_AsDevices()
    {
        await using var host = CreateHost();

        var required = SequenceDraftBuilder.RequiredDeviceIds([Session()], Context(host));

        Assert.Contains(new DeviceId("mount.eq6"), required);
        Assert.Contains(new DeviceId("guider.main"), required);
        Assert.Contains(DemoSetup.MainCameraId, required);
        Assert.Equal(required.Count, required.Distinct().Count());
    }

    [Fact]
    public async Task NoTriggerRig_IsReported()
    {
        await using var host = CreateHost();
        var policy = Policy() with { TriggerRigId = null };

        Assert.Equal(["Step 1 (Multi-Rig Imaging): No trigger rig selected."], Problems(host, [Session(policy)]));
    }

    [Fact]
    public async Task ATriggerRigThatIsNoTrackOfTheBlock_IsReported_EvenWhenTheRigExists()
    {
        await using var host = CreateHost();
        var block = Block(Policy(Narrow), Track(Main, Exposure()), Track(Wide, Exposure()));

        Assert.Equal(
            ["Step 1 (Multi-Rig Imaging): The trigger rig 'rig.narrow' is not a track of this block."],
            Problems(host, [block]));
    }

    [Fact]
    public async Task ATriggerRigThatIsNotAvailable_IsKeptAndReported()
    {
        await using var host = CreateHost();
        var block = Block(Policy(new RigId("rig.observatory")), Track(Main, Exposure()), Track(Wide, Exposure()));

        var problems = Problems(host, [block]);

        Assert.Contains("Step 1 (Multi-Rig Imaging): The trigger rig 'rig.observatory' is not a track of this block.", problems);
    }

    [Fact]
    public async Task ATriggerRigWithoutAnyExposureToCount_IsReported()
    {
        await using var host = CreateHost();
        var onlyDelay = Block(Policy(), Track(Main, Exposure()), Track(Wide, Delay()));
        var emptyRepeatBody = Block(Policy(), Track(Main, Exposure()), Track(Wide, Repeat(0, Exposure())));
        var noSteps = Block(Policy(), Track(Main, Exposure()), Track(Wide));

        foreach (var block in new[] { onlyDelay, emptyRepeatBody, noSteps })
        {
            Assert.Contains(
                "Step 1 (Multi-Rig Imaging): The trigger rig 'rig.wide' has no exposure to count: dithering would never start.",
                Problems(host, [block]));
        }
    }

    [Fact]
    public async Task ATriggerRigWithFewerFramesThanTheInterval_IsValid_ItJustNeverDithers()
    {
        await using var host = CreateHost();
        var block = Block(Policy() with { EveryNFrames = 50 }, Track(Main, Exposure()), Track(Wide, Repeat(2, Exposure())));

        Assert.Empty(Problems(host, [block]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task AnIntervalBelowOne_IsReported(int every)
    {
        await using var host = CreateHost();

        Assert.Equal(
            ["Step 1 (Multi-Rig Imaging): Dither interval must be at least 1 frame."],
            Problems(host, [Session(Policy() with { EveryNFrames = every })]));
    }

    [Theory]
    [InlineData("amplitude", 0.0, "Dither amplitude must be greater than 0 px.")]
    [InlineData("amplitude", -1.0, "Dither amplitude must be greater than 0 px.")]
    [InlineData("amplitude", double.NaN, "Dither amplitude must be greater than 0 px.")]
    [InlineData("amplitude", double.PositiveInfinity, "Dither amplitude must be greater than 0 px.")]
    [InlineData("threshold", 0.0, "Settle threshold must be greater than 0 px.")]
    [InlineData("threshold", double.NaN, "Settle threshold must be greater than 0 px.")]
    [InlineData("stable", 0.0, "Settle stable time must be greater than 0 s.")]
    [InlineData("stable", -2.0, "Settle stable time must be greater than 0 s.")]
    [InlineData("timeout", 0.0, "Settle timeout must be greater than 0 s.")]
    [InlineData("timeout", double.NaN, "Settle timeout must be greater than 0 s.")]
    public async Task ANumberThatIsNotPositive_IsReportedByName(string field, double value, string expected)
    {
        await using var host = CreateHost();
        var policy = field switch
        {
            "amplitude" => Policy() with { AmplitudePixels = value },
            "threshold" => Policy() with { SettleThresholdPixels = value },
            "stable" => Policy() with { SettleStableSeconds = value },
            _ => Policy() with { SettleTimeoutSeconds = value },
        };

        Assert.Equal([$"Step 1 (Multi-Rig Imaging): {expected}"], Problems(host, [Session(policy)]));
    }

    [Theory]
    [InlineData(2.0, 2.0)]
    [InlineData(5.0, 1.0)]
    public async Task ATimeoutThatIsNotLongerThanTheStableTime_IsReported(double stable, double timeout)
    {
        await using var host = CreateHost();
        var policy = Policy() with { SettleStableSeconds = stable, SettleTimeoutSeconds = timeout };

        Assert.Equal(
            ["Step 1 (Multi-Rig Imaging): Settle timeout must be longer than the stable time."],
            Problems(host, [Session(policy)]));
    }

    [Fact]
    public async Task WithoutASharedMountOrGuider_TheyAreReportedBothWays()
    {
        await using var host = CreateHost();

        var none = Problems(host, [Session()], new SharedEquipmentDraft(null, null));
        var noMount = Problems(host, [Session()], new SharedEquipmentDraft(null, new DeviceId("guider.main")));

        Assert.Contains("Step 1 (Multi-Rig Imaging): Dither needs a shared mount: select one in the shared equipment.", none);
        Assert.Contains("Step 1 (Multi-Rig Imaging): Dither needs a shared guider: select one in the shared equipment.", none);
        Assert.Contains("Step 1 (Multi-Rig Imaging): Dither needs a shared mount: select one in the shared equipment.", noMount);
        Assert.DoesNotContain(noMount, p => p.Contains("shared guider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADitherAfterGuidingWasStopped_IsReported_LikeADitherStep()
    {
        await using var host = CreateHost();

        var problems = Problems(host, [Start(), Stop(), Session()]);

        Assert.Single(problems);
        Assert.StartsWith("Step 3 (Multi-Rig Imaging): Dither needs guiding, but step 2 stopped it.", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGuiderThatCannotDither_IsReported()
    {
        await using var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);

        // The demo mount is no guider: using it as the shared guider is a wrong kind of device, reported by its checks.
        var problems = Problems(host, [Session()], new SharedEquipmentDraft(new DeviceId("mount.eq6"), new DeviceId("mount.eq6")));

        Assert.NotEmpty(problems);
    }

    [Fact]
    public async Task EveryProblemOfAPolicy_ShowsOnTheBlock_NotOnTheTracks()
    {
        await using var host = CreateHost();
        var policy = Policy() with { EveryNFrames = 0, AmplitudePixels = 0 };
        var block = Session(policy);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));

        Assert.Equal(2, validation.ProblemsOf(block.Id).Count);
        Assert.All(block.Tracks, track => Assert.Empty(validation.ProblemsOf(track.Id)));
    }

    [Fact]
    public async Task ADraftWithAnInvalidPolicy_CannotBeBuilt()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Session(Policy() with { EveryNFrames = 0 }) };

        Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host)));
    }

    // Compilation

    private static ParallelStep Parallel(BuiltSequence built, int index = 0) => Assert.IsType<ParallelStep>(built.Sequence.Steps[index]);

    private static IEnumerable<ISequenceStep> Flatten(ISequenceStep step)
    {
        yield return step;
        var children = step switch
        {
            ParallelStep p => p.Children,
            RigTrackStep t => t.Steps,
            SequenceGroup g => g.Children,
            RepeatStep r => [r.Child],
            DitherEveryNthFrameStep n => [n.Dither],
            _ => (IReadOnlyList<ISequenceStep>)[],
        };
        foreach (var inner in children.SelectMany(Flatten))
        {
            yield return inner;
        }
    }

    [Fact]
    public async Task APolicy_GivesTheBlockACoordinationGroup_OneForEachBlock()
    {
        await using var host = CreateHost();
        var a = Session();
        var b = Session();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [a, b], Context(host));

        var groupA = Parallel(built, 0).CoordinationGroup;
        var groupB = Parallel(built, 1).CoordinationGroup;
        Assert.NotNull(groupA);
        Assert.NotNull(groupB);
        Assert.NotEqual(groupA, groupB);
    }

    [Fact]
    public async Task APolicyThatIsOff_CompilesToExactlyWhatNoPolicyCompilesTo()
    {
        await using var host = CreateHost();
        var withOffPolicy = Session(MultiRigDitherPolicyDraft.Default);
        var without = SessionOf(null);

        foreach (var block in new[] { withOffPolicy, without })
        {
            var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));
            var parallel = Parallel(built);
            Assert.Null(parallel.CoordinationGroup);
            var all = Flatten(parallel).ToList();
            Assert.DoesNotContain(all, step => step is SafePointStep or DitherEveryNthFrameStep or DitherAction);
        }
    }

    [Fact]
    public async Task ASafePointFollowsEveryExposureAndEveryDelay_OfEveryTrack_AndNothingElse()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Session()], Context(host));

        var tracks = Parallel(built).Children.Cast<RigTrackStep>().ToList();
        foreach (var track in tracks)
        {
            var sequence = Flatten(track).Where(s => s is CameraExposureAction or DelayAction or SafePointStep).ToList();
            // Exposure/Delay and a safe point always come in pairs, in that order.
            for (var i = 0; i < sequence.Count; i += 2)
            {
                Assert.True(sequence[i] is CameraExposureAction or DelayAction, track.Name);
                Assert.IsType<SafePointStep>(sequence[i + 1]);
            }
        }

        // Main: Repeat × 40 [Exposure]; the body is [Exposure, SafePoint] (no trigger there).
        var mainBody = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(tracks[0].Steps.First()).Child);
        Assert.Collection(mainBody.Children, s => Assert.IsType<CameraExposureAction>(s), s => Assert.IsType<SafePointStep>(s));

        // Narrow: Exposure, SafePoint.
        Assert.Collection(tracks[2].Steps, s => Assert.IsType<CameraExposureAction>(s), s => Assert.IsType<SafePointStep>(s));
    }

    [Fact]
    public async Task OnlyTheTriggerTrack_CountsItsExposures_BetweenTheExposureAndItsSafePoint()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Session()], Context(host));

        var tracks = Parallel(built).Children.Cast<RigTrackStep>().ToList();
        Assert.Single(Flatten(Parallel(built)).OfType<DitherEveryNthFrameStep>());
        var wideBody = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(tracks[1].Steps.First()).Child);
        Assert.Collection(
            wideBody.Children,
            s => Assert.IsType<CameraExposureAction>(s),
            s => Assert.IsType<DitherEveryNthFrameStep>(s), // counts the exposure that just completed
            s => Assert.IsType<SafePointStep>(s),
            s => Assert.IsType<DelayAction>(s), // a delay is no frame
            s => Assert.IsType<SafePointStep>(s));
        Assert.DoesNotContain(Flatten(tracks[0]), s => s is DitherEveryNthFrameStep);
        Assert.DoesNotContain(Flatten(tracks[2]), s => s is DitherEveryNthFrameStep);
    }

    [Fact]
    public async Task TheDither_IsTheExistingDitherAction_WithThePolicyAndEveryCameraOfTheBlock()
    {
        await using var host = CreateHost();
        var policy = Policy() with
        {
            EveryNFrames = 4, AmplitudePixels = 2.5, SettleThresholdPixels = 0.4, SettleStableSeconds = 3, SettleTimeoutSeconds = 40,
        };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Session(policy)], Context(host));

        var dither = Flatten(Parallel(built)).OfType<DitherAction>().Single();
        Assert.Equal(2.5, dither.AmplitudePixels);
        Assert.Equal(new DeviceId("guider.main"), dither.GuiderId);
        Assert.Equal(new DeviceId("mount.eq6"), dither.MountId);
        Assert.Equal(
            new[] { DemoSetup.MainCameraId, DemoSetup.WideCameraId, DemoSetup.NarrowCameraId }.Select(id => id.Value).Order(),
            dither.CameraIds.Select(id => id.Value).Order());
        Assert.Equal(0.4, dither.SettleOptions!.MaximumErrorPixels);
        Assert.Equal(TimeSpan.FromSeconds(3), dither.SettleOptions.StableDuration);
        Assert.Equal(TimeSpan.FromSeconds(40), dither.SettleOptions.Timeout);
        Assert.Equal(4, Flatten(Parallel(built)).OfType<DitherEveryNthFrameStep>().Single().EveryNFrames);
    }

    [Fact]
    public async Task GeneratedStepsAreNotDraftSteps_TheyAreMarkedAsGenerated_AndNothingMapsToThem()
    {
        await using var host = CreateHost();
        var block = Session();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var all = new List<BuiltStep>();
        void Collect(BuiltStep step)
        {
            all.Add(step);
            foreach (var child in step.Children ?? [])
            {
                Collect(child);
            }
        }

        foreach (var step in built.Steps)
        {
            Collect(step);
        }

        var generated = all.Where(s => s.IsGenerated).ToList();
        Assert.NotEmpty(generated);
        Assert.All(generated, s => Assert.Equal(Guid.Empty, s.DraftId));
        Assert.Contains(generated, s => s.Step is SafePointStep);
        Assert.Contains(generated, s => s.Step is DitherEveryNthFrameStep);
        Assert.DoesNotContain(all.Where(s => !s.IsGenerated), s => s.Step is SafePointStep or DitherEveryNthFrameStep);
    }

    [Fact]
    public async Task EveryBuild_MakesFreshCounters_SoRunsNeverShareACount()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Session() };

        var first = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));
        var second = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host));

        var a = Flatten(Parallel(first)).OfType<DitherEveryNthFrameStep>().Single();
        var b = Flatten(Parallel(second)).OfType<DitherEveryNthFrameStep>().Single();
        Assert.NotSame(a, b);
        Assert.NotSame(a.Dither, b.Dither);
    }

    [Fact]
    public async Task ThePolicyIsDescribedOnTheBlock_InTermsOfTheRigName_AndOnlyWhenOn()
    {
        await using var host = CreateHost();
        var on = Session(Policy() with { EveryNFrames = 3 });
        var every = Session(Policy() with { EveryNFrames = 1 });
        var off = SessionOf(null);

        var onText = SequenceDraftBuilder.Describe(host.DeviceRegistry, on, Context(host)).Summary;
        var everyText = SequenceDraftBuilder.Describe(host.DeviceRegistry, every, Context(host)).Summary;
        var offText = SequenceDraftBuilder.Describe(host.DeviceRegistry, off, Context(host)).Summary;

        Assert.Equal("3 rig tracks\nDither every 3 Wide Rig frames · 1.5 px · settle ≤ 0.5 px for 1 s", onText);
        Assert.Equal("3 rig tracks\nDither after every Wide Rig frame · 1.5 px · settle ≤ 0.5 px for 1 s", everyText);
        Assert.Equal("3 rig tracks", offText);
    }

    [Fact]
    public async Task ATwoRigBlock_AndAThreeRigBlock_BothCompile_WithTheirOwnGroups()
    {
        await using var host = CreateHost();
        var two = Block(Policy(), Track(Main, Exposure()), Track(Wide, Exposure()));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [two, Session()], Context(host));

        Assert.All(built.Sequence.Steps.Cast<ParallelStep>(), p => Assert.NotNull(p.CoordinationGroup));
    }
}

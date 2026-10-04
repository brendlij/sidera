using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

public class SequenceRepeatBuilderTests
{
    private static readonly DeviceId Camera = new("camera.main");
    private static readonly DeviceId Mount = new("mount.eq6");
    private static readonly DeviceId Guider = new("guider.main");

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        return host;
    }

    private static ExposureStepDraft Exposure(double seconds = 2) => new(Guid.NewGuid(), Camera, seconds);
    private static DelayStepDraft Delay(double seconds = 3) => new(Guid.NewGuid(), seconds);
    private static StartGuidingStepDraft StartGuiding() => new(Guid.NewGuid(), Guider);
    private static StopGuidingStepDraft StopGuiding() => new(Guid.NewGuid(), Guider);

    private static DitherStepDraft Dither() => new(Guid.NewGuid(), Guider, Mount, Camera, 1.5, 0.5, 1, 10);

    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) =>
        new(Guid.NewGuid(), count, children);

    // Mapping to the runtime

    [Fact]
    public async Task Repeat_BecomesARuntimeRepeatStepAroundAGroupOfItsChildren_InOrder()
    {
        await using var host = CreateHost();
        var repeat = Repeat(5, Exposure(300), Delay(2), Dither());

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [StartGuiding(), repeat, StopGuiding()]);

        Assert.Equal(3, built.Sequence.Steps.Count);
        Assert.IsType<StartGuidingAction>(built.Sequence.Steps[0]);
        Assert.IsType<StopGuidingAction>(built.Sequence.Steps[2]);
        var runtimeRepeat = Assert.IsType<RepeatStep>(built.Sequence.Steps[1]);
        Assert.Equal(5, runtimeRepeat.Count);
        var body = Assert.IsType<SequenceGroup>(runtimeRepeat.Child);
        Assert.Collection(
            body.Children,
            child =>
            {
                var exposure = Assert.IsType<CameraExposureAction>(child);
                Assert.Equal(Camera, exposure.CameraId);
                Assert.Equal(TimeSpan.FromSeconds(300), exposure.Duration);
            },
            child => Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<DelayAction>(child).Duration),
            child =>
            {
                var dither = Assert.IsType<DitherAction>(child);
                Assert.Equal(Guider, dither.GuiderId);
                Assert.Equal(Mount, dither.MountId);
                Assert.Equal([Camera], dither.CameraIds);
                Assert.Equal(1.5, dither.AmplitudePixels);
                Assert.Equal(0.5, dither.SettleOptions!.MaximumErrorPixels);
                Assert.Equal(TimeSpan.FromSeconds(1), dither.SettleOptions.StableDuration);
                Assert.Equal(TimeSpan.FromSeconds(10), dither.SettleOptions.Timeout);
            });
    }

    [Fact]
    public async Task ARepeatOfOneStep_IsBuiltTheSameWay_SoThatPositionsAlwaysHaveTheSameShape()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [Repeat(3, Exposure())]);

        var body = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(Assert.Single(built.Sequence.Steps)).Child);
        Assert.IsType<CameraExposureAction>(Assert.Single(body.Children));
    }

    [Fact]
    public async Task Repeat_AddsNoParallelBranchesSafePointsOrCoordination_AroundADither()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [StartGuiding(), Repeat(2, Exposure(), Dither())]);

        var body = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(built.Sequence.Steps[1]).Child);
        Assert.All(body.Children, child => Assert.IsNotType<SafePointStep>(child));
        Assert.All(body.Children, child => Assert.IsNotType<ParallelStep>(child));
        Assert.Equal(2, body.Children.Count);
    }

    [Fact]
    public async Task Build_TellsWhichDraftStepEveryRuntimeStepCameFrom_IncludingTheStepsInsideARepeat()
    {
        await using var host = CreateHost();
        var exposure = Exposure(4);
        var delay = Delay(7);
        var repeat = Repeat(3, exposure, delay);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [repeat]);

        var top = Assert.Single(built.Steps);
        Assert.Equal(repeat.Id, top.DraftId);
        Assert.Same(built.Sequence.Steps[0], top.Step);
        Assert.Equal(("Repeat × 3", "2 steps"), (top.Description.Title, top.Description.Summary));
        Assert.Equal([exposure.Id, delay.Id], top.Children!.Select(c => c.DraftId));
        var group = Assert.IsType<SequenceGroup>(Assert.IsType<RepeatStep>(top.Step).Child);
        Assert.Equal(group.Children, top.Children!.Select(c => c.Step));
        Assert.Equal("Main Camera · 4 s · Camera defaults", top.Children![0].Description.Summary);
    }

    [Fact]
    public async Task Build_MakesNewRuntimeObjectsOnEveryCall_AlsoInsideARepeat()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Repeat(2, Exposure(), Delay()) };

        var first = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);
        var second = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);

        var a = Assert.IsType<RepeatStep>(first.Sequence.Steps[0]);
        var b = Assert.IsType<RepeatStep>(second.Sequence.Steps[0]);
        Assert.NotSame(a, b);
        Assert.NotSame(a.Child, b.Child);
        var childrenA = ((SequenceGroup)a.Child).Children;
        var childrenB = ((SequenceGroup)b.Child).Children;
        Assert.All(childrenA.Zip(childrenB), pair => Assert.NotSame(pair.First, pair.Second));
    }

    [Fact]
    public async Task Describe_ShowsTheCountAndHowManyStepsAreInside()
    {
        await using var host = CreateHost();

        Assert.Equal(("Repeat × 12", "no steps"), Describe(host, Repeat(12)));
        Assert.Equal(("Repeat × 2", "1 step"), Describe(host, Repeat(2, Delay())));
        Assert.Equal(("Repeat × 2", "3 steps"), Describe(host, Repeat(2, Delay(), Delay(), Delay())));
    }

    private static (string, string) Describe(SideraRuntimeHost host, RepeatStepDraft repeat)
    {
        var d = SequenceDraftBuilder.Describe(host.DeviceRegistry, repeat);
        return (d.Title, d.Summary);
    }

    [Fact]
    public void ARepeatCanHoldLeafStepsOnly_ByTheTypeOfItsChildren()
    {
        var children = typeof(RepeatStepDraft).GetProperty(nameof(RepeatStepDraft.Children))!.PropertyType;

        Assert.Equal(typeof(IReadOnlyList<LeafStepDraft>), children);
        Assert.False(typeof(LeafStepDraft).IsAssignableFrom(typeof(RepeatStepDraft)));
    }

    [Fact]
    public async Task TheDevicesOfARepeat_AreTheDevicesOfItsChildren()
    {
        await using var host = CreateHost();
        var repeat = Repeat(2, Exposure(), Dither());

        Assert.Equivalent(new[] { Camera, Guider, Mount }, repeat.DeviceIds.Distinct());
        Assert.Empty(Repeat(2, Delay()).DeviceIds);
    }

    // Validation

    [Fact]
    public async Task AnEmptyRepeat_IsInvalid_AndCannotBeBuilt()
    {
        await using var host = CreateHost();
        var repeat = Repeat(3);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [repeat]);
        var error = Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, [repeat]));

        Assert.Equal(["Repeat must contain at least one step."], validation.ProblemsOf(repeat.Id));
        Assert.Equal(["Step 1 (Repeat): Repeat must contain at least one step."], error.Problems);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task ACountBelowOne_IsInvalid(int count)
    {
        await using var host = CreateHost();
        var repeat = Repeat(count, Delay());

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [repeat]);

        Assert.Equal(["Repeat count must be at least 1."], validation.ProblemsOf(repeat.Id));
    }

    [Fact]
    public async Task AProblemOfAChild_IsReportedOnTheChild_NamedBy_ItsNumber()
    {
        await using var host = CreateHost();
        var bad = Exposure(0);
        var repeat = Repeat(2, Delay(), bad);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Delay(), repeat]);
        var error = Assert.Throws<SequenceConfigurationException>(
            () => SequenceDraftBuilder.Build(host.DeviceRegistry, [Delay(), repeat]));

        Assert.Equal(["Exposure must be greater than 0 s."], validation.ProblemsOf(bad.Id));
        Assert.Empty(validation.ProblemsOf(repeat.Id));
        Assert.Equal(["Step 2.2 (Exposure): Exposure must be greater than 0 s."], error.Problems);
    }

    [Fact]
    public async Task AMissingDeviceInsideARepeat_IsInvalid()
    {
        await using var host = CreateHost();
        var exposure = new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.gone"), 1);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Repeat(2, exposure)]);

        Assert.Equal(["The camera 'camera.gone' is not available."], validation.ProblemsOf(exposure.Id));
    }

    [Fact]
    public async Task ADitherInsideARepeat_IsCheckedLikeADitherOutsideOne()
    {
        await using var host = CreateHost();
        var bad = new DitherStepDraft(Guid.NewGuid(), Guider, Mount, Camera, 1, 0.5, 5, 5);
        var noGuider = new DitherStepDraft(Guid.NewGuid(), null, Mount, Camera, 1, 0.5, 1, 10);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Repeat(2, bad, noGuider)]);

        Assert.Equal(["Settle timeout must be longer than the stable time."], validation.ProblemsOf(bad.Id));
        Assert.Equal(["No guider selected."], validation.ProblemsOf(noGuider.Id));
    }

    [Fact]
    public async Task ADitherInsideARepeat_NeedsAGuiderThatCanDither()
    {
        await using var host = CreateHost();
        host.DeviceRegistry.Register(new PlainGuider(new DeviceId("guider.plain")));
        var dither = new DitherStepDraft(Guid.NewGuid(), new DeviceId("guider.plain"), Mount, Camera, 1, 0.5, 1, 10);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Repeat(2, dither)]);

        Assert.Equal(["Guider 'guider.plain' does not support dithering."], validation.ProblemsOf(dither.Id));
    }

    [Fact]
    public async Task StepIdsMustBeUniqueAcrossTheWholeDraft_AlsoInsideRepeats()
    {
        await using var host = CreateHost();
        var shared = Guid.NewGuid();
        var inside = new DelayStepDraft(shared, 1);
        var outside = new DelayStepDraft(shared, 1);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [outside, Repeat(2, inside)]);

        Assert.Contains("Two steps share the same id.", validation.SequenceProblems);
    }

    // Guiding order with Repeat: the draft is played through, a Repeat body a second time when it runs again.

    [Fact]
    public async Task StartGuiding_RepeatOfExposureAndDither_StopGuiding_IsValid()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [StartGuiding(), Repeat(5, Exposure(), Dither()), StopGuiding()]);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task ARepeatThatStartsAndStopsGuidingEachTime_IsValid_BecauseEveryRepetitionLeavesItAsItFoundIt()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [Repeat(5, StartGuiding(), Exposure(), StopGuiding())]);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task ARepeatThatOnlyStartsGuiding_FailsOnItsSecondRepetition_AndIsInvalid()
    {
        await using var host = CreateHost();
        var start = StartGuiding();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Repeat(2, start, Exposure())]);

        Assert.Equal(
            ["Guiding was already started by step 1.1 in the previous repetition."], validation.ProblemsOf(start.Id));
    }

    [Fact]
    public async Task TheSameRepeatRunOnce_IsValid()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Repeat(1, StartGuiding(), Exposure())]);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task StartGuidingBeforeARepeatThatStartsIt_IsInvalid()
    {
        await using var host = CreateHost();
        var inner = StartGuiding();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [StartGuiding(), Repeat(1, inner)]);

        Assert.Equal(["Guiding was already started by step 1."], validation.ProblemsOf(inner.Id));
    }

    [Fact]
    public async Task ADitherInARepeatAfterGuidingWasStopped_IsInvalid()
    {
        await using var host = CreateHost();
        var dither = Dither();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [StartGuiding(), StopGuiding(), Repeat(2, Exposure(), dither)]);

        Assert.Equal(["Dither needs guiding, but step 2 stopped it."], validation.ProblemsOf(dither.Id));
    }

    [Fact]
    public async Task ARepeatThatStopsGuidingBeforeItsDither_FailsOnTheSecondRepetition()
    {
        await using var host = CreateHost();
        var dither = Dither();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [StartGuiding(), Repeat(2, dither, StopGuiding())]);

        Assert.Equal(
            ["Dither needs guiding, but step 2.2 in the previous repetition stopped it."], validation.ProblemsOf(dither.Id));
    }

    [Fact]
    public async Task GuidingAfterARepeat_IsCheckedAgainstWhatTheRepeatLeft()
    {
        await using var host = CreateHost();
        var stop = StopGuiding();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [Repeat(2, StartGuiding(), StopGuiding()), stop]);

        // The repeat leaves the guider stopped, so a Stop afterwards stops it a second time.
        Assert.Equal(["Guiding was already stopped by step 1.2."], validation.ProblemsOf(stop.Id));
    }

    private sealed class PlainGuider(DeviceId id) : Sidera.Core.Guiding.IGuider
    {
        public DeviceId Id { get; } = id;
        public string Name => "Plain guider";
        public DeviceType Type => DeviceType.Guider;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
        public Sidera.Core.Guiding.GuidingState GuidingState => Sidera.Core.Guiding.GuidingState.Idle;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

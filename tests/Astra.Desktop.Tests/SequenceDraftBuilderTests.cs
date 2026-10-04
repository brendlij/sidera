using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop.Tests;

public class SequenceDraftBuilderTests
{
    private static readonly DeviceId Camera = new("camera.main");
    private static readonly DeviceId Mount = new("mount.eq6");
    private static readonly DeviceId Guider = new("guider.main");

    private static AstraRuntimeHost CreateHost()
    {
        var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        return host;
    }

    private static ExposureStepDraft Exposure(double seconds = 2, DeviceId? camera = null) =>
        new(Guid.NewGuid(), camera ?? Camera, seconds);

    private static DelayStepDraft Delay(double seconds = 3) => new(Guid.NewGuid(), seconds);

    private static SlewStepDraft Slew(double ra = 5.588, double dec = -5.39) => new(Guid.NewGuid(), Mount, ra, dec);

    private static StartGuidingStepDraft StartGuiding() => new(Guid.NewGuid(), Guider);

    private static StopGuidingStepDraft StopGuiding() => new(Guid.NewGuid(), Guider);

    private static DitherStepDraft Dither(
        double amplitude = 1.5, double threshold = 0.5, double stable = 1, double timeout = 10) =>
        new(Guid.NewGuid(), Guider, Mount, Camera, amplitude, threshold, stable, timeout);

    [Fact]
    public async Task Build_KeepsTheOrderOfTheDraft_AndMapsEveryStepKindToItsRuntimeStep()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[]
        {
            StartGuiding(), Slew(12.5, 45), Exposure(300), Dither(2, 0.7, 2.5, 30), Delay(10), StopGuiding(),
        };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);

        Assert.Equal(6, built.Sequence.Steps.Count);
        Assert.Collection(
            built.Sequence.Steps,
            step => Assert.Equal([ResourceId.ForDevice(Guider)], Assert.IsType<StartGuidingAction>(step).RequiredResources),
            step =>
            {
                var slew = Assert.IsType<SlewAction>(step);
                Assert.Equal(12.5, slew.Target.RightAscensionHours);
                Assert.Equal(45, slew.Target.DeclinationDegrees);
                Assert.Equal([ResourceId.ForDevice(Mount)], slew.RequiredResources);
            },
            step =>
            {
                var exposure = Assert.IsType<CameraExposureAction>(step);
                Assert.Equal(Camera, exposure.CameraId);
                Assert.Equal(TimeSpan.FromSeconds(300), exposure.Duration);
            },
            step =>
            {
                var dither = Assert.IsType<DitherAction>(step);
                Assert.Equal(Guider, dither.GuiderId);
                Assert.Equal(Mount, dither.MountId);
                Assert.Equal([Camera], dither.CameraIds);
                Assert.Equal(2, dither.AmplitudePixels);
                Assert.Equal(0.7, dither.SettleOptions!.MaximumErrorPixels);
                Assert.Equal(TimeSpan.FromSeconds(2.5), dither.SettleOptions.StableDuration);
                Assert.Equal(TimeSpan.FromSeconds(30), dither.SettleOptions.Timeout);
            },
            step => Assert.Equal(TimeSpan.FromSeconds(10), Assert.IsType<DelayAction>(step).Duration),
            step => Assert.Equal([ResourceId.ForDevice(Guider)], Assert.IsType<StopGuidingAction>(step).RequiredResources));
    }

    [Fact]
    public async Task Build_IsLinear_NothingIsWrappedInContainersOrCoordinated()
    {
        await using var host = CreateHost();

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [StartGuiding(), Exposure(), Dither(), StopGuiding()]);

        // A dither outside a coordination group needs no safe points; the draft adds no containers around it.
        Assert.All(built.Sequence.Steps, step => Assert.IsNotType<ParallelStep>(step));
        Assert.All(built.Sequence.Steps, step => Assert.IsNotType<SequenceGroup>(step));
        Assert.All(built.Sequence.Steps, step => Assert.IsNotType<SafePointStep>(step));
    }

    [Fact]
    public async Task Build_ReportsWhichDraftStepEachRuntimeStepCameFrom()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Exposure(4), Delay(7), Dither() };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);

        Assert.Equal(steps.Select(s => s.Id), built.Steps.Select(s => s.DraftId));
        Assert.Equal(built.Sequence.Steps, built.Steps.Select(s => s.Step));
        Assert.Equal(
            new[] { ("Exposure", "Main Camera · 4 s · Camera defaults"), ("Delay", "7 s"), ("Dither", "1.5 px · settle ≤ 0.5 px for 1 s") },
            built.Steps.Select(s => (s.Description.Title, s.Description.Summary)));
    }

    [Fact]
    public async Task Build_MakesNewRuntimeObjectsOnEveryCall()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Exposure(), Delay(), Dither() };

        var first = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);
        var second = SequenceDraftBuilder.Build(host.DeviceRegistry, steps);

        Assert.NotSame(first.Sequence, second.Sequence);
        Assert.Equal(first.Sequence.Steps.Count, second.Sequence.Steps.Count);
        for (var i = 0; i < first.Sequence.Steps.Count; i++)
        {
            Assert.NotSame(first.Sequence.Steps[i], second.Sequence.Steps[i]);
        }
    }

    [Fact]
    public async Task Build_RefusesAnEmptyDraft()
    {
        await using var host = CreateHost();

        var error = Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, []));

        Assert.Equal(["The sequence has no steps."], error.Problems);
    }

    [Fact]
    public async Task Build_RefusesADraftWithAnInvalidStep_AndNamesTheStep()
    {
        await using var host = CreateHost();
        var steps = new SequenceStepDraft[] { Exposure(), Delay(0), Slew(25, 0) };

        var error = Assert.Throws<SequenceConfigurationException>(() => SequenceDraftBuilder.Build(host.DeviceRegistry, steps));

        Assert.Contains("Step 2 (Delay): Delay must be greater than 0 s.", error.Problems);
        Assert.Contains(error.Problems, p => p.StartsWith("Step 3 (Slew): ", StringComparison.Ordinal));
        Assert.DoesNotContain(error.Problems, p => p.StartsWith("Step 1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e300)]
    public async Task Validate_RefusesExposureAndDelayDurationsThatAreNotPositiveAndFinite(double seconds)
    {
        await using var host = CreateHost();
        var exposure = Exposure(seconds);
        var delay = Delay(seconds);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [exposure, delay]);

        Assert.NotEmpty(validation.ProblemsOf(exposure.Id));
        Assert.NotEmpty(validation.ProblemsOf(delay.Id));
    }

    [Fact]
    public async Task Validate_ChecksEveryDeviceSelection()
    {
        await using var host = CreateHost();
        var noCamera = new ExposureStepDraft(Guid.NewGuid(), null, 1);
        var unknownMount = new SlewStepDraft(Guid.NewGuid(), new DeviceId("mount.gone"), 1, 1);
        var wrongKind = new StartGuidingStepDraft(Guid.NewGuid(), Camera);
        var ditherWithoutDevices = new DitherStepDraft(Guid.NewGuid(), null, Camera, Guider, 1, 1, 1, 2);

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [noCamera, unknownMount, wrongKind, ditherWithoutDevices]);

        Assert.Equal(["No camera selected."], validation.ProblemsOf(noCamera.Id));
        Assert.Equal(["The mount 'mount.gone' is not available."], validation.ProblemsOf(unknownMount.Id));
        Assert.Equal(["'camera.main' is not a guider."], validation.ProblemsOf(wrongKind.Id));
        Assert.Contains("No guider selected.", validation.ProblemsOf(ditherWithoutDevices.Id));
        Assert.Contains("'camera.main' is not a mount.", validation.ProblemsOf(ditherWithoutDevices.Id));
        Assert.Contains("'guider.main' is not a camera.", validation.ProblemsOf(ditherWithoutDevices.Id));
    }

    [Theory]
    [InlineData(25, 0)]
    [InlineData(-1, 0)]
    [InlineData(5, 91)]
    [InlineData(5, -91)]
    [InlineData(double.NaN, 0)]
    public async Task Validate_RefusesCoordinatesOutsideTheCelestialSphere(double ra, double dec)
    {
        await using var host = CreateHost();
        var slew = Slew(ra, dec);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [slew]);

        Assert.NotEmpty(validation.ProblemsOf(slew.Id));
    }

    [Theory]
    [InlineData(0, 0.5, 1, 10, "Dither amplitude must be greater than 0 px.")]
    [InlineData(-1, 0.5, 1, 10, "Dither amplitude must be greater than 0 px.")]
    [InlineData(1, 0, 1, 10, "Settle threshold must be greater than 0 px.")]
    [InlineData(1, 0.5, 0, 10, "Settle stable time must be greater than 0 s.")]
    [InlineData(1, 0.5, 1, 0, "Settle timeout must be greater than 0 s.")]
    [InlineData(1, 0.5, 5, 5, "Settle timeout must be longer than the stable time.")]
    [InlineData(1, 0.5, 5, 2, "Settle timeout must be longer than the stable time.")]
    public async Task Validate_RefusesDitherValuesThatDitherActionOrTheSettleOptionsRefuse(
        double amplitude, double threshold, double stable, double timeout, string expected)
    {
        await using var host = CreateHost();
        var dither = Dither(amplitude, threshold, stable, timeout);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [dither]);

        Assert.Equal([expected], validation.ProblemsOf(dither.Id));
    }

    [Fact]
    public async Task Validate_RefusesADitherOnAGuiderThatCannotDither()
    {
        await using var host = CreateHost();
        host.DeviceRegistry.Register(new PlainGuider(new DeviceId("guider.plain")));
        var dither = new DitherStepDraft(Guid.NewGuid(), new DeviceId("guider.plain"), Mount, Camera, 1, 0.5, 1, 10);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [dither]);

        Assert.Equal(["Guider 'guider.plain' does not support dithering."], validation.ProblemsOf(dither.Id));
    }

    // Guiding order: only what the draft itself makes certain.

    [Fact]
    public async Task Validate_RefusesADitherAfterGuidingWasStopped()
    {
        await using var host = CreateHost();
        var stop = StopGuiding();
        var dither = Dither();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [StartGuiding(), stop, dither]);

        Assert.Equal(["Dither needs guiding, but step 2 stopped it."], validation.ProblemsOf(dither.Id));
        Assert.Empty(validation.ProblemsOf(stop.Id));
    }

    [Fact]
    public async Task Validate_AcceptsADitherAfterGuidingWasStartedAgain()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [StartGuiding(), StopGuiding(), StartGuiding(), Dither()]);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task Validate_AcceptsADitherWithoutAStartGuidingStep_BecauseTheGuiderMayAlreadyBeGuiding()
    {
        await using var host = CreateHost();

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Exposure(), Dither()]);

        Assert.True(validation.IsValid);
    }

    [Fact]
    public async Task Validate_RefusesStartingGuidingTwice_AndStoppingItTwice()
    {
        await using var host = CreateHost();
        var secondStart = StartGuiding();
        var secondStop = StopGuiding();

        var validation = SequenceDraftBuilder.Validate(
            host.DeviceRegistry, [StartGuiding(), secondStart, StopGuiding(), secondStop]);

        Assert.Equal(["Guiding was already started by step 1."], validation.ProblemsOf(secondStart.Id));
        Assert.Equal(["Guiding was already stopped by step 3."], validation.ProblemsOf(secondStop.Id));
    }

    private sealed class PlainGuider(DeviceId id) : Astra.Core.Guiding.IGuider
    {
        public DeviceId Id { get; } = id;
        public string Name => "Plain guider";
        public DeviceType Type => DeviceType.Guider;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
        public Astra.Core.Guiding.GuidingState GuidingState => Astra.Core.Guiding.GuidingState.Idle;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopGuidingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

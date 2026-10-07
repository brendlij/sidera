using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>
/// Autofocus of one rig inside Parallel Imaging, run by the real runner on the simulator: the other rigs carry on, a
/// pending dither waits for it, a pause takes effect after it, and a cancellation leaves nothing behind.
/// </summary>
public class MultiRigAutofocusRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");

    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    // Quick moves, so that an autofocus takes about as long as its exposures: 15 samples of 0.1 s.
    private static DemoOptions Options => new()
    {
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(150),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(50),
    };

    private static RigExposureStepDraft Exposure(double seconds) => new(Guid.NewGuid(), seconds);
    private static RigAutofocusStepDraft Autofocus(double seconds = 0.1, int step = 400, int samples = 7) => new(Guid.NewGuid(), seconds, step, samples);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigDitherPolicyDraft Policy(RigId trigger, int every) => new(true, trigger, every, 0.6, 0.5, 0.1, 5);

    private static MultiRigStepDraft Block(MultiRigDitherPolicyDraft? policy, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, policy);

    private sealed class Fixture : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }

        public async Task ConnectEverything()
        {
            foreach (var device in Host.DeviceRegistry.GetAll())
            {
                await device.ConnectAsync();
            }
        }

        public BuiltSequence Build(MultiRigStepDraft block, bool withGuiding = true)
        {
            var steps = new List<SequenceStepDraft>();
            if (withGuiding)
            {
                steps.Add(new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId));
            }

            steps.Add(block);
            if (withGuiding)
            {
                steps.Add(new StopGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId));
            }

            return SequenceDraftBuilder.Build(
                Host.DeviceRegistry, steps, new SequenceDraftContext(Host.RigRegistry, Shared, Host.FocusMetrics, Host.EventBus));
        }

        public SequenceRunner NewRunner() => new(Host.ResourceManager, Host.SafePointCoordinator);

        public T Device<T>(DeviceId id) where T : class, IDevice =>
            Host.DeviceRegistry.TryGet(id, out var device) ? (T)device! : throw new InvalidOperationException(id.Value);

        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public async Task AssertCleanAsync()
        {
            var everything = Host.DeviceRegistry.GetAll().Select(d => ResourceId.ForDevice(d.Id)).ToList();
            using var lease = await Host.ResourceManager.AcquireAsync(everything, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(Host.DeviceRegistry.GetAll().OfType<IFocuser>(), f => Assert.Equal(FocuserMotionState.Idle, f.MotionState));
            Assert.All(Host.DeviceRegistry.GetAll().OfType<ICamera>(), c => Assert.Equal(CameraExposureState.Idle, c.ExposureState));
        }
    }

    private static Fixture CreateFixture()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Options);
        DemoSetup.AddDemoRigs(host, Options);
        return new Fixture { Host = host };
    }

    // What a run did: when steps started and ended, when the mount dithered, and when each camera really exposed.
    private sealed class Probe
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly HashSet<SequenceExecutionPosition> _seen = [];
        private readonly Dictionary<DeviceId, TimeSpan> _exposureStart = new();

        public List<(TimeSpan At, string Track, string Step)> Started { get; } = [];
        public List<(TimeSpan At, string Track, string Step)> Completed { get; } = [];
        public List<(TimeSpan Start, TimeSpan? End)> Dithers { get; } = [];
        public List<(DeviceId Camera, TimeSpan Start, TimeSpan End)> CameraExposures { get; } = [];
        public TaskCompletionSource AutofocusRunning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(SequenceRunner runner, SideraRuntimeHost host)
        {
            runner.Changed += (_, _) => Observe(runner.ActivePositions);
            runner.StepCompleted += (_, e) => Done(e.Position);
            host.EventBus.Subscribe<CameraExposureStateChanged>((e, _) =>
            {
                lock (_gate)
                {
                    var now = _clock.Elapsed;
                    if (e.NewState == CameraExposureState.Exposing)
                    {
                        _exposureStart[e.DeviceId] = now;
                    }
                    else if (_exposureStart.Remove(e.DeviceId, out var start))
                    {
                        CameraExposures.Add((e.DeviceId, start, now));
                    }
                }

                return Task.CompletedTask;
            });
        }

        private static string TrackOf(SequenceExecutionPosition p)
        {
            var chain = new List<string>();
            for (var c = p; c is not null; c = c.Parent)
            {
                chain.Insert(0, c.StepName);
            }

            return chain.Count > 1 ? chain[1] : string.Empty;
        }

        private static bool Interesting(SequenceExecutionPosition p) =>
            p.StepName == "Autofocus" || p.StepName.StartsWith("Exposure", StringComparison.Ordinal);

        private void Observe(IReadOnlyCollection<SequenceExecutionPosition> active)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                foreach (var position in active.Where(p => Interesting(p) && _seen.Add(p)))
                {
                    Started.Add((now, TrackOf(position), position.StepName));
                    if (position.StepName == "Autofocus")
                    {
                        AutofocusRunning.TrySetResult();
                    }
                }

                foreach (var _ in active.Where(p => p.StepName == "Dither command" && _seen.Add(p)))
                {
                    Dithers.Add((now, null));
                }

                if (active.Any(p => p.StepName.StartsWith("Dither ", StringComparison.Ordinal) && p.StepName != "Dither command"
                        && !p.StepName.StartsWith("Dither every", StringComparison.Ordinal)))
                {
                    Pending.TrySetResult();
                }
            }
        }

        private void Done(SequenceExecutionPosition position)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                if (Interesting(position))
                {
                    Completed.Add((now, TrackOf(position), position.StepName));
                }
                else if (position.StepName == "Dither command")
                {
                    var open = Dithers.FindLastIndex(d => d.End is null);
                    if (open >= 0)
                    {
                        Dithers[open] = (Dithers[open].Start, now);
                    }
                }
            }
        }

        public List<TimeSpan> CompletionsOf(string track, string prefix) =>
            Completed.Where(c => c.Track == track && c.Step.StartsWith(prefix, StringComparison.Ordinal)).Select(c => c.At).ToList();

        /// <summary>A camera exposed at some point of the time the mount dithered (with a little tolerance).</summary>
        public bool AnyCameraExposedWhileDithering() =>
            Dithers.Any(d => CameraExposures.Any(c =>
                c.Start < d.End!.Value - TimeSpan.FromMilliseconds(5) && c.End > d.Start + TimeSpan.FromMilliseconds(5)));
    }

    private static async Task<Probe> RunAsync(Fixture fixture, BuiltSequence built)
    {
        var probe = new Probe();
        var runner = fixture.NewRunner();
        probe.Attach(runner, fixture.Host);
        await runner.RunAsync(built.Sequence).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        return probe;
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    // Independence

    [Fact]
    public async Task WhileMainFocuses_WideKeepsExposing_AndTheBlockCompletes()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Wide, Repeat(40, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        var autofocusDone = probe.CompletionsOf("Main Rig", "Autofocus").Single();
        var wide = probe.CompletionsOf("Wide Rig", "Exposure");
        Assert.Equal(40, wide.Count);
        Assert.True(wide.Count(at => at < autofocusDone) >= 6, "Wide was held back by Main's autofocus");
        var mainExposureStart = probe.Started.Single(s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal)).At;
        Assert.True(mainExposureStart >= autofocusDone, "Main's exposure did not wait for its autofocus");

        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);
        Assert.InRange(focuser.Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task ThreeRigs_TwoOfThemFocusAtTheSameTime_TheThirdKeepsExposing_EachFocusesItsOwnDevices()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Narrow, Autofocus(), Exposure(0.1)),
            Track(Wide, Repeat(40, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        var mainDone = probe.CompletionsOf("Main Rig", "Autofocus").Single();
        var narrowDone = probe.CompletionsOf("Narrow Rig", "Autofocus").Single();
        var mainStart = probe.Started.Single(s => s.Track == "Main Rig" && s.Step == "Autofocus").At;
        var narrowStart = probe.Started.Single(s => s.Track == "Narrow Rig" && s.Step == "Autofocus").At;
        Assert.True(narrowStart < mainDone && mainStart < narrowDone, "the two autofocus runs did not overlap");
        var firstDone = mainDone < narrowDone ? mainDone : narrowDone;
        Assert.True(probe.CompletionsOf("Wide Rig", "Exposure").Count(at => at < firstDone) >= 4, "Wide was held back by the autofocus runs");
        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.NarrowFocuserId).Position, DemoSetup.NarrowBestFocus - 50, DemoSetup.NarrowBestFocus + 50);
        Assert.Equal(DemoSetup.WideBestFocus - 800, fixture.Device<IFocuser>(DemoSetup.WideFocuserId).Position); // untouched
    }

    [Fact]
    public async Task FromTheOtherSideOfFocus_ItConvergesAgain()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        await fixture.Device<IFocuser>(DemoSetup.MainFocuserId).MoveToAsync(21800);
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Wide, Exposure(0.1))), withGuiding: false);

        await RunAsync(fixture, built);

        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
    }

    [Fact]
    public async Task AnAutofocusInARepeat_RunsEachTime_AndStaysAtFocus()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Repeat(2, Autofocus(), Exposure(0.1))),
            Track(Wide, Repeat(10, Exposure(0.1)))), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        Assert.Equal(2, probe.CompletionsOf("Main Rig", "Autofocus").Count);
        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
    }

    [Fact]
    public async Task TheFilterThatIsInPlace_IsLeftAlone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        await fixture.Device<Sidera.Core.FilterWheels.IFilterWheel>(DemoSetup.MainFilterWheelId).MoveToSlotAsync(4);
        var built = fixture.Build(Block(null, Track(Main, Autofocus(), Exposure(0.1)), Track(Wide, Exposure(0.1))), withGuiding: false);

        await RunAsync(fixture, built);

        Assert.Equal("Ha", fixture.Device<Sidera.Core.FilterWheels.IFilterWheel>(DemoSetup.MainFilterWheelId).CurrentSlot.Name);
    }

    // Dither

    [Fact]
    public async Task APendingDither_WaitsForTheAutofocus_ThenTheTrackReachesItsSafePoint_AndTheMountDithersAlone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 2),
            Track(Main, Autofocus(), Exposure(0.1)),               // about two seconds of focusing
            Track(Wide, Repeat(10, Exposure(0.1)))));              // asks after 0.2 s, 0.4 s, ...

        var probe = await RunAsync(fixture, built);

        var autofocusDone = probe.CompletionsOf("Main Rig", "Autofocus").Single();
        Assert.Equal(5, probe.Dithers.Count);
        Assert.All(probe.Dithers, d => Assert.NotNull(d.End));
        // The first dither was asked for long before the autofocus ended, and started only after it.
        Assert.True(probe.Dithers[0].Start >= autofocusDone, "the mount dithered during the autofocus");
        // Main held at its safe point for the dither: its exposure began after that dither was over.
        var mainExposure = probe.Started.Single(s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal)).At;
        Assert.True(mainExposure >= probe.Dithers[0].End!.Value, "Main exposed during the dither");
        // No camera exposed at any time of any dither: neither a frame nor a focus exposure.
        Assert.False(probe.AnyCameraExposedWhileDithering(), "a camera exposed while the mount dithered");
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task TheFocusExposures_DoNotCountAsFramesForTheDitherTrigger()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        // Main images three frames after focusing, and dithers every 3 frames of Main: one dither, not five.
        var built = fixture.Build(Block(
            Policy(Main, 3),
            Track(Main, Autofocus(), Repeat(3, Exposure(0.1))),
            Track(Wide, Repeat(30, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        Assert.Single(probe.Dithers);
    }

    [Fact]
    public async Task ADitherFromTheRigThatFocuses_NeverDeadlocks()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Main, 2),
            Track(Main, Repeat(3, Exposure(0.1), Exposure(0.1), Autofocus())),
            Track(Wide, Repeat(30, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        Assert.Equal(3, probe.Dithers.Count);
        Assert.False(probe.AnyCameraExposedWhileDithering());
        await fixture.AssertCleanAsync();
    }

    // Pause

    [Fact]
    public async Task PauseDuringAnAutofocus_LetsTheWholeCurveFinish_ThenPausesBeforeTheNextStep_AndResumes()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Wide, Repeat(120, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);
        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);

        var run = runner.RunAsync(built.Sequence);
        await probe.AutofocusRunning.Task.WaitAsync(Bound);
        // Somewhere in the middle of the curve: when the focuser has moved to its first sample. Waiting on that, and not on a fixed time, keeps the pause inside the autofocus whatever the load is.
        var startPosition = focuser.Position;
        await WaitFor(() => focuser.Position != startPosition, "the first sample of the curve");
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Single(probe.CompletionsOf("Main Rig", "Autofocus")); // the whole run finished first
        Assert.InRange(focuser.Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50); // not left at a sample
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal));

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        await fixture.AssertCleanAsync();
    }

    // Cancel and failure

    [Fact]
    public async Task CancelDuringAnAutofocusExposure_EndsTheRun_ReleasesEverything_AndAFreshRunWorks()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(seconds: 5), Exposure(0.1)),
            Track(Wide, Repeat(100, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var camera = fixture.Device<ICamera>(DemoSetup.MainCameraId);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await WaitFor(() => camera.ExposureState == CameraExposureState.Exposing, "the first focus exposure");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        await fixture.AssertCleanAsync();
        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);
        Assert.Equal(18200 - 1200, focuser.Position); // where it last arrived: the lowest sample

        await RunAsync(fixture, fixture.Build(Block(null, Track(Main, Autofocus(), Exposure(0.1)), Track(Wide, Exposure(0.1))), withGuiding: false));
        Assert.InRange(focuser.Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
    }

    [Fact]
    public async Task CancelDuringAnAutofocusWhileADitherIsPending_EndsCleanly()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 2),
            Track(Main, Autofocus(seconds: 0.3), Exposure(0.1)),
            Track(Wide, Repeat(30, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await probe.Pending.Task.WaitAsync(Bound);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task AnAutofocusThatFails_FailsItsTrack_NamesIt_AndReleasesEveryone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        fixture.Host.AddSimulatedFocusModel(Main, new SimulatedFocusModel(20000, 2.5, 0)); // no curve at all
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Wide, Repeat(30, Exposure(0.1)))));
        var runner = fixture.NewRunner();

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Main Rig", failure.TrackName);
        Assert.IsType<AutofocusFailedException>(failure.InnerException);
        Assert.Equal("Autofocus failed: no reliable focus minimum was found.", failure.InnerException!.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task AFocuserThatIsNotConnected_FailsTheTrackBeforeAnythingMoves()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        await fixture.Device<IFocuser>(DemoSetup.MainFocuserId).DisconnectAsync();
        var built = fixture.Build(Block(
            null,
            Track(Main, Autofocus(), Exposure(0.1)),
            Track(Wide, Repeat(10, Exposure(0.1)))));
        var runner = fixture.NewRunner();

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Focuser 'focuser.main' is not connected.", failure.InnerException!.Message);
        await fixture.AssertCleanAsync();
    }
}

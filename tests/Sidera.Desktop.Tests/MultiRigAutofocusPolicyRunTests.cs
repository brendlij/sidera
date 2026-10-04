using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
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
/// The autofocus policy of a Rig Track, run by the real runner on the simulator: the generated autofocus runs where the
/// policy says, as the one autofocus action; the other rigs carry on; a dither waits for it; a pause takes effect after
/// it; a cancellation or a failure stops the track before it goes on imaging out of focus.
/// </summary>
public class MultiRigAutofocusPolicyRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");

    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    private static DemoOptions Options(TimeSpan? filterMove = null) => new()
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
        FilterWheelMoveDuration = filterMove ?? TimeSpan.FromMilliseconds(100),
    };

    private static RigAutofocusPolicyDraft Policy(bool start = false, bool filter = false, double seconds = 0.1) =>
        new(true, start, filter, seconds, 400, 7);

    private static RigExposureStepDraft Exposure(double seconds = 0.1) => new(Guid.NewGuid(), seconds);
    private static RigChangeFilterStepDraft ChangeFilter(int slot) => new(Guid.NewGuid(), slot);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);

    private static RigTrackDraft Track(RigId rig, RigAutofocusPolicyDraft? policy, params SequenceStepDraft[] steps) =>
        new(Guid.NewGuid(), rig, steps, policy);

    private static MultiRigDitherPolicyDraft Dither(RigId trigger, int every) => new(true, trigger, every, 0.6, 0.5, 0.1, 5);

    private static MultiRigStepDraft Block(MultiRigDitherPolicyDraft? dither, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, dither);

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
            Assert.All(Host.DeviceRegistry.GetAll().OfType<IFilterWheel>(), w => Assert.Equal(FilterWheelMotionState.Idle, w.MotionState));
        }
    }

    private static Fixture CreateFixture(TimeSpan? filterMove = null)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Options(filterMove));
        DemoSetup.AddDemoRigs(host, Options(filterMove));
        return new Fixture { Host = host };
    }

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
        public List<(TimeSpan At, RigId Rig, AutofocusPhase Phase)> Focus { get; } = [];
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
            host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
            {
                lock (_gate)
                {
                    Focus.Add((_clock.Elapsed, e.RigId, e.Progress.Phase));
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
            p.StepName == "Autofocus" || p.StepName.StartsWith("Exposure", StringComparison.Ordinal)
            || p.StepName.StartsWith("Change filter", StringComparison.Ordinal);

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

        // What a track did, in the order it finished it: "Autofocus", "Change filter to slot 4", "Exposure 0.1s", ...
        public List<string> Names(string track) => Completed.Where(c => c.Track == track).Select(c => c.Step).ToList();

        public List<TimeSpan> CompletionsOf(string track, string prefix) =>
            Completed.Where(c => c.Track == track && c.Step.StartsWith(prefix, StringComparison.Ordinal)).Select(c => c.At).ToList();

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

    // What runs, and when

    [Fact]
    public async Task TheMainTrack_FocusesAtTheStart_AndAfterEachFilterChange_InThatOrder_WhileWideKeepsExposing()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(start: true, filter: true), ChangeFilter(4), Exposure(), ChangeFilter(5), Exposure()),
            Track(Wide, null, Repeat(60, Exposure()))));

        var probe = await RunAsync(fixture, built);

        Assert.Equal(
            [
                "Autofocus", "Change filter to slot 4", "Autofocus", "Exposure 0.1s",
                "Change filter to slot 5", "Autofocus", "Exposure 0.1s",
            ],
            probe.Names("Main Rig"));
        Assert.Equal(3, probe.Focus.Count(f => f.Phase == AutofocusPhase.Completed));
        Assert.All(probe.Focus, f => Assert.Equal(Main, f.Rig)); // only the rig of the policy focused

        // Wide went on exposing while Main focused for the first time and changed its filter.
        var firstFocus = probe.CompletionsOf("Main Rig", "Autofocus")[0];
        Assert.True(probe.CompletionsOf("Wide Rig", "Exposure").Count(at => at < firstFocus) >= 5, "Wide was held back");
        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
        Assert.Equal(5, fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).CurrentSlot.Index);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task APolicyThatIsOff_RunsExactlyWhatTheUserWrote()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var off = new RigAutofocusPolicyDraft(false, true, true, 0.1, 400, 7);
        var built = fixture.Build(Block(
            null,
            Track(Main, off, ChangeFilter(4), Exposure()),
            Track(Wide, null, Exposure())), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        Assert.Equal(["Change filter to slot 4", "Exposure 0.1s"], probe.Names("Main Rig"));
        Assert.Empty(probe.Focus);
    }

    [Fact]
    public async Task InARepeat_EveryChangeFilterIsFollowedByItsAutofocus_TheStartOnlyOnce()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(start: true, filter: true), Repeat(3, ChangeFilter(4), Exposure())),
            Track(Wide, null, Repeat(10, Exposure()))), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        var names = probe.Names("Main Rig");
        Assert.Equal(4, names.Count(n => n == "Autofocus")); // one at the start, one per iteration
        Assert.Equal("Autofocus", names[0]);
        Assert.Equal(
            ["Autofocus", "Change filter to slot 4", "Autofocus", "Exposure 0.1s", "Change filter to slot 4", "Autofocus", "Exposure 0.1s", "Change filter to slot 4", "Autofocus", "Exposure 0.1s"],
            names);
        Assert.Equal(4, probe.Focus.Count(f => f.Phase == AutofocusPhase.Completed));
    }

    [Fact]
    public async Task AnExplicitAutofocusAfterTheChange_IsTheOnlyOne_ThatRuns()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true), ChangeFilter(4), new RigAutofocusStepDraft(Guid.NewGuid(), 0.1, 400, 7), Exposure()),
            Track(Wide, null, Exposure())), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        Assert.Equal(["Change filter to slot 4", "Autofocus", "Exposure 0.1s"], probe.Names("Main Rig"));
        Assert.Single(probe.Focus, f => f.Phase == AutofocusPhase.Completed);
    }

    [Fact]
    public async Task TheTracksFocusIndependently_EachByItsOwnPolicy()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var narrow = new RigId("rig.narrow");
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(start: true), Exposure()),
            Track(Wide, null, Repeat(10, Exposure())),
            Track(narrow, Policy(start: true), Exposure())), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        Assert.Equal(["Autofocus", "Exposure 0.1s"], probe.Names("Main Rig"));
        Assert.Equal(["Autofocus", "Exposure 0.1s"], probe.Names("Narrow Rig"));
        Assert.DoesNotContain("Autofocus", probe.Names("Wide Rig"));
        Assert.InRange(fixture.Device<IFocuser>(DemoSetup.NarrowFocuserId).Position, DemoSetup.NarrowBestFocus - 50, DemoSetup.NarrowBestFocus + 50);
        Assert.Equal(DemoSetup.WideBestFocus - 800, fixture.Device<IFocuser>(DemoSetup.WideFocuserId).Position); // untouched
    }

    [Fact]
    public async Task TheFilterWheelIsFreeWhenTheAutofocusStarts_AndTheAutofocusDoesNotHoldIt()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true), ChangeFilter(4), Exposure()),
            Track(Wide, null, Exposure())), withGuiding: false);
        var runner = fixture.NewRunner();
        var wheelResource = ResourceId.ForDevice(DemoSetup.MainFilterWheelId);
        var heldDuringFocus = false;
        runner.Changed += (_, _) =>
        {
            if (runner.ActivePositions.Any(p => p.StepName == "Autofocus") && fixture.Host.ResourceManager.IsHeld(wheelResource))
            {
                heldDuringFocus = true;
            }
        };

        await runner.RunAsync(built.Sequence).WaitAsync(Bound);

        Assert.False(heldDuringFocus);
    }

    // Dither

    [Fact]
    public async Task APendingDither_WaitsForAGeneratedAutofocus_ThenTheTrackReachesItsSafePoint_AndTheMountDithersAlone()
    {
        await using var fixture = CreateFixture(filterMove: TimeSpan.FromMilliseconds(300));
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Dither(Wide, 15),                                                   // asks after 1.5 s, while Main focuses
            Track(Main, Policy(filter: true, seconds: 0.15), ChangeFilter(4), Exposure()),
            Track(Wide, null, Repeat(30, Exposure()))));

        var probe = await RunAsync(fixture, built);

        var autofocusDone = probe.CompletionsOf("Main Rig", "Autofocus").Single();
        Assert.Equal(2, probe.Dithers.Count);
        Assert.All(probe.Dithers, d => Assert.NotNull(d.End));
        Assert.True(probe.Dithers[0].Start >= autofocusDone, "the mount dithered during the generated autofocus");
        var mainExposure = probe.Started.Single(s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal)).At;
        Assert.True(mainExposure >= probe.Dithers[0].End!.Value, "Main exposed during the dither");
        Assert.False(probe.AnyCameraExposedWhileDithering(), "a camera exposed while the mount dithered");
        await fixture.AssertCleanAsync();
    }

    // Pause, cancel, failure

    [Fact]
    public async Task PauseDuringAGeneratedAutofocus_LetsItFinish_ThenPausesBeforeTheNextStep_AndResumes()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(start: true), Exposure()),
            Track(Wide, null, Repeat(60, Exposure()))), withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);
        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);

        var run = runner.RunAsync(built.Sequence);
        await probe.AutofocusRunning.Task.WaitAsync(Bound);
        await Task.Delay(300);
        runner.RequestPause();
        Assert.Equal(SequenceState.Pausing, runner.State);
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Single(probe.CompletionsOf("Main Rig", "Autofocus")); // the whole run finished first
        Assert.InRange(focuser.Position, DemoSetup.MainBestFocus - 50, DemoSetup.MainBestFocus + 50);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal));

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task CancelDuringAGeneratedAutofocus_EndsTheRun_ReleasesEverything_AndNoExposureStartsAfterwards()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true, seconds: 5), ChangeFilter(4), Exposure()),
            Track(Wide, null, Repeat(100, Exposure()))), withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);
        var camera = fixture.Device<ICamera>(DemoSetup.MainCameraId);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await probe.AutofocusRunning.Task.WaitAsync(Bound);
        await WaitFor(() => camera.ExposureState == CameraExposureState.Exposing, "the focus exposure");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal));
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task AGeneratedAutofocusThatFails_FailsTheTrack_AndNoExposureStartsAfterIt()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        fixture.Host.AddSimulatedFocusModel(Main, new SimulatedFocusModel(20000, 2.5, 0)); // no curve
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true), ChangeFilter(4), Exposure()),
            Track(Wide, null, Repeat(100, Exposure()))), withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Main Rig", failure.TrackName);
        Assert.Equal("Autofocus failed: no reliable focus minimum was found.", failure.InnerException!.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step.StartsWith("Exposure", StringComparison.Ordinal));
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task AChangeFilterThatFails_IsNotFollowedByAnAutofocus()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        await fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).DisconnectAsync();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true), ChangeFilter(4), Exposure()),
            Track(Wide, null, Repeat(30, Exposure()))), withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Main Rig", failure.TrackName);
        Assert.Contains("is not connected", failure.InnerException!.Message);
        Assert.Empty(probe.Focus);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step == "Autofocus");
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task AChangeFilterThatIsCancelled_IsNotFollowedByAnAutofocus()
    {
        await using var fixture = CreateFixture(filterMove: TimeSpan.FromSeconds(30));
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Policy(filter: true), ChangeFilter(4), Exposure()),
            Track(Wide, null, Repeat(100, Exposure()))), withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner, fixture.Host);
        var wheel = fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await WaitFor(() => wheel.MotionState == FilterWheelMotionState.Moving, "the wheel turning");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Empty(probe.Focus);
        Assert.DoesNotContain(probe.Started, s => s.Track == "Main Rig" && s.Step == "Autofocus");
        await fixture.AssertCleanAsync();
    }
}

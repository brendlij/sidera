using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>
/// Rig-local focuser moves and filter changes in Multi-Rig Imaging, run by the real runner on the simulator: the
/// tracks stay independent, a shared focuser is kept apart by the resource manager, and a pending dither waits for the
/// atomic hardware steps without a deadlock and without holding anyone back before it is asked for.
/// </summary>
public class MultiRigHardwareRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(40);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");

    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    // Moves and turns that take long enough to be seen: 1000 steps per second, 600 ms for a filter change.
    private static DemoOptions Options => new()
    {
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(150),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
        FocuserStepsPerSecond = 1000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(50),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(600),
    };

    private static RigExposureStepDraft Exposure(double seconds) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigMoveFocuserStepDraft MoveFocuser(int position) => new(Guid.NewGuid(), position);
    private static RigChangeFilterStepDraft ChangeFilter(int slot) => new(Guid.NewGuid(), slot);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigDitherPolicyDraft Policy(RigId trigger, int every) =>
        new(true, trigger, every, 0.6, 0.5, 0.1, 5);

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

            return SequenceDraftBuilder.Build(Host.DeviceRegistry, steps, new SequenceDraftContext(Host.RigRegistry, Shared));
        }

        public SequenceRunner NewRunner() => new(Host.ResourceManager, Host.SafePointCoordinator);

        public T Device<T>(DeviceId id) where T : class, IDevice =>
            Host.DeviceRegistry.TryGet(id, out var device) ? (T)device! : throw new InvalidOperationException(id.Value);

        public ValueTask DisposeAsync() => Host.DisposeAsync();

        // No hardware held, and none of the focusers or wheels moving.
        public async Task AssertCleanAsync()
        {
            var everything = Host.DeviceRegistry.GetAll().Select(d => ResourceId.ForDevice(d.Id)).ToList();
            using var lease = await Host.ResourceManager.AcquireAsync(everything, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(Host.DeviceRegistry.GetAll().OfType<IFocuser>(), f => Assert.Equal(FocuserMotionState.Idle, f.MotionState));
            Assert.All(Host.DeviceRegistry.GetAll().OfType<IFilterWheel>(), w => Assert.Equal(FilterWheelMotionState.Idle, w.MotionState));
        }
    }

    private static Fixture CreateFixture()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Options);
        DemoSetup.AddDemoRigs(host, Options);
        return new Fixture { Host = host };
    }

    // What a run did, seen through every change of the runner.
    private sealed class Probe
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly HashSet<SequenceExecutionPosition> _seen = [];

        public List<(TimeSpan At, string Track, string Step)> Started { get; } = [];
        public List<(TimeSpan At, string Track, string Step)> Completed { get; } = [];
        public List<(TimeSpan Start, TimeSpan? End)> Dithers { get; } = [];
        public bool ExposedWhileDithering { get; private set; }
        public bool HardwareWhileDithering { get; private set; }
        public bool HardwareAndExposureOverlapped { get; private set; }
        public TaskCompletionSource FocuserMoving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(SequenceRunner runner)
        {
            runner.Changed += (_, _) => Observe(runner.ActivePositions);
            runner.StepCompleted += (_, e) => Done(e.Position);
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

        private static bool IsHardware(SequenceExecutionPosition p) =>
            p.StepName.StartsWith("Move focuser", StringComparison.Ordinal) || p.StepName.StartsWith("Change filter", StringComparison.Ordinal);

        private static bool IsExposure(SequenceExecutionPosition p) => p.StepName.StartsWith("Exposure", StringComparison.Ordinal);

        private void Observe(IReadOnlyCollection<SequenceExecutionPosition> active)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                foreach (var position in active.Where(p => (IsHardware(p) || IsExposure(p)) && _seen.Add(p)))
                {
                    Started.Add((now, TrackOf(position), position.StepName));
                    if (position.StepName.StartsWith("Move focuser", StringComparison.Ordinal))
                    {
                        FocuserMoving.TrySetResult();
                    }
                }

                var dithers = active.Where(p => p.StepName == "Dither command").ToList();
                foreach (var dither in dithers.Where(_seen.Add))
                {
                    Dithers.Add((now, null));
                }

                if (active.Any(p => p.StepName.StartsWith("Dither ", StringComparison.Ordinal) && p.StepName != "Dither command"
                        && !p.StepName.StartsWith("Dither every", StringComparison.Ordinal)))
                {
                    Pending.TrySetResult();
                }

                ExposedWhileDithering |= dithers.Count > 0 && active.Any(IsExposure);
                HardwareWhileDithering |= dithers.Count > 0 && active.Any(IsHardware);
                HardwareAndExposureOverlapped |= active.Any(IsHardware) && active.Any(IsExposure);
            }
        }

        private void Done(SequenceExecutionPosition position)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                if (IsHardware(position) || IsExposure(position))
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
    }

    private static async Task<Probe> RunAsync(Fixture fixture, BuiltSequence built)
    {
        var probe = new Probe();
        var runner = fixture.NewRunner();
        probe.Attach(runner);
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
    public async Task AFilterChangeAndAFocuserMoveOfOneRig_DoNotHoldBackAnotherRig_AndAreDoneOnItsHardware()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, ChangeFilter(4), MoveFocuser(19200), Exposure(0.3)),
            Track(Wide, Repeat(20, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        var wide = probe.CompletionsOf("Wide Rig", "Exposure");
        var focuserDone = probe.CompletionsOf("Main Rig", "Move focuser").Single();
        var filterDone = probe.CompletionsOf("Main Rig", "Change filter").Single();

        // Wide exposed through the whole filter change and focuser move of Main (0.6 s + 1 s): no waiting for them.
        Assert.True(wide.Count(at => at < filterDone) >= 4, "Wide was held back by Main's filter change");
        Assert.True(wide.Count(at => at > filterDone && at < focuserDone) >= 4, "Wide was held back by Main's focuser move");
        Assert.True(probe.HardwareAndExposureOverlapped, "hardware steps and exposures never overlapped");
        Assert.True(focuserDone > filterDone); // within the track: in order

        Assert.Equal(new FilterSlot(4, "Ha"), fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).CurrentSlot);
        Assert.Equal(19200, fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task EachRigUsesItsOwnHardware_NotTheHardwareOfAnotherRig()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, MoveFocuser(20000), ChangeFilter(2), Exposure(0.1)),
            Track(Narrow, MoveFocuser(25000), ChangeFilter(1), Exposure(0.1)),
            Track(Wide, MoveFocuser(6000), Exposure(0.1))));

        await RunAsync(fixture, built);

        Assert.Equal(20000, fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position);
        Assert.Equal(25000, fixture.Device<IFocuser>(DemoSetup.NarrowFocuserId).Position);
        Assert.Equal(6000, fixture.Device<IFocuser>(DemoSetup.WideFocuserId).Position);
        Assert.Equal(2, fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).CurrentSlot.Index);
        Assert.Equal(1, fixture.Device<IFilterWheel>(DemoSetup.NarrowFilterWheelId).CurrentSlot.Index);
    }

    [Fact]
    public async Task TheRigsOfATrackDecideTheDevice_TheSameStepsOnAnotherRigMoveTheOtherFocuser()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var steps = new SequenceStepDraft[] { MoveFocuser(7000), Exposure(0.1) };

        await RunAsync(fixture, fixture.Build(Block(null, Track(Wide, steps), Track(Narrow, Exposure(0.1))), withGuiding: false));

        Assert.Equal(7000, fixture.Device<IFocuser>(DemoSetup.WideFocuserId).Position);
        Assert.Equal(18200, fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position); // untouched
    }

    [Fact]
    public async Task TwoRigsThatShareAFocuser_AreKeptApartByTheResourceManager()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        // A second rig that names the focuser of the main rig: allowed, the resource manager keeps use apart.
        fixture.Host.AddRig(new Rig(
            new RigId("rig.alias"), "Alias Rig", DemoSetup.WideCameraId, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176),
            DemoSetup.MainFocuserId));
        var built = fixture.Build(Block(
            null,
            Track(Main, MoveFocuser(19200), Exposure(0.1)),
            Track(new RigId("rig.alias"), MoveFocuser(17200), Exposure(0.1))), withGuiding: false);

        var probe = await RunAsync(fixture, built);

        var moves = probe.Completed.Where(c => c.Step.StartsWith("Move focuser", StringComparison.Ordinal)).OrderBy(c => c.At).ToList();
        Assert.Equal(2, moves.Count);
        Assert.True(moves[1].At - moves[0].At >= TimeSpan.FromMilliseconds(700), "the two moves overlapped on one focuser");
        Assert.Contains(fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position, new[] { 19200, 17200 });
        await fixture.AssertCleanAsync();
    }

    // Dither

    [Fact]
    public async Task APendingDither_WaitsForRunningFilterChanges_AtTheSafePointsAfterThem_ThenDithersWithNoHardwareOrExposureRunning()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 2),
            Track(Main, ChangeFilter(1), MoveFocuser(19200), Exposure(0.3)),   // 0.6 s + 1 s of hardware, then a frame
            Track(Narrow, ChangeFilter(2), Exposure(0.5)),                      // 0.6 s of hardware, then a frame
            Track(Wide, Repeat(8, Exposure(0.1)))));                            // asks after 0.2 s, 0.4 s, 0.6 s, 0.8 s

        var probe = await RunAsync(fixture, built);

        Assert.Equal(4, probe.Dithers.Count);
        Assert.All(probe.Dithers, d => Assert.NotNull(d.End));
        Assert.False(probe.ExposedWhileDithering, "a camera was exposing while the mount dithered");
        Assert.False(probe.HardwareWhileDithering, "a focuser or filter wheel was moving while the mount dithered");

        // The first dither was asked for at 0.2 s. Main was in a filter change (0.6 s) and Narrow in one too: the dither
        // started only after both, at the safe point each of them reached after its atomic step.
        var mainFilterDone = probe.CompletionsOf("Main Rig", "Change filter").Single();
        var narrowFilterDone = probe.CompletionsOf("Narrow Rig", "Change filter").Single();
        Assert.True(probe.Dithers[0].Start >= mainFilterDone, "the dither did not wait for Main's filter change");
        Assert.True(probe.Dithers[0].Start >= narrowFilterDone, "the dither did not wait for Narrow's filter change");

        // Main held at its safe point for that dither: its focuser move began only after the dither was over.
        var focuserStart = probe.Started.Single(s => s.Track == "Main Rig" && s.Step.StartsWith("Move focuser", StringComparison.Ordinal)).At;
        Assert.True(focuserStart >= probe.Dithers[0].End!.Value, "Main began its focuser move during the dither");

        // The atomic steps were not interrupted: the move and the change completed.
        Assert.Equal(19200, fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position);
        Assert.Equal(1, fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).CurrentSlot.Index);
        Assert.Equal(2, fixture.Device<IFilterWheel>(DemoSetup.NarrowFilterWheelId).CurrentSlot.Index);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task WithAPolicyThatNeverFires_TheSafePointsAfterHardwareSteps_HoldNobodyBack()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 1000),
            Track(Main, ChangeFilter(4), MoveFocuser(19200), Exposure(0.3)),
            Track(Wide, Repeat(20, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        Assert.Empty(probe.Dithers);
        var wide = probe.CompletionsOf("Wide Rig", "Exposure");
        var focuserDone = probe.CompletionsOf("Main Rig", "Move focuser").Single();
        Assert.True(wide.Count(at => at < focuserDone) >= 8, "Wide was held back although no dither was pending");
    }

    [Fact]
    public async Task ADitherAskedForByTheRigWhoseHardwareIsBusy_NeverDeadlocks()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        // The trigger rig itself moves its focuser between its exposures: it asks, waits at the safe point, goes on.
        var built = fixture.Build(Block(
            Policy(Main, 2),
            Track(Main, Repeat(4, Exposure(0.1), MoveFocuser(19000)), Exposure(0.1)),
            Track(Wide, Repeat(6, Exposure(0.2)))));

        var probe = await RunAsync(fixture, built);

        Assert.False(probe.ExposedWhileDithering);
        Assert.False(probe.HardwareWhileDithering);
        Assert.NotEmpty(probe.Dithers);
        await fixture.AssertCleanAsync();
    }

    // Pause, cancel, failure

    [Fact]
    public async Task PauseDuringARigFocuserMove_LetsTheMoveFinish_ThenPausesBeforeTheNextStep()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, MoveFocuser(19200), ChangeFilter(4), Exposure(0.2)),
            Track(Wide, Repeat(15, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);
        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);
        var wheel = fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId);

        var run = runner.RunAsync(built.Sequence);
        await WaitFor(() => focuser.MotionState == FocuserMotionState.Moving, "focuser moving");
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Equal(19200, focuser.Position); // the move that was running completed
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(0, wheel.CurrentSlot.Index); // the filter change after it did not start
        await Task.Delay(300);
        Assert.Equal(FilterWheelMotionState.Idle, wheel.MotionState);
        Assert.Equal(0, wheel.CurrentSlot.Index);

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(4, wheel.CurrentSlot.Index);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task PauseDuringARigFilterChange_LetsTheChangeFinish_ThenPausesBeforeTheNextStep()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, ChangeFilter(3), MoveFocuser(19200), Exposure(0.2)),
            Track(Wide, Repeat(15, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var focuser = fixture.Device<IFocuser>(DemoSetup.MainFocuserId);
        var wheel = fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId);

        var run = runner.RunAsync(built.Sequence);
        await WaitFor(() => wheel.MotionState == FilterWheelMotionState.Moving, "wheel turning");
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Equal(3, wheel.CurrentSlot.Index);
        Assert.Equal(18200, focuser.Position);
        await Task.Delay(300);
        Assert.Equal(18200, focuser.Position);

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(19200, focuser.Position);
    }

    [Fact]
    public async Task CancelDuringAFilterChange_EndsTheRun_ReleasesEveryResource_AndLeavesNoMovingState()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, ChangeFilter(5), Exposure(0.2)),
            Track(Wide, Repeat(30, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var wheel = fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await WaitFor(() => wheel.MotionState == FilterWheelMotionState.Moving, "wheel turning");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, wheel.CurrentSlot.Index); // never reported as reached
        await fixture.AssertCleanAsync();

        // A fresh run of the same hardware works afterwards.
        await RunAsync(fixture, fixture.Build(Block(null, Track(Main, ChangeFilter(5), Exposure(0.1)), Track(Wide, Exposure(0.1)))));
        Assert.Equal(5, wheel.CurrentSlot.Index);
    }

    [Fact]
    public async Task CancelDuringAFocuserMoveWhileADitherIsPending_EndsCleanly()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 2),
            Track(Main, MoveFocuser(20200), Exposure(0.2)),
            Track(Wide, Repeat(10, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await probe.Pending.Task.WaitAsync(Bound); // Wide asked; Main is still moving its focuser
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(18200, fixture.Device<IFocuser>(DemoSetup.MainFocuserId).Position);
        await fixture.AssertCleanAsync();
    }

    [Fact]
    public async Task ARigWhoseFilterWheelIsNotConnected_FailsItsTrack_NamesIt_AndReleasesEveryone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        await fixture.Device<IFilterWheel>(DemoSetup.MainFilterWheelId).DisconnectAsync();
        var built = fixture.Build(Block(
            null,
            Track(Main, ChangeFilter(4), Exposure(0.2)),
            Track(Wide, Repeat(30, Exposure(0.1)))));
        var runner = fixture.NewRunner();

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Main Rig", failure.TrackName);
        Assert.Contains("is not connected", failure.InnerException!.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        await fixture.AssertCleanAsync();
    }
}

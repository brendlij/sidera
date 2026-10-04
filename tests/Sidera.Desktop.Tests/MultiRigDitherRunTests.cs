using System.Diagnostics;
using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>
/// Coordinated dithering of a Multi-Rig block on the simulator, run by the real runner: when the shared mount moves
/// and when it must not, what waits for what, and that nothing is left behind.
/// </summary>
public class MultiRigDitherRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly RigId Narrow = new("rig.narrow");

    private static readonly SharedEquipmentDraft Shared = new(DemoSetup.MountId, DemoSetup.GuiderId);

    private static DemoOptions Options(TimeSpan? ditherDuration = null, TimeSpan? settleStable = null) => new()
    {
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = ditherDuration ?? TimeSpan.FromMilliseconds(20),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = settleStable ?? TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
    };

    private static RigExposureStepDraft Exposure(double seconds) => new(Guid.NewGuid(), seconds);
    private static DelayStepDraft Delay(double seconds) => new(Guid.NewGuid(), seconds);
    private static RepeatStepDraft Repeat(int count, params LeafStepDraft[] children) => new(Guid.NewGuid(), count, children);
    private static RigTrackDraft Track(RigId rig, params SequenceStepDraft[] steps) => new(Guid.NewGuid(), rig, steps);

    private static MultiRigDitherPolicyDraft Policy(
        RigId trigger, int every, double amplitude = 0.6, double threshold = 0.5, double stable = 0.1, double timeout = 5) =>
        new(true, trigger, every, amplitude, threshold, stable, timeout);

    private static MultiRigStepDraft Block(MultiRigDitherPolicyDraft? policy, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, policy);

    private sealed class Fixture : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }

        public SimulatedGuider Guider => (SimulatedGuider)Host.DeviceRegistry.GetAll().Single(d => d.Id == DemoSetup.GuiderId);

        public async Task ConnectEverything(params DeviceId[] except)
        {
            foreach (var device in Host.DeviceRegistry.GetAll().Where(d => !except.Contains(d.Id)))
            {
                await device.ConnectAsync();
            }
        }

        // [Start Guiding, block, Stop Guiding], or just the block when the guider is not to be started.
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

        public ValueTask DisposeAsync() => Host.DisposeAsync();

        public static CoordinationGroupId GroupOf(BuiltSequence built) =>
            built.Sequence.Steps.OfType<ParallelStep>().Single().CoordinationGroup!.Value;

        // Nobody holds a camera, the mount or the guider, and the group has no participant and no request.
        public async Task AssertCleanAsync(CoordinationGroupId? group)
        {
            if (group is { } id)
            {
                var status = Host.SafePointCoordinator.GetStatus(id);
                Assert.Empty(status.Participants);
                Assert.False(status.RequestPending);
            }

            var everything = Host.DeviceRegistry.GetAll().Select(d => ResourceId.ForDevice(d.Id)).ToList();
            using var lease = await Host.ResourceManager.AcquireAsync(everything, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    private static Fixture CreateFixture(DemoOptions? options = null)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, options ?? Options());
        DemoSetup.AddDemoRigs(host);
        return new Fixture { Host = host };
    }

    // What a run did, seen through every change of the runner: exact, not sampled.
    private sealed class Probe
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<SequenceExecutionPosition, int> _dithers = new();
        private readonly HashSet<SequenceExecutionPosition> _seen = [];

        public List<(TimeSpan At, string Track)> ExposureStarts { get; } = [];
        public List<(TimeSpan At, string Track)> ExposureEnds { get; } = [];
        public List<(TimeSpan Start, TimeSpan? End)> Dithers { get; } = [];
        public List<TimeSpan> TrackEnds { get; } = [];
        public bool ExposedWhileDithering { get; private set; }
        public int MostDithersAtOnce { get; private set; }
        public TaskCompletionSource DitherStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Settling { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Attach(SequenceRunner runner)
        {
            runner.Changed += (_, _) => Observe(runner.ActivePositions);
            runner.StepCompleted += (_, e) => Completed(e.Position);
        }

        private static List<string> Chain(SequenceExecutionPosition position)
        {
            var chain = new List<string>();
            for (var p = position; p is not null; p = p.Parent)
            {
                chain.Insert(0, p.StepName);
            }

            return chain;
        }

        private static string TrackOf(SequenceExecutionPosition position)
        {
            var chain = Chain(position);
            return chain.Count > 1 ? chain[1] : string.Empty;
        }

        private static bool IsExposure(SequenceExecutionPosition p) => p.StepName.StartsWith("Exposure", StringComparison.Ordinal);

        private void Observe(IReadOnlyCollection<SequenceExecutionPosition> active)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                var dithering = active.Where(p => p.StepName == "Dither command").ToList();
                var exposing = active.Any(IsExposure);

                foreach (var position in active.Where(IsExposure).Where(_seen.Add))
                {
                    ExposureStarts.Add((now, TrackOf(position)));
                }

                foreach (var position in dithering.Where(p => !_dithers.ContainsKey(p)))
                {
                    _dithers[position] = Dithers.Count;
                    Dithers.Add((now, null));
                    DitherStarted.TrySetResult();
                }

                if (active.Any(p => p.StepName.StartsWith("Settle", StringComparison.Ordinal)))
                {
                    Settling.TrySetResult();
                }

                if (active.Any(p => p.StepName.StartsWith("Dither ", StringComparison.Ordinal) && p.StepName != "Dither command"
                        && !p.StepName.StartsWith("Dither every", StringComparison.Ordinal)))
                {
                    Pending.TrySetResult();
                }

                ExposedWhileDithering |= exposing && dithering.Count > 0;
                MostDithersAtOnce = Math.Max(MostDithersAtOnce, dithering.Count);
            }
        }

        private void Completed(SequenceExecutionPosition position)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed;
                if (IsExposure(position))
                {
                    ExposureEnds.Add((now, TrackOf(position)));
                }
                else if (position.StepName == "Dither command" && _dithers.TryGetValue(position, out var index))
                {
                    Dithers[index] = (Dithers[index].Start, now);
                }
                else if (Chain(position).Count == 2)
                {
                    TrackEnds.Add(now);
                }
            }
        }

        public List<TimeSpan> EndsOf(string track) => ExposureEnds.Where(e => e.Track == track).Select(e => e.At).ToList();

        public List<TimeSpan> StartsOf(string track) => ExposureStarts.Where(e => e.Track == track).Select(e => e.At).ToList();
    }

    private static async Task<Probe> RunAsync(Fixture fixture, BuiltSequence built, CancellationToken cancellationToken = default)
    {
        var probe = new Probe();
        var runner = fixture.NewRunner();
        probe.Attach(runner);
        await runner.RunAsync(built.Sequence, cancellationToken).WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        return probe;
    }

    // The core behaviour

    [Fact]
    public async Task TheTriggerRigAsksForADitherAfterEveryThirdFrame_AndTheMountOnlyMovesWhenNoCameraIsExposing()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.8))),
            Track(Wide, Repeat(10, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        // Frames 3, 6 and 9 of Wide each asked for one dither; frame 10 did not.
        Assert.Equal(3, probe.Dithers.Count);
        Assert.All(probe.Dithers, d => Assert.NotNull(d.End));
        Assert.False(probe.ExposedWhileDithering, "a camera was exposing while the mount was dithering");
        Assert.Equal(1, probe.MostDithersAtOnce);

        var wide = probe.EndsOf("Wide Rig");
        var main = probe.EndsOf("Main Rig");
        Assert.Equal(10, wide.Count);
        Assert.Equal(2, main.Count);

        // Wide did its first three frames while Main was still in its first exposure: no lockstep before a request.
        Assert.True(wide[2] < main[0], "Wide did not run on while Main exposed");

        // The dither started only once Main's running exposure was over, and after exactly three frames of Wide.
        var first = probe.Dithers[0];
        Assert.True(first.Start >= main[0], "the dither did not wait for Main's exposure");
        Assert.Equal(3, wide.Count(at => at < first.Start));

        // Main's safe point after its first exposure let the dither in: it happened between Main's two frames, not
        // only once Main was done altogether.
        Assert.True(first.Start - main[0] < TimeSpan.FromMilliseconds(400), "the dither came late: Main had no safe point");
        Assert.True(probe.StartsOf("Main Rig")[1] >= first.End!.Value, "Main began its next frame during the dither");

        // Wide was held while the dither was pending and being made: its fourth frame started after it.
        var wideStarts = probe.StartsOf("Wide Rig");
        Assert.True(wideStarts[3] >= first.End!.Value, "Wide started frame 4 before the dither was over");

        // Counting starts again after a dither: the next one after frame 6, and the one after that after frame 9.
        Assert.Equal(6, wide.Count(at => at < probe.Dithers[1].Start));
        Assert.Equal(9, wide.Count(at => at < probe.Dithers[2].Start));
        Assert.Equal(2, probe.TrackEnds.Count);
    }

    [Fact]
    public async Task ThreeRigs_TheFastTrackWaitsFirst_TheMountMovesOnlyAfterTheLastOneIsAtASafePoint()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.7))),
            Track(Wide, Repeat(6, Exposure(0.1))),
            Track(Narrow, Repeat(3, Exposure(0.4)))));

        var probe = await RunAsync(fixture, built);

        Assert.Equal(2, probe.Dithers.Count);
        Assert.False(probe.ExposedWhileDithering);
        var wide = probe.EndsOf("Wide Rig");
        var main = probe.EndsOf("Main Rig");
        var narrow = probe.EndsOf("Narrow Rig");
        var request = wide[2]; // Wide's third frame asks
        var first = probe.Dithers[0];

        // Narrow (0.4 s) and Main (0.7 s) were exposing when the request came; both finished that exposure first.
        var mainRunning = main.First(at => at >= request);
        var narrowRunning = narrow.First(at => at >= request);
        Assert.True(narrowRunning <= mainRunning);
        Assert.True(first.Start >= mainRunning && first.Start >= narrowRunning, "the dither started before every track was safe");

        // Nobody exposed anew in between: no exposure of any track started between the request and the end.
        var starts = probe.ExposureStarts.Where(s => s.At > request && s.At < first.End!.Value).ToList();
        Assert.Empty(starts);
    }

    [Fact]
    public async Task WithoutAPolicy_TheTracksStayIndependentAndNothingDithers()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            null,
            Track(Main, Repeat(2, Exposure(0.6))),
            Track(Wide, Repeat(10, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        Assert.Empty(probe.Dithers);
        Assert.Null(built.Sequence.Steps.OfType<ParallelStep>().Single().CoordinationGroup);
        Assert.True(probe.EndsOf("Wide Rig").Count(at => at < probe.EndsOf("Main Rig")[0]) >= 3);
    }

    [Fact]
    public async Task SafePoints_HoldNobodyBack_WhileNoDitherIsPending()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();

        // The trigger would need 1000 frames: the policy is on, a dither is never requested.
        var built = fixture.Build(Block(
            Policy(Wide, 1000),
            Track(Main, Repeat(2, Exposure(0.6))),
            Track(Wide, Repeat(12, Exposure(0.1)))));

        var probe = await RunAsync(fixture, built);

        Assert.Empty(probe.Dithers);
        var wide = probe.EndsOf("Wide Rig");
        var main = probe.EndsOf("Main Rig");
        Assert.Equal(12, wide.Count);
        Assert.True(wide.Count(at => at < main[0]) >= 4, "Wide was held back by the safe points");
    }

    [Fact]
    public async Task OnlyCompletedExposuresOfTheTriggerRigCount_NotDelaysNorOtherRigs()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(20, Exposure(0.05))), // plenty of frames of another rig
            Track(Wide, Repeat(6, Exposure(0.05), Delay(0.05)))));

        var probe = await RunAsync(fixture, built);

        // Six exposures of Wide, with delays between: two dithers, not four.
        Assert.Equal(2, probe.Dithers.Count);
        Assert.False(probe.ExposedWhileDithering);
    }

    [Fact]
    public async Task ADitherEveryFrame_IsOneDitherPerFrame_OneAfterTheOther()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 1),
            Track(Main, Repeat(3, Exposure(0.4))),
            Track(Wide, Repeat(4, Exposure(0.05)))));

        var probe = await RunAsync(fixture, built);

        Assert.Equal(4, probe.Dithers.Count);
        Assert.Equal(1, probe.MostDithersAtOnce);
        for (var i = 1; i < probe.Dithers.Count; i++)
        {
            Assert.True(probe.Dithers[i].Start >= probe.Dithers[i - 1].End!.Value, "two dithers overlapped");
        }

        Assert.False(probe.ExposedWhileDithering);
    }

    [Fact]
    public async Task AFastTriggerRig_WaitsAfterItsRequest_AndNeverQueuesAnotherDither()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Exposure(1.2)),
            Track(Wide, Repeat(9, Exposure(0.05)))));

        var probe = await RunAsync(fixture, built);

        // Main is in one long exposure all the time. Wide made three frames, asked, and stood still until Main was safe.
        var wide = probe.EndsOf("Wide Rig");
        var mainEnd = probe.EndsOf("Main Rig").Single();
        Assert.Equal(3, wide.Count(at => at <= mainEnd));
        Assert.Equal(3, probe.Dithers.Count); // frames 3, 6, 9: one per threshold, none doubled
        Assert.Equal(1, probe.MostDithersAtOnce);
        Assert.False(probe.ExposedWhileDithering);
    }

    // Tracks that end

    [Fact]
    public async Task ATrackThatHasEnded_IsNotWaitedFor_ByALaterDither()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.5))),
            Track(Wide, Repeat(8, Exposure(0.1))),
            Track(Narrow, Exposure(0.05)))); // done long before the first dither

        var probe = await RunAsync(fixture, built);

        Assert.Equal(2, probe.Dithers.Count);
        Assert.False(probe.ExposedWhileDithering);
        Assert.Single(probe.EndsOf("Narrow Rig"));
        Assert.True(probe.EndsOf("Narrow Rig")[0] < probe.Dithers[0].Start);
        Assert.Equal(8, probe.EndsOf("Wide Rig").Count);
    }

    [Fact]
    public async Task WhenTheTriggerRigEndsBeforeTheBlock_NoMoreDithersAreAsked_AndTheOthersRunOn()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(5, Exposure(0.3))),
            Track(Wide, Repeat(4, Exposure(0.05)))));

        var probe = await RunAsync(fixture, built);

        // Frame 3 asked once; frame 4 was the last of Wide and does not reach the next threshold.
        Assert.Single(probe.Dithers);
        Assert.Equal(5, probe.EndsOf("Main Rig").Count);
        Assert.True(probe.EndsOf("Main Rig")[^1] > probe.Dithers[0].End!.Value); // Main went on alone afterwards
    }

    [Fact]
    public async Task APolicyRunsAgain_WhenTheSameSequenceIsRunAgain_CountingFromZero()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 4),
            Track(Main, Exposure(0.5)),
            Track(Wide, Repeat(6, Exposure(0.05)))));

        var first = await RunAsync(fixture, built);
        var second = await RunAsync(fixture, built); // the very same runtime sequence

        Assert.Single(first.Dithers);
        Assert.Single(second.Dithers); // the count started over: frame 4 of this run, not frame 7 of both
    }

    // Pause

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task PauseBeforeAnyRequest_PausesAsBefore_AndResumeGoesOnWithTheDithers()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.4))),
            Track(Wide, Repeat(6, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var run = runner.RunAsync(built.Sequence);
        await WaitFor(() => probe.ExposureStarts.Count >= 2, "both tracks exposing");
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Empty(probe.Dithers); // nothing was asked yet
        var frames = probe.ExposureEnds.Count;
        await Task.Delay(300);
        Assert.Equal(frames, probe.ExposureEnds.Count);

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(2, probe.Dithers.Count);
        Assert.False(probe.ExposedWhileDithering);
    }

    [Fact]
    public async Task PauseWhileADitherIsPending_DoesNotDeadlock_TheRoundFinishes_ThenEveryTrackStops()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(1.2))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var run = runner.RunAsync(built.Sequence);
        await probe.Pending.Task.WaitAsync(Bound); // Wide asked, and waits for Main to be safe
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        // The round that was pending was done (Main finished its exposure, the dither ran), and then everything stopped.
        Assert.Single(probe.Dithers);
        Assert.NotNull(probe.Dithers[0].End);
        var wideStarted = probe.StartsOf("Wide Rig").Count;
        Assert.Equal(3, wideStarted); // Wide never began frame 4
        await Task.Delay(300);
        Assert.Equal(wideStarted, probe.StartsOf("Wide Rig").Count);

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(3, probe.Dithers.Count);
        Assert.False(probe.ExposedWhileDithering);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }

    [Fact]
    public async Task PauseWhileOneTrackIsAlreadyAtItsSafePoint_ReachesPausedWithoutDeadlock()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(1.0))),
            Track(Narrow, Repeat(3, Exposure(0.5))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var run = runner.RunAsync(built.Sequence);
        await probe.Pending.Task.WaitAsync(Bound);
        await Task.Delay(120); // the fast track waits already, the others are still exposing
        runner.RequestPause();

        await WaitFor(() => runner.State == SequenceState.Paused, "paused");
        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(probe.ExposedWhileDithering);
    }

    [Theory]
    [InlineData("dither")]
    [InlineData("settle")]
    public async Task PauseDuringTheDitherOrItsSettle_LetsThemFinish_AndStopsTheTracksBeforeTheirNextFrame(string when)
    {
        var options = Options(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(500));
        await using var fixture = CreateFixture(options);
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3, stable: 0.5),
            Track(Main, Repeat(2, Exposure(0.4))),
            Track(Wide, Repeat(8, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var run = runner.RunAsync(built.Sequence);
        await (when == "dither" ? probe.DitherStarted.Task : probe.Settling.Task).WaitAsync(Bound);
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");

        Assert.Single(probe.Dithers);
        Assert.NotNull(probe.Dithers[0].End); // it was not cut short
        Assert.Equal(3, probe.StartsOf("Wide Rig").Count); // and Wide did not go on to its next frame

        runner.Resume();
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(probe.ExposedWhileDithering);
    }

    [Fact]
    public async Task CancelWhilePausedAfterAPendingDither_EndsCleanly_AndAFreshRunWorks()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(1.0))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var group = Fixture.GroupOf(built);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await probe.Pending.Task.WaitAsync(Bound);
        runner.RequestPause();
        await WaitFor(() => runner.State == SequenceState.Paused, "paused");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(SequenceState.Cancelled, runner.State);
        await fixture.AssertCleanAsync(group);

        var again = await RunAsync(fixture, fixture.Build(Block(
            Policy(Wide, 3), Track(Main, Exposure(0.3)), Track(Wide, Repeat(4, Exposure(0.05))))));
        Assert.Single(again.Dithers);
    }

    // Cancel

    public static TheoryData<string> CancelPoints => new() { "imaging", "pending", "dither", "settle" };

    [Theory]
    [MemberData(nameof(CancelPoints))]
    public async Task Cancel_AtAnyPoint_EndsCleanly_WithNothingHeldAndNothingWaiting(string point)
    {
        var options = Options(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(500));
        await using var fixture = CreateFixture(options);
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3, stable: 0.5),
            Track(Main, Repeat(2, Exposure(1.0))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var group = Fixture.GroupOf(built);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        switch (point)
        {
            case "imaging":
                await WaitFor(() => probe.ExposureStarts.Count >= 2, "imaging");
                break;
            case "pending":
                await probe.Pending.Task.WaitAsync(Bound);
                break;
            case "dither":
                await probe.DitherStarted.Task.WaitAsync(Bound);
                break;
            default:
                await probe.Settling.Task.WaitAsync(Bound);
                break;
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.False(probe.ExposedWhileDithering);
        await fixture.AssertCleanAsync(group);

        // A fresh run afterwards works, and dithers.
        var again = await RunAsync(fixture, fixture.Build(Block(
            Policy(Wide, 3, stable: 0.1), Track(Main, Exposure(0.3)), Track(Wide, Repeat(4, Exposure(0.05))))));
        Assert.Single(again.Dithers);
    }

    // Failure

    [Fact]
    public async Task ADitherThatCannotBeMade_FailsTheRunAtTheTriggerTrack_AndReleasesEveryone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();

        // The guider is connected but nobody started guiding: the dither command refuses to move the mount.
        var built = fixture.Build(
            Block(Policy(Wide, 3), Track(Main, Repeat(2, Exposure(0.6))), Track(Wide, Repeat(9, Exposure(0.1)))),
            withGuiding: false);
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal("Wide Rig", failure.TrackName);
        Assert.IsType<InvalidOperationException>(failure.InnerException);
        Assert.False(probe.ExposedWhileDithering);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }

    [Fact]
    public async Task ASettleThatTimesOut_FailsTheRun_NamesTheTrack_AndReleasesEveryone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything();

        // The guide error never gets below this threshold within the timeout.
        var built = fixture.Build(Block(
            Policy(Wide, 3, amplitude: 5, threshold: 0.31, stable: 0.1, timeout: 0.3),
            Track(Main, Repeat(2, Exposure(0.5))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.IsType<GuidingSettleTimeoutException>(failure.InnerException);
        Assert.Equal("Wide Rig", failure.TrackName);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }

    [Fact]
    public async Task GuidingThatStopsWhileSettling_FailsTheRun_AndReleasesEveryone()
    {
        var options = Options(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(600));
        await using var fixture = CreateFixture(options);
        await fixture.ConnectEverything();
        var built = fixture.Build(Block(
            Policy(Wide, 3, stable: 0.6, timeout: 5),
            Track(Main, Repeat(2, Exposure(0.5))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var run = runner.RunAsync(built.Sequence);
        await probe.Settling.Task.WaitAsync(Bound);
        await fixture.Guider.StopGuidingAsync(); // the guider is stopped from outside while the settle waits

        await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(Bound));

        Assert.Equal(SequenceState.Failed, runner.State);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }

    [Fact]
    public async Task ATriggerRigWhoseExposureFailsBeforeTheThreshold_NeverDithers_AndTheRunFailsAtThatTrack()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything(except: DemoSetup.WideCameraId);
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.4))),
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var failure = await Assert.ThrowsAsync<RigTrackFailedException>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal("Wide Rig", failure.TrackName);
        Assert.Empty(probe.Dithers);
        Assert.Equal(SequenceState.Failed, runner.State);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }

    [Fact]
    public async Task AnotherRigFailing_WhileADitherIsPending_AbortsTheDither_AndReleasesEveryone()
    {
        await using var fixture = CreateFixture();
        await fixture.ConnectEverything(except: DemoSetup.NarrowCameraId);
        var built = fixture.Build(Block(
            Policy(Wide, 3),
            Track(Main, Repeat(2, Exposure(0.6))),
            Track(Narrow, Repeat(2, Exposure(0.4))), // its camera is not connected: fails at once
            Track(Wide, Repeat(9, Exposure(0.1)))));
        var runner = fixture.NewRunner();
        var probe = new Probe();
        probe.Attach(runner);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => runner.RunAsync(built.Sequence).WaitAsync(Bound));

        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Contains("Narrow Rig", failure is RigTrackFailedException t ? t.TrackName : failure.ToString());
        Assert.False(probe.ExposedWhileDithering);
        await fixture.AssertCleanAsync(Fixture.GroupOf(built));
    }
}

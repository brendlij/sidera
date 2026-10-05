using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>
/// The meridian flip on the simulator, run by the real runner with a clock that the test controls: the sky moves when the test says so and never by waiting. Shared mounts flip once for
/// everybody, separate mounts independently; guiding stops and starts once; nothing exposes while the mount moves; nothing ever synchronizes the mount.
/// </summary>
public sealed class MeridianFlipRunTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Start = new(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId A = new("rig.a");
    private static readonly RigId B = new("rig.b");
    private static readonly RigId C = new("rig.c");

    // The sky moves only when the test moves it: the target is 10 minutes before its meridian at the start.
    private sealed class SkyClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = Start;

        public double TargetRightAscensionHours => MeridianFlipTiming.LocalSiderealTimeHours(Start, Site.LongitudeDegrees) + 10.0 / 60;

        public DateTime UtcNow
        {
            get { lock (_gate) { return _now; } }
        }

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

        // Sets the clock so that the target's hour angle is this many minutes (negative: before the meridian).
        public void SetHourAngle(double minutes)
        {
            lock (_gate)
            {
                _now = Start + TimeSpan.FromMinutes((minutes + 10) / 1.00273790935);
            }
        }
    }

    private sealed class CountingSolver(SideraRuntimeHost host) : IPlateSolver
    {
        private int _calls;
        public bool Fails { get; set; }
        public int Calls => Volatile.Read(ref _calls);
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            if (Fails)
            {
                return Task.FromResult(new PlateSolveResult { Success = false, Backend = Name, Message = "No stars" });
            }

            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    private SideraRuntimeHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private sealed record World(SideraRuntimeHost Host, SkyClock Clock, CountingSolver Solver, SimulatedMount Mount1, SimulatedMount Mount2, SimulatedGuider Guider1);

    // A and B: their own camera and focuser, on mount 1 and guider 1. C: on mount 2 and guider 2.
    private async Task<World> CreateAsync(TimeSpan? slew1 = null)
    {
        var clock = new SkyClock();
        var host = new SideraRuntimeHost();
        _host = host;
        foreach (var name in new[] { "a", "b", "c" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        var m1 = host.AddSimulatedMount(new("mount.1"), "Mount 1", slew1 ?? TimeSpan.FromMilliseconds(20), () => clock.UtcNow, new MountSite(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.ElevationMeters));
        var m2 = host.AddSimulatedMount(new("mount.2"), "Mount 2", TimeSpan.FromMilliseconds(20), () => clock.UtcNow, new MountSite(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.ElevationMeters));
        var g1 = host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddSimulatedGuider(new("guider.2"), "Guider 2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddRig(new Rig(A, "Main", new("camera.a"), Optics, new("focuser.a"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(B, "Wide", new("camera.b"), Optics, new("focuser.b"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(C, "Remote", new("camera.c"), Optics, new("focuser.c"), null, null, null, new("mount.2"), new("guider.2")));
        foreach (var rig in new[] { A, B, C })
        {
            host.AddSimulatedFocusModel(rig, new SimulatedFocusModel(2600));
        }

        var solver = new CountingSolver(host);
        host.ConfigurePlateSolver(solver);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        clock.SetHourAngle(-10);
        return new World(host, clock, solver, m1, m2, g1);
    }

    private static MeridianFlipSettings Flip(Func<MeridianFlipSettings, MeridianFlipSettings>? change = null)
    {
        var settings = new MeridianFlipSettings
        {
            Enabled = true, PauseBeforeMeridianMinutes = 5, FlipAfterMeridianMinutes = 2, LatestAllowedFlipMinutes = 15, RecenterAfterFlip = true,
            VerifyRotationAfterFlip = false, AutofocusAfterFlip = false, RestartGuidingAfterFlip = true, DitherAfterFlip = false, PauseAfterFlipMinutes = 0, CenteringToleranceArcseconds = 60,
            MaxCenteringAttempts = 3, SolveExposureSeconds = 0.05,
        };
        return change?.Invoke(settings) ?? settings;
    }

    private static RigTrackDraft Track(RigId rig, int frames, double seconds, RigAutofocusPolicyDraft? policy = null) =>
        new(Guid.NewGuid(), rig, [new RepeatStepDraft(Guid.NewGuid(), frames, [new RigExposureStepDraft(Guid.NewGuid(), seconds)])], policy);

    private static MeridianFlipPolicyDraft Policy(World w, MeridianFlipSettings settings) =>
        new(settings, w.Clock.TargetRightAscensionHours, 41.3, "M31", null, null, 0.6, 0.5, 0.1, 5);

    private static BuiltSequence Build(World w, MeridianFlipSettings settings, params RigTrackDraft[] tracks)
    {
        var steps = new List<SequenceStepDraft>
        {
            new StartGuidingStepDraft(Guid.NewGuid(), new("guider.1")),
            new MultiRigStepDraft(Guid.NewGuid(), tracks, null, true, Policy(w, settings)),
            new StopGuidingStepDraft(Guid.NewGuid(), new("guider.1")),
        };
        if (tracks.Any(t => t.RigId == C))
        {
            steps.Insert(1, new StartGuidingStepDraft(Guid.NewGuid(), new("guider.2")));
            steps.Add(new StopGuidingStepDraft(Guid.NewGuid(), new("guider.2")));
        }

        var host = w.Host;
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
        var context = new SequenceDraftContext(
            host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, host.Rotation, w.Clock, () => Site, TimeSpan.FromMilliseconds(30));
        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, steps, context);
        Assert.True(validation.IsValid, string.Join(" ", SequenceDraftBuilder.Sentences(steps, validation)));
        return SequenceDraftBuilder.Build(host.DeviceRegistry, steps, context);
    }

    private static IEnumerable<MeridianFlipGroup> GroupsOf(IEnumerable<BuiltStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.Step is MeridianGateStep gate)
            {
                yield return gate.Group;
            }

            foreach (var inner in GroupsOf(step.Children ?? []))
            {
                yield return inner;
            }
        }
    }

    private static List<MeridianFlipGroup> Groups(BuiltSequence built) => GroupsOf(built.Steps).DistinctBy(g => g.MountId).ToList();

    private sealed class Observation
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _completed = [];
        public List<MeridianFlipStateChanged> States { get; } = [];
        public bool ExposedWhileFlipping { get; private set; }
        public bool ExposedWhileGroupFlipping { get; private set; }

        public int Count(string name)
        {
            lock (_gate)
            {
                return _completed.GetValueOrDefault(name);
            }
        }

        public List<MeridianFlipState> StatesOf(string mount)
        {
            lock (_gate)
            {
                return States.Where(s => s.MountId.Value == mount).Select(s => s.State).ToList();
            }
        }

        public void Attach(SideraRuntimeHost host, SequenceRunner runner)
        {
            host.EventBus.Subscribe<MeridianFlipStateChanged>((e, _) =>
            {
                lock (_gate)
                {
                    States.Add(e);
                    var exposing = runner.ActivePositions.Any(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal));
                    if (e.State is MeridianFlipState.Flipping or MeridianFlipState.Solving or MeridianFlipState.Centering or MeridianFlipState.Settling && e.Setups.Count > 1 && exposing)
                    {
                        ExposedWhileFlipping = true;
                    }
                }

                return Task.CompletedTask;
            });
            runner.StepCompleted += (_, e) =>
            {
                lock (_gate)
                {
                    var name = e.StepName.StartsWith("Exposure", StringComparison.Ordinal) ? "Exposure" : e.StepName.StartsWith("Dither ", StringComparison.Ordinal) && e.StepName.EndsWith(" px", StringComparison.Ordinal) ? "Dither" : e.StepName;
                    _completed[name] = _completed.GetValueOrDefault(name) + 1;
                }
            };
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    // Runs the sequence; moves the sky to `flipAt` minutes of hour angle after `afterExposures` exposures have completed.
    private async Task<(Observation Run, SequenceRunner Runner, Task Task)> StartAsync(World w, BuiltSequence built)
    {
        var runner = new SequenceRunner(w.Host.ResourceManager, w.Host.SafePointCoordinator);
        var run = new Observation();
        run.Attach(w.Host, runner);
        var task = runner.RunAsync(built.Sequence);
        await Task.Yield();
        return (run, runner, task);
    }

    [Fact]
    public async Task TwoSetupsOnOneMount_FlipOnce_TogetherWithOneGuiderStopAndStart_AndNeverExposeWhileTheMountMoves()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(), Track(A, 14, 0.4), Track(B, 36, 0.15));
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 4, "the first exposures");

        w.Clock.SetHourAngle(3); // the flip is due

        await task.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(50, run.Count("Exposure")); // both setups resumed and finished
        var states = run.StatesOf("mount.1");
        Assert.Equal(1, states.Count(s => s == MeridianFlipState.Completed)); // one flip for both setups, not one for each
        Assert.Contains(MeridianFlipState.Flipping, states);
        Assert.Equal(1, run.Count("Stop guiding for the flip")); // the shared guider is stopped once
        Assert.Equal(2, run.Count("Start guiding")); // the sequence starts it once, the flip starts it once again
        Assert.Equal(1, run.Count("Stop guiding")); // only the sequence's own stop
        Assert.Contains(MeridianFlipState.Settling, states);
        Assert.False(run.ExposedWhileFlipping, "a camera exposed while the mount flipped");
        Assert.Equal(0, w.Mount1.SyncCount); // never a sync
        Assert.True(w.Solver.Calls >= 1); // centered by a plate solve
        Assert.Equal(["Main", "Wide"], run.States.First(s => s.State == MeridianFlipState.Completed).Setups);
    }

    [Fact]
    public async Task TheStatesOfAFlip_FollowTheDocumentedOrder()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(s => s with { AutofocusAfterFlip = true, DitherAfterFlip = true }), Track(A, 12, 0.3), Track(B, 12, 0.3));
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        var order = run.StatesOf("mount.1").Distinct().ToList();
        var expected = new[]
        {
            MeridianFlipState.StoppingGuiding, MeridianFlipState.Flipping, MeridianFlipState.Solving, MeridianFlipState.Autofocusing, MeridianFlipState.StartingGuiding,
            MeridianFlipState.Settling, MeridianFlipState.Dithering, MeridianFlipState.Completed,
        };
        var seen = order.Where(expected.Contains).ToList();
        Assert.Equal(expected, seen.Distinct().ToArray()); // autofocus comes after the final pointing, guiding restarts after it, the dither after the settle
        Assert.Equal(1, run.Count("Dither after the flip")); // one dither for both setups on the shared guider
        Assert.Equal(2, run.Count("Autofocus")); // both setups have a focuser
        Assert.Equal(0, w.Mount1.SyncCount);
    }

    [Fact]
    public async Task SwitchedOffOperations_AreSkipped_AndWhatIsOnStillRuns()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(s => s with { RecenterAfterFlip = false, AutofocusAfterFlip = false, RestartGuidingAfterFlip = true, DitherAfterFlip = false }), Track(A, 10, 0.3), Track(B, 10, 0.3));
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        var states = run.StatesOf("mount.1");
        Assert.DoesNotContain(MeridianFlipState.Solving, states);
        Assert.DoesNotContain(MeridianFlipState.Centering, states);
        Assert.DoesNotContain(MeridianFlipState.Autofocusing, states);
        Assert.DoesNotContain(MeridianFlipState.Dithering, states);
        Assert.Contains(MeridianFlipState.StartingGuiding, states);
        Assert.Equal(0, w.Solver.Calls); // no recenter, no solve
        Assert.Equal(0, run.Count("Autofocus"));
        Assert.Equal(0, run.Count("Dither after the flip"));
    }

    [Fact]
    public async Task WithGuidingRestartOff_TheGuiderStaysStopped_AfterTheFlip()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(s => s with { RestartGuidingAfterFlip = false }), Track(A, 8, 0.3), Track(B, 8, 0.3));
        var (run, _, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await task.WaitAsync(Bound);

        Assert.Equal(1, run.Count("Start guiding")); // only the sequence's own start
        Assert.DoesNotContain(MeridianFlipState.StartingGuiding, run.StatesOf("mount.1"));
    }

    [Fact]
    public async Task ThePauseAfterTheFlip_IsAdditional_AndDoesNotReplaceTheSettle()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(s => s with { PauseAfterFlipMinutes = 0.02 }), Track(A, 6, 0.3), Track(B, 6, 0.3)); // 1.2 s
        var (run, _, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await task.WaitAsync(Bound);

        var states = run.StatesOf("mount.1");
        Assert.True(states.IndexOf(MeridianFlipState.Settling) < states.IndexOf(MeridianFlipState.PostFlipPause)); // after the settle, not instead of it
        Assert.True(states.IndexOf(MeridianFlipState.PostFlipPause) < states.IndexOf(MeridianFlipState.Completed));
    }

    [Fact]
    public async Task IndependentMounts_FlipIndependently_AndAMountThatIsFlippingDoesNotHoldTheOther()
    {
        var w = await CreateAsync(slew1: TimeSpan.FromMilliseconds(1500)); // mount 1 takes long to slew
        var built = Build(w, Flip(s => s with { RecenterAfterFlip = false }), Track(A, 30, 0.3), Track(B, 30, 0.3), Track(C, 30, 0.15));
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 6, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await WaitAsync(() => run.StatesOf("mount.2").Contains(MeridianFlipState.Completed), "the flip of mount 2");

        // Mount 2 is done while mount 1 is still slewing: its setup is imaging again, not held by the other group.
        Assert.DoesNotContain(MeridianFlipState.Completed, run.StatesOf("mount.1"));
        var before = run.Count("Exposure");
        await WaitAsync(() => run.Count("Exposure") >= before + 3, "exposures of the remote setup while mount 1 flips");
        Assert.DoesNotContain(MeridianFlipState.Completed, run.StatesOf("mount.1"));

        await task.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(90, run.Count("Exposure"));
        Assert.Equal(["Remote"], run.States.First(s => s.MountId.Value == "mount.2" && s.State == MeridianFlipState.Completed).Setups); // only its own setup
        Assert.Equal(["Main", "Wide"], run.States.First(s => s.MountId.Value == "mount.1" && s.State == MeridianFlipState.Completed).Setups);
        Assert.Equal(0, w.Mount1.SyncCount);
        Assert.Equal(0, w.Mount2.SyncCount);
    }

    [Fact]
    public async Task ATargetThatIsAlreadyPastTheMeridian_NeedsNoFlip()
    {
        var w = await CreateAsync();
        w.Clock.SetHourAngle(30); // long past: the slew to it chose the side of the pier already
        var built = Build(w, Flip(), Track(A, 3, 0.1), Track(B, 3, 0.1));
        var (run, runner, task) = await StartAsync(w, built);

        await task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.DoesNotContain(run.StatesOf("mount.1"), s => s != MeridianFlipState.Monitoring);
        Assert.Equal(6, run.Count("Exposure"));
        Assert.Equal(1, run.Count("Start guiding"));
    }

    [Fact]
    public async Task AFailedCentering_HoldsTheMountGroup_UntilTheUserRetriesOrAborts_AndNothingExposesMeanwhile()
    {
        var w = await CreateAsync();
        w.Solver.Fails = true;
        var built = Build(w, Flip(s => s with { MaxFlipAttempts = 2 }), Track(A, 20, 0.3), Track(B, 20, 0.3));
        var group = Groups(built).Single();
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(2.5);
        await WaitAsync(() => group.IsWaitingForDecision, "the failed flip to wait for a decision");

        Assert.Equal(MeridianFlipState.Failed, group.State);
        Assert.Contains("Retry the flip or abort", group.Message, StringComparison.Ordinal);
        var exposures = run.Count("Exposure");
        await Task.Delay(300);
        Assert.Equal(exposures, run.Count("Exposure")); // nothing images after a failed flip by itself
        Assert.False(task.IsCompleted);

        w.Solver.Fails = false; // the user fixed what was wrong
        Assert.True(group.Retry());
        await task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(40, run.Count("Exposure"));
        Assert.Contains(MeridianFlipState.Completed, run.StatesOf("mount.1"));
        Assert.Equal(0, w.Mount1.SyncCount);
    }

    [Fact]
    public async Task AbortingAFailedFlip_FailsTheSequence_WithTheReason()
    {
        var w = await CreateAsync();
        w.Solver.Fails = true;
        var built = Build(w, Flip(s => s with { MaxFlipAttempts = 1 }), Track(A, 20, 0.3), Track(B, 20, 0.3));
        var group = Groups(built).Single();
        var (_, runner, task) = await StartAsync(w, built);
        await Task.Delay(100);
        w.Clock.SetHourAngle(2.5);
        await WaitAsync(() => group.IsWaitingForDecision, "the failed flip to wait for a decision");

        Assert.True(group.Abort());

        await Assert.ThrowsAnyAsync<Exception>(() => task.WaitAsync(Bound));
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Contains("meridian flip failed", runner.Failure?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WithAbortAsTheFailureBehavior_AFailedFlipEndsTheSequenceAtOnce()
    {
        var w = await CreateAsync();
        w.Solver.Fails = true;
        var built = Build(w, Flip(s => s with { MaxFlipAttempts = 1, FailureBehavior = MeridianFlipFailureBehavior.AbortSession }), Track(A, 20, 0.3), Track(B, 20, 0.3));
        var (_, runner, task) = await StartAsync(w, built);
        await Task.Delay(100);

        w.Clock.SetHourAngle(2.5);

        await Assert.ThrowsAnyAsync<Exception>(() => task.WaitAsync(Bound));
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task CancellingDuringAFailedFlip_EndsTheSession_WithoutReversingTheMount()
    {
        var w = await CreateAsync();
        w.Solver.Fails = true;
        var built = Build(w, Flip(s => s with { MaxFlipAttempts = 1 }), Track(A, 20, 0.3), Track(B, 20, 0.3));
        var group = Groups(built).Single();
        using var cts = new CancellationTokenSource();
        var runner = new SequenceRunner(w.Host.ResourceManager, w.Host.SafePointCoordinator);
        var task = runner.RunAsync(built.Sequence, cts.Token);
        await Task.Delay(100);
        w.Clock.SetHourAngle(2.5);
        await WaitAsync(() => group.IsWaitingForDecision, "the failed flip to wait for a decision");
        var where = w.Mount1.Coordinates;

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(where.RightAscensionHours, w.Mount1.Coordinates.RightAscensionHours, 6); // the physical flip is not reversed
        Assert.False(group.IsWaitingForDecision);
    }

    [Fact]
    public async Task AFlipThatIsOverdue_IsAFailure_AndTheUserCanFlipAnyway()
    {
        var w = await CreateAsync();
        var built = Build(w, Flip(s => s with { RecenterAfterFlip = false, MaxFlipAttempts = 1 }), Track(A, 12, 0.3), Track(B, 12, 0.3));
        var group = Groups(built).Single();
        var (run, runner, task) = await StartAsync(w, built);
        await WaitAsync(() => run.Count("Exposure") >= 2, "the first exposures");

        w.Clock.SetHourAngle(20); // the latest allowed flip (15 min) has passed without a flip
        await WaitAsync(() => group.IsWaitingForDecision, "the overdue flip to wait for a decision");
        Assert.Contains("latest allowed flip", group.Message, StringComparison.Ordinal);

        Assert.True(group.Retry());
        await task.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Contains(MeridianFlipState.Completed, run.StatesOf("mount.1"));
    }
}

using System.Diagnostics;
using Sidera.Core.Astronomy;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>
/// Several cameras, focusers, mounts and guiders at once, run on the simulators: what may overlap and what must not follow from the claims of the actions and from which devices the setups share,
/// not from names. Two cameras expose together; a focus run that holds the stability of a shared mount waits for the exposure that is running and keeps the next one back; independent mounts
/// do not wait for each other; a shared guider is dithered once for everybody; a flip stops only the mount group it moves.
/// </summary>
public sealed class MultiDeviceClaimTests : IAsyncLifetime
{
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);
    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");

    private sealed record Span(string Name, string Track, TimeSpan Start, TimeSpan? End);

    // Which actions ran when, by observing the runner: an interval for every step of the runs that were seen.
    private sealed class Probe
    {
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<SequenceExecutionPosition, int> _open = [];
        private readonly List<Span> _spans = [];
        private int _maxShared;

        public IReadOnlyList<Span> Spans { get { lock (_gate) { return _spans.ToList(); } } }

        public int MostSharedStability => _maxShared;

        public Holds? Held { get; set; }

        public void Attach(SequenceRunner runner, SideraRuntimeHost host, DeviceId mount)
        {
            runner.Changed += (_, _) =>
            {
                lock (_gate)
                {
                    foreach (var position in runner.ActivePositions.Where(p => !_open.ContainsKey(p) && Interesting(p.StepName)))
                    {
                        _open[position] = _spans.Count;
                        _spans.Add(new Span(Kind(position.StepName), TrackOf(position), _clock.Elapsed, null));
                    }

                    _maxShared = Math.Max(_maxShared, host.ResourceManager.SharedHolders(ResourceId.ForMountStability(mount)));
                }
            };
            runner.StepCompleted += (_, e) =>
            {
                lock (_gate)
                {
                    if (_open.Remove(e.Position, out var index))
                    {
                        _spans[index] = _spans[index] with { End = _clock.Elapsed };
                    }
                }
            };
        }

        private static bool Interesting(string name) => name.StartsWith("Exposure", StringComparison.Ordinal) || name == "Autofocus" || name == "Dither command" || name.StartsWith("Slew", StringComparison.Ordinal);

        private static string Kind(string name) => name.StartsWith("Exposure", StringComparison.Ordinal) ? "Exposure" : name.StartsWith("Slew", StringComparison.Ordinal) ? "Slew" : name;

        private static string TrackOf(SequenceExecutionPosition position)
        {
            var track = string.Empty;
            for (var p = position; p is not null; p = p.Parent)
            {
                if (p.StepName is "Main" or "Wide")
                {
                    track = p.StepName;
                }
            }

            return track;
        }

        public IReadOnlyList<Span> Of(string kind, string? track = null) => Spans.Where(s => s.Name == kind && (track is null || s.Track == track)).ToList();

        public static bool Overlap(Span a, Span b) => a.Start < (b.End ?? TimeSpan.MaxValue) && b.Start < (a.End ?? TimeSpan.MaxValue);
    }

    // What was held when, exactly: from the events of the resource manager, in the order of the changes.
    private sealed class Holds
    {
        private readonly object _gate = new();
        private readonly Dictionary<ResourceId, (int Shared, bool Exclusive)> _held = [];
        private readonly HashSet<(ResourceId, ResourceId)> _together = [];
        private readonly Dictionary<ResourceId, int> _mostShared = [];

        /// <summary>A resource was held exclusively and shared at the same moment: never.</summary>
        public bool Violated { get; private set; }

        public Holds(SideraRuntimeHost host) => host.ResourceManager.Changed += (_, change) =>
        {
            lock (_gate)
            {
                foreach (var claim in change.Claims)
                {
                    _held.TryGetValue(claim.Resource, out var state);
                    state = claim.Mode == ClaimMode.Exclusive ? (state.Shared, change.Granted) : (state.Shared + (change.Granted ? 1 : -1), state.Exclusive);
                    _held[claim.Resource] = state;
                    Violated |= state.Exclusive && state.Shared > 0;
                    if (claim.Mode == ClaimMode.Shared)
                    {
                        _mostShared[claim.Resource] = Math.Max(_mostShared.GetValueOrDefault(claim.Resource), state.Shared);
                    }
                }

                if (change.Granted)
                {
                    var now = _held.Where(h => h.Value.Shared > 0 || h.Value.Exclusive).Select(h => h.Key).ToList();
                    foreach (var x in now)
                    {
                        foreach (var y in now)
                        {
                            _together.Add((x, y));
                        }
                    }
                }
            }
        };

        public bool WereTogether(ResourceId a, ResourceId b)
        {
            lock (_gate)
            {
                return _together.Contains((a, b));
            }
        }

        public int MostShared(ResourceId resource)
        {
            lock (_gate)
            {
                return _mostShared.GetValueOrDefault(resource);
            }
        }
    }

    private static ResourceId Dev(string id) => ResourceId.ForDevice(new DeviceId(id));

    private static ResourceId Stable(string mount) => ResourceId.ForMountStability(new DeviceId(mount));

    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // Two setups, each with its camera and its focuser; the mounts and guiders are given per setup, so that they can be shared or not.
    private async Task<SideraRuntimeHost> CreateAsync(string mountOfMain, string mountOfWide, string guiderOfMain, string guiderOfWide)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        foreach (var mount in new[] { mountOfMain, mountOfWide }.Distinct())
        {
            host.AddSimulatedMount(new(mount), mount, TimeSpan.FromMilliseconds(20));
        }

        foreach (var guider in new[] { guiderOfMain, guiderOfWide }.Distinct())
        {
            host.AddSimulatedGuider(new(guider), guider, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        }

        host.AddRig(new Rig(Main, "Main", new("camera.a"), Optics, new("focuser.a"), null, null, null, new(mountOfMain), new(guiderOfMain)));
        host.AddRig(new Rig(Wide, "Wide", new("camera.b"), Optics, new("focuser.b"), null, null, null, new(mountOfWide), new(guiderOfWide)));
        foreach (var rig in new[] { Main, Wide })
        {
            host.AddSimulatedFocusModel(rig, new SimulatedFocusModel(2600));
        }

        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        return host;
    }

    private static WorkflowDefinition Workflow(int mainFrames, double mainSeconds, int wideFrames, double wideSeconds, Func<WorkflowDefinition, WorkflowDefinition>? change = null)
    {
        var workflow = WorkflowDefinition.Empty with
        {
            Target = new WorkflowTarget("M31", 0.712, 41.27),
            Imaging = [new ImagingBlock(Guid.NewGuid(), Main, null, mainSeconds, mainFrames), new ImagingBlock(Guid.NewGuid(), Wide, null, wideSeconds, wideFrames)],
        };
        return change?.Invoke(workflow) ?? workflow;
    }

    private static async Task<Probe> RunAsync(SideraRuntimeHost host, WorkflowDefinition workflow, DeviceId mount, bool autofocusHoldsMount = false, Func<SequenceRunner, Task>? during = null)
    {
        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
        var context = new SequenceDraftContext(
            host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, null, MeridianPollInterval: Poll, AutofocusHoldsMount: autofocusHoldsMount);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var probe = new Probe { Held = new Holds(host) };
        probe.Attach(runner, host, mount);
        var run = runner.RunAsync(built.Sequence);
        if (during is not null)
        {
            await during(runner);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(90));
        Assert.Equal(SequenceState.Completed, runner.State);
        return probe;
    }

    private static SetupAutofocus FocusAtStart(RigId setup) => new(setup, true, true, 0, false, new AutofocusSettings(0.05, 400, 5));

    // ---- two cameras

    [Fact]
    public async Task TwoCameras_OnOneMount_ExposeAtTheSameTime_AndShareItsStability()
    {
        var host = await CreateAsync("mount.1", "mount.1", "guider.1", "guider.1");

        var probe = await RunAsync(host, Workflow(3, 0.25, 3, 0.25), new("mount.1"));

        Assert.Equal(3, probe.Of("Exposure", "Main").Count);
        Assert.Equal(3, probe.Of("Exposure", "Wide").Count);
        Assert.True(probe.Held!.WereTogether(Dev("camera.a"), Dev("camera.b"))); // not serialized
        Assert.Equal(2, probe.Held.MostShared(Stable("mount.1"))); // both held the stability of the mount, shared
    }

    // ---- autofocus and a shared mount

    [Fact]
    public async Task AFocusThatHoldsTheMount_WaitsForTheExposureOfTheOtherCamera_AndKeepsTheNextOneBack()
    {
        var host = await CreateAsync("mount.1", "mount.1", "guider.1", "guider.1");
        // Wide takes one long exposure first; Main wants to focus at its start. The focus holds the stability of the shared mount.
        var workflow = Workflow(2, 0.05, 3, 0.6, w => w with { AutofocusPolicies = [FocusAtStart(Main)] });

        var probe = await RunAsync(host, workflow, new("mount.1"), autofocusHoldsMount: true);

        Assert.True(probe.Of("Autofocus", "Main").Count >= 1);
        Assert.Equal(3, probe.Of("Exposure", "Wide").Count); // Wide goes on after the focus
        Assert.False(probe.Held!.WereTogether(Dev("focuser.a"), Dev("camera.b")), "the camera of Wide was held while the focuser of Main was held");
    }

    [Fact]
    public async Task AFocusThatDoesNotHoldTheMount_RunsWhileTheOtherCameraExposes()
    {
        var host = await CreateAsync("mount.1", "mount.1", "guider.1", "guider.1");
        var workflow = Workflow(2, 0.05, 6, 0.4, w => w with { AutofocusPolicies = [FocusAtStart(Main)] });

        var probe = await RunAsync(host, workflow, new("mount.1"), autofocusHoldsMount: false);

        Assert.True(probe.Of("Autofocus", "Main").Count >= 1);
        Assert.True(probe.Held!.WereTogether(Dev("focuser.a"), Dev("camera.b"))); // the default: nobody waits for a focus
    }

    [Fact]
    public async Task OnIndependentMounts_AFocusThatHoldsItsMount_DoesNotWaitForTheOtherMountsExposure()
    {
        var host = await CreateAsync("mount.1", "mount.2", "guider.1", "guider.2");
        var workflow = Workflow(2, 0.05, 6, 0.4, w => w with { AutofocusPolicies = [FocusAtStart(Main)] });

        var probe = await RunAsync(host, workflow, new("mount.2"), autofocusHoldsMount: true);

        Assert.True(probe.Of("Autofocus", "Main").Count >= 1);
        Assert.True(probe.Held!.WereTogether(Dev("focuser.a"), Dev("camera.b"))); // another mount group: its exposures are not affected
    }

    // ---- dither

    [Fact]
    public async Task ASharedGuider_IsDitheredOnce_ForTheFrameThatAskedForIt_AndNobodyExposesMeanwhile()
    {
        var host = await CreateAsync("mount.1", "mount.1", "guider.1", "guider.1");
        var workflow = Workflow(4, 0.1, 4, 0.1, w => w with
        {
            Prepare = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StartGuiding)],
            Finish = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StopGuiding)],
            Dither = new WorkflowDither(true, 2, Main, 1.5, 0.5, 0.05, 5),
        });

        var probe = await RunAsync(host, workflow, new("mount.1"));

        Assert.Equal(2, probe.Of("Dither command").Count); // after frames 2 and 4 of Main: one dither each, for both setups together
        Assert.True(probe.Held!.WereTogether(Dev("guider.1"), Stable("mount.1"))); // the dither held the stability of the mount, and so no exposure was running on it
        Assert.False(probe.Held.Violated);
    }

    [Fact]
    public async Task IndependentGuiders_OnIndependentMounts_AreDitheredWithoutHoldingTheOtherTrack()
    {
        var host = await CreateAsync("mount.1", "mount.2", "guider.1", "guider.2");
        var workflow = Workflow(4, 0.1, 6, 0.3, w => w with
        {
            Prepare = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StartGuiding)],
            Finish = [new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.StopGuiding)],
            Dither = new WorkflowDither(true, 2, Main, 1.5, 0.5, 0.05, 5),
        });

        var probe = await RunAsync(host, workflow, new("mount.2"));

        Assert.True(probe.Of("Dither command").Count >= 1);
        // Wide is on another mount and another guider: it exposes while Main is dithered; and the cameras of Main do not.
        Assert.True(probe.Held!.WereTogether(Dev("guider.1"), Dev("camera.b")));
        Assert.False(probe.Held.Violated);
    }

    // ---- a move of the mount

    [Fact]
    public async Task ASlew_WaitsForTheExposuresOnItsMount_AndNewOnesWaitForTheSlew()
    {
        var host = await CreateAsync("mount.1", "mount.1", "guider.1", "guider.1");
        var camera = new CameraExposureAction(host.DeviceRegistry, new("camera.a"), TimeSpan.FromMilliseconds(400), null, null, null, new("mount.1"));
        var slew = new SlewAction(host.DeviceRegistry, new("mount.1"), new CelestialCoordinates(2, 20));
        var second = new CameraExposureAction(host.DeviceRegistry, new("camera.b"), TimeSpan.FromMilliseconds(100), null, null, null, new("mount.1"));
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var probe = new Probe { Held = new Holds(host) };
        probe.Attach(runner, host, new("mount.1"));

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [camera, new SequenceGroup("slew later", [new Wait(60), slew, second])])]));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(probe.Held!.WereTogether(Dev("mount.1"), Dev("camera.a"))); // the slew started after 60 ms and waited for the exposure that was running
        Assert.False(probe.Held.WereTogether(Dev("mount.1"), Dev("camera.b"))); // and the exposure that came after it waited for the slew
    }

    private sealed class Wait(int milliseconds) : ISequenceStep
    {
        public string Name => "Wait";

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(milliseconds, cancellationToken);
            return new SequenceStepResult();
        }
    }

    // ---- the meridian flip

    private static readonly ObservingSite Site = new(50.1, 8.6, 120);

    [Fact]
    public async Task OnIndependentMounts_AFlipOfOneMount_DoesNotPauseTheOtherMountsTrack()
    {
        var host = await CreateAsync("mount.1", "mount.2", "guider.1", "guider.2");
        var t0 = new DateTime(2026, 3, 1, 21, 0, 0, DateTimeKind.Utc);
        var ra = MeridianFlipTiming.LocalSiderealTimeHours(t0.AddMinutes(3), Site.LongitudeDegrees);
        var clock = new SkyClock(t0);
        var flip = new MeridianFlipSettings { Enabled = true, SolveExposureSeconds = 0.05, MaxCenteringAttempts = 3, VerifyRotationAfterFlip = false, RecenterAfterFlip = false, RestartGuidingAfterFlip = false, StopGuidingBeforeFlip = false };
        var workflow = Workflow(3, 0.3, 8, 0.3, w => w with { Target = new WorkflowTarget("M31", ra, 41.27), MeridianFlip = flip });
        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
        var context = new SequenceDraftContext(
            host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, null, Time: clock, Site: () => Site, MeridianPollInterval: Poll);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        var groups = built.Steps.SelectMany(Flatten).Select(b => b.Step).OfType<MeridianGateStep>().Select(g => g.Group).Distinct().ToList();
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var probe = new Probe();
        probe.Attach(runner, host, new("mount.2"));

        // Both mounts are in the same sky (the same clock): the flips of the two groups are separate and both happen, each only holding the setups of its own mount.
        var run = runner.RunAsync(built.Sequence);
        await Task.Delay(200);
        clock.Set(t0.AddMinutes(6));
        await run.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(2, groups.Count); // one flip group for each mount
        Assert.Equal(["mount.1", "mount.2"], groups.Select(g => g.MountId.Value).Order());
        Assert.All(groups, g => Assert.Equal(MeridianFlipState.Completed, g.State));
        Assert.Equal(3, probe.Of("Exposure", "Main").Count);
        Assert.Equal(8, probe.Of("Exposure", "Wide").Count);
    }

    private static IEnumerable<BuiltStep> Flatten(BuiltStep step) => new[] { step }.Concat((step.Children ?? []).SelectMany(Flatten));

    private sealed class SkyClock(DateTime start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = start;

        public void Set(DateTime utc)
        {
            lock (_gate)
            {
                _now = utc;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(_now, TimeSpan.Zero);
            }
        }
    }
}

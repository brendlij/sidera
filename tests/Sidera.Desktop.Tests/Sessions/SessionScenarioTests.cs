using Sidera.Core.Astrometry;
using Sidera.Core.Conditions;
using Sidera.Core.FilterWheels;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Rigs;
using Sidera.Runtime.Sequencing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;
using StartGuidingAction = Sidera.Desktop.Sessions.StartGuidingAction;
using StopGuidingAction = Sidera.Desktop.Sessions.StopGuidingAction;
using SlewAndCenterAction = Sidera.Desktop.Sessions.SlewAndCenterAction;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>
/// A night on the simulator, as the spec of the redesign tells it: a short wait to start; M42 with its shared preparation (Slew &amp; Center, Start Guiding); Main with a block of Ha frames (set filter, an
/// exposure, a dither every 2 frames, an autofocus on a short interval) next to Wide with a block of RGB frames (an autofocus of its own); a meridian flip of the session; a limit of the target; and Stop Guiding
/// at the end. Both setups share one mount and one guider.
/// </summary>
public sealed class SessionScenarioTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Start = new(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId MainSetup = new("rig.main");
    private static readonly RigId WideSetup = new("rig.wide");
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    private sealed class SkyClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = Start;
        public double TargetRightAscensionHours => MeridianFlipTiming.LocalSiderealTimeHours(Start, Site.LongitudeDegrees) + 10.0 / 60;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return new DateTimeOffset(_now, TimeSpan.Zero);
            }
        }

        public DateTime UtcNow => GetUtcNow().UtcDateTime;

        public void SetHourAngle(double minutes)
        {
            lock (_gate)
            {
                _now = Start + TimeSpan.FromMinutes((minutes + 10) / 1.00273790935);
            }
        }
    }

    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    // What the run did, seen through every change of the runner.
    private sealed class Observation
    {
        private readonly object _gate = new();
        public Dictionary<string, int> Completed { get; } = [];
        public List<string> Names { get; } = [];
        public int MostExposuresAtOnce { get; private set; }
        public bool ExposedWhileDithering { get; private set; }

        public void Attach(SequenceRunner runner)
        {
            runner.Changed += (_, _) =>
            {
                var names = runner.ActivePositions.Select(p => p.StepName).ToList();
                var exposures = names.Count(n => n.StartsWith("Exposure", StringComparison.Ordinal));
                lock (_gate)
                {
                    MostExposuresAtOnce = Math.Max(MostExposuresAtOnce, exposures);
                    ExposedWhileDithering |= names.Any(n => n == "Dither command") && exposures > 0;
                }
            };
            runner.StepCompleted += (_, e) =>
            {
                lock (_gate)
                {
                    var name = e.StepName.StartsWith("Exposure", StringComparison.Ordinal) ? "Exposure"
                        : e.StepName.StartsWith("Dither ", StringComparison.Ordinal) && e.StepName.EndsWith(" px", StringComparison.Ordinal) ? "Dither" : e.StepName;
                    Completed[name] = Completed.GetValueOrDefault(name) + 1;
                    Names.Add(e.StepName);
                }
            };
        }

        public int Count(string name)
        {
            lock (_gate)
            {
                return Completed.GetValueOrDefault(name);
            }
        }
    }

    // What was held when, from the events of the resource manager: whether the focuser of one setup was held while the camera of the other was.
    private sealed class FocusWatch
    {
        private readonly object _gate = new();
        private readonly Dictionary<ResourceId, int> _held = [];

        public bool FocusedWhileTheOtherExposed { get; private set; }

        public FocusWatch(SideraRuntimeHost host) => host.ResourceManager.Changed += (_, change) =>
        {
            lock (_gate)
            {
                foreach (var claim in change.Claims)
                {
                    _held[claim.Resource] = _held.GetValueOrDefault(claim.Resource) + (change.Granted ? 1 : -1);
                }

                bool Held(string id) => _held.GetValueOrDefault(ResourceId.ForDevice(new DeviceId(id))) > 0;
                FocusedWhileTheOtherExposed |= (Held("focuser.a") && Held("camera.b")) || (Held("focuser.b") && Held("camera.a"));
            }
        };
    }

    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<(SideraRuntimeHost Host, SkyClock Clock, SimulatedMount Mount)> CreateAsync()
    {
        var clock = new SkyClock();
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        host.AddSimulatedFilterWheel(new("wheel.a"), "Wheel A", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha"), new FilterSlot(2, "OIII"), new FilterSlot(3, "SII")]);
        var mount = host.AddSimulatedMount(new("mount.1"), "AM3", TimeSpan.FromMilliseconds(20), () => clock.UtcNow, new MountSite(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.ElevationMeters));
        host.AddSimulatedGuider(new("guider.1"), "PHD2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddRig(new Rig(MainSetup, "Main 750", new("camera.a"), Optics, new("focuser.a"), new("wheel.a"), null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(WideSetup, "Wide 400", new("camera.b"), Optics, new("focuser.b"), null, null, null, new("mount.1"), new("guider.1")));
        foreach (var rig in new[] { MainSetup, WideSetup })
        {
            host.AddSimulatedFocusModel(rig, new SimulatedFocusModel(2600));
        }

        host.ConfigurePlateSolver(new Solver(host));
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        clock.SetHourAngle(-10);
        return (host, clock, mount);
    }

    private static SessionDefinition TheNight(SkyClock clock)
    {
        var quickDither = new DitherSettings(0.6, 0.5, 0.1, 5);
        var quickFocus = new FocusSettings(0.05, 400, 5);
        var ha = Block(1, 0.3, 6, b => b.Dither(2, quickDither).Focus(everyMinutes: 0.005, settings: quickFocus));
        var rgb = Block(null, 0.15, 12, b => b.Focus(atStart: true, settings: quickFocus));
        var target = new SessionTarget(
            Guid.NewGuid(), "M42", clock.TargetRightAscensionHours, 41.3, null, true,
            [new SlewAndCenterAction(Guid.NewGuid()), new StartGuidingAction(Guid.NewGuid())],
            [Lane(MainSetup, ha with { Name = "Ha" }), Lane(WideSetup, rgb with { Name = "RGB" })],
            [new DurationCondition(TimeSpan.FromHours(2))]);
        var flip = new MeridianFlipSettings { Enabled = true, SolveExposureSeconds = 0.05, MaxCenteringAttempts = 3, VerifyRotationAfterFlip = false };
        return new SessionDefinition(new SessionAutomation(flip), [new WaitAction(Guid.NewGuid(), 0.2)], [target], [new StopGuidingAction(Guid.NewGuid())]);
    }

    private static IEnumerable<BuiltStep> Flatten(BuiltStep step) => new[] { step }.Concat((step.Children ?? []).SelectMany(Flatten));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheNight_RunsToTheEnd_BothSetupsExposeTogether_OneDitherForBoth_OneFlipForTheMount_AndTheEndRuns(bool autofocusHoldsTheMount)
    {
        var (host, clock, mount) = await CreateAsync();
        var compiled = SessionCompiler.Compile(TheNight(clock), new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry));
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
        var context = new SequenceDraftContext(
            host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, null,
            Time: clock, Site: () => Site, MeridianPollInterval: Poll, AutofocusHoldsMount: autofocusHoldsTheMount);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        var groups = built.Steps.SelectMany(Flatten).Select(b => b.Step).OfType<MeridianGateStep>().Select(g => g.Group).Distinct().ToList();
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var run = new Observation();
        run.Attach(runner);
        var focus = new FocusWatch(host);

        var task = runner.RunAsync(built.Sequence);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (run.Count("Exposure") < 3 && !task.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the first frames.");
            await Task.Delay(5);
        }

        clock.SetHourAngle(3); // the flip is due while the setups image
        await task.WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(18, run.Count("Exposure")); // 6 of Main, 12 of Wide: the target completes
        Assert.Equal(2, run.MostExposuresAtOnce); // both setups exposed at the same time
        Assert.True(run.Count("Dither") >= 3, "Main dithers every 2 of its 6 frames: " + string.Join(", ", run.Completed.Select(c => c.Key + "=" + c.Value)));
        Assert.False(run.ExposedWhileDithering); // one dither for both: nobody exposed while it ran
        Assert.Equal(2, run.Count("Start guiding")); // the guider is shared: started once for both setups, and once again by the flip after it moved the mount
        Assert.Equal(1, run.Count("Stop guiding for the flip")); // stopped once, for the one flip of the one mount
        Assert.Equal(1, run.Count("Stop guiding")); // and by the end of the session, once
        Assert.True(run.Count("Autofocus") >= 2, "Main on its interval and Wide at its start both focus.");
        var group = Assert.Single(groups); // one flip for the one mount the two setups share
        Assert.Equal(MeridianFlipState.Completed, group.State);
        Assert.Equal(0, mount.SyncCount);
        if (autofocusHoldsTheMount)
        {
            Assert.False(focus.FocusedWhileTheOtherExposed, "with the mount held still, nobody exposes while a setup focuses");
        }
    }
}

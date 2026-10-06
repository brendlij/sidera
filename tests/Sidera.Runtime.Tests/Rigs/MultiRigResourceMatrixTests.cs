using System.Diagnostics;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>
/// What two rigs may do at the same time, decided by the devices they name: rigs on the same mount (guider, rotator) take turns with it, rigs on different ones run side by side, and a rig never
/// holds more than it uses. Every case runs the real actions of the rigs through the sequence runner and looks at when each one was really running.
/// </summary>
public sealed class MultiRigResourceMatrixTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Work = TimeSpan.FromMilliseconds(250);
    private static readonly PlateSolveDefaults Defaults = new();

    private sealed class Probe
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        public List<(string Name, long Start, long End)> Spans { get; } = [];

        public (long Start, long End) Enter() => (_clock.ElapsedTicks, 0);

        public void Record(string name, long start)
        {
            lock (_gate)
            {
                Spans.Add((name, start, _clock.ElapsedTicks));
            }
        }

        /// <summary>Any span called <paramref name="a"/> that ran at the same time as any span called <paramref name="b"/> .</summary>
        public bool Overlapped(string a, string b)
        {
            lock (_gate)
            {
                return Spans.Any(x => x.Name == a && Spans.Any(y => y.Name == b && x.Start < y.End && y.Start < x.End));
            }
        }

        public int CountOf(string name)
        {
            lock (_gate)
            {
                return Spans.Count(s => s.Name == name);
            }
        }

        // The spans in which a mount was really slewing (from the motion events of the host): what two operations on one mount cannot do at once, and two operations on two mounts can.
        public void Watch(SideraRuntimeHost host)
        {
            var open = new Dictionary<DeviceId, long>();
            host.EventBus.Subscribe<MountMotionStateChanged>((e, _) =>
            {
                lock (_gate)
                {
                    if (e.NewState == MountMotionState.Slewing)
                    {
                        open[e.DeviceId] = _clock.ElapsedTicks;
                    }
                    else if (e.PreviousState == MountMotionState.Slewing && open.Remove(e.DeviceId, out var start))
                    {
                        Spans.Add(($"slew {e.DeviceId.Value}", start, _clock.ElapsedTicks));
                    }
                }

                return Task.CompletedTask;
            });
        }
    }

    // Runs the real step and records when it really ran (after the runner got its resources, for the steps that it leases).
    private sealed class SpyStep(ISequenceStep inner, string name, Probe probe) : ISequenceStep
    {
        public string Name => name;

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var (start, _) = probe.Enter();
            try
            {
                return await inner.ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                probe.Record(name, start);
            }
        }
    }

    private sealed class LeasedSpyStep(IResourceAwareSequenceStep inner, string name, Probe probe) : IResourceAwareSequenceStep
    {
        public string Name => name;

        public IReadOnlyCollection<ResourceId> RequiredResources => inner.RequiredResources;

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var (start, _) = probe.Enter();
            try
            {
                return await inner.ExecuteAsync(context, cancellationToken);
            }
            finally
            {
                probe.Record(name, start);
            }
        }
    }

    private static ISequenceStep Spy(ISequenceStep step, string name, Probe probe) =>
        step is IResourceAwareSequenceStep aware ? new LeasedSpyStep(aware, name, probe) : new SpyStep(step, name, probe);

    private sealed class CenteredSolver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        // The camera of the rig points where its mount points; the rotator is the first one of the host that is a simulated one.
        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var mount = host.DeviceRegistry.GetAll().OfType<IMount>().First();
            return Task.FromResult(new PlateSolveResult { Success = true, Center = request.ApproximateCenter ?? mount.Coordinates, RotationDegrees = 0, Backend = Name });
        }
    }

    private sealed record Env(SideraRuntimeHost Host, Rig A, Rig B, SimulatedMount Mount1, SimulatedMount Mount2, SimulatedGuider Guider1, SimulatedGuider Guider2) : IAsyncDisposable
    {
        public Probe Probe { get; } = new();

        public SequenceRunner Runner => new(Host.ResourceManager);

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    // Two rigs A and B, each with its own camera, focuser and rotator; the mounts and guiders are given as the matrix says.
    private static async Task<Env> CreateAsync(bool sameMount, bool sameGuider)
    {
        var host = new SideraRuntimeHost();
        var slew = Work;
        var m1 = host.AddSimulatedMount(new("mount.1"), "Mount 1", slew);
        var m2 = host.AddSimulatedMount(new("mount.2"), "Mount 2", slew);
        var g1 = host.AddSimulatedGuider(new("guider.1"), "Guider 1", Work, Work, Work);
        var g2 = host.AddSimulatedGuider(new("guider.2"), "Guider 2", Work, Work, Work);
        host.AddSimulatedCamera(new("camera.a"), "Camera A", 1);
        host.AddSimulatedCamera(new("camera.b"), "Camera B", 2);
        host.AddSimulatedFocuser(new("focuser.a"), "Focuser A", 19500, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFocuser(new("focuser.b"), "Focuser B", 5500, maxPosition: 12000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedRotator(new("rotator.a"), "Rotator A", 0, 3600);
        host.AddSimulatedRotator(new("rotator.b"), "Rotator B", 0, 3600);
        var optics = new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176);
        var model = new RotatorSkyModel(0);
        var a = new Rig(new("rig.a"), "Rig A", new("camera.a"), optics, new("focuser.a"), null, new("rotator.a"), model, m1.Id, g1.Id);
        var b = new Rig(new("rig.b"), "Rig B", new("camera.b"), optics, new("focuser.b"), null, new("rotator.b"), model, sameMount ? m1.Id : m2.Id, sameGuider ? g1.Id : g2.Id);
        host.AddRig(a);
        host.AddRig(b);
        host.AddSimulatedFocusModel(a.Id, new SimulatedFocusModel(20000));
        host.AddSimulatedFocusModel(b.Id, new SimulatedFocusModel(6000));
        host.ConfigurePlateSolver(new CenteredSolver(host));
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var env = new Env(host, a, b, m1, m2, g1, g2);
        env.Probe.Watch(host);
        return env;
    }

    private static CameraExposureAction Exposure(Env e, Rig rig) =>
        new(e.Host.DeviceRegistry, rig.CameraId, Work, AcquisitionIntent.Default, e.Host.AcquisitionDefaults);

    private static SlewAndCenterAction Center(Env e, Rig rig) =>
        new(e.Host.PlateSolving!, rig, rig.MountId!.Value, new CelestialCoordinates(5, 30), 5, 3, TimeSpan.FromMilliseconds(20), Defaults);

    private static CenterAndRotateAction CenterAndRotate(Env e, Rig rig) =>
        new(e.Host.Rotation!, rig, rig.MountId!.Value, new CelestialCoordinates(5, 30), 0, 5, 0.5, 3, 3, 3, TimeSpan.FromMilliseconds(20), Defaults);

    private static async Task RunParallelAsync(Env e, params ISequenceStep[] steps) =>
        await e.Runner.RunAsync(new Sequence("matrix", [new ParallelStep("both", steps)]), CancellationToken.None).WaitAsync(Bound);

    // ---- Mount

    [Fact]
    public async Task TwoRigs_OnTheSameMount_SlewOneAfterTheOther()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var probe = new Probe();

        await RunParallelAsync(e,
            Spy(new SlewAction(e.Host.DeviceRegistry, e.A.MountId!.Value, new CelestialCoordinates(1, 10)), "A slews", probe),
            Spy(new SlewAction(e.Host.DeviceRegistry, e.B.MountId!.Value, new CelestialCoordinates(2, 20)), "B slews", probe));

        Assert.False(probe.Overlapped("A slews", "B slews"));
    }

    [Fact]
    public async Task TwoRigs_OnDifferentMounts_SlewAtTheSameTime()
    {
        await using var e = await CreateAsync(sameMount: false, sameGuider: false);
        var probe = new Probe();

        await RunParallelAsync(e,
            Spy(new SlewAction(e.Host.DeviceRegistry, e.A.MountId!.Value, new CelestialCoordinates(1, 10)), "A slews", probe),
            Spy(new SlewAction(e.Host.DeviceRegistry, e.B.MountId!.Value, new CelestialCoordinates(2, 20)), "B slews", probe));

        Assert.True(probe.Overlapped("A slews", "B slews"));
    }

    [Fact]
    public async Task SlewAndCenter_OfTwoRigs_OnTheSameMount_TakeTurns_AndOnDifferentMounts_DoNot()
    {
        await using var same = await CreateAsync(sameMount: true, sameGuider: true);
        await RunParallelAsync(same, Center(same, same.A), Center(same, same.B));
        Assert.Equal(2, same.Probe.CountOf("slew mount.1")); // both centered, one after the other: a mount that is told to slew while it slews refuses
        Assert.False(same.Host.PlateSolving!.IsSolving);

        await using var apart = await CreateAsync(sameMount: false, sameGuider: false);
        await RunParallelAsync(apart, Center(apart, apart.A), Center(apart, apart.B));
        Assert.True(apart.Probe.Overlapped("slew mount.1", "slew mount.2"));
        Assert.False(apart.Host.PlateSolving!.IsSolving);
    }

    // ---- Cameras

    [Fact]
    public async Task TwoRigs_WithTheirOwnCameras_ExposeAtTheSameTime_EvenOnOneMount()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var probe = new Probe();

        await RunParallelAsync(e, Spy(Exposure(e, e.A), "A exposes", probe), Spy(Exposure(e, e.B), "B exposes", probe));

        Assert.True(probe.Overlapped("A exposes", "B exposes"));
    }

    [Fact]
    public async Task OneCamera_IsNeverUsedByTwoStepsAtOnce()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var probe = new Probe();

        await RunParallelAsync(e, Spy(Exposure(e, e.A), "first", probe), Spy(Exposure(e, e.A), "second", probe));

        Assert.False(probe.Overlapped("first", "second"));
    }

    // ---- Autofocus

    [Fact]
    public async Task AutofocusOfOneRig_DoesNotBlockTheCameraOfTheOther()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var probe = new Probe();
        var autofocus = AutofocusAction.ForRig(e.Host.DeviceRegistry, e.A, new AutofocusOptions(TimeSpan.FromMilliseconds(40), 300, 7), e.Host.FocusMetrics, e.Host.EventBus);

        await RunParallelAsync(e, Spy(autofocus, "A focuses", probe), Spy(Exposure(e, e.B), "B exposes", probe));

        Assert.True(probe.Overlapped("A focuses", "B exposes"));
    }

    [Fact]
    public async Task AutofocusHoldsTheCameraAndFocuserOfItsRig_AndNothingElse()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var autofocus = AutofocusAction.ForRig(e.Host.DeviceRegistry, e.A, new AutofocusOptions(TimeSpan.FromMilliseconds(40), 300, 7), e.Host.FocusMetrics, e.Host.EventBus);

        Assert.Equal(
            new[] { "device:camera.a", "device:focuser.a" }.Order(),
            StepResources.Of(autofocus).Select(r => r.Value).Order());
    }

    // ---- Rotation and centering

    [Fact]
    public async Task CenterAndRotate_OfOneRig_BlocksOnlyWhatItReallyShares()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var probe = e.Probe;

        // The camera of the other rig is its own, and the other rig has its own rotator: both go on while this rig centers and rotates. Only the mount is shared.
        await RunParallelAsync(e,
            Center(e, e.A),
            Spy(Exposure(e, e.B), "B exposes", probe),
            new RotateToAngleAction(e.Host.Rotation!, e.B, 40));

        Assert.True(probe.Overlapped("slew mount.1", "B exposes"));
        Assert.Equal(40, ((IRotator)e.Host.DeviceRegistry.GetAll().Single(d => d.Id == new DeviceId("rotator.b"))).Position, 3);
        Assert.False(e.Host.Rotation!.IsBusy);
    }

    [Fact]
    public async Task CenterAndRotate_OfTwoRigs_OnOneMount_TakeTurns_OnTwoMounts_DoNot()
    {
        await using var same = await CreateAsync(sameMount: true, sameGuider: true);
        await RunParallelAsync(same, CenterAndRotate(same, same.A), CenterAndRotate(same, same.B));
        Assert.Equal(2, same.Probe.CountOf("slew mount.1"));

        await using var apart = await CreateAsync(sameMount: false, sameGuider: false);
        await RunParallelAsync(apart, CenterAndRotate(apart, apart.A), CenterAndRotate(apart, apart.B));
        Assert.True(apart.Probe.Overlapped("slew mount.1", "slew mount.2"));
        Assert.False(apart.Host.Rotation!.IsBusy);
    }

    [Fact]
    public async Task CenterAndRotate_AndASlewOfTheSharedMount_TakeTurns()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);

        await RunParallelAsync(e,
            CenterAndRotate(e, e.A),
            new SlewAction(e.Host.DeviceRegistry, e.B.MountId!.Value, new CelestialCoordinates(3, 15)));

        Assert.Equal(2, e.Probe.CountOf("slew mount.1")); // the second slew did not meet a mount that was slewing
    }

    [Fact]
    public async Task ARotation_OfOneRig_DoesNotHoldTheCameraOfAnotherRig_ButHoldsEveryCameraOnItsOwnRotator()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);

        var held = e.Host.Rotation!.ResourcesOfRotation(e.A).Select(r => r.Value).Order().ToList();

        Assert.Equal(["device:camera.a", "device:rotator.a"], held);
    }

    // ---- Guiders

    [Fact]
    public async Task TwoRigs_WithTheirOwnGuiders_StartGuidingAtTheSameTime()
    {
        await using var e = await CreateAsync(sameMount: false, sameGuider: false);
        var probe = new Probe();

        await RunParallelAsync(e,
            Spy(new StartGuidingAction(e.Host.DeviceRegistry, e.A.GuiderId!.Value), "A guides", probe),
            Spy(new StartGuidingAction(e.Host.DeviceRegistry, e.B.GuiderId!.Value), "B guides", probe));

        Assert.True(probe.Overlapped("A guides", "B guides"));
    }

    [Fact]
    public async Task TwoRigs_OnOneGuider_NameTheSameResource_SoTheirGuiderOperationsTakeTurns()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var a = new StopGuidingAction(e.Host.DeviceRegistry, e.A.GuiderId!.Value);
        var b = new StopGuidingAction(e.Host.DeviceRegistry, e.B.GuiderId!.Value);

        Assert.Equal(StepResources.Of(a), StepResources.Of(b));
        Assert.Equal(["device:guider.1"], StepResources.Of(a).Select(r => r.Value));

        // The same resource in two parallel steps: the runner lets them take turns (a stop of a guider that is not guiding does nothing, so both finish).
        var probe = new Probe();
        await RunParallelAsync(e, Spy(a, "first", probe), Spy(b, "second", probe));
        Assert.False(probe.Overlapped("first", "second"));
    }

    [Fact]
    public async Task EveryRigResolvesToItsOwnDevices_NotToAGlobalOne()
    {
        await using var e = await CreateAsync(sameMount: false, sameGuider: false);

        // Centering moves the mount: it holds the mount, the stability of the mount (no exposure on it meanwhile) and the camera, all of its own rig.
        Assert.Equal(["device:camera.a", "device:mount.1", "mountstability:mount.1"], StepResources.Of(Center(e, e.A)).Select(r => r.Value).Order());
        Assert.Equal(["device:camera.b", "device:mount.2", "mountstability:mount.2"], StepResources.Of(Center(e, e.B)).Select(r => r.Value).Order());
        Assert.Equal(
            ["device:camera.b", "device:mount.2", "device:rotator.b", "mountstability:mount.2"],
            StepResources.Of(CenterAndRotate(e, e.B)).Select(r => r.Value).Order());
        Assert.DoesNotContain("device:mount.1", StepResources.Of(CenterAndRotate(e, e.B)).Select(r => r.Value));
        Assert.DoesNotContain("mountstability:mount.1", StepResources.Of(CenterAndRotate(e, e.B)).Select(r => r.Value));
    }

    // ---- No self-deadlock

    [Fact]
    public async Task EveryServiceStep_FinishesWhenItsResourcesAreFree_NoStepWaitsForItsOwnLease()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        var steps = new ISequenceStep[]
        {
            Center(e, e.A),
            CenterAndRotate(e, e.A),
            new RotateToAngleAction(e.Host.Rotation!, e.A, 33),
            new RotateAndVerifyAction(e.Host.Rotation!, e.A, e.A.MountId, 0, 0.5, 3, TimeSpan.FromMilliseconds(20), Defaults),
            new PlateSolveAction(e.Host.PlateSolving!, e.A, e.A.MountId, TimeSpan.FromMilliseconds(20), Defaults),
            new SyncMountToSolvedPositionAction(e.Host.PlateSolving!, e.A.MountId!.Value),
            AutofocusAction.ForRig(e.Host.DeviceRegistry, e.A, new AutofocusOptions(TimeSpan.FromMilliseconds(40), 300, 7), e.Host.FocusMetrics, e.Host.EventBus),
            Exposure(e, e.A),
        };

        await e.Runner.RunAsync(new Sequence("one after another", steps), CancellationToken.None).WaitAsync(Bound);

        using var lease = await e.Host.ResourceManager.AcquireAsync(
            [.. e.A.Devices().Select(d => ResourceId.ForDevice(d.Device))], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(e.Host.PlateSolving!.IsSolving);
        Assert.False(e.Host.Rotation!.IsBusy);
    }

    [Fact]
    public async Task TwoRigs_AllOfTheirServiceSteps_InParallel_FinishWithoutDeadlock_OnOneMountAndOnTwo()
    {
        foreach (var sameMount in new[] { true, false })
        {
            await using var e = await CreateAsync(sameMount, sameGuider: sameMount);
            var branchA = new Sequence("A", [Center(e, e.A), new RotateAndVerifyAction(e.Host.Rotation!, e.A, e.A.MountId, 0, 0.5, 3, TimeSpan.FromMilliseconds(20), Defaults), CenterAndRotate(e, e.A), Exposure(e, e.A)]);
            var branchB = new Sequence("B", [CenterAndRotate(e, e.B), Exposure(e, e.B), Center(e, e.B), new RotateToAngleAction(e.Host.Rotation!, e.B, 10)]);
            var group = new SequenceGroup("a", branchA.Steps);
            var other = new SequenceGroup("b", branchB.Steps);

            await e.Runner.RunAsync(new Sequence("both", [new ParallelStep("rigs", [group, other])]), CancellationToken.None).WaitAsync(Bound);

            using var lease = await e.Host.ResourceManager.AcquireAsync(
                [.. e.Host.DeviceRegistry.GetAll().Select(d => ResourceId.ForDevice(d.Id))], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(e.Host.PlateSolving!.IsSolving);
            Assert.False(e.Host.Rotation!.IsBusy);
        }
    }

    [Fact]
    public async Task ACancelledCenterAndRotate_ReleasesWhatItHeld_SoTheOtherRigGoesOn()
    {
        await using var e = await CreateAsync(sameMount: true, sameGuider: true);
        using var cts = new CancellationTokenSource();
        var running = e.Runner.RunAsync(new Sequence("a", [CenterAndRotate(e, e.A)]), cts.Token);
        await Task.Delay(60);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Bound));

        await e.Runner.RunAsync(new Sequence("b", [Center(e, e.B)]), CancellationToken.None).WaitAsync(Bound);

        Assert.True(e.Probe.CountOf("slew mount.1") >= 1);
    }
}

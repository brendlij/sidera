using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Safety;

/// <summary>
/// What comes before real equipment moves: a question that the user answers (cancel moves nothing, continue goes on), once for each mount and rotator for a run of Sidera, never for a simulator, and
/// never an environment variable.
/// </summary>
public sealed class HardwareSafetyTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<MainViewModel> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var vm in _apps)
        {
            vm.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    // A mount that is not a simulator: it slews by setting where it points, and counts.
    private sealed class RealMount(string id) : IMount
    {
        private int _slews;
        public DeviceId Id { get; } = new(id);
        public string Name => "EQ6 (real)";
        public DeviceType Type => DeviceType.Mount;
        public DeviceConnectionState ConnectionState { get; private set; } = DeviceConnectionState.Disconnected;
        public MountMotionState MotionState => MountMotionState.Idle;
        public CelestialCoordinates Coordinates { get; private set; } = new(18.6, 38.78);
        public int Slews => Volatile.Read(ref _slews);

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectionState = DeviceConnectionState.Connected;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectionState = DeviceConnectionState.Disconnected;
            return Task.CompletedTask;
        }

        public Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _slews);
            Coordinates = target;
            return Task.CompletedTask;
        }
    }

    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlateSolveResult { Success = true, Center = host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates, RotationDegrees = 0, Backend = Name });
    }

    private async Task<(SideraRuntimeHost Host, MainViewModel Vm, IMount Mount)> CreateAsync(bool real)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 1);
        IMount mount = real ? new RealMount("mount.real") : host.AddSimulatedMount(new("mount.sim"), "Sim Mount", TimeSpan.FromMilliseconds(1));
        if (real)
        {
            host.AddDevice(mount);
        }

        host.AddRig(new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76), mountId: mount.Id));
        host.ConfigurePlateSolver(new Solver(host));
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add(vm);
        vm.PlateSolve.RefreshEquipment();
        return (host, vm, mount);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }


    [Fact]
    public async Task ARealMount_AsksBeforeSlewAndCenter_AndCancelMovesNothing()
    {
        var (_, vm, mount) = await CreateAsync(real: true);

        var run = vm.PlateSolve.SlewAndCenterCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Safety.IsPending, "the question");

        Assert.Equal("This operation will move the mount (EQ6 (real)).", vm.Safety.Message);
        Assert.Equal("Slew & Center", vm.Safety.Title);
        Assert.Equal(0, ((RealMount)mount).Slews); // nothing moved while the question is open
        vm.Safety.CancelCommand.Execute(null);
        await run;

        Assert.False(vm.Safety.IsPending);
        Assert.Equal(0, ((RealMount)mount).Slews);
        Assert.Contains("Cancelled", vm.PlateSolve.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, vm.Safety.AnsweredCount); // a no is not remembered
    }

    [Fact]
    public async Task Continuing_LetsTheOperationGoOn_WithoutAnyEnvironmentVariable_AndTheMountIsNotAskedAgain()
    {
        var (_, vm, mount) = await CreateAsync(real: true);
        Assert.Null(Sidera.Core.SideraEnvironment.Get("SIDERA_ASTROMETRY_CENTERING_OK"));
        Assert.Null(Sidera.Core.SideraEnvironment.Get("SIDERA_ASTAP_CAMERA_OK"));

        var first = vm.PlateSolve.SlewAndCenterCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Safety.IsPending, "the question");
        vm.Safety.ContinueCommand.Execute(null);
        await first;

        Assert.True(((RealMount)mount).Slews >= 1);
        Assert.StartsWith("Centered", vm.PlateSolve.StatusText, StringComparison.Ordinal);
        Assert.Equal(1, vm.Safety.AnsweredCount);

        await vm.PlateSolve.SlewAndCenterCommand.ExecuteAsync(null); // answered for this mount: no question
        Assert.False(vm.Safety.IsPending);

        vm.Safety.Forget();
        var third = vm.PlateSolve.SlewAndCenterCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Safety.IsPending, "the question again");
        vm.Safety.CancelCommand.Execute(null);
        await third;
    }

    [Fact]
    public async Task ASimulator_NeverAsks()
    {
        var (_, vm, _) = await CreateAsync(real: false);

        await vm.PlateSolve.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.False(vm.Safety.IsPending);
        Assert.StartsWith("Centered", vm.PlateSolve.StatusText, StringComparison.Ordinal);
        Assert.Equal(0, vm.Safety.AnsweredCount);
    }

    [Fact]
    public async Task ASequenceThatSlewsARealMount_AsksBeforeItRuns_AndCancelMeansItDoesNotRun()
    {
        var (host, vm, mount) = await CreateAsync(real: true);
        vm.SequenceDraft.ReplaceSteps([new SlewStepDraft(Guid.NewGuid(), mount.Id, 5.5, 22)]);
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Safety.IsPending, "the question");
        Assert.Equal("Run the sequence", vm.Safety.Title);
        vm.Safety.CancelCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Idle, vm.Sequencer.State);
        Assert.Equal(0, ((RealMount)mount).Slews);
        Assert.Contains("nothing was moved", vm.Sequencer.ReadinessHint ?? string.Empty, StringComparison.Ordinal);

        var again = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitAsync(() => vm.Safety.IsPending, "the question again");
        vm.Safety.ContinueCommand.Execute(null);
        await again;
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Completed or SequenceState.Failed, "the run to end");

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.Equal(1, ((RealMount)mount).Slews);
        _ = host;
    }

    [Fact]
    public async Task WhatASequenceMoves_IsTheMountsThatSlewAndTheRotatorsThatTurn_NotGuiding()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 1);
        host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedRotator(new("rotator"), "Rotator");
        host.AddSimulatedGuider(new("guider"), "Guider");
        host.AddRig(new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500), null, null, new("rotator"), new RotatorSkyModel(0), new("mount"), new("guider")));
        var rig = new RigId("rig");
        var steps = new SequenceStepDraft[]
        {
            new StartGuidingStepDraft(Guid.NewGuid(), new("guider")),
            new SlewAndCenterStepDraft(Guid.NewGuid(), null, rig, 5, 30, 60, 5, 5),
            new RotateAndVerifyStepDraft(Guid.NewGuid(), rig, 10, 0.5, 3, 5),
            new RepeatStepDraft(Guid.NewGuid(), 2, [new SlewStepDraft(Guid.NewGuid(), new("mount"), 1, 1)]),
            new DelayStepDraft(Guid.NewGuid(), 1),
        };

        var moving = SequenceDraftBuilder.MovingEquipment(steps, new SequenceDraftContext(host.RigRegistry));

        Assert.Equal([(false, new DeviceId("mount")), (true, new DeviceId("rotator"))], moving);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task OnlyRealEquipmentGetsANotice()
    {
        var (host, _, _) = await CreateAsync(real: true);
        host.AddSimulatedMount(new("mount.sim"), "Sim", TimeSpan.FromMilliseconds(1));

        Assert.NotNull(HardwareSafetyViewModel.NoticeFor(host.DeviceRegistry, MovingEquipment.Mount, new("mount.real")));
        Assert.Null(HardwareSafetyViewModel.NoticeFor(host.DeviceRegistry, MovingEquipment.Mount, new("mount.sim")));
        Assert.Null(HardwareSafetyViewModel.NoticeFor(host.DeviceRegistry, MovingEquipment.Mount, new("camera"))); // the wrong kind
        Assert.Null(HardwareSafetyViewModel.NoticeFor(host.DeviceRegistry, MovingEquipment.Mount, new("nothing")));
    }
}

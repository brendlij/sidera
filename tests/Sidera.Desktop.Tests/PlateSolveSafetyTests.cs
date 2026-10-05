using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests;

/// <summary>Slew &amp; Center on the plate solve page never starts from a target of 0 h / 0 degrees: it is empty, or the position of the mount, and a typed target is kept.</summary>
public sealed class PlateSolveSafetyTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
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

    private async Task<(SideraRuntimeHost Host, PlateSolveViewModel Vm, IMount Mount)> CreateAsync(bool connectMount)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 1);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        host.AddRig(new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76), mountId: mount.Id));
        host.ConfigurePlateSolver(new Solver(host));
        await camera.ConnectAsync();
        if (connectMount)
        {
            await mount.ConnectAsync();
            await mount.SlewToAsync(new CelestialCoordinates(18.6, 38.78));
        }

        var vm = new PlateSolveViewModel(host, new ImagingViewModel(null, a => a()), null, a => a());
        return (host, vm, mount);
    }

    [Fact]
    public async Task TheTarget_IsEmpty_NotZeroZero_WithoutAConnectedMount()
    {
        var (_, vm, _) = await CreateAsync(connectMount: false);

        Assert.Equal(string.Empty, vm.RaText);
        Assert.Equal(string.Empty, vm.DecText);
        Assert.Null(vm.Target);
        Assert.False(vm.SlewAndCenterCommand.CanExecute(null));
        Assert.Contains("Connect the mount", vm.CenterDisabledText);
    }

    [Fact]
    public async Task TheTarget_StartsAsThePositionOfTheConnectedMount()
    {
        var (_, vm, _) = await CreateAsync(connectMount: true);

        Assert.Equal("18.6", vm.RaText);
        Assert.Equal("38.78", vm.DecText);
        Assert.Equal(new CelestialCoordinates(18.6, 38.78), vm.Target);
        Assert.True(vm.SlewAndCenterCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.CenterDisabledText);
    }

    [Fact]
    public async Task ATargetThatWasTyped_IsKept_WhenTheMountMoves()
    {
        var (_, vm, mount) = await CreateAsync(connectMount: true);
        vm.RaText = "5.5";
        vm.DecText = "-5.4";

        await mount.SlewToAsync(new CelestialCoordinates(2, 2));
        vm.RefreshEquipment();

        Assert.Equal("5.5", vm.RaText);
        Assert.Equal("-5.4", vm.DecText);
    }

    [Fact]
    public async Task AnUntouchedTarget_FollowsTheMount_UntilThePersonTypesSomething()
    {
        var (_, vm, mount) = await CreateAsync(connectMount: true);

        await mount.SlewToAsync(new CelestialCoordinates(2, 2));
        vm.RefreshEquipment();
        Assert.Equal("2", vm.RaText);

        vm.RaText = "3";
        await mount.SlewToAsync(new CelestialCoordinates(9, 9));
        vm.RefreshEquipment();
        Assert.Equal("3", vm.RaText);
    }

    [Theory]
    [InlineData("", "10", "Enter the target")]
    [InlineData("5", "", "Enter the target")]
    [InlineData("abc", "10", "not a position")]
    [InlineData("24", "10", "not a position")]
    [InlineData("-1", "10", "not a position")]
    [InlineData("5", "91", "not a position")]
    [InlineData("5", "NaN", "not a position")]
    public async Task ATargetThatIsMissingOrNotAPosition_DisablesSlewAndCenter_AndSaysWhy(string ra, string dec, string reason)
    {
        var (_, vm, _) = await CreateAsync(connectMount: true);
        vm.RaText = ra;
        vm.DecText = dec;

        Assert.False(vm.SlewAndCenterCommand.CanExecute(null));
        Assert.Contains(reason, vm.CenterDisabledText);
        Assert.Null(vm.Target);
    }

    [Fact]
    public async Task ATypedTarget_IsCentered_AndTheMountIsNeverSynchronized()
    {
        var (host, vm, mount) = await CreateAsync(connectMount: true);
        vm.RaText = "5.5";
        vm.DecText = "22";
        vm.ExposureSeconds = 0.02;

        await vm.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.Equal("Centered", vm.StatusText);
        Assert.True(SkyMath.AngularSeparationDegrees(mount.Coordinates, new CelestialCoordinates(5.5, 22)) < 0.01);
        Assert.Equal(0, ((Sidera.Runtime.Devices.SimulatedMount)mount).SyncCount);
        Assert.NotNull(host);
    }

    [Fact]
    public async Task WithoutATarget_NothingIsSlewed_EvenIfTheCommandIsInvoked()
    {
        var (_, vm, mount) = await CreateAsync(connectMount: false);
        await mount.ConnectAsync();
        var before = mount.Coordinates;

        await vm.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.Equal(before, mount.Coordinates);
    }
}

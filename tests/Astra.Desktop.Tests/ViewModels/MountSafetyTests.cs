using Astra.Ascom;
using Astra.Ascom.Mounts;
using Astra.Ascom.Tests;
using Astra.Core.Devices;
using Astra.Core.Mounts;
using Astra.Desktop.ViewModels;
using Astra.Runtime;
using Astra.Runtime.Devices;

namespace Astra.Desktop.Tests.ViewModels;

/// <summary>
/// What protects a real mount from the Mount page: a slew target that starts where the mount points (never at 0 / 0), validated
/// input, a question before a large slew, and a jog pad that cannot leave an axis moving.
/// </summary>
public sealed class MountSafetyTests : IAsyncLifetime
{
    private static readonly DeviceId MountId = new("mount.sim");

    private readonly List<AstraRuntimeHost> _hosts = [];
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<(MountViewModel Vm, AstraRuntimeHost Host, SimulatedMount Mount)> SimulatedAt(
        CelestialCoordinates? at, bool connect = true)
    {
        var host = new AstraRuntimeHost();
        _hosts.Add(host);
        var mount = host.AddSimulatedMount(MountId, "Sim Mount", TimeSpan.FromMilliseconds(20));
        if (connect)
        {
            await host.DeviceOperations.ConnectAsync(MountId);
            if (at is not null)
            {
                await host.DeviceOperations.SlewToAsync(MountId, at);
            }
        }

        var vm = new MountViewModel(mount, host, a => a(), new SessionActivity());
        _disposables.Add(vm);
        return (vm, host, mount);
    }

    // ---- The slew target

    [Fact]
    public async Task TheTarget_StartsWhereTheMountPoints()
    {
        var (vm, _, _) = await SimulatedAt(new CelestialCoordinates(5.5, 20));

        Assert.True(vm.IsPositionKnown);
        Assert.Equal("5.5", vm.RightAscensionInput);
        Assert.Equal("20", vm.DeclinationInput);
        Assert.True(vm.SlewCommand.CanExecute(null));
        Assert.False(vm.HasSlewProblem);
    }

    [Fact]
    public async Task AMountThatIsNotConnected_HasNoTarget_NotZeroZero_AndCannotBeSlewed()
    {
        var (vm, _, _) = await SimulatedAt(null, connect: false);

        Assert.False(vm.IsPositionKnown);
        Assert.Equal(string.Empty, vm.RightAscensionInput);
        Assert.Equal(string.Empty, vm.DeclinationInput);
        Assert.False(vm.SlewCommand.CanExecute(null));
        Assert.True(vm.HasSlewProblem);
        Assert.Contains("not known", vm.SlewProblemText);
    }

    [Fact]
    public async Task TheTarget_IsFilledWhenTheMountIsConnectedLater_NotBefore()
    {
        var (vm, host, _) = await SimulatedAt(null, connect: false);
        Assert.Equal(string.Empty, vm.RightAscensionInput);

        await host.DeviceOperations.ConnectAsync(MountId);

        Assert.True(vm.IsPositionKnown);
        Assert.Equal("0", vm.RightAscensionInput); // the simulator really points at 0 / 0: it said so
        Assert.Equal("0", vm.DeclinationInput);
    }

    [Fact]
    public async Task ATypedTarget_IsNotOverwritten_NotByARefreshAndNotByAReconnect()
    {
        var (vm, host, _) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = "7";
        vm.DeclinationInput = "21";

        vm.Refresh();
        await host.DeviceOperations.DisconnectAsync(MountId);
        await host.DeviceOperations.ConnectAsync(MountId);

        Assert.Equal("7", vm.RightAscensionInput);
        Assert.Equal("21", vm.DeclinationInput);
    }

    [Fact]
    public async Task ATargetThatWasNotEdited_FollowsAReconnect_AndIsEmptyWhileDisconnected()
    {
        var (vm, host, _) = await SimulatedAt(new CelestialCoordinates(5.5, 20));

        await host.DeviceOperations.DisconnectAsync(MountId);
        Assert.Equal(string.Empty, vm.RightAscensionInput);
        Assert.False(vm.SlewCommand.CanExecute(null));

        await host.DeviceOperations.ConnectAsync(MountId);
        Assert.True(vm.IsPositionKnown);
        Assert.NotEqual(string.Empty, vm.RightAscensionInput);
    }

    // ---- Validation

    [Theory]
    [InlineData("24", "0", "Right ascension")]
    [InlineData("-0.5", "0", "Right ascension")]
    [InlineData("abc", "0", "Right ascension")]
    [InlineData("", "0", "Right ascension")]
    [InlineData("NaN", "0", "Right ascension")]
    [InlineData("Infinity", "0", "Right ascension")]
    [InlineData("5", "90.5", "Declination")]
    [InlineData("5", "-91", "Declination")]
    [InlineData("5", "x", "Declination")]
    [InlineData("5", "NaN", "Declination")]
    [InlineData("5", "-Infinity", "Declination")]
    public async Task AnInvalidTarget_IsRefused_WithTheReason_AndNeverClamped(string ra, string dec, string which)
    {
        var (vm, _, mount) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = ra;
        vm.DeclinationInput = dec;

        Assert.False(vm.SlewCommand.CanExecute(null));
        Assert.True(vm.HasSlewProblem);
        Assert.StartsWith(which, vm.SlewProblemText);
        Assert.Equal(new CelestialCoordinates(5.5, 20), mount.Coordinates);
    }

    [Fact]
    public async Task ACorrectedTarget_ClearsTheProblem()
    {
        var (vm, _, _) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = "99";
        Assert.True(vm.HasSlewProblem);

        vm.RightAscensionInput = "5.6";

        Assert.False(vm.HasSlewProblem);
        Assert.True(vm.SlewCommand.CanExecute(null));
    }

    // ---- The question before a large slew

    [Fact]
    public async Task ASmallSlew_GoesAtOnce()
    {
        var (vm, _, mount) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = "5.51"; // about 0.15 degrees of right ascension at this declination

        await vm.SlewCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmingLargeSlew);
        Assert.Equal(5.51, mount.Coordinates.RightAscensionHours, 3);
    }

    [Fact]
    public async Task ALargeSlew_WaitsForConfirmation_AndSlewsOnlyAfterIt()
    {
        var (vm, _, mount) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = "12";
        vm.DeclinationInput = "-5";

        await vm.SlewCommand.ExecuteAsync(null);

        Assert.True(vm.IsConfirmingLargeSlew);
        Assert.Contains("large slew", vm.LargeSlewText);
        Assert.Equal(new CelestialCoordinates(5.5, 20), mount.Coordinates); // nothing moved yet

        await vm.ConfirmSlewCommand.ExecuteAsync(null);

        Assert.False(vm.IsConfirmingLargeSlew);
        Assert.Equal(12, mount.Coordinates.RightAscensionHours, 3);
    }

    [Fact]
    public async Task CancellingALargeSlew_MovesNothing_AndEditingTheTargetCancelsTheQuestion()
    {
        var (vm, _, mount) = await SimulatedAt(new CelestialCoordinates(5.5, 20));
        vm.RightAscensionInput = "12";
        await vm.SlewCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirmingLargeSlew);

        vm.CancelSlewCommand.Execute(null);
        Assert.False(vm.IsConfirmingLargeSlew);

        await vm.SlewCommand.ExecuteAsync(null);
        Assert.True(vm.IsConfirmingLargeSlew);
        vm.RightAscensionInput = "13"; // another target: the question was about the old one
        Assert.False(vm.IsConfirmingLargeSlew);
        await vm.ConfirmSlewCommand.ExecuteAsync(null);

        Assert.Equal(new CelestialCoordinates(5.5, 20), mount.Coordinates);
    }

    // ---- The jog pad

    private async Task<(MountControlViewModel Panel, FakeMountDriver Driver, AscomMount Mount)> JogPanel()
    {
        var drivers = new FakeDriverFactory(new CallLog())
        {
            ConfigureMount = d =>
            {
                d.MovableAxes.Add(MountAxis.Primary);
                d.MovableAxes.Add(MountAxis.Secondary);
            },
        };
        var host = new AstraRuntimeHost();
        _hosts.Add(host);
        var mount = new AscomMount(new DeviceId("mount.ascom"), "ASCOM Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        host.AddDevice(mount);
        var vm = new MountViewModel(mount, host, a => a(), new SessionActivity());
        _disposables.Add(vm);
        var panel = new MountControlViewModel(vm, mount);
        _disposables.Add(panel);
        await mount.ConnectAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!panel.IsAvailable)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5);
        }

        return (panel, drivers.Mounts[0], mount);
    }

    [Fact]
    public async Task Pressing_MovesTheAxis_AndReleasing_SetsItBackToZero()
    {
        var (panel, driver, _) = await JogPanel();

        await panel.JogStartCommand.ExecuteAsync("E");
        Assert.True(panel.IsJogging);
        Assert.Single(driver.Operations);
        Assert.StartsWith("MoveAxis Primary ", driver.Operations[0]);
        Assert.NotEqual("MoveAxis Primary 0", driver.Operations[0]);

        await panel.JogEndCommand.ExecuteAsync("E");

        Assert.False(panel.IsJogging);
        Assert.Equal("MoveAxis Primary 0", driver.Operations[^1]);
    }

    [Fact]
    public async Task ADiagonal_IsReleasedOnBothAxes()
    {
        var (panel, driver, _) = await JogPanel();

        await panel.JogStartCommand.ExecuteAsync("NE");
        await panel.JogEndCommand.ExecuteAsync("NE");

        Assert.Equal(4, driver.Operations.Count);
        Assert.Contains("MoveAxis Primary 0", driver.Operations.Skip(2));
        Assert.Contains("MoveAxis Secondary 0", driver.Operations.Skip(2));
    }

    [Fact]
    public async Task AReleaseThatComesWhileThePressIsStillBeingSent_DoesNotOvertakeIt()
    {
        var (panel, driver, _) = await JogPanel();

        var press = panel.JogStartCommand.ExecuteAsync("NE");
        var release = panel.JogEndCommand.ExecuteAsync("NE");
        await Task.WhenAll(press, release);

        // Both starts first, then both stops: no axis is set moving after its stop.
        Assert.Equal(4, driver.Operations.Count);
        Assert.DoesNotContain(driver.Operations.Take(2), o => o.EndsWith(" 0"));
        Assert.All(driver.Operations.Skip(2), o => Assert.EndsWith(" 0", o));
        Assert.False(panel.IsJogging);
    }

    [Fact]
    public async Task AReleaseWithoutAPress_SendsNothing()
    {
        var (panel, driver, _) = await JogPanel();

        await panel.JogEndCommand.ExecuteAsync("E");

        Assert.Empty(driver.Operations);
    }

    [Fact]
    public async Task AFailedRelease_FallsBackToAFullStop()
    {
        var (panel, driver, _) = await JogPanel();
        await panel.JogStartCommand.ExecuteAsync("E");
        driver.MoveAxisThrows = new InvalidOperationException("the driver failed");

        await panel.JogEndCommand.ExecuteAsync("E");

        Assert.Contains(driver.Log.Calls, c => c.Name == "AbortSlew");
        Assert.False(panel.IsJogging);
    }

    [Fact]
    public async Task Stop_EndsTheJogging_AndSetsTheAxesToZero()
    {
        var (panel, driver, _) = await JogPanel();
        await panel.JogStartCommand.ExecuteAsync("E");

        await panel.StopCommand.ExecuteAsync(null);

        Assert.False(panel.IsJogging);
        Assert.Equal("MoveAxis Secondary 0", driver.Operations[^1]);
        Assert.Contains("MoveAxis Primary 0", driver.Operations);
    }

    [Fact]
    public async Task TheFieldsOfTheSyncAndOfTheHorizontalSlew_StartFromTheMount_NotFromMadeUpNumbers()
    {
        var (panel, _, mount) = await JogPanel();

        var coordinates = mount.Telemetry!.Coordinates!;
        Assert.Equal(
            coordinates.RightAscensionHours.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture), panel.SyncRaText);
        Assert.NotEqual("45", panel.AltitudeText);
        Assert.NotEqual("180", panel.AzimuthText);
    }
}

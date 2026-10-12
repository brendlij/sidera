using Sidera.Alpaca;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// The dome (and the roll-off roof) in the equipment: the slot with its Alpaca choice, how an address is entered and found, what is stored, what the dome page shows, and when real equipment is
/// asked about before it moves. Nothing here touches a network: the discovery is a fake, and creating an Alpaca dome sends nothing.
/// </summary>
public sealed class DomeEquipmentTests : IAsyncLifetime
{
    private sealed class NoDiscovery : IAscomDiscovery
    {
        public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : Sidera.Ascom.IAscomSetupService
    {
        public Task<Sidera.Ascom.AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) => Task.FromResult(new Sidera.Ascom.AscomSetupResult(true, null));
    }

    /// <summary>Records what is asked and answers with what the test says.</summary>
    private sealed class FakeAlpaca : IAlpacaDiscovery
    {
        public List<AlpacaDiscoveryRequest> Requests { get; } = [];
        public List<(string Host, int Port)> Probes { get; } = [];
        public AlpacaDiscoveryResult Answer { get; set; } = new([]);
        public AlpacaDiscoveryResult ProbeAnswer { get; set; } = new([]);

        public Task<AlpacaDiscoveryResult> DiscoverAsync(AlpacaDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }

        public Task<AlpacaDiscoveryResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Probes.Add((host, port));
            return Task.FromResult(ProbeAnswer);
        }
    }

    /// <summary>A dome that is not a simulator as far as the safety question goes: it wraps one.</summary>
    private sealed class RealLookingDome(SimulatedDome inner) : IDomeControl
    {
        public DeviceId Id => inner.Id;
        public string Name => inner.Name;
        public DeviceType Type => DeviceType.Dome;
        public DeviceConnectionState ConnectionState => inner.ConnectionState;
        public DomeShutterState ShutterState => inner.ShutterState;
        public double? Azimuth => inner.Azimuth;
        public bool IsMoving => inner.IsMoving;
        public DeviceCapabilities<DomeCapabilities> Capabilities => inner.Capabilities;
        public DomeTelemetry? Telemetry => inner.Telemetry;
        public event EventHandler? CapabilitiesChanged { add => inner.CapabilitiesChanged += value; remove => inner.CapabilitiesChanged -= value; }
        public event EventHandler? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => inner.RefreshAsync(cancellationToken);
        public Task OpenShutterAsync(CancellationToken cancellationToken = default) => inner.OpenShutterAsync(cancellationToken);
        public Task CloseShutterAsync(CancellationToken cancellationToken = default) => inner.CloseShutterAsync(cancellationToken);
        public Task SlewToAzimuthAsync(double azimuthDegrees, CancellationToken cancellationToken = default) => inner.SlewToAzimuthAsync(azimuthDegrees, cancellationToken);
        public Task ParkAsync(CancellationToken cancellationToken = default) => inner.ParkAsync(cancellationToken);
        public Task FindHomeAsync(CancellationToken cancellationToken = default) => inner.FindHomeAsync(cancellationToken);
        public Task HaltAsync(CancellationToken cancellationToken = default) => inner.HaltAsync(cancellationToken);
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-dome-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public DomeEquipmentTests() => Directory.CreateDirectory(_directory);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private string EquipmentFile => Path.Combine(_directory, "equipment.json");

    private (SideraRuntimeHost Host, MainViewModel Vm, EquipmentService Service, FakeAlpaca Alpaca) Create(Action<SideraRuntimeHost>? devices = null, FakeAlpaca? alpaca = null)
    {
        var host = new SideraRuntimeHost();
        devices?.Invoke(host);
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(new DemoOptions()), new AlpacaBackendFactory()]);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(EquipmentFile), factories);
        service.Load(); // what an earlier start left in the file
        alpaca ??= new FakeAlpaca();
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), equipmentManagement: new EquipmentManagement(service, new NoDiscovery(), new NoSetup(), null, alpaca), withDemoSequence: false);
        _apps.Add((host, vm));
        return (host, vm, service, alpaca);
    }

    private static DeviceSlotViewModel DomeSlot(MainViewModel vm) => vm.Equipment.Slots.Single(s => s.Type == DeviceType.Dome);

    private static DriverChoice Alpaca(DeviceSlotViewModel slot) => slot.Choices.Single(c => c.IsAlpaca);

    private static void Enter(DeviceSlotViewModel slot, string host, string port = "11111", string number = "0")
    {
        slot.SelectedChoice = Alpaca(slot);
        slot.AlpacaHost = host;
        slot.AlpacaPortText = port;
        slot.AlpacaNumberText = number;
    }

    // ---- the slot

    [Fact]
    public void TheDomeSlot_OffersNoDome_TheSimulator_AndAlpaca_AndOnlyTheDomeDoes()
    {
        var (_, vm, _, _) = Create();

        var slot = DomeSlot(vm);

        Assert.Equal(["No dome", "Simulator", "ASCOM Alpaca (network)"], slot.Choices.Select(c => c.Text));
        Assert.All(vm.Equipment.Slots.Where(s => s.Type != DeviceType.Dome), s => Assert.DoesNotContain(s.Choices, c => c.IsAlpaca));
        Assert.True(slot.IsEmpty);
    }

    [Fact]
    public async Task TheSimulatorChoice_MakesADome_WithAPageAndAShutter()
    {
        var (host, vm, _, _) = Create();
        var slot = DomeSlot(vm);

        slot.SelectedChoice = slot.Choices.Single(c => c.Backend == DeviceBackend.Simulator);

        var dome = Assert.Single(vm.Equipment.Domes);
        Assert.IsType<SimulatedDome>(host.DeviceRegistry.GetAll().Single(d => d.Type == DeviceType.Dome));
        Assert.IsType<DomeDetailViewModel>(slot.Detail);
        Assert.Contains(vm.Equipment.Sections, s => s.Section == EquipmentSection.Domes);

        await dome.ConnectCommand.ExecuteAsync(null);
        await dome.OpenShutterCommand.ExecuteAsync(null);
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);
        Assert.Equal("Open", dome.ShutterText);
        Assert.True(dome.ShowAzimuth && dome.ShowPark && dome.ShowHome && !dome.IsRoof);
        await dome.CloseShutterCommand.ExecuteAsync(null);
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
    }

    [Fact]
    public async Task ARoofShowsAShutterAndNothingElse()
    {
        var (_, vm, _, _) = Create(host => host.AddSimulatedDome(new DeviceId("roof.1"), "Roll-off roof", shutterOnly: true, shutterTime: TimeSpan.FromMilliseconds(20)));
        var roof = Assert.Single(vm.Equipment.Domes);

        await roof.ConnectCommand.ExecuteAsync(null);

        Assert.True(roof.IsRoof);
        Assert.Equal("Roll-off roof", roof.KindText);
        Assert.True(roof.ShowShutter);
        Assert.False(roof.ShowAzimuth);
        Assert.False(roof.ShowPark);
        Assert.False(roof.ShowHome);
        Assert.Equal("Not reported", roof.AzimuthText);
        Assert.False(roof.SlewCommand.CanExecute(null));
        Assert.True(roof.OpenShutterCommand.CanExecute(null));
        Assert.Equal("ROLL-OFF ROOF", ((DomeDetailViewModel)vm.Equipment.Slots.Single(s => s.Type == DeviceType.Dome).Detail!).SectionTitle);
    }

    // ---- an Alpaca address

    [Fact]
    public void ChoosingAlpaca_ShowsWhereToEnterTheAddress_AndMakesNoDeviceBeforeItIsUsed()
    {
        var (_, vm, service, _) = Create();
        var slot = DomeSlot(vm);

        slot.SelectedChoice = Alpaca(slot);

        Assert.True(slot.IsAlpacaSelected);
        Assert.Null(slot.Device);
        Assert.Empty(service.Configuration.Devices);
    }

    [Fact]
    public void UsingAnAddress_MakesTheAlpacaDome_AndSendsNothing_AndKeepsItInTheEquipmentFile()
    {
        var (host, vm, service, alpaca) = Create();
        var slot = DomeSlot(vm);
        Enter(slot, "192.0.2.5", "11111", "1");

        slot.UseAlpacaCommand.Execute(null);

        var dome = Assert.IsType<DomeViewModel>(slot.Device);
        Assert.Equal("Alpaca", dome.BackendText);
        Assert.Equal("192.0.2.5:11111/1", dome.DriverIdText);
        Assert.Equal(DeviceConnectionState.Disconnected, dome.ConnectionState); // nothing was connected
        Assert.IsType<AlpacaDome>(host.DeviceRegistry.GetAll().Single(d => d.Type == DeviceType.Dome));
        Assert.Empty(alpaca.Requests); // and nothing was asked of the network
        Assert.Empty(alpaca.Probes);
        var file = File.ReadAllText(EquipmentFile);
        Assert.Contains("\"backend\": \"Alpaca\"", file, StringComparison.Ordinal);
        Assert.Contains("192.0.2.5", file, StringComparison.Ordinal);

        // A new start reads it back.
        var again = Create();
        Assert.IsType<AlpacaDome>(again.Host.DeviceRegistry.GetAll().Single(d => d.Type == DeviceType.Dome));
        Assert.Equal("192.0.2.5:11111/1", again.Vm.Equipment.Domes.Single().DriverIdText);
        _ = service;
    }

    [Theory]
    [InlineData("", "11111", "0")]
    [InlineData("192.0.2.5", "eleven", "0")]
    [InlineData("192.0.2.5", "70000", "0")]
    [InlineData("192.0.2.5", "11111", "-1")]
    [InlineData("bad host", "11111", "0")]
    public void AnAddressThatIsNotOne_IsRefused_AndNothingIsMade(string host, string port, string number)
    {
        var (_, vm, service, _) = Create();
        var slot = DomeSlot(vm);
        Enter(slot, host, port, number);

        slot.UseAlpacaCommand.Execute(null);

        Assert.True(slot.HasProblem);
        Assert.Null(slot.Device);
        Assert.Empty(service.Configuration.Devices);
    }

    [Fact]
    public void TheAddressOfAnAlpacaDome_CanBeChanged_WhileItIsDisconnected()
    {
        var (_, vm, _, _) = Create();
        var slot = DomeSlot(vm);
        Enter(slot, "192.0.2.5", "11111", "0");
        slot.UseAlpacaCommand.Execute(null);

        slot.AlpacaHost = "192.0.2.77";
        slot.UseAlpacaCommand.Execute(null);

        Assert.Equal("192.0.2.77:11111/0", Assert.Single(vm.Equipment.Domes).DriverIdText);
    }

    [Fact]
    public void ReplacingTheSimulatorByAnAlpacaDome_KeepsTheId()
    {
        var (_, vm, service, _) = Create();
        var slot = DomeSlot(vm);
        slot.SelectedChoice = slot.Choices.Single(c => c.Backend == DeviceBackend.Simulator);
        var id = Assert.Single(service.Configuration.Devices).Id;

        Enter(slot, "192.0.2.5");
        slot.UseAlpacaCommand.Execute(null);

        var configuration = Assert.Single(service.Configuration.Devices);
        Assert.Equal(id, configuration.Id);
        Assert.Equal(DeviceBackend.Alpaca, configuration.Backend);
    }

    // ---- finding one

    private static AlpacaDiscoveryResult Found(string host = "192.0.2.9", int port = 32323, int number = 1) =>
        new([new AlpacaServer(host, port, "Observatory server", [new AlpacaDeviceInfo("Camera", "Camera", 0, null), new AlpacaDeviceInfo("Roof", "Dome", number, null)])]);

    [Fact]
    public async Task AskingAHost_AsksThatHostOnly_NeverTheNetwork_AndListsItsDomes()
    {
        var (_, vm, _, alpaca) = Create();
        alpaca.Answer = Found();
        var slot = DomeSlot(vm);
        Enter(slot, "192.0.2.9");

        await slot.SearchHostCommand.ExecuteAsync(null);

        var request = Assert.Single(alpaca.Requests);
        Assert.Equal(["192.0.2.9"], request.Hosts);
        Assert.False(request.Broadcast);
        var roof = Assert.Single(slot.AlpacaFound);
        Assert.Equal("Roof", roof.Name);
        Assert.Contains("Found 1 dome", slot.AlpacaSearchText, StringComparison.Ordinal);

        slot.SelectedAlpacaFound = roof;
        Assert.Equal(("192.0.2.9", "32323", "1", "Roof"), (slot.AlpacaHost, slot.AlpacaPortText, slot.AlpacaNumberText, slot.AlpacaName));
        slot.UseAlpacaCommand.Execute(null);
        Assert.Equal("Roof", Assert.Single(vm.Equipment.Domes).Name);
        Assert.Equal("192.0.2.9:32323/1", vm.Equipment.Domes.Single().DriverIdText);
    }

    [Fact]
    public async Task OnlyTheSearchOfTheNetwork_Broadcasts()
    {
        var (_, vm, _, alpaca) = Create();
        alpaca.Answer = Found();
        var slot = DomeSlot(vm);
        slot.SelectedChoice = Alpaca(slot);

        await slot.SearchNetworkCommand.ExecuteAsync(null);

        var request = Assert.Single(alpaca.Requests);
        Assert.True(request.Broadcast);
        Assert.Empty(request.Hosts);
        Assert.Single(slot.AlpacaFound);
    }

    [Fact]
    public async Task AskingAHostWithoutAHost_SaysSo_AndAsksNobody()
    {
        var (_, vm, _, alpaca) = Create();
        var slot = DomeSlot(vm);
        slot.SelectedChoice = Alpaca(slot);

        await slot.SearchHostCommand.ExecuteAsync(null);

        Assert.Empty(alpaca.Requests);
        Assert.Contains("Enter the name or address", slot.AlpacaSearchText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatDoesNotAnswerDiscovery_IsAskedDirectlyAtItsPort()
    {
        var (_, vm, _, alpaca) = Create();
        alpaca.ProbeAnswer = Found("192.0.2.9", 11111, 0);
        var slot = DomeSlot(vm);
        Enter(slot, "192.0.2.9", "11111");

        await slot.SearchHostCommand.ExecuteAsync(null);

        Assert.Equal([("192.0.2.9", 11111)], alpaca.Probes);
        Assert.Single(slot.AlpacaFound);
    }

    [Fact]
    public async Task WhenNothingAnswers_TheSearchSaysSo()
    {
        var (_, vm, _, _) = Create();
        var slot = DomeSlot(vm);
        Enter(slot, "192.0.2.9");

        await slot.SearchHostCommand.ExecuteAsync(null);

        Assert.Empty(slot.AlpacaFound);
        Assert.Contains("No Alpaca server answered at 192.0.2.9", slot.AlpacaSearchText, StringComparison.Ordinal);
    }

    // ---- the equipment file

    [Fact]
    public void AnAlpacaDome_IsRefusedForAnythingButADome_AndWithoutAnAddress()
    {
        var (_, _, service, _) = Create();

        var camera = service.Add(new DeviceConfiguration("camera.x", "Camera", DeviceType.Camera, DeviceBackend.Alpaca, new AlpacaEndpoint("192.0.2.5", 11111).ToSettings()));
        var noAddress = service.Add(new DeviceConfiguration("dome.x", "Dome", DeviceType.Dome, DeviceBackend.Alpaca, DeviceConfiguration.NoSettings));

        Assert.False(camera.Succeeded);
        Assert.Contains("domes only", camera.Problem, StringComparison.Ordinal);
        Assert.False(noAddress.Succeeded);
        Assert.Contains("no host", noAddress.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "id": "dome.a", "name": "A", "type": "Dome", "backend": "Alpaca" }""", "no host")]
    [InlineData("""{ "id": "dome.a", "name": "A", "type": "Dome", "backend": "Alpaca", "settings": { "host": "192.0.2.5", "port": "abc" } }""", "port")]
    [InlineData("""{ "id": "cam.a", "name": "A", "type": "Camera", "backend": "Alpaca", "settings": { "host": "192.0.2.5", "port": "11111" } }""", "Alpaca Camera")]
    public void AnAlpacaDeviceThatCannotBeUsed_IsRefusedWhenTheFileIsRead(string device, string expected)
    {
        var file = $$"""{ "format": "astra-equipment", "version": 1, "devices": [ {{device}} ], "rigs": [] }""";

        var failure = Assert.Throws<EquipmentConfigurationException>(() => EquipmentConfigurationSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(file)));

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAlpacaDome_IsWrittenAndReadBack()
    {
        var endpoint = new AlpacaEndpoint("roof.local", 80, 2);
        var configuration = new EquipmentConfiguration([new DeviceConfiguration("dome.main", "Roof", DeviceType.Dome, DeviceBackend.Alpaca, endpoint.ToSettings())], []);

        var again = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(configuration));

        var device = Assert.Single(again.Devices);
        Assert.Equal(DeviceBackend.Alpaca, device.Backend);
        Assert.Equal(endpoint, AlpacaEndpoint.FromSettings(device.Settings));
    }

    // ---- real equipment asks first

    private (SideraRuntimeHost Host, MainViewModel Vm, DomeViewModel Dome) WithRealLookingDome()
    {
        var (host, vm, _, _) = Create(h => h.AddDevice(new RealLookingDome(new SimulatedDome(new DeviceId("dome.real"), "Real dome", h.EventBus, shutterTime: TimeSpan.FromMilliseconds(20), degreesPerSecond: 7200))));
        return (host, vm, vm.Equipment.Domes.Single());
    }

    [Fact]
    public async Task OpeningTheShutterOfRealEquipment_Asks_EveryTime_AndCancelMovesNothing()
    {
        var (_, vm, dome) = WithRealLookingDome();
        await dome.ConnectCommand.ExecuteAsync(null);

        var first = dome.OpenShutterCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending);
        Assert.Contains("the dome (Real dome)", vm.Safety.Message, StringComparison.Ordinal);
        vm.Safety.CancelCommand.Execute(null);
        await first;
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);

        var second = dome.OpenShutterCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending);
        vm.Safety.ContinueCommand.Execute(null);
        await second;
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);

        await dome.CloseShutterCommand.ExecuteAsync(null);
        Assert.False(vm.Safety.IsPending); // closing never asks
        var third = dome.OpenShutterCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending); // not remembered: asked again
        vm.Safety.ContinueCommand.Execute(null);
        await third;
    }

    [Fact]
    public async Task TurningRealEquipment_AsksOnce_ForTheDome()
    {
        var (_, vm, dome) = WithRealLookingDome();
        await dome.ConnectCommand.ExecuteAsync(null);
        dome.TargetInput = "90";

        var first = dome.SlewCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending);
        vm.Safety.ContinueCommand.Execute(null);
        await first;
        Assert.Equal(90, dome.Azimuth!.Value, 3);

        dome.TargetInput = "180";
        await dome.SlewCommand.ExecuteAsync(null);
        Assert.False(vm.Safety.IsPending);
        Assert.Equal(180, dome.Azimuth!.Value, 3);
    }

    [Fact]
    public async Task ASimulatedDome_NeverAsks()
    {
        var (_, vm, _, _) = Create(h => h.AddSimulatedDome(new DeviceId("dome.sim"), "Dome", shutterTime: TimeSpan.FromMilliseconds(20)));
        var dome = vm.Equipment.Domes.Single();
        await dome.ConnectCommand.ExecuteAsync(null);

        await dome.OpenShutterCommand.ExecuteAsync(null);

        Assert.False(vm.Safety.IsPending);
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);
    }

    [Fact]
    public async Task AzimuthThatIsNotAnAzimuth_IsRefused_BeforeAnythingMoves()
    {
        var (_, vm, _, _) = Create(h => h.AddSimulatedDome(new DeviceId("dome.sim"), "Dome"));
        var dome = vm.Equipment.Domes.Single();
        await dome.ConnectCommand.ExecuteAsync(null);
        dome.TargetInput = "400";

        await dome.SlewCommand.ExecuteAsync(null);

        Assert.True(dome.HasError);
        Assert.Contains("0 up to 360", dome.ErrorMessage, StringComparison.Ordinal);
        Assert.False(dome.IsMoving);
    }
}

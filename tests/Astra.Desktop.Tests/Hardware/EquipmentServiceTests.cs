using Astra.Ascom;
using Astra.Ascom.Tests;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Core.Rigs;
using Astra.Desktop.Hardware;
using Astra.Runtime;

namespace Astra.Desktop.Tests.Hardware;

/// <summary>
/// The equipment as the runtime holds it: loaded at startup without connecting anything, changed while Astra runs, saved
/// after every change, and the same for simulators and ASCOM devices, which live side by side in one host.
/// </summary>
public sealed class EquipmentServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astra-equipment-service-" + Guid.NewGuid().ToString("N"));
    private readonly List<AstraRuntimeHost> _hosts = [];
    private readonly CallLog _driverLog = new();
    private readonly FakeDriverFactory _drivers;

    public EquipmentServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _drivers = new FakeDriverFactory(_driverLog);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
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

    private string Path_ => Path.Combine(_directory, "equipment.json");

    private (EquipmentService Service, AstraRuntimeHost Host, List<EquipmentChange> Changes) Create(IDeviceFactory? simulator = null, string? path = null)
    {
        var host = new AstraRuntimeHost();
        _hosts.Add(host);
        var factories = new DeviceFactoryRegistry(
        [
            simulator ?? new SimulatorDeviceFactory(new DemoOptions { FocuserStepsPerSecond = 1_000_000, FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(1) }),
            new AscomBackendFactory(new AscomDeviceFactory(_drivers, null, FastTimings.Create())),
        ]);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(path ?? Path_), factories);
        var changes = new List<EquipmentChange>();
        service.Changed += (_, change) => changes.Add(change);
        return (service, host, changes);
    }

    private static DeviceConfiguration AscomFocuser(string id = "focuser.main", string progId = "ASCOM.Simulator.Focuser") =>
        DeviceConfiguration.Ascom(id, "Focuser", DeviceType.Focuser, progId);

    private static DeviceConfiguration AscomCamera(string id = "camera.main") =>
        DeviceConfiguration.Ascom(id, "Camera", DeviceType.Camera, "ASCOM.Simulator.Camera");

    private static DeviceConfiguration SimulatedCamera(string id = "camera.sim") =>
        DeviceConfiguration.Simulator(id, "Simulated Camera", DeviceType.Camera);

    private sealed class ThrowingFactory(DeviceBackend backend) : IDeviceFactory
    {
        public DeviceBackend Backend { get; } = backend;

        public IDevice CreateAndAdd(AstraRuntimeHost host, DeviceConfiguration configuration) => throw new NotSupportedException("no such thing");
    }

    // Startup

    [Fact]
    public void WithoutAFile_TheInstallationIsEmpty()
    {
        var (service, host, _) = Create();

        service.Load();

        Assert.Empty(service.Problems);
        Assert.Empty(host.DeviceRegistry.GetAll());
        Assert.False(File.Exists(Path_)); // reading does not create a file
    }

    [Fact]
    public async Task TheDevicesOfTheFile_AreLoaded_BothBackendsTogether_AndNoneIsConnected()
    {
        new EquipmentConfigurationStore(Path_).Save(new EquipmentConfiguration(
            [AscomCamera(), AscomFocuser(), SimulatedCamera(), DeviceConfiguration.Ascom("mount.eq6", "EQ6", DeviceType.Mount, "ASCOM.Simulator.Telescope")],
            []));
        var (service, host, _) = Create();

        service.Load();

        Assert.Empty(service.Problems);
        Assert.Equal(["camera.main", "camera.sim", "focuser.main", "mount.eq6"], host.DeviceRegistry.GetAll().Select(d => d.Id.Value).Order());
        Assert.All(host.DeviceRegistry.GetAll(), d => Assert.Equal(DeviceConnectionState.Disconnected, d.ConnectionState));
        Assert.Empty(_driverLog.Calls); // no driver was created: loading touches no hardware
        Assert.Equal(["ASCOM", "Simulator", "ASCOM", "ASCOM"], host.DeviceRegistry.GetAll().OrderBy(d => d.Id.Value).Select(d => d is IBackendDescribed described ? described.BackendName : "Simulator"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ADeviceThatWasConnectedBefore_IsDisconnectedAfterARestart()
    {
        var (first, firstHost, _) = Create();
        first.Add(SimulatedCamera());
        await firstHost.DeviceOperations.ConnectAsync(new DeviceId("camera.sim"));
        Assert.Equal(DeviceConnectionState.Connected, firstHost.DeviceRegistry.GetAll().Single().ConnectionState);

        var (second, secondHost, _) = Create();
        second.Load();

        Assert.Equal(DeviceConnectionState.Disconnected, secondHost.DeviceRegistry.GetAll().Single().ConnectionState);
    }

    [Fact]
    public void TheRigsOfTheFile_AreAddedToo_SimulatedFocusIncluded()
    {
        var (writer, _, _) = Create();
        Assert.True(writer.AddDemoEquipment().Succeeded);
        var (service, host, _) = Create();

        service.Load();

        Assert.Equal(["rig.main", "rig.narrow", "rig.wide"], host.RigRegistry.GetAll().Select(r => r.Id.Value).Order());
        Assert.True(host.FocusMetrics.TryGetModel(new RigId("rig.main"), out var model));
        Assert.Equal(20000, model!.BestPosition);
    }

    [Fact]
    public void AnUnreadableFile_LeavesTheInstallationEmpty_SaysWhy_AndIsNotTouched()
    {
        File.WriteAllText(Path_, "{ nope");
        var (service, host, _) = Create();

        service.Load();

        Assert.Empty(host.DeviceRegistry.GetAll());
        var problem = Assert.Single(service.Problems);
        Assert.Contains("not valid JSON", problem);
        Assert.Equal("{ nope", File.ReadAllText(Path_));
    }

    [Fact]
    public void AChangeAfterAnUnreadableFile_KeepsTheOldFileAsABackup()
    {
        File.WriteAllText(Path_, "{ nope");
        var (service, _, _) = Create();
        service.Load();

        Assert.True(service.Add(SimulatedCamera()).Succeeded);

        Assert.Equal("{ nope", File.ReadAllText(Path_ + ".bak"));
    }

    [Fact]
    public void ADeviceThatCannotBeCreated_IsReported_TheOthersLoad_AndItStaysInTheFile()
    {
        new EquipmentConfigurationStore(Path_).Save(new EquipmentConfiguration([AscomCamera(), SimulatedCamera()], []));
        var (service, host, _) = Create(simulator: new ThrowingFactory(DeviceBackend.Simulator));
        service.Load();

        Assert.Equal(["camera.main"], host.DeviceRegistry.GetAll().Select(d => d.Id.Value));
        Assert.Contains("camera.sim", Assert.Single(service.Problems));
        Assert.NotNull(service.ConfigurationOf("camera.sim")); // not lost: the next save writes it back
    }

    // Adding

    [Fact]
    public void AddingASimulator_RegistersIt_SavesIt_AndReportsIt()
    {
        var (service, host, changes) = Create();

        var result = service.Add(SimulatedCamera());

        Assert.True(result.Succeeded);
        Assert.Same(host.DeviceRegistry.GetAll().Single(), result.Device);
        Assert.Equal("camera.sim", new EquipmentConfigurationStore(Path_).Load().Devices.Single().Id);
        var change = Assert.Single(changes);
        Assert.Equal((EquipmentChangeKind.DeviceAdded, "camera.sim"), (change.Kind, change.DeviceId));
    }

    [Fact]
    public void AddingAnAscomDevice_RegistersTheAdapter_WithoutConnectingOrCreatingADriver()
    {
        var (service, host, _) = Create();

        var result = service.Add(AscomFocuser());

        Assert.True(result.Succeeded);
        var device = Assert.IsAssignableFrom<IFocuser>(host.DeviceRegistry.GetAll().Single());
        Assert.Equal(DeviceConnectionState.Disconnected, device.ConnectionState);
        Assert.Equal("ASCOM.Simulator.Focuser", ((IBackendDescribed)device).DriverId);
        Assert.Empty(_driverLog.Calls);
        Assert.Equal("ASCOM.Simulator.Focuser", new EquipmentConfigurationStore(Path_).Load().Devices.Single().ProgId);
    }

    [Fact]
    public async Task AnAscomDevice_AndASimulator_WorkInOneRuntime_ThroughTheSameOperations()
    {
        var (service, host, _) = Create();
        service.Add(AscomFocuser());
        service.Add(SimulatedCamera());

        await host.DeviceOperations.ConnectAsync(new DeviceId("focuser.main"));
        await host.DeviceOperations.ConnectAsync(new DeviceId("camera.sim"));
        await host.DeviceOperations.MoveFocuserToAsync(new DeviceId("focuser.main"), 26000);
        var frame = await host.DeviceOperations.ExposeAsync(new DeviceId("camera.sim"), TimeSpan.FromMilliseconds(10));

        Assert.Equal(26000, _drivers.Focusers.Single().PositionValue);
        Assert.Equal(800, frame.Width);
        await host.DeviceOperations.DisconnectAsync(new DeviceId("focuser.main"));
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("bad id", "can only contain")]
    public void ABadId_IsRefused_AndNothingIsAdded(string id, string expected)
    {
        var (service, host, changes) = Create();

        var result = service.Add(SimulatedCamera(id));

        Assert.False(result.Succeeded);
        Assert.Contains(expected, result.Problem);
        Assert.Empty(host.DeviceRegistry.GetAll());
        Assert.Empty(changes);
        Assert.False(File.Exists(Path_));
    }

    [Fact]
    public void ATakenId_IsRefused_EvenWithAnotherCase_AndTheFirstDeviceStaysUntouched()
    {
        var (service, host, _) = Create();
        service.Add(SimulatedCamera("camera.main"));

        var result = service.Add(AscomCamera("Camera.Main"));

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Problem);
        Assert.Single(host.DeviceRegistry.GetAll());
        Assert.Single(new EquipmentConfigurationStore(Path_).Load().Devices);
    }

    [Fact]
    public void AnIdOfADeviceThatIsLoadedButNotInTheFile_IsAlsoTaken()
    {
        var (service, host, _) = Create();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Added in code");

        Assert.Contains("already exists", service.Add(SimulatedCamera("camera.main")).Problem);
    }

    [Fact]
    public void AnAscomDeviceWithoutAProgId_OrOfAnUnsupportedKind_IsRefused()
    {
        var (service, host, _) = Create();

        var without = service.Add(new DeviceConfiguration("camera.a", "A", DeviceType.Camera, DeviceBackend.Ascom, DeviceConfiguration.NoSettings));
        var wheel = service.Add(DeviceConfiguration.Ascom("wheel.a", "W", DeviceType.FilterWheel, "X.Wheel"));
        var noName = service.Add(DeviceConfiguration.Simulator("camera.b", " ", DeviceType.Camera));

        Assert.Contains("ProgId", without.Problem);
        Assert.Contains("not supported yet", wheel.Problem);
        Assert.Contains("needs a name", noName.Problem);
        Assert.Empty(host.DeviceRegistry.GetAll());
    }

    [Fact]
    public void ABackendThatIsNotAvailable_IsRefused()
    {
        var host = new AstraRuntimeHost();
        _hosts.Add(host);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(Path_), new DeviceFactoryRegistry([new SimulatorDeviceFactory()]));

        var result = service.Add(AscomCamera());

        Assert.Contains("ASCOM backend is not available", result.Problem);
    }

    [Fact]
    public void ASaveThatFails_LeavesTheRuntimeAsItWas()
    {
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "file");
        var (service, host, changes) = Create(path: Path.Combine(blocker, "equipment.json"));

        var result = service.Add(SimulatedCamera());

        Assert.False(result.Succeeded);
        Assert.Contains("could not be saved", result.Problem);
        Assert.Empty(host.DeviceRegistry.GetAll());
        Assert.Empty(changes);
        Assert.Empty(service.Configuration.Devices);
    }

    // Changing

    [Fact]
    public void RenamingADevice_ReplacesItUnderTheSameId_AndSavesTheName()
    {
        var (service, host, changes) = Create();
        var original = service.Add(AscomFocuser()).Device!;
        changes.Clear();

        var result = service.Update(AscomFocuser() with { Name = "Renamed" });

        Assert.True(result.Succeeded);
        var now = host.DeviceRegistry.GetAll().Single();
        Assert.Equal(("focuser.main", "Renamed"), (now.Id.Value, now.Name));
        Assert.NotSame(original, now);
        Assert.Equal("Renamed", new EquipmentConfigurationStore(Path_).Load().Devices.Single().Name);
        var change = Assert.Single(changes);
        Assert.Equal((EquipmentChangeKind.DeviceReplaced, "focuser.main"), (change.Kind, change.DeviceId));
    }

    [Fact]
    public void ChoosingAnotherDriver_ReplacesTheAdapter_WithTheNewProgId()
    {
        var (service, host, _) = Create();
        service.Add(AscomFocuser());

        service.Update(AscomFocuser(progId: "ASCOM.OmniSim.Focuser"));

        Assert.Equal("ASCOM.OmniSim.Focuser", ((IBackendDescribed)host.DeviceRegistry.GetAll().Single()).DriverId);
        Assert.Equal("ASCOM.OmniSim.Focuser", service.ConfigurationOf("focuser.main")!.ProgId);
    }

    [Fact]
    public async Task AConnectedDevice_CannotBeChanged()
    {
        var (service, host, _) = Create();
        service.Add(AscomFocuser());
        await host.DeviceOperations.ConnectAsync(new DeviceId("focuser.main"));

        var result = service.Update(AscomFocuser() with { Name = "Renamed" });

        Assert.False(result.Succeeded);
        Assert.Contains("Disconnect the device", result.Problem);
        Assert.Equal("Focuser", host.DeviceRegistry.GetAll().Single().Name);
        Assert.Equal("Focuser", service.ConfigurationOf("focuser.main")!.Name);
        Assert.NotNull(service.WhyCannotEdit("focuser.main"));
    }

    [Fact]
    public void TheKindAndTheBackend_CannotBeChanged()
    {
        var (service, _, _) = Create();
        service.Add(AscomFocuser());

        var kind = service.Update(DeviceConfiguration.Ascom("focuser.main", "F", DeviceType.Camera, "ASCOM.Simulator.Camera"));
        var backend = service.Update(DeviceConfiguration.Simulator("focuser.main", "F", DeviceType.Focuser));

        Assert.Contains("cannot be changed", kind.Problem);
        Assert.Contains("cannot be changed", backend.Problem);
    }

    [Fact]
    public void AChangeToAnUnknownDevice_IsRefused()
    {
        var (service, _, _) = Create();

        Assert.Contains("not part of the equipment", service.Update(AscomFocuser()).Problem);
    }

    [Fact]
    public void ASaveThatFailsOnChange_PutsTheOldDeviceBack()
    {
        var (service, host, _) = Create();
        var original = service.Add(AscomFocuser()).Device!;
        // the folder disappears behind the store's back: the next save cannot be done
        File.Delete(Path_);
        Directory.Delete(_directory, recursive: true);
        File.WriteAllText(_directory, "now a file");

        var result = service.Update(AscomFocuser() with { Name = "Renamed" });

        Assert.False(result.Succeeded);
        Assert.Same(original, host.DeviceRegistry.GetAll().Single());
        Assert.Equal("Focuser", service.ConfigurationOf("focuser.main")!.Name);
        File.Delete(_directory);
        Directory.CreateDirectory(_directory);
    }

    // Removing

    [Fact]
    public void RemovingADevice_TakesItOutOfTheRuntime_AndOutOfTheFile()
    {
        var (service, host, changes) = Create();
        service.Add(AscomFocuser());
        service.Add(SimulatedCamera());
        changes.Clear();

        var result = service.Remove("focuser.main");

        Assert.True(result.Succeeded);
        Assert.Equal(["camera.sim"], host.DeviceRegistry.GetAll().Select(d => d.Id.Value));
        Assert.Equal(["camera.sim"], new EquipmentConfigurationStore(Path_).Load().Devices.Select(d => d.Id));
        var change = Assert.Single(changes);
        Assert.Equal((EquipmentChangeKind.DeviceRemoved, "focuser.main"), (change.Kind, change.DeviceId));
    }

    [Fact]
    public void AnIdThatWasRemoved_CanBeUsedAgain_ByAnotherBackend()
    {
        var (service, host, _) = Create();
        service.Add(AscomCamera("camera.main"));
        service.Remove("camera.main");

        var result = service.Add(SimulatedCamera("camera.main"));

        Assert.True(result.Succeeded);
        Assert.Equal("Simulated Camera", host.DeviceRegistry.GetAll().Single().Name);
    }

    [Fact]
    public async Task AConnectedDevice_CannotBeRemoved()
    {
        var (service, host, _) = Create();
        service.Add(AscomFocuser());
        await host.DeviceOperations.ConnectAsync(new DeviceId("focuser.main"));

        var result = service.Remove("focuser.main");

        Assert.False(result.Succeeded);
        Assert.Contains("Disconnect the device", result.Problem);
        Assert.Single(host.DeviceRegistry.GetAll());
        Assert.Single(service.Configuration.Devices);
    }

    [Fact]
    public void ADeviceOfARig_CannotBeRemoved_AndTheMessageNamesTheRig()
    {
        var (service, host, _) = Create();
        service.AddDemoEquipment();

        var result = service.Remove("focuser.main");

        Assert.False(result.Succeeded);
        Assert.Contains("part of the rig 'Main Rig'", result.Problem);
        Assert.NotNull(host.DeviceRegistry.GetAll().SingleOrDefault(d => d.Id.Value == "focuser.main"));
        Assert.Equal(result.Problem, service.WhyCannotRemove("focuser.main"));
    }

    [Fact]
    public void ADeviceOfNoRig_CanBeRemoved_FromTheDemoToo()
    {
        var (service, _, _) = Create();
        service.AddDemoEquipment();

        Assert.Null(service.WhyCannotRemove("mount.eq6"));
        Assert.True(service.Remove("mount.eq6").Succeeded);
    }

    [Fact]
    public void AnUnknownDevice_CannotBeRemoved()
    {
        var (service, _, _) = Create();

        Assert.Contains("not part of the equipment", service.Remove("nothing").Problem);
    }

    // The demo

    [Fact]
    public void TheDemo_AddsTheSimulatedDevicesAndRigs_AndStoresThemLikeAnyEquipment()
    {
        var (service, host, changes) = Create();

        var result = service.AddDemoEquipment();

        Assert.True(result.Succeeded);
        Assert.Equal(10, host.DeviceRegistry.GetAll().Count);
        Assert.Equal(3, host.RigRegistry.GetAll().Count);
        Assert.Equal(11, changes.Count); // ten devices and the rigs
        Assert.Equal(EquipmentChangeKind.RigsAdded, changes.Last().Kind);
        var stored = new EquipmentConfigurationStore(Path_).Load();
        Assert.Equal(10, stored.Devices.Count);
        Assert.Equal(3, stored.Rigs.Count);
        Assert.All(stored.Devices, d => Assert.Equal(DeviceBackend.Simulator, d.Backend));
    }

    [Fact]
    public void TheDemo_IsTheSameEquipmentAsTheOneComposedInCode()
    {
        var (service, stored, _) = Create();
        service.AddDemoEquipment();
        var code = new AstraRuntimeHost();
        _hosts.Add(code);
        DemoSetup.AddDemoEquipment(code);
        DemoSetup.AddDemoRigs(code);

        string Describe(AstraRuntimeHost h) => string.Join(
            "; ",
            h.DeviceRegistry.GetAll().OrderBy(d => d.Id.Value).Select(d => $"{d.Id.Value}|{d.Name}|{d.Type}|{Range(d)}"));
        static string Range(IDevice d) => d switch
        {
            IFocuser f => $"{f.Position}-{f.MinPosition}-{f.MaxPosition}",
            Astra.Core.FilterWheels.IFilterWheel w => string.Join(",", w.Slots.Select(s => s.Name)),
            _ => string.Empty,
        };

        Assert.Equal(Describe(code), Describe(stored));
        Assert.Equal(
            code.RigRegistry.GetAll().OrderBy(r => r.Id.Value).Select(r => (r.Id.Value, r.Name, r.CameraId.Value, r.FocuserId?.Value, r.FilterWheelId?.Value, r.Optics)),
            stored.RigRegistry.GetAll().OrderBy(r => r.Id.Value).Select(r => (r.Id.Value, r.Name, r.CameraId.Value, r.FocuserId?.Value, r.FilterWheelId?.Value, r.Optics)));
        foreach (var rig in code.RigRegistry.GetAll())
        {
            Assert.True(code.FocusMetrics.TryGetModel(rig.Id, out var a));
            Assert.True(stored.FocusMetrics.TryGetModel(rig.Id, out var b));
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void TheDemoTwice_IsRefused_AndChangesNothing()
    {
        var (service, host, _) = Create();
        service.AddDemoEquipment();

        var result = service.AddDemoEquipment();

        Assert.False(result.Succeeded);
        Assert.Contains("already in use", result.Problem);
        Assert.Equal(10, host.DeviceRegistry.GetAll().Count);
    }

    [Fact]
    public void TheDemo_IsRefused_WhenOneOfItsIdsIsTaken_AndNothingIsLeftBehind()
    {
        var (service, host, _) = Create();
        service.Add(AscomCamera("camera.main"));

        var result = service.AddDemoEquipment();

        Assert.False(result.Succeeded);
        Assert.Single(host.DeviceRegistry.GetAll());
        Assert.Empty(host.RigRegistry.GetAll());
    }

    [Fact]
    public void WhatTheServiceSaved_LoadsIntoAFreshHost_WithTheSameDevices()
    {
        var (writer, _, _) = Create();
        writer.Add(AscomCamera());
        writer.Add(AscomFocuser());
        writer.Add(SimulatedCamera());
        var (reader, host, _) = Create();

        reader.Load();

        Assert.Equal(["camera.main", "camera.sim", "focuser.main"], host.DeviceRegistry.GetAll().Select(d => d.Id.Value).Order());
        Assert.Empty(reader.Problems);
    }

    [Fact]
    public void TheFileOfTheSequence_NeverMentionsABackend()
    {
        // .astraseq stays backend-agnostic: the device ids it uses are the ones of the equipment file, nothing else.
        var configuration = EquipmentConfigurationSerializer.ToText(new EquipmentConfiguration([AscomCamera()], []));

        Assert.Contains("camera.main", configuration);
        Assert.DoesNotContain("progId", Astra.Desktop.Documents.SequenceDocumentFiles.Extension);
    }
}

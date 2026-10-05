using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>The rotator as a configured device, its association with a rig, the calibration that is kept with it, and its page: capability-driven controls and explicit moves only.</summary>
public sealed class RotatorEquipmentTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-rotator-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public RotatorEquipmentTests() => Directory.CreateDirectory(_directory);

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

    private sealed record App(MainViewModel Vm, EquipmentService Equipment, SideraRuntimeHost Host);

    private App Create(EquipmentConfiguration? stored = null)
    {
        if (stored is not null)
        {
            new EquipmentConfigurationStore(EquipmentFile).Save(stored);
        }

        var host = new SideraRuntimeHost();
        var options = new DemoOptions { ManualExposure = TimeSpan.FromMilliseconds(30) };
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]);
        var equipment = new EquipmentService(host, new EquipmentConfigurationStore(EquipmentFile), factories);
        equipment.Load();
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        var vm = new MainViewModel(
            host, a => a(), options,
            equipmentManagement: new EquipmentManagement(equipment, new NoDiscovery(), new NoSetup(), settings), withDemoSequence: false);
        _apps.Add((host, vm));
        return new App(vm, equipment, host);
    }

    private sealed class NoDiscovery : Sidera.Ascom.Discovery.IAscomDiscovery
    {
        public Task<Sidera.Ascom.Discovery.AscomDiscoveryResult> DiscoverAsync(Sidera.Ascom.Discovery.AscomDeviceKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Sidera.Ascom.Discovery.AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : Sidera.Ascom.IAscomSetupService
    {
        public Task<Sidera.Ascom.AscomSetupResult> ShowAsync(Sidera.Ascom.Discovery.AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Sidera.Ascom.AscomSetupResult(true, null));
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private static readonly DeviceConfiguration Camera = DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera);
    private static readonly DeviceConfiguration Rotator = DeviceConfiguration.Simulator("rotator.main", "Rotator", DeviceType.Rotator);

    // ---- Configuration and persistence

    [Fact]
    public void ARotator_IsAnOrdinaryConfiguredDevice_AndOnlyItsConfigurationIsStored()
    {
        var app = Create();

        Assert.True(app.Equipment.Add(Rotator).Succeeded);

        var text = File.ReadAllText(EquipmentFile);
        Assert.Contains("\"type\": \"Rotator\"", text);
        Assert.DoesNotContain("position", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("moving", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connected", text, StringComparison.OrdinalIgnoreCase);
        var again = Create();
        Assert.IsType<RotatorViewModel>(again.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Rotator).Device);
    }

    [Fact]
    public void TheRigAssociation_AndItsCalibration_AreSavedAndLoadedAgain()
    {
        var app = Create();
        app.Equipment.Add(Camera);
        app.Equipment.Add(Rotator);

        Assert.True(app.Equipment.SetCameraRotator("camera.main", "rotator.main").Succeeded);
        var rig = app.Host.RigRegistry.GetAll().Single();
        Assert.Equal(new DeviceId("rotator.main"), rig.RotatorId);
        Assert.Null(rig.RotatorModel); // associated, not calibrated

        var model = new RotatorSkyModel(45.5, Reversed: true, new DateTimeOffset(2026, 10, 5, 12, 30, 0, TimeSpan.Zero));
        Assert.True(app.Equipment.SetRotatorModel(rig.Id.Value, model).Succeeded);

        var again = Create();
        var loaded = again.Host.RigRegistry.GetAll().Single();
        Assert.Equal(new DeviceId("rotator.main"), loaded.RotatorId);
        Assert.Equal(model, loaded.RotatorModel);
        var text = File.ReadAllText(EquipmentFile);
        Assert.Contains("\"skyOffsetDegrees\": 45.5", text);
        Assert.Contains("\"reversed\": true", text);
        Assert.Contains("calibratedAt", text);
        Assert.DoesNotContain("solved", text, StringComparison.OrdinalIgnoreCase); // the measurement is not configuration
    }

    [Fact]
    public void AnEquipmentFileOfBeforeTheRotator_StillLoads_WithRigsThatHaveNone()
    {
        const string old = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","optics":{"focalLengthMm":500}}]}
            """;
        var parsed = EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(old)).Rigs.Single();

        Assert.Null(parsed.RotatorId);
        Assert.Null(parsed.RotatorModel);
        Assert.Equal(500, parsed.Optics!.FocalLengthMm);
    }

    [Fact]
    public void ARigThatNamesARotatorThatIsNotInTheFile_IsRefused()
    {
        const string bad = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","rotatorId":"rotator.gone"}]}
            """;

        var ex = Assert.Throws<EquipmentConfigurationException>(() => EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(bad)));

        Assert.Contains("rotator.gone", ex.Message);
    }

    [Fact]
    public void ACalibrationWithoutARotator_IsNotKept()
    {
        const string odd = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","rotator":{"skyOffsetDegrees":10}}]}
            """;

        Assert.Null(EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(odd)).Rigs.Single().RotatorModel);
    }

    [Fact]
    public void ChangingTheRotatorOfARig_ForgetsTheCalibrationOfTheOldOne_AndRemovingItDropsTheRigThatHeldOnlyIt()
    {
        var app = Create();
        app.Equipment.Add(Camera);
        app.Equipment.Add(Rotator);
        app.Equipment.Add(DeviceConfiguration.Simulator("rotator.second", "Second", DeviceType.Rotator));
        app.Equipment.SetCameraRotator("camera.main", "rotator.main");
        app.Equipment.SetRotatorModel(app.Host.RigRegistry.GetAll().Single().Id.Value, new RotatorSkyModel(10));

        app.Equipment.SetCameraRotator("camera.main", "rotator.second");
        Assert.Null(app.Host.RigRegistry.GetAll().Single().RotatorModel);

        app.Equipment.SetCameraRotator("camera.main", null);
        Assert.Empty(app.Host.RigRegistry.GetAll());
        Assert.Empty(app.Equipment.Configuration.Rigs);
    }

    [Fact]
    public void RemovingARotator_LeavesTheRigsThatHadIt_WithoutIt()
    {
        var app = Create();
        app.Equipment.Add(Camera);
        app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750));
        app.Equipment.Add(Rotator);
        app.Equipment.SetCameraRotator("camera.main", "rotator.main");

        Assert.True(app.Equipment.Remove("rotator.main").Succeeded);

        var rig = Assert.Single(app.Host.RigRegistry.GetAll());
        Assert.Null(rig.RotatorId);
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.Null(new EquipmentConfigurationStore(EquipmentFile).Load().Rigs.Single().RotatorId);
    }

    [Fact]
    public void ARigWithoutARotator_RemainsCompletelyValid()
    {
        var app = Create();
        app.Equipment.Add(Camera);

        Assert.True(app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750)).Succeeded);

        Assert.Null(app.Host.RigRegistry.GetAll().Single().RotatorId);
        Assert.False(app.Equipment.SetRotatorModel(app.Host.RigRegistry.GetAll().Single().Id.Value, new RotatorSkyModel(1)).Succeeded);
    }

    [Fact]
    public void ARotatorThatIsNotThere_CannotBeGivenToARig()
    {
        var app = Create();
        app.Equipment.Add(Camera);

        Assert.False(app.Equipment.SetCameraRotator("camera.main", "rotator.nothing").Succeeded);
        Assert.Empty(app.Host.RigRegistry.GetAll());
    }

    // ---- The camera page offers the rotator

    [Fact]
    public void TheCameraPage_OffersTheRotators_AndChoosingOneOnlyRecordsIt()
    {
        var app = Create();
        app.Equipment.Add(Camera);
        app.Equipment.Add(Rotator);
        var form = Assert.IsType<CameraDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Camera).Detail).Optics;
        Assert.True(form.HasRotatorChoices);
        Assert.Equal(["None", "Rotator"], form.RotatorChoices.Select(c => c.Text));
        var rotator = app.Host.DeviceRegistry.GetAll().OfType<SimulatedRotator>().Single();

        form.SelectedRotator = form.RotatorChoices[1];

        Assert.Equal(new DeviceId("rotator.main"), app.Host.RigRegistry.GetAll().Single().RotatorId);
        Assert.Equal(DeviceConnectionState.Disconnected, rotator.ConnectionState); // nothing was connected
        Assert.Equal(0, rotator.MovesStarted);
        var again = Assert.IsType<CameraDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Camera).Detail).Optics;
        Assert.Equal("Rotator", again.SelectedRotator.Text);
    }

    [Fact]
    public void WithoutARotator_TheCameraPageOffersNoChoice()
    {
        var app = Create();
        app.Equipment.Add(Camera);

        var form = Assert.IsType<CameraDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Camera).Detail).Optics;

        Assert.False(form.HasRotatorChoices);
    }

    // ---- The rotator page

    private async Task<(App App, RotatorViewModel Vm, SimulatedRotator Rotator)> ConnectedAsync()
    {
        var app = Create();
        app.Equipment.Add(Rotator);
        var vm = Assert.IsType<RotatorViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Rotator).Device);
        var rotator = app.Host.DeviceRegistry.GetAll().OfType<SimulatedRotator>().Single();
        await app.Host.DeviceOperations.ConnectAsync(rotator.Id);
        await WaitAsync(() => vm.IsConnected, "the connection");
        return (app, vm, rotator);
    }

    [Fact]
    public async Task ConnectingARotator_ShowsItsPosition_AndMovesNothing()
    {
        var (_, vm, rotator) = await ConnectedAsync();

        Assert.Equal("0.0°", vm.PositionText);
        Assert.Equal("Idle", vm.MotionText);
        Assert.Equal(0, rotator.MovesStarted);
        Assert.True(vm.ShowAbsolute && vm.ShowRelative && vm.ShowMechanical);
        Assert.True(vm.MoveCommand.CanExecute(null));
        Assert.False(vm.HaltCommand.CanExecute(null)); // nothing to halt
    }

    [Fact]
    public async Task AnExplicitMove_MovesTheRotator_AndOnlyThen()
    {
        var (_, vm, rotator) = await ConnectedAsync();
        vm.TargetInput = "45,5";

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.Equal(45.5, rotator.Position, 6);
        await WaitAsync(() => vm.PositionText == "45.5°", "the position");
        Assert.Equal(1, rotator.MovesStarted);
    }

    [Fact]
    public async Task TheRelativeButtons_MoveByTheAngleTypedInTheirOwnField()
    {
        var (_, vm, rotator) = await ConnectedAsync();
        vm.RelativeInput = "7.5";

        await vm.MoveByCommand.ExecuteAsync("+");
        await vm.MoveByCommand.ExecuteAsync("+");
        await vm.MoveByCommand.ExecuteAsync("-");

        Assert.Equal(7.5, rotator.Position, 6);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public async Task AnAngleThatIsNotANumber_IsRefused_AndNothingMoves(string text)
    {
        var (_, vm, rotator) = await ConnectedAsync();
        vm.TargetInput = text;
        await vm.MoveCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);

        vm.RelativeInput = text;
        await vm.MoveByCommand.ExecuteAsync("+");

        Assert.Equal(0, rotator.MovesStarted);
    }

    [Fact]
    public async Task AFailedMove_IsShown_AndTheRotatorIsIdleAfterwards()
    {
        var (_, vm, rotator) = await ConnectedAsync();
        rotator.FailMovesAfter = 0;
        vm.TargetInput = "30";

        await vm.MoveCommand.ExecuteAsync(null);

        Assert.True(vm.HasError);
        Assert.Equal("Idle", vm.MotionText);
        Assert.False(vm.IsManualMoveRunning);
        Assert.True(vm.MoveCommand.CanExecute(null));
    }

    [Fact]
    public async Task AHalt_IsAvailableWhileItMoves_AndStopsIt_AndTheDisconnectWaits()
    {
        var app = Create();
        app.Equipment.Add(Rotator);
        var vm = Assert.IsType<RotatorViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Rotator).Device);
        // A slow rotator: the simulator of the factory is fast, so the move is made on one that is slow.
        var slow = app.Host.AddSimulatedRotator(new DeviceId("rotator.slow"), "Slow", degreesPerSecond: 40);
        var slowVm = new RotatorViewModel(slow, app.Host, a => a(), new SessionActivity());
        await app.Host.DeviceOperations.ConnectAsync(slow.Id);
        await WaitAsync(() => slowVm.IsConnected, "the connection");
        slowVm.TargetInput = "180";

        var move = slowVm.MoveCommand.ExecuteAsync(null);
        await WaitAsync(() => slowVm.IsManualMoveRunning, "the move to start");

        Assert.True(slowVm.HaltCommand.CanExecute(null));
        Assert.False(slowVm.DisconnectCommand.CanExecute(null));
        await slowVm.HaltCommand.ExecuteAsync(null);
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(slowVm.IsManualMoveRunning);
        Assert.True(slow.Position < 179); // halted before the end
        Assert.True(slowVm.DisconnectCommand.CanExecute(null));
        slowVm.Dispose();
        Assert.NotNull(vm);
    }

    [Fact]
    public async Task OnlyWhatTheRotatorSupportsIsShown()
    {
        // A rotator that cannot move by an angle shows no relative controls (the capability decides; no ASCOM name is involved).
        var app = Create();
        app.Equipment.Add(Rotator);
        var vm = Assert.IsType<RotatorViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Rotator).Device);

        // Before the connection nothing is known: no move is offered.
        Assert.False(vm.ShowRelative);
        Assert.False(vm.MoveCommand.CanExecute(null));
        Assert.False(vm.ShowMechanical);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task TheRotatorPage_ShowsTheCalibrationOfItsRigApart_FromTheMechanicalPosition()
    {
        var app = Create();
        app.Equipment.Add(Camera);
        app.Equipment.Add(Rotator);
        app.Equipment.SetCameraRotator("camera.main", "rotator.main");
        var rig = app.Host.RigRegistry.GetAll().Single();
        app.Equipment.SetRotatorModel(rig.Id.Value, new RotatorSkyModel(12));
        var rotator = app.Host.DeviceRegistry.GetAll().OfType<SimulatedRotator>().Single();
        await app.Host.DeviceOperations.ConnectAsync(rotator.Id);
        var detail = Assert.IsType<RotatorDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Rotator).Detail);
        await WaitAsync(() => detail.Rotator.IsConnected, "the connection");

        await app.Host.DeviceOperations.MoveRotatorToAsync(rotator.Id, 30);
        await WaitAsync(() => detail.Rotator.PositionText == "30.0°", "the position");

        Assert.Equal("30.0°", detail.Rotator.PositionText);
        Assert.Equal("42.0°", detail.SkyNowText); // sky 42° at position 30° with an offset of 12°
        Assert.Equal("+12.0°", detail.OffsetText);
        Assert.True(detail.HasRig);
        Assert.Equal(rig.Name, detail.RigText);
    }
}

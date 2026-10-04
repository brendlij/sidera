using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Ascom.Tests;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// The equipment page with the means to change the equipment: adding, editing and removing devices through the form,
/// with simulators and ASCOM devices side by side, and the pages that follow (the dashboard, the session).
/// </summary>
public sealed class EquipmentManagementTests : IAsyncLifetime
{
    private sealed class FakeDiscovery : IAscomDiscovery
    {
        public Dictionary<AscomDeviceKind, AscomDiscoveryResult> Results { get; } = new()
        {
            [AscomDeviceKind.Camera] = new(true, [new AscomDriverInfo("ASCOM.Simulator.Camera", "Camera V3 simulator"), new AscomDriverInfo("ASCOM.ZWO.Camera", "ZWO Camera")], null),
            [AscomDeviceKind.Focuser] = new(true, [new AscomDriverInfo("ASCOM.Simulator.Focuser", "ASCOM Simulator Focuser Driver")], null),
            [AscomDeviceKind.Mount] = new(true, [new AscomDriverInfo("ASCOM.Simulator.Telescope", "Telescope Simulator")], null),
        };

        public List<AscomDeviceKind> Asked { get; } = [];

        public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default)
        {
            Asked.Add(kind);
            return Task.FromResult(Results[kind]);
        }
    }

    private sealed class FakeSetup : IAscomSetupService
    {
        public List<(AscomDeviceKind Kind, string ProgId)> Shown { get; } = [];
        public AscomSetupResult Result { get; set; } = new(true, null);
        public TaskCompletionSource? Hold { get; set; }

        public async Task<AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default)
        {
            Shown.Add((kind, progId));
            if (Hold is not null)
            {
                await Hold.Task;
            }

            return Result;
        }
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astra-equipment-vm-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDiscovery _discovery = new();
    private readonly FakeSetup _setup = new();
    private readonly CallLog _log = new();
    private readonly FakeDriverFactory _drivers;
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public EquipmentManagementTests()
    {
        Directory.CreateDirectory(_directory);
        _drivers = new FakeDriverFactory(_log);
    }

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

    private string File_ => Path.Combine(_directory, "equipment.json");

    private (MainViewModel Vm, EquipmentService Service, SideraRuntimeHost Host) CreateApp(EquipmentConfiguration? stored = null)
    {
        if (stored is not null)
        {
            new EquipmentConfigurationStore(File_).Save(stored);
        }

        var host = new SideraRuntimeHost();
        var options = new DemoOptions { FocuserStepsPerSecond = 1_000_000, FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(1), ManualExposure = TimeSpan.FromMilliseconds(30) };
        var factories = new DeviceFactoryRegistry(
            [new SimulatorDeviceFactory(options), new AscomBackendFactory(new AscomDeviceFactory(_drivers, null, FastTimings.Create()))]);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(File_), factories);
        service.Load();
        var vm = new MainViewModel(host, a => a(), options, equipmentManagement: new EquipmentManagement(service, _discovery, _setup));
        _apps.Add((host, vm));
        return (vm, service, host);
    }

    private static DeviceEditorViewModel Open(EquipmentViewModel equipment)
    {
        equipment.AddDeviceCommand.Execute(null);
        return Assert.IsType<DeviceEditorViewModel>(equipment.Editor);
    }

    private static async Task<DeviceEditorViewModel> OpenAscom(EquipmentViewModel equipment, DeviceType type = DeviceType.Focuser)
    {
        var editor = Open(equipment);
        editor.SelectedBackend = editor.BackendChoices.Single(b => b.Backend == DeviceBackend.Ascom);
        editor.SelectedType = editor.TypeChoices.Single(t => t.Type == type);
        await UxWait(() => !editor.IsDiscovering);
        return editor;
    }

    private static async Task UxWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }

    // A first start

    [Fact]
    public void AFirstStart_HasNoEquipment_AndOffersToAddSome()
    {
        var (vm, _, _) = CreateApp();

        Assert.False(vm.Equipment.HasDevices);
        Assert.True(vm.Equipment.CanManage);
        Assert.False(vm.Equipment.HasLandingGroups);
        Assert.False(vm.Equipment.HasProblems);
        Assert.True(vm.Equipment.AddDeviceCommand.CanExecute(null));
        Assert.True(vm.Equipment.AddDemoEquipmentCommand.CanExecute(null));
    }

    [Fact]
    public void AFirstStart_HasAnEmptySession_NotOneThatPointsAtDevicesThatAreNotThere()
    {
        var (vm, _, _) = CreateApp();

        Assert.True(vm.SequenceDraft.IsEmpty);
        Assert.Empty(vm.SequenceDraft.Rows);
        Assert.All(vm.SequenceDraft.ValidationErrors, e => Assert.Contains("step", e, StringComparison.OrdinalIgnoreCase)); // only "no steps yet"
    }

    [Fact]
    public void AStartWithEquipment_HasTheSessionOfTheDemo()
    {
        var (vm, _, _) = CreateApp(DemoSetup.Configuration());

        Assert.Equal(8, vm.SequenceDraft.Steps.Count);
    }

    [Fact]
    public void TheDemo_AddedToAnEmptyInstallation_GivesDevicesRigsAndASession_WhileTheListsChangeInPlace()
    {
        var (vm, _, _) = CreateApp();
        var cameras = vm.Equipment.Cameras; // what other pages were given at the start

        vm.Equipment.AddDemoEquipmentCommand.Execute(null);

        Assert.True(vm.Equipment.HasDevices);
        Assert.Same(cameras, vm.Equipment.Cameras);
        Assert.Equal(3, cameras.Count);
        Assert.Equal(["Main Rig", "Narrow Rig", "Wide Rig", "Standalone devices"], vm.Equipment.LandingGroups.Select(g => g.Title));
        Assert.True(vm.Equipment.HasRigs);
        Assert.Equal(3, vm.Equipment.Rigs.Count);
        Assert.Equal(8, vm.SequenceDraft.Steps.Count);
        Assert.True(vm.Dashboard.UnitsAreRigs);
        Assert.Equal(3, vm.Dashboard.Units.Count);
        Assert.True(vm.Equipment.IsLanding); // the overview, with everything that was added
        Assert.Equal(["Standalone", "Main Rig", "Narrow Rig", "Wide Rig"], vm.Equipment.Contexts.Select(c => c.Title));
    }

    [Fact]
    public void TheDemoTwice_ShowsTheRefusal_AndLeavesTheEquipmentAlone()
    {
        var (vm, _, _) = CreateApp();
        vm.Equipment.AddDemoEquipmentCommand.Execute(null);

        vm.Equipment.AddDemoEquipmentCommand.Execute(null);

        Assert.True(vm.Equipment.HasNotice);
        Assert.Contains("already in use", vm.Equipment.NoticeText);
        Assert.Equal(10, vm.Equipment.Devices.Count());
    }

    [Fact]
    public void TheDemo_DoesNotReplaceASessionTheUserHasAlreadyBegun()
    {
        var (vm, _, _) = CreateApp();
        vm.SequenceDraft.AddStepCommand.Execute(SequenceStepKind.Delay);

        vm.Equipment.AddDemoEquipmentCommand.Execute(null);

        Assert.Single(vm.SequenceDraft.Steps);
    }

    [Fact]
    public void ProblemsOfTheEquipmentFile_AreShownOnThePage()
    {
        File.WriteAllText(File_, "{ nope");

        var (vm, _, _) = CreateApp();

        Assert.True(vm.Equipment.HasProblems);
        Assert.Contains("not valid JSON", vm.Equipment.ProblemsText);
    }

    [Fact]
    public void WithoutTheMeansToChangeTheEquipment_ThePageOnlyShows()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");
        var vm = new MainViewModel(host, a => a());
        _apps.Add((host, vm));

        Assert.False(vm.Equipment.CanManage);
        Assert.False(vm.Equipment.AddDeviceCommand.CanExecute(null));
        vm.Equipment.Contexts[0].SelectCommand.Execute(null);
        Assert.Null(vm.Equipment.SelectedDetail!.Configuration);
    }

    // Adding through the form

    [Fact]
    public void ASimulatedDevice_IsAddedThroughTheForm_ShownSelected_AndSaved()
    {
        var (vm, _, host) = CreateApp();
        var editor = Open(vm.Equipment);
        Assert.Equal("Add device", editor.Title);
        editor.SelectedType = editor.TypeChoices.Single(t => t.Type == DeviceType.Camera);
        editor.NameInput = "Guide Scope Camera";

        Assert.Equal("camera.guide-scope-camera", editor.IdInput);
        editor.SaveCommand.Execute(null);

        Assert.Null(vm.Equipment.Editor);
        var camera = Assert.Single(vm.Equipment.Cameras);
        Assert.Equal(("camera.guide-scope-camera", "Guide Scope Camera"), (camera.DeviceIdText, camera.Name));
        Assert.Same(camera, vm.Equipment.SelectedDevice);
        Assert.True(camera.IsSelected);
        Assert.Equal("Simulator", camera.BackendText);
        Assert.Single(host.DeviceRegistry.GetAll());
        Assert.Equal("camera.guide-scope-camera", new EquipmentConfigurationStore(File_).Load().Devices.Single().Id);
    }

    [Fact]
    public async Task AnAscomDevice_IsAddedThroughTheForm_WithTheDriverFromTheList()
    {
        var (vm, _, host) = CreateApp();
        var editor = await OpenAscom(vm.Equipment);

        Assert.True(editor.IsAscom);
        Assert.Equal(new[] { "Camera", "Focuser", "Mount" }, editor.TypeChoices.Select(t => t.Title).Order()); // no filter wheel, no guider
        Assert.Equal(["ASCOM Simulator Focuser Driver"], editor.Drivers.Select(d => d.Name));
        Assert.Equal("ASCOM.Simulator.Focuser", editor.ProgId);
        Assert.Equal("ASCOM Simulator Focuser Driver", editor.NameInput); // the driver names the device until the user does
        Assert.Equal("focuser.ascom-simulator-focuser-driver", editor.IdInput);

        editor.NameInput = "Main Focuser";
        editor.IdInput = "focuser.main";
        editor.SaveCommand.Execute(null);

        var focuser = Assert.Single(vm.Equipment.Focusers);
        Assert.Equal(("focuser.main", "Main Focuser", "ASCOM"), (focuser.DeviceIdText, focuser.Name, focuser.BackendText));
        Assert.Equal("ASCOM.Simulator.Focuser", focuser.DriverIdText);
        Assert.Equal(DeviceConnectionState.Disconnected, host.DeviceRegistry.GetAll().Single().ConnectionState);
        Assert.Empty(_log.Calls); // adding touches no driver
        var stored = new EquipmentConfigurationStore(File_).Load().Devices.Single();
        Assert.Equal(("focuser.main", DeviceBackend.Ascom, "ASCOM.Simulator.Focuser", "ASCOM Simulator Focuser Driver"), (stored.Id, stored.Backend, stored.ProgId, stored.DriverName));
    }

    [Fact]
    public async Task ATypedProgId_WinsOverTheList_AndIsStoredAsIs()
    {
        var (vm, _, _) = CreateApp();
        var editor = await OpenAscom(vm.Equipment);

        editor.CustomProgIdInput = "  ASCOM.Vendor.Focuser ";
        editor.NameInput = "Vendor Focuser";
        editor.IdInput = "focuser.vendor";
        editor.SaveCommand.Execute(null);

        Assert.Equal("ASCOM.Vendor.Focuser", new EquipmentConfigurationStore(File_).Load().Devices.Single().ProgId);
        Assert.Null(new EquipmentConfigurationStore(File_).Load().Devices.Single().DriverName); // the name belongs to a listed driver only
    }

    [Fact]
    public async Task ChangingTheKind_AsksDiscoveryForThatKind_AndReplacesTheDrivers()
    {
        var (vm, _, _) = CreateApp();
        var editor = await OpenAscom(vm.Equipment, DeviceType.Camera);
        Assert.Equal(["Camera V3 simulator", "ZWO Camera"], editor.Drivers.Select(d => d.Name));

        editor.SelectedType = editor.TypeChoices.Single(t => t.Type == DeviceType.Mount);
        await UxWait(() => !editor.IsDiscovering && editor.Drivers.Count == 1);

        Assert.Equal(["Telescope Simulator"], editor.Drivers.Select(d => d.Name));
        Assert.Equal([AscomDeviceKind.Camera, AscomDeviceKind.Mount], _discovery.Asked.TakeLast(2));
        Assert.Equal("mount.telescope-simulator", editor.IdInput);
    }

    [Fact]
    public async Task SwitchingToAscom_WhileAKindWithoutAnAscomAdapterIsChosen_ChoosesACamera()
    {
        var (vm, _, _) = CreateApp();
        var editor = Open(vm.Equipment);
        editor.SelectedType = editor.TypeChoices.Single(t => t.Type == DeviceType.FilterWheel);

        editor.SelectedBackend = editor.BackendChoices.Single(b => b.Backend == DeviceBackend.Ascom);
        await UxWait(() => !editor.IsDiscovering);

        Assert.Equal(DeviceType.Camera, editor.SelectedType.Type);
        Assert.DoesNotContain(editor.TypeChoices, t => t.Type is DeviceType.FilterWheel or DeviceType.Guider);

        editor.SelectedBackend = editor.BackendChoices.Single(b => b.Backend == DeviceBackend.Simulator);
        Assert.Contains(editor.TypeChoices, t => t.Type == DeviceType.FilterWheel);
        Assert.Empty(editor.Drivers);
    }

    [Fact]
    public async Task AMissingAscomPlatform_IsExplained_AndATypedProgIdStillWorks()
    {
        var (vm, _, _) = CreateApp();
        _discovery.Results[AscomDeviceKind.Focuser] = new(false, [], "ASCOM Platform is not installed on this computer.");

        var editor = await OpenAscom(vm.Equipment);

        Assert.True(editor.HasDiscoveryMessage);
        Assert.Contains("not installed", editor.DiscoveryText);
        Assert.Empty(editor.Drivers);
        Assert.True(editor.HasProblem);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.CustomProgIdInput = "ASCOM.Some.Focuser";
        editor.NameInput = "Focuser";

        Assert.False(editor.HasProblem);
        Assert.True(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task NoInstalledDriverOfAKind_IsSaidInAsentence()
    {
        var (vm, _, _) = CreateApp();
        _discovery.Results[AscomDeviceKind.Mount] = new(true, [], null);

        var editor = await OpenAscom(vm.Equipment, DeviceType.Mount);

        Assert.Contains("No ASCOM driver of this kind is installed", editor.DiscoveryText);
    }

    // The validation of the form

    [Theory]
    [InlineData("", "camera.a", "Give the device a name")]
    [InlineData("Name", "bad id", "can only contain")]
    [InlineData("Name", "", "required")]
    public void ABadForm_CannotBeSaved_AndSaysWhy(string name, string id, string expected)
    {
        var (vm, _, _) = CreateApp();
        var editor = Open(vm.Equipment);

        editor.NameInput = name;
        editor.IdInput = id;

        Assert.True(editor.HasProblem);
        Assert.Contains(expected, editor.ProblemText);
        Assert.False(editor.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void ATakenId_IsSaidBeforeSaving_AndTheSuggestionAvoidsIt()
    {
        var (vm, _, _) = CreateApp();
        var first = Open(vm.Equipment);
        first.NameInput = "Main";
        first.SaveCommand.Execute(null);
        Assert.Equal("camera.main", vm.Equipment.Cameras.Single().DeviceIdText);

        var second = Open(vm.Equipment);
        second.NameInput = "Main";

        Assert.Equal("camera.main-2", second.IdInput); // suggested, never in use

        second.IdInput = "camera.main";
        Assert.Contains("already exists", second.ProblemText);
        Assert.False(second.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void CancellingTheForm_ChangesNothing()
    {
        var (vm, _, host) = CreateApp();
        var editor = Open(vm.Equipment);
        editor.NameInput = "Camera";

        editor.CancelCommand.Execute(null);

        Assert.Null(vm.Equipment.Editor);
        Assert.Empty(host.DeviceRegistry.GetAll());
        Assert.False(File.Exists(File_));
    }

    [Fact]
    public void AFailureOfTheService_IsShownInTheForm_AndTheFormStaysOpen()
    {
        var (vm, service, _) = CreateApp();
        var editor = Open(vm.Equipment);
        editor.NameInput = "Camera";
        service.Add(DeviceConfiguration.Simulator(editor.IdInput, "Taken meanwhile", DeviceType.Camera)); // added behind the form's back

        editor.SaveCommand.Execute(null);

        Assert.NotNull(vm.Equipment.Editor);
        Assert.True(editor.HasFailure);
        Assert.Contains("already exists", editor.FailureText);
    }

    // Setup

    [Fact]
    public async Task TheSetupButton_OpensTheDriversDialog_ForTheChosenKindAndProgId_AndWaitsForIt()
    {
        var (vm, _, _) = CreateApp();
        var editor = await OpenAscom(vm.Equipment, DeviceType.Camera);
        _setup.Hold = new TaskCompletionSource();

        var setup = editor.SetupDriverCommand.ExecuteAsync(null);

        Assert.True(editor.IsBusy);
        Assert.False(editor.SaveCommand.CanExecute(null)); // the form waits for the dialog
        Assert.Equal([(AscomDeviceKind.Camera, "ASCOM.ZWO.Camera")], _setup.Shown); // the real driver, not the simulator that comes first

        _setup.Hold.SetResult();
        await setup;
        Assert.False(editor.IsBusy);
        Assert.False(editor.HasFailure);
    }

    [Fact]
    public async Task TheDriverThatIsPreselected_IsARealOneWhenThereIsOne_AndAPickFromTheListBeatsATypedProgId()
    {
        var (vm, _, _) = CreateApp();
        var editor = await OpenAscom(vm.Equipment, DeviceType.Camera);
        Assert.DoesNotContain("Simulator", editor.ProgId);

        editor.CustomProgIdInput = "ASCOM.Typed.Camera";
        Assert.Equal("ASCOM.Typed.Camera", editor.ProgId);
        editor.SelectedDriver = editor.Drivers.First(d => d.ProgId == "ASCOM.Simulator.Camera");

        Assert.Equal("ASCOM.Simulator.Camera", editor.ProgId);
        Assert.Equal(string.Empty, editor.CustomProgIdInput);
    }

    [Fact]
    public async Task ASetupDialogThatCannotBeOpened_IsAProblemInTheForm_NotACrash()
    {
        var (vm, _, _) = CreateApp();
        var editor = await OpenAscom(vm.Equipment, DeviceType.Camera);
        _setup.Result = new AscomSetupResult(false, "The setup dialog of ASCOM.Simulator.Camera could not be opened.");

        await editor.SetupDriverCommand.ExecuteAsync(null);

        Assert.True(editor.HasFailure);
        Assert.Contains("could not be opened", editor.FailureText);
        Assert.False(editor.IsBusy);
        Assert.NotNull(vm.Equipment.Editor);
    }

    [Fact]
    public void ASimulatorHasNoDriverToSetUp()
    {
        var (vm, _, _) = CreateApp();
        var editor = Open(vm.Equipment);

        Assert.False(editor.SetupDriverCommand.CanExecute(null));
    }

    // Editing

    [Fact]
    public async Task EditingADevice_KeepsItsIdKindAndBackend_AndReplacesItsViewModel_KeepingTheSelection()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("focuser.main", "Focuser", DeviceType.Focuser, "ASCOM.Simulator.Focuser"));
        var before = vm.Equipment.SelectedDevice!;

        vm.Equipment.SelectedDetail!.Configuration!.EditCommand.Execute(null);
        var editor = Assert.IsType<DeviceEditorViewModel>(vm.Equipment.Editor);
        await UxWait(() => !editor.IsDiscovering);

        Assert.Equal("Edit device", editor.Title);
        Assert.False(editor.CanChangeKind);
        Assert.False(editor.CanChangeId);
        Assert.Equal(("focuser.main", "Focuser"), (editor.IdInput, editor.NameInput));
        Assert.Equal("ASCOM.Simulator.Focuser", editor.ProgId); // the driver of the device is the chosen one

        editor.NameInput = "Renamed";
        editor.SaveCommand.Execute(null);

        Assert.Null(vm.Equipment.Editor);
        var after = Assert.Single(vm.Equipment.Focusers);
        Assert.NotSame(before, after);
        Assert.Equal(("focuser.main", "Renamed"), (after.DeviceIdText, after.Name));
        Assert.Same(after, vm.Equipment.SelectedDevice);
        Assert.Equal("Renamed", new EquipmentConfigurationStore(File_).Load().Devices.Single().Name);
    }

    [Fact]
    public void EditingADevice_LeavesItsPageOpen()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.a", "A", DeviceType.Camera));
        Assert.Equal(EquipmentPage.Camera, vm.Equipment.SelectedPage);

        service.Update(DeviceConfiguration.Simulator("camera.a", "Renamed", DeviceType.Camera));

        Assert.Equal(EquipmentPage.Camera, vm.Equipment.SelectedPage);
        Assert.Equal("Renamed", vm.Equipment.SelectedDetail!.Device.Name);
    }

    [Fact]
    public async Task EditingTheDriver_ToOneThatIsNotListed_ShowsItAsTheTypedProgId()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("focuser.main", "Focuser", DeviceType.Focuser, "ASCOM.Vendor.Focuser"));

        vm.Equipment.SelectedDetail!.Configuration!.EditCommand.Execute(null);
        var editor = vm.Equipment.Editor!;
        await UxWait(() => !editor.IsDiscovering);

        Assert.Null(editor.SelectedDriver);
        Assert.Equal("ASCOM.Vendor.Focuser", editor.CustomProgIdInput);
        Assert.Equal("ASCOM.Vendor.Focuser", editor.ProgId);
    }

    [Fact]
    public async Task AConnectedDevice_CannotBeEdited_AndTheConfigurationSaysWhy()
    {
        var (vm, service, host) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("focuser.main", "Focuser", DeviceType.Focuser, "ASCOM.Simulator.Focuser"));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;
        Assert.True(configuration.CanEdit);

        await host.DeviceOperations.ConnectAsync(new DeviceId("focuser.main"));

        Assert.False(configuration.CanEdit);
        Assert.False(configuration.EditCommand.CanExecute(null));
        Assert.False(configuration.RemoveCommand.CanExecute(null));
        Assert.Contains("Disconnect the device", configuration.EditBlockText);

        await host.DeviceOperations.DisconnectAsync(new DeviceId("focuser.main"));

        Assert.True(configuration.CanEdit);
        Assert.True(configuration.EditCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheDriversSetupOfADevice_CanBeOpenedWhileItIsDisconnected_AndReportsAProblem()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("camera.main", "Camera", DeviceType.Camera, "ASCOM.Simulator.Camera"));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;

        await configuration.SetupDriverCommand.ExecuteAsync(null);
        Assert.Equal([(AscomDeviceKind.Camera, "ASCOM.Simulator.Camera")], _setup.Shown);
        Assert.False(configuration.HasMessage);

        _setup.Result = new AscomSetupResult(false, "no dialog");
        await configuration.SetupDriverCommand.ExecuteAsync(null);
        Assert.Equal("no dialog", configuration.MessageText);
    }

    [Fact]
    public void ASimulatorHasNoDriverSetup_ButCanBeEditedAndRemoved()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.sim", "Camera", DeviceType.Camera));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;

        Assert.False(configuration.IsAscom);
        Assert.Equal("Built in", configuration.DriverText);
        Assert.False(configuration.SetupDriverCommand.CanExecute(null));
        Assert.True(configuration.EditCommand.CanExecute(null));
        Assert.True(configuration.RemoveCommand.CanExecute(null));
    }

    // Removing

    [Fact]
    public void RemovingADevice_NeedsASecondClick_AndThenTakesItEverywhereOut()
    {
        var (vm, service, host) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.a", "A", DeviceType.Camera));
        service.Add(DeviceConfiguration.Simulator("camera.b", "B", DeviceType.Camera));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;
        Assert.Equal("camera.b", vm.Equipment.SelectedDevice!.DeviceIdText); // the one added last is shown

        configuration.RemoveCommand.Execute(null);
        Assert.True(configuration.IsConfirmingRemove);
        Assert.Equal(2, host.DeviceRegistry.GetAll().Count); // nothing yet

        configuration.CancelRemoveCommand.Execute(null);
        Assert.False(configuration.IsConfirmingRemove);
        Assert.Equal(2, host.DeviceRegistry.GetAll().Count);

        configuration.RemoveCommand.Execute(null);
        configuration.ConfirmRemoveCommand.Execute(null);

        Assert.Equal(["camera.a"], host.DeviceRegistry.GetAll().Select(d => d.Id.Value));
        Assert.Equal(["camera.a"], vm.Equipment.Cameras.Select(c => c.DeviceIdText));
        Assert.Equal("camera.a", vm.Equipment.SelectedDevice!.DeviceIdText); // the selection moves to a neighbour
        Assert.Equal(["camera.a"], new EquipmentConfigurationStore(File_).Load().Devices.Select(d => d.Id));
    }

    [Fact]
    public void RemovingTheLastDevice_LeavesTheEmptyWorkspace()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.a", "A", DeviceType.Camera));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;

        configuration.RemoveCommand.Execute(null);
        configuration.ConfirmRemoveCommand.Execute(null);

        Assert.False(vm.Equipment.HasDevices);
        Assert.Null(vm.Equipment.SelectedDevice);
        Assert.Null(vm.Equipment.SelectedDetail);
        Assert.False(vm.Equipment.HasLandingGroups);
    }

    [Fact]
    public void ADeviceOfARig_CannotBeRemoved_AndTheConfigurationNamesTheRig()
    {
        var (vm, _, _) = CreateApp(DemoSetup.Configuration());
        vm.Equipment.Focusers.Single(f => f.DeviceIdText == "focuser.main").OpenCommand!.Execute(null);
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;

        Assert.False(configuration.CanRemove);
        Assert.True(configuration.HasRemoveBlock);
        Assert.Contains("part of the rig 'Main Rig'", configuration.RemoveBlockText);
        Assert.False(configuration.RemoveCommand.CanExecute(null));
        Assert.True(configuration.CanEdit); // it can still be renamed
    }

    [Fact]
    public void ADeviceTheSequenceUses_CannotBeRemoved()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        service.Add(DeviceConfiguration.Simulator("camera.spare", "Spare", DeviceType.Camera));
        vm.SequenceDraft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.main"), 1)]);

        var used = vm.Equipment.Cameras.Single(c => c.DeviceIdText == "camera.main");
        var spare = vm.Equipment.Cameras.Single(c => c.DeviceIdText == "camera.spare");

        Assert.Contains("used by a step of the current sequence", vm.Equipment.WhyCannotRemove(used));
        Assert.Null(vm.Equipment.WhyCannotRemove(spare));
        Assert.Contains("used by a step", vm.Equipment.Remove(used));
        Assert.Equal(2, vm.Equipment.Cameras.Count);
    }

    // The other pages follow

    [Fact]
    public void ADeviceThatIsAddedLater_IsFollowedByTheShell_AndTheSessionCanUseIt()
    {
        var (vm, service, host) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var camera = vm.Equipment.Cameras.Single();
        var refreshes = 0;
        vm.Equipment.DevicesChanged += (_, _) => refreshes++;

        service.Add(DeviceConfiguration.Simulator("mount.main", "Mount", DeviceType.Mount));

        Assert.Equal(1, refreshes);
        Assert.Equal(["camera.main"], vm.Equipment.Cameras.Select(c => c.DeviceIdText));
        Assert.Same(camera, vm.Equipment.Cameras.Single());
        Assert.Equal(["mount.main"], vm.Equipment.Mounts.Select(m => m.DeviceIdText));
        Assert.Equal(2, host.DeviceRegistry.GetAll().Count);
    }

    [Fact]
    public async Task ADeviceThatIsAddedLater_ReportsItsConnection_ToTheRuntimeSummary()
    {
        var (vm, service, host) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));

        await host.DeviceOperations.ConnectAsync(new DeviceId("camera.main"));

        Assert.True(vm.Equipment.Cameras.Single().IsConnected);
        Assert.Equal(DeviceConnectionState.Connected, host.DeviceRegistry.GetAll().Single().ConnectionState);
    }

    [Fact]
    public async Task SimulatorsAndAscomDevices_ListedTogether_AreConnectedAndUsedThroughTheSameViewModels()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Simulator("camera.sim", "Sim Camera", DeviceType.Camera));
        service.Add(DeviceConfiguration.Ascom("focuser.main", "ASCOM Focuser", DeviceType.Focuser, "ASCOM.Simulator.Focuser"));
        var camera = vm.Equipment.Cameras.Single();
        var focuser = vm.Equipment.Focusers.Single();

        await camera.ConnectCommand.ExecuteAsync(null);
        await focuser.ConnectCommand.ExecuteAsync(null);
        focuser.TargetInput = "31000";
        await focuser.MoveCommand.ExecuteAsync(null);

        Assert.Equal(("Simulator", "ASCOM"), (camera.BackendText, focuser.BackendText));
        Assert.True(camera.IsConnected && focuser.IsConnected);
        Assert.Equal("31000 steps", focuser.PositionText);
        Assert.Equal(31000, _drivers.Focusers.Single().PositionValue);
        Assert.False(focuser.HasError);
        await focuser.DisconnectCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task AFailureOfAnAscomDevice_IsShownLikeAnyOther_AsASentenceOnTheDevice()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("focuser.main", "ASCOM Focuser", DeviceType.Focuser, "ASCOM.Simulator.Focuser"));
        _drivers.ConfigureFocuser = d => d.ConnectThrows = new System.Runtime.InteropServices.COMException("The device is not plugged in", unchecked((int)0x80004005));
        var focuser = vm.Equipment.Focusers.Single();

        await focuser.ConnectCommand.ExecuteAsync(null);

        Assert.True(focuser.HasError);
        Assert.Contains("Could not connect ASCOM Focuser (ASCOM.Simulator.Focuser)", focuser.ErrorMessage);
        Assert.Contains("not plugged in", focuser.ErrorMessage);
        Assert.False(focuser.IsConnected);
    }

    [Fact]
    public void TheDetail_OfADevice_HasItsConfigurationInTheSettingsTab()
    {
        var (vm, service, _) = CreateApp();
        service.Add(DeviceConfiguration.Ascom("camera.main", "Camera", DeviceType.Camera, "ASCOM.Simulator.Camera"));
        var configuration = vm.Equipment.SelectedDetail!.Configuration!;

        Assert.Equal(("ASCOM", "ASCOM.Simulator.Camera", true), (configuration.BackendText, configuration.DriverText, configuration.IsAscom));
    }

    // ---- The slots: one choice of driver for each kind of device

    private static DeviceSlotViewModel Slot(EquipmentViewModel equipment, DeviceType type) => equipment.Slots.Single(sl => sl.Type == type);

    private static DriverChoice Choice(DeviceSlotViewModel slot, string text) => slot.Choices.Single(c => c.Text == text);

    [Fact]
    public async Task TheEquipmentPage_HasATabForEachKind_NothingSelected_AndEveryKindStartsWithNoDeviceAndNoChoice()
    {
        var (vm, _, _) = CreateApp();
        var equipment = vm.Equipment;

        Assert.Equal(["Camera", "Mount", "Focuser", "Filter Wheel", "Guider"], equipment.Slots.Select(sl => sl.Title));
        Assert.Null(equipment.SelectedSlot);
        Assert.All(equipment.Slots, sl =>
        {
            Assert.False(sl.IsSelected);
            Assert.True(sl.IsEmpty);
            Assert.Null(sl.SelectedChoice);
            Assert.True(sl.Choices[0].IsNone);
            Assert.Contains(sl.Choices, c => c.Text == "Simulator");
        });
        await UxWait(() => Slot(equipment, DeviceType.Camera).Choices.Any(c => c.IsAscom));
        Assert.DoesNotContain(Slot(equipment, DeviceType.FilterWheel).Choices, c => c.IsAscom); // no ASCOM adapter for it
    }

    [Fact]
    public async Task ChoosingADriver_MakesTheDevice_WithAStableId_AndItIsKept()
    {
        var (vm, service, _) = CreateApp();
        var slot = Slot(vm.Equipment, DeviceType.Camera);

        slot.SelectedChoice = Choice(slot, "Simulator");

        Assert.Equal("camera.main", Assert.Single(vm.Equipment.Cameras).DeviceIdText);
        Assert.False(slot.IsEmpty);
        Assert.NotNull(slot.Detail);
        Assert.Equal("Simulator", slot.SelectedChoice!.Text);
        Assert.Equal(DeviceBackend.Simulator, service.Configuration.Find("camera.main")!.Backend);
    }

    [Fact]
    public async Task ChoosingAnotherDriver_ReplacesTheDevice_AndKeepsItsId()
    {
        var (vm, service, _) = CreateApp();
        var slot = Slot(vm.Equipment, DeviceType.Camera);
        await UxWait(() => slot.Choices.Any(c => c.IsAscom));
        slot.SelectedChoice = Choice(slot, "Simulator");

        slot.SelectedChoice = Choice(slot, "ZWO Camera");

        var configuration = service.Configuration.Find("camera.main")!;
        Assert.Equal((DeviceBackend.Ascom, "ASCOM.ZWO.Camera"), (configuration.Backend, configuration.ProgId));
        Assert.Equal("camera.main", Assert.Single(vm.Equipment.Cameras).DeviceIdText);
        Assert.Equal("ZWO Camera", slot.SelectedChoice!.Text);
        Assert.False(slot.HasProblem);
    }

    [Fact]
    public async Task ChoosingNone_RemovesTheDevice()
    {
        var (vm, service, _) = CreateApp();
        var slot = Slot(vm.Equipment, DeviceType.Focuser);
        slot.SelectedChoice = Choice(slot, "Simulator");

        slot.SelectedChoice = slot.Choices[0];

        Assert.True(slot.IsEmpty);
        Assert.Empty(vm.Equipment.Focusers);
        Assert.Null(service.Configuration.Find("focuser.main"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ADeviceThatIsConnected_IsNotSwapped_TheSlotSaysSo_AndKeepsItsChoice()
    {
        var (vm, service, _) = CreateApp();
        var slot = Slot(vm.Equipment, DeviceType.Mount);
        slot.SelectedChoice = Choice(slot, "Simulator");
        await slot.Device!.ConnectCommand.ExecuteAsync(null);

        slot.SelectedChoice = slot.Choices[0];

        Assert.True(slot.HasProblem);
        Assert.Contains("Disconnect", slot.ProblemText);
        Assert.False(slot.IsEmpty);
        Assert.Equal("Simulator", slot.SelectedChoice!.Text);
        Assert.NotNull(service.Configuration.Find("mount.main"));
    }

    [Fact]
    public async Task Setup_OpensTheDialogOfTheChosenAscomDriver_AndIsNotOfferedForTheSimulator()
    {
        var (vm, _, _) = CreateApp();
        var slot = Slot(vm.Equipment, DeviceType.Camera);
        await UxWait(() => slot.Choices.Any(c => c.IsAscom));
        slot.SelectedChoice = Choice(slot, "Simulator");
        Assert.False(slot.SetupCommand.CanExecute(null));

        slot.SelectedChoice = Choice(slot, "ZWO Camera");
        Assert.True(slot.SetupCommand.CanExecute(null));
        await slot.SetupCommand.ExecuteAsync(null);

        Assert.Equal([(AscomDeviceKind.Camera, "ASCOM.ZWO.Camera")], _setup.Shown);
    }

    [Fact]
    public async Task AStoredDevice_ShowsItsDriverAsTheChoice_EvenWhenTheDriverIsNoLongerInstalled()
    {
        var stored = new EquipmentConfiguration([DeviceConfiguration.Ascom("camera.asi", "Gone Camera", DeviceType.Camera, "ASCOM.Gone.Camera", "Gone Camera")], []);
        var (vm, _, _) = CreateApp(stored);
        var slot = Slot(vm.Equipment, DeviceType.Camera);

        await UxWait(() => slot.Choices.Any(c => c.IsAscom));

        Assert.Equal("ASCOM.Gone.Camera", slot.SelectedChoice!.ProgId);
        Assert.Equal("camera.asi", slot.Device!.DeviceIdText);
    }
}

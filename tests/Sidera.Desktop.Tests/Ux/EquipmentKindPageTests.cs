using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The equipment by kind of device: every kind has its page with zero or more devices, another device is added on the page of its kind and named there, one device is simply the device, and the imaging
/// setups are a section only when there are some. Nothing is called "standalone".
/// </summary>
public sealed class EquipmentKindPageTests : IAsyncLifetime
{
    private sealed class NoDiscovery : IAscomDiscovery
    {
        public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : IAscomSetupService
    {
        public Task<AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) => Task.FromResult(new AscomSetupResult(true, null));
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-kind-pages-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public EquipmentKindPageTests() => Directory.CreateDirectory(_directory);

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

    private (MainViewModel Vm, EquipmentService Service) CreateApp()
    {
        var host = new SideraRuntimeHost();
        var options = new DemoOptions { ManualExposure = TimeSpan.FromMilliseconds(30) };
        var service = new EquipmentService(host, new EquipmentConfigurationStore(Path.Combine(_directory, "equipment.json")), new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]));
        service.Load();
        var vm = new MainViewModel(host, a => a(), options, equipmentManagement: new EquipmentManagement(service, new NoDiscovery(), new NoSetup()));
        _apps.Add((host, vm));
        return (vm, service);
    }

    private static void Show(EquipmentViewModel equipment, EquipmentSection section) => equipment.Sections.Single(s => s.Section == section).SelectCommand.Execute(null);

    private static void ChooseSimulator(DeviceSlotViewModel slot) => slot.SelectedChoice = slot.Choices.Single(c => c.Backend == DeviceBackend.Simulator);

    // ---- one device of a kind

    [Fact]
    public void AnEmptyInstallation_HasAPageForEveryKind_AndNothingAboutSetups()
    {
        var (vm, _) = CreateApp();
        var equipment = vm.Equipment;

        Assert.Equal(["Overview", "Cameras", "Focusers", "Filter Wheels", "Rotators", "Mounts", "Guiders"], equipment.Sections.Select(s => s.Title));
        Assert.True(equipment.ShowSections); // the first device is added on the page of its kind

        Show(equipment, EquipmentSection.Cameras);

        Assert.True(equipment.IsDeviceWorkspace);
        Assert.Null(equipment.SelectedDevice);
        Assert.True(equipment.SelectedSlot!.IsEmpty);
        Assert.False(equipment.HasDeviceChooser);
        Assert.Equal("+ Add Camera", equipment.AddKindText);
    }

    [Fact]
    public void TheFirstDevice_IsChosenByItsDriver_AndIsThenSimplyTheDevice_WithNoChooser()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);

        ChooseSimulator(equipment.SelectedSlot!);

        Assert.Equal(["camera.main"], equipment.Cameras.Select(c => c.DeviceIdText));
        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText);
        Assert.False(equipment.HasDeviceChooser); // one camera: nothing to choose
        Assert.Equal("Simulator", equipment.NameText);
        Assert.Equal(EquipmentSection.Cameras, equipment.SelectedSection);
        Assert.NotNull(service.ConfigurationOf("camera.main"));
        Assert.False(equipment.HasImagingSetups);
        Assert.False(equipment.NeedsSetupHint);
    }

    // ---- another device

    [Fact]
    public void AnotherDevice_IsAddedOnThePageOfItsKind_WithAnIdAndANameOfItsOwn_AndThenIsChosenFromAList()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);

        equipment.AddToKindCommand.Execute(null);

        Assert.True(equipment.IsAddingDevice);
        Assert.Null(equipment.SelectedDevice); // an empty slot for the driver of the new camera
        Assert.True(equipment.SelectedSlot!.IsEmpty);
        Assert.Contains("new camera", equipment.SelectedSlot.EmptyText, StringComparison.Ordinal);
        Assert.False(equipment.AddToKindCommand.CanExecute(null));

        ChooseSimulator(equipment.SelectedSlot);

        Assert.False(equipment.IsAddingDevice);
        Assert.Equal(["camera.main", "camera.main-2"], equipment.Cameras.Select(c => c.DeviceIdText));
        Assert.Equal(["Simulator", "Simulator 2"], equipment.Cameras.Select(c => c.Name)); // two devices of a kind are told apart by name
        Assert.Equal("camera.main-2", equipment.SelectedDevice!.DeviceIdText); // the new one is shown
        Assert.True(equipment.HasDeviceChooser);
        Assert.Equal(["camera.main", "camera.main-2"], equipment.DeviceChoices.Select(d => d.DeviceIdText));
        Assert.NotNull(service.ConfigurationOf("camera.main")); // the first one was not replaced
        Assert.NotNull(service.ConfigurationOf("camera.main-2"));
    }

    [Fact]
    public void CancellingAnAdd_ShowsTheDeviceThatWasThere()
    {
        var (vm, _) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);
        equipment.AddToKindCommand.Execute(null);

        equipment.CancelAddCommand.Execute(null);

        Assert.False(equipment.IsAddingDevice);
        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText);
        Assert.Single(equipment.Cameras);
    }

    [Fact]
    public void EachKindTakesMoreThanOneDevice()
    {
        var (vm, _) = CreateApp();
        var equipment = vm.Equipment;
        foreach (var (section, expected) in new[]
                 {
                     (EquipmentSection.Cameras, "camera"), (EquipmentSection.Focusers, "focuser"), (EquipmentSection.FilterWheels, "filterwheel"), (EquipmentSection.Rotators, "rotator"),
                     (EquipmentSection.Mounts, "mount"), (EquipmentSection.Guiders, "guider"),
                 })
        {
            Show(equipment, section);
            ChooseSimulator(equipment.SelectedSlot!);
            equipment.AddToKindCommand.Execute(null);
            ChooseSimulator(equipment.SelectedSlot!);

            Assert.Equal([$"{expected}.main", $"{expected}.main-2"], equipment.DeviceChoices.Select(d => d.DeviceIdText));
        }
    }

    // ---- names

    [Fact]
    public void ADeviceIsRenamed_AndKeepsItsId()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);

        equipment.NameText = "Wide Camera";
        equipment.RenameCommand.Execute(null);

        Assert.Equal("Wide Camera", service.ConfigurationOf("camera.main")!.Name);
        Assert.Equal("Wide Camera", equipment.Cameras.Single().Name);
        Assert.Equal("camera.main", equipment.SelectedDevice!.DeviceIdText); // what sequences refer to does not change
        Assert.False(equipment.HasRenameProblem);
    }

    [Fact]
    public async Task ANameThatCannotBeUsed_IsRefusedInOneSentence_AndAConnectedDeviceIsNotRenamed()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);
        equipment.NameText = "Main";
        equipment.RenameCommand.Execute(null);
        equipment.AddToKindCommand.Execute(null);
        ChooseSimulator(equipment.SelectedSlot!);

        equipment.NameText = "main"; // the other camera has it
        equipment.RenameCommand.Execute(null);
        Assert.Contains("already a camera named", equipment.RenameProblemText, StringComparison.Ordinal);

        equipment.NameText = "  ";
        equipment.RenameCommand.Execute(null);
        Assert.Contains("needs a name", equipment.RenameProblemText, StringComparison.Ordinal);
        Assert.Equal("Simulator", service.ConfigurationOf("camera.main-2")!.Name);

        await equipment.SelectedDevice!.ConnectCommand.ExecuteAsync(null);
        equipment.NameText = "Other";
        equipment.RenameCommand.Execute(null);
        Assert.Contains("Disconnect", equipment.RenameProblemText, StringComparison.Ordinal);
        Assert.Equal("Simulator", service.ConfigurationOf("camera.main-2")!.Name);
    }

    // ---- imaging setups

    [Fact]
    public void TwoCameras_AreToldAboutImagingSetups_AndTheSectionAppearsWhenOneIsMade()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);
        equipment.AddToKindCommand.Execute(null);
        ChooseSimulator(equipment.SelectedSlot!);

        Assert.True(equipment.NeedsSetupHint); // two cameras: Sidera does not guess which one an instruction means
        Assert.False(equipment.HasImagingSetups);
        Assert.DoesNotContain(equipment.Sections, s => s.Section == EquipmentSection.ImagingSetups);

        Assert.True(service.AddRig("Main 750", "camera.main").Succeeded);

        Assert.True(equipment.HasImagingSetups);
        Assert.False(equipment.NeedsSetupHint);
        Assert.Contains(equipment.Sections, s => s.Section == EquipmentSection.ImagingSetups);
        Show(equipment, EquipmentSection.ImagingSetups);
        Assert.True(equipment.IsImagingSetupsSection);
        Assert.Equal(["Main 750"], equipment.SetupContexts.Select(c => c.Title));
        Assert.True(equipment.IsRigOverview);
    }

    [Fact]
    public void NothingOnThePageIsCalledStandalone()
    {
        var (vm, service) = CreateApp();
        var equipment = vm.Equipment;
        Show(equipment, EquipmentSection.Cameras);
        ChooseSimulator(equipment.SelectedSlot!);
        service.AddRig("Main 750", "camera.main");

        var words = new List<string>();
        words.AddRange(equipment.Sections.Select(s => s.Title));
        words.AddRange(equipment.Contexts.Select(c => c.Title));
        words.AddRange(equipment.Pages.Select(p => p.Title));
        words.AddRange(equipment.LandingGroups.Select(g => g.Title));
        words.AddRange(equipment.Breadcrumb.Select(b => b.Title));
        words.Add(vm.Runtime.ModeText);

        Assert.DoesNotContain(words, w => w.Contains("standalone", StringComparison.OrdinalIgnoreCase));
    }
}

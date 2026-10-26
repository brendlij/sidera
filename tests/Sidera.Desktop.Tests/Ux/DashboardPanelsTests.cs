using System.Text;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>Which panels the dashboard shows: all of them until the settings say otherwise, saved with the appearance, and followed by the dashboard at once.</summary>
public sealed class DashboardPanelsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-panels-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
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

    private (MainViewModel Vm, SiteService Site) Create()
    {
        Directory.CreateDirectory(_directory);
        var host = new SideraRuntimeHost();
        host.AddSimulatedMount(new("mount.a"), "EQ", TimeSpan.FromMilliseconds(20));
        var site = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        site.Load();
        var service = new Sidera.Desktop.Hardware.EquipmentService(
            host, new Sidera.Desktop.Hardware.EquipmentConfigurationStore(Path.Combine(_directory, "equipment.json")), new Sidera.Desktop.Hardware.DeviceFactoryRegistry([new Sidera.Desktop.Hardware.SimulatorDeviceFactory()]));
        var management = new EquipmentManagement(service, new NoDiscovery(), new NoSetup(), site);
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), withDemoSequence: false, equipmentManagement: management, startWithSettingsMode: true);
        _apps.Add((host, vm));
        return (vm, site);
    }

    [Fact]
    public void AllPanelsAreShown_UntilTheSettingsSayOtherwise()
    {
        var panels = new AppearanceSettings();

        Assert.True(panels.DashboardSetups && panels.DashboardMount && panels.DashboardGuiding && panels.DashboardImage && panels.DashboardShared);
    }

    [Fact]
    public void ThePanels_RoundTrip_AndAnOldFileShowsAllOfThem()
    {
        var settings = SideraSettings.Empty with { Appearance = new AppearanceSettings { DashboardMount = false, DashboardImage = false } };

        var loaded = SideraSettingsSerializer.Deserialize(SideraSettingsSerializer.Serialize(settings)).Appearance;

        Assert.Equal(settings.Appearance, loaded);
        var old = SideraSettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"format\":\"sidera-settings\",\"version\":1,\"appearance\":{\"theme\":\"midnight\"}}")).Appearance;
        Assert.True(old.DashboardMount && old.DashboardImage && old.DashboardGuiding && old.DashboardSetups && old.DashboardShared);
    }

    [Fact]
    public void ThePanelsOfTheDashboard_FollowTheSettings_AtOnce()
    {
        var (vm, site) = Create();
        Assert.True(vm.Dashboard.ShowMountPanel);
        Assert.True(vm.Dashboard.ShowImagePanel);

        site.SetAppearance(site.Appearance with { DashboardMount = false, DashboardImage = false });

        Assert.False(vm.Dashboard.ShowMountPanel);
        Assert.False(vm.Dashboard.ShowImagePanel);

        site.SetAppearance(site.Appearance with { DashboardMount = true });

        Assert.True(vm.Dashboard.ShowMountPanel);
    }

    [Fact]
    public void TheSettingsPage_ShowsThePanels_AndSavesWhatWasChosen()
    {
        var (vm, site) = Create();
        var tab = new AppearanceSettingsViewModel(site);
        Assert.True(tab.DashboardMount);

        tab.DashboardMount = false;
        tab.DashboardGuiding = false;
        tab.SaveCommand.Execute(null);

        Assert.False(tab.ResultIsProblem);
        Assert.False(site.Appearance.DashboardMount);
        Assert.False(site.Appearance.DashboardGuiding);
        Assert.True(site.Appearance.DashboardImage);
        Assert.False(vm.Dashboard.ShowMountPanel);
        Assert.False(new AppearanceSettingsViewModel(site).DashboardMount); // kept for the next start
    }
}

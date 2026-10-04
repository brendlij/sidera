using Sidera.Ascom;
using Sidera.Ascom.Tests;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// The acquisition defaults of a camera belong to the equipment configuration: persisted with the device, read by the runtime from
/// there, kept when the driver of the device changes, and never part of a sequence document.
/// </summary>
public sealed class AcquisitionDefaultsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-acquisition-defaults-" + Guid.NewGuid().ToString("N"));
    private readonly List<SideraRuntimeHost> _hosts = [];

    public AcquisitionDefaultsTests()
    {
        Directory.CreateDirectory(_directory);
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

    private string File_ => Path.Combine(_directory, "equipment.json");

    private (EquipmentService Service, SideraRuntimeHost Host) Create()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var factories = new DeviceFactoryRegistry(
        [
            new SimulatorDeviceFactory(new DemoOptions()),
            new AscomBackendFactory(new AscomDeviceFactory(new FakeDriverFactory(new CallLog()), null, FastTimings.Create())),
        ]);
        return (new EquipmentService(host, new EquipmentConfigurationStore(File_), factories), host);
    }

    private static readonly DeviceId Camera = new("camera.main");

    private static readonly AcquisitionIntent Defaults = new()
    {
        Gain = AcquisitionLevel.OfNumber(100), Offset = AcquisitionLevel.OfNumber(20), BinX = 1, BinY = 1, Region = AcquisitionRegion.Full, ReadoutMode = "Normal",
    };

    [Fact]
    public void SavingThePreferencesOfACamera_GivesTheRuntimeItsDefaults_AndTheFileKeepsThem()
    {
        var (service, host) = Create();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        Assert.Null(host.AcquisitionDefaults.DefaultsFor(Camera));

        var problem = service.SavePreferences("camera.main", DevicePreferences.WithAcquisition(DeviceConfiguration.NoSettings, Defaults));

        Assert.Null(problem);
        Assert.Equal(Defaults, host.AcquisitionDefaults.DefaultsFor(Camera));
        var text = System.IO.File.ReadAllText(File_);
        Assert.Contains("camera.gain", text, StringComparison.Ordinal);
        Assert.Contains("camera.region", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsComeBackWithTheEquipment_WhenSideraStartsAgain()
    {
        var (first, _) = Create();
        first.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        first.SavePreferences("camera.main", DevicePreferences.WithAcquisition(DeviceConfiguration.NoSettings, Defaults));

        var (second, host) = Create();
        second.Load();

        Assert.Equal(Defaults, host.AcquisitionDefaults.DefaultsFor(Camera));
    }

    [Fact]
    public void ADefaultThatNoCameraHasAnyMore_IsNotKept_AndRemovingTheCameraDropsIt()
    {
        var (service, host) = Create();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        service.SavePreferences("camera.main", DevicePreferences.WithAcquisition(DeviceConfiguration.NoSettings, Defaults));

        var removed = service.Remove("camera.main");

        Assert.True(removed.Succeeded);
        Assert.Null(host.AcquisitionDefaults.DefaultsFor(Camera));
    }

    [Fact]
    public void ChangingTheNameOfACamera_KeepsItsDefaults()
    {
        var (service, host) = Create();
        service.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        service.SavePreferences("camera.main", DevicePreferences.WithAcquisition(DeviceConfiguration.NoSettings, Defaults));

        var result = service.Update(DeviceConfiguration.Simulator("camera.main", "Renamed", DeviceType.Camera));

        Assert.True(result.Succeeded, result.Problem);
        Assert.Equal(Defaults, host.AcquisitionDefaults.DefaultsFor(Camera));
        Assert.Equal("100", service.GetPreferences("camera.main")[DevicePreferences.Gain]);
    }

    [Fact]
    public void ADeviceThatIsNotACamera_HasNoAcquisitionDefaults()
    {
        var (service, host) = Create();
        service.Add(DeviceConfiguration.Simulator("focuser.main", "Focuser", DeviceType.Focuser));

        service.SavePreferences("focuser.main", DevicePreferences.WithTempComp(true, DeviceConfiguration.NoSettings));

        Assert.Null(host.AcquisitionDefaults.DefaultsFor(new DeviceId("focuser.main")));
    }

    [Fact]
    public void AnEmptyDefault_IsNoDefault()
    {
        var registry = new Sidera.Runtime.Devices.AcquisitionDefaultsRegistry();
        registry.Set(Camera, Defaults);

        registry.Set(Camera, AcquisitionIntent.Default);

        Assert.Null(registry.DefaultsFor(Camera));
    }
}

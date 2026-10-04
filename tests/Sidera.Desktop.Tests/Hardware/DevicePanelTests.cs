using Sidera.Ascom;
using Sidera.Ascom.Cameras;
using Sidera.Ascom.Focusers;
using Sidera.Ascom.Mounts;
using Sidera.Ascom.Tests;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// The capability-driven panels of the Equipment page: they show only what the connected device supports, the same for the
/// simulator and for an ASCOM device (here behind fake drivers), and keep their preferences in the equipment configuration.
/// </summary>
public sealed class DevicePanelTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<IDisposable> _panels = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var panel in _panels)
        {
            panel.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private sealed class MemoryPreferences : IDevicePreferenceStore
    {
        public Dictionary<string, IReadOnlyDictionary<string, string>> Stored { get; } = [];

        public IReadOnlyDictionary<string, string> GetPreferences(string deviceId) =>
            Stored.TryGetValue(deviceId, out var value) ? value : DeviceConfiguration.NoSettings;

        public string? SavePreferences(string deviceId, IReadOnlyDictionary<string, string> preferences)
        {
            Stored[deviceId] = preferences;
            return null;
        }
    }

    private SideraRuntimeHost NewHost()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        return host;
    }

    private (CameraSettingsViewModel Panel, ICameraControl Camera) CameraPanel(ICameraControl camera, MemoryPreferences? preferences = null)
    {
        var host = NewHost();
        host.AddDevice(camera);
        var vm = new CameraViewModel(camera, host, a => a(), new SessionActivity(), new ImagingViewModel(), TimeSpan.FromSeconds(1));
        var panel = new CameraSettingsViewModel(vm, camera, preferences);
        _panels.Add(panel);
        return (panel, camera);
    }

    private static async Task Settle(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(5);
        }
    }

    private static AscomCamera FakeAscomCamera(FakeDriverFactory drivers) =>
        new(new DeviceId("camera.ascom"), "ASCOM Camera", "ASCOM.Test.Camera", drivers, null, null, FastTimings.Create());

    // ---- Persistence

    [Fact]
    public void Preferences_AreWrittenAndReadBack_AndAFileWithoutThemStillLoads()
    {
        var device = DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera) with
        {
            Preferences = new Dictionary<string, string> { [DevicePreferences.Gain] = "100", [DevicePreferences.BinX] = "2" },
        };
        var configuration = new EquipmentConfiguration([device], []);

        var read = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(configuration));

        Assert.Equal("100", read.Devices[0].Preferences[DevicePreferences.Gain]);
        Assert.Equal("2", read.Devices[0].Preferences[DevicePreferences.BinX]);
        var old = """{"format":"astra-equipment","version":1,"devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}]}""";
        Assert.Empty(EquipmentConfigurationSerializer.Deserialize(System.Text.Encoding.UTF8.GetBytes(old)).Devices[0].Preferences);
    }

    [Fact]
    public void CameraDefaults_AreKeptAsIntent_AndNeverIncludeTheCooler()
    {
        var intent = new AcquisitionIntent
        {
            Gain = AcquisitionLevel.OfNumber(5), Offset = AcquisitionLevel.OfName("High"), BinX = 2, BinY = 2,
            Region = AcquisitionRegion.Of(1, 2, 30, 40), ReadoutMode = "Slow", FastReadout = true,
        };

        var preferences = DevicePreferences.WithTargetTemperature(
            DevicePreferences.WithAcquisition(DeviceConfiguration.NoSettings, intent), -10);

        Assert.Equal("5", preferences[DevicePreferences.Gain]);
        Assert.Equal("High", preferences[DevicePreferences.OffsetName]);
        Assert.Equal("1,2,30,40", preferences[DevicePreferences.Region]);
        Assert.Equal("Slow", preferences[DevicePreferences.ReadoutMode]);
        Assert.Equal("-10", preferences[DevicePreferences.TargetTemperature]);
        Assert.DoesNotContain(preferences.Keys, k => k.Contains("cooler", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(intent, DevicePreferences.AcquisitionDefaults(preferences));
        Assert.Equal(AcquisitionRegion.Full, DevicePreferences.AcquisitionDefaults(
            new Dictionary<string, string> { [DevicePreferences.Region] = "full" }).Region);
    }

    // ---- Camera panel

    [Fact]
    public async Task ACameraPanel_OffersNothingWhileDisconnected_AndOnlyWhatTheCameraSupportsAfterwards()
    {
        var drivers = new FakeDriverFactory(new CallLog())
        {
            ConfigureCamera = d => { d.GainValue = 10; d.GainMinValue = 0; d.GainMaxValue = 300; d.MaxBinX = d.MaxBinY = 3; },
        };
        var (panel, camera) = CameraPanel(FakeAscomCamera(drivers));
        Assert.False(panel.IsAvailable);
        Assert.False(panel.Gain.IsAvailable);
        Assert.False(panel.ShowBinning);

        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        Assert.True(panel.Gain.IsRange);
        Assert.Equal("0 to 300", panel.Gain.Hint);
        Assert.False(panel.Offset.IsAvailable);
        Assert.True(panel.ShowBinning);
        Assert.False(panel.ShowAsymmetricBinning);
        Assert.Equal([1, 2, 3], panel.BinChoices);
        Assert.False(panel.ShowCooling);
        Assert.False(panel.ShowReadout);
        Assert.False(panel.ShowFastReadout);
        Assert.Contains(panel.DriverInfo, l => l.Label == "Sensor");

        await camera.DisconnectAsync();
        await Settle(() => !panel.IsAvailable);
        Assert.False(panel.Gain.IsAvailable);
    }

    [Fact]
    public async Task ANamedGain_IsShownAsChoices()
    {
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureCamera = d => { d.GainValue = 1; d.GainsValue = ["Low", "High", "HDR"]; } };
        var (panel, camera) = CameraPanel(FakeAscomCamera(drivers));

        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        Assert.True(panel.Gain.IsList);
        Assert.Equal(["Low", "High", "HDR"], panel.Gain.Choices);
        Assert.Equal(1, panel.Gain.SelectedIndex);
    }

    [Fact]
    public async Task TheSimulatorAndAnAscomCamera_FillTheSamePanel()
    {
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated);

        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        Assert.True(panel.Gain.IsAvailable);
        Assert.True(panel.ShowBinning);
        Assert.True(panel.ShowSubframe);
        Assert.True(panel.ShowCooling);
        Assert.True(panel.ShowReadout);
        Assert.Equal(["Normal", "Slow"], panel.ReadoutChoices);
    }

    [Fact]
    public async Task Apply_SendsOnlyWhatChanged_ShowsAFailureAsASentence_AndKeepsThePreferences()
    {
        var preferences = new MemoryPreferences();
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated, preferences);
        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        panel.Gain.Text = "50";
        panel.SelectedBinX = 2;
        await panel.ApplyCommand.ExecuteAsync(null);

        Assert.False(panel.HasError);
        Assert.Equal(50, camera.Settings!.Gain);
        Assert.Equal((2, 2), (camera.Settings.BinX, camera.Settings.BinY));
        var stored = preferences.GetPreferences("camera.sim");
        Assert.Equal("50", stored[DevicePreferences.Gain]);
        Assert.Equal("2", stored[DevicePreferences.BinX]);
        Assert.Equal("full", stored[DevicePreferences.Region]);

        panel.Gain.Text = "5000";
        await panel.ApplyCommand.ExecuteAsync(null);
        Assert.True(panel.HasError);
        Assert.Equal(50, camera.Settings.Gain);
        Assert.Equal("50", preferences.GetPreferences("camera.sim")[DevicePreferences.Gain]);
    }

    [Fact]
    public async Task CameraDefaultsThatFit_AreAppliedAfterTheNextConnect()
    {
        var preferences = new MemoryPreferences();
        preferences.SavePreferences("camera.sim", new Dictionary<string, string>
        {
            [DevicePreferences.Gain] = "40",
            [DevicePreferences.Offset] = "7",
            [DevicePreferences.BinX] = "2",
            [DevicePreferences.ReadoutMode] = "Slow",
        });
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated, preferences);

        await camera.ConnectAsync();
        await Settle(() => camera.Settings?.Gain == 40);

        Assert.Equal(7, camera.Settings!.Offset);
        Assert.Equal((2, 2), (camera.Settings.BinX, camera.Settings.BinY));
        Assert.Equal(1, camera.Settings.ReadoutMode);
        Assert.False(panel.HasNotice);
    }

    [Fact]
    public async Task CameraDefaultsThatDoNotFit_AreKeptAsTheyAre_NotApplied_AndSaid()
    {
        var preferences = new MemoryPreferences();
        preferences.SavePreferences("camera.sim", new Dictionary<string, string>
        {
            [DevicePreferences.Gain] = "40",
            [DevicePreferences.BinX] = "9",
        });
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated, preferences);

        await camera.ConnectAsync();
        await Settle(() => panel.HasNotice);

        Assert.Contains("do not fit", panel.NoticeText);
        Assert.Equal(0, camera.Settings!.Gain);
        Assert.Equal("9", preferences.GetPreferences("camera.sim")[DevicePreferences.BinX]);
        Assert.Equal("40", preferences.GetPreferences("camera.sim")[DevicePreferences.Gain]);
    }

    [Fact]
    public async Task AChoiceIsAppliedByItself_WithoutAnApplyButton()
    {
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated);
        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        panel.CoolerOn = true;

        await Settle(() => camera.Settings!.CoolerOn == true);
        panel.SelectedBinX = 2;
        await Settle(() => camera.Settings!.BinX == 2);
    }

    [Fact]
    public async Task ATypedValueIsAppliedWhenTheTypingHasPaused_AndAnUnfinishedOneIsLeftAlone()
    {
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (panel, camera) = CameraPanel(simulated);
        await camera.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        panel.Gain.Text = "4x"; // not a number yet
        await Task.Delay(1300);
        Assert.False(panel.HasError);
        Assert.Equal(0, camera.Settings!.Gain);

        panel.Gain.Text = "40";
        await Settle(() => camera.Settings!.Gain == 40);
    }

    [Fact]
    public async Task TheCoolerIsNotSwitchedOnByAConnect()
    {
        var preferences = new MemoryPreferences();
        preferences.SavePreferences("camera.sim", new Dictionary<string, string> { [DevicePreferences.TargetTemperature] = "-15" });
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var (_, camera) = CameraPanel(simulated, preferences);

        await camera.ConnectAsync();
        await Settle(() => camera.Settings?.TargetTemperature == -15);

        Assert.False(camera.Settings!.CoolerOn);
    }

    [Fact]
    public async Task APanelThatIsShown_ReadsTheTemperatureAgain()
    {
        var simulated = new SimulatedCamera(new DeviceId("camera.sim"), seed: 1);
        var host = NewHost();
        host.AddDevice(simulated);
        var vm = new CameraViewModel(simulated, host, a => a(), new SessionActivity(), new ImagingViewModel(), TimeSpan.FromSeconds(1));
        var panel = new CameraSettingsViewModel(vm, simulated, null, TimeSpan.FromMilliseconds(20)) { IsShown = true };
        _panels.Add(panel);
        await simulated.ConnectAsync();
        await Settle(() => panel.IsAvailable);
        await simulated.ApplyAsync(new CameraSettings { TargetTemperature = -20, CoolerOn = true });
        var before = panel.CcdTemperatureText;

        await Settle(() => panel.CcdTemperatureText != before);

        Assert.EndsWith("°C", panel.CcdTemperatureText);
    }

    // ---- Mount panel

    private (MountControlViewModel Panel, IMountControl Mount) MountPanel(IMountControl mount, MemoryPreferences? preferences = null)
    {
        var host = NewHost();
        host.AddDevice(mount);
        var vm = new MountViewModel(mount, host, a => a(), new SessionActivity());
        var panel = new MountControlViewModel(vm, mount, preferences);
        _panels.Add(panel);
        return (panel, mount);
    }

    [Fact]
    public async Task AMountPanel_ShowsOnlyTheControlsTheMountSupports()
    {
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureMount = d => { d.CanPark = true; d.CanPulseGuide = true; } };
        var ascom = new AscomMount(new DeviceId("mount.ascom"), "ASCOM Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        var (panel, mount) = MountPanel(ascom);
        Assert.False(panel.ShowPark);

        await mount.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        Assert.True(panel.ShowPark);
        Assert.True(panel.ShowPulseGuide);
        Assert.False(panel.ShowSync);
        Assert.False(panel.ShowAltAz);
        Assert.False(panel.ShowHome);
        Assert.False(panel.ShowMoveAxis);
        Assert.True(panel.ShowTrackingRates);
        Assert.Contains(panel.DriverInfo, l => l.Label == "Slew");
    }

    [Fact]
    public async Task AMountPanel_OfTheSimulator_ParksAndShowsIt()
    {
        var simulated = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20));
        var (panel, mount) = MountPanel(simulated);
        await mount.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        await panel.ParkCommand.ExecuteAsync(null);

        Assert.True(mount.Telemetry!.AtPark);
        Assert.True(panel.IsParked);
        Assert.Contains(panel.TelemetryLines, l => l.Label == "Parked" && l.Value == "Yes");
        Assert.False(panel.HasError);
    }

    [Fact]
    public async Task AMountCommandThatFails_ShowsASentence()
    {
        var simulated = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20));
        var (panel, mount) = MountPanel(simulated);
        await mount.ConnectAsync();
        await Settle(() => panel.IsAvailable);
        panel.AltitudeText = "95";

        await panel.SlewAltAzCommand.ExecuteAsync(null);

        Assert.True(panel.HasError);
    }

    [Fact]
    public async Task MountPreferences_AreAppliedAfterAConnect()
    {
        var preferences = new MemoryPreferences();
        preferences.SavePreferences("mount.sim", new Dictionary<string, string>
        {
            [DevicePreferences.TrackingRate] = "Lunar",
            [DevicePreferences.Refraction] = "true",
        });
        var simulated = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20));
        var (_, mount) = MountPanel(simulated, preferences);

        await mount.ConnectAsync();
        await Settle(() => mount.Telemetry?.Rate == TrackingRate.Lunar && mount.Telemetry.DoesRefraction == true);
    }

    [Fact]
    public async Task TheJogPad_MovesOnlyWhileHeld_AndStopIsAlwaysThere()
    {
        var drivers = new FakeDriverFactory(new CallLog())
        {
            ConfigureMount = d => { d.MovableAxes.Add(MountAxis.Primary); d.MovableAxes.Add(MountAxis.Secondary); },
        };
        var ascom = new AscomMount(new DeviceId("mount.jog"), "Jog Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        var (panel, mount) = MountPanel(ascom);
        await mount.ConnectAsync();
        await Settle(() => panel.IsAvailable);
        panel.JogRateText = "0.5";

        await panel.JogStartCommand.ExecuteAsync("NE");
        Assert.True(panel.IsJogging);
        Assert.Contains(drivers.Mounts[0].Operations, o => o.StartsWith("MoveAxis Primary 0", StringComparison.Ordinal) && o != "MoveAxis Primary 0");
        Assert.Contains(drivers.Mounts[0].Operations, o => o.StartsWith("MoveAxis Secondary 0", StringComparison.Ordinal) && o != "MoveAxis Secondary 0");

        await panel.JogEndCommand.ExecuteAsync("NE");
        Assert.False(panel.IsJogging);
        Assert.Contains("MoveAxis Primary 0", drivers.Mounts[0].Operations);
        Assert.Contains("MoveAxis Secondary 0", drivers.Mounts[0].Operations);

        await panel.StopCommand.ExecuteAsync(null);
        Assert.Contains("AbortSlew", drivers.Mounts[0].Operations);
        Assert.False(panel.HasError);
        Assert.True(panel.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheJogPad_OnlyUsesTheAxesTheMountCanMove_AndRefusesARateItDoesNotHave()
    {
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureMount = d => d.MovableAxes.Add(MountAxis.Primary) };
        var ascom = new AscomMount(new DeviceId("mount.jog"), "Jog Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        var (panel, mount) = MountPanel(ascom);
        await mount.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        await panel.JogStartCommand.ExecuteAsync("NE");
        Assert.DoesNotContain(drivers.Mounts[0].Operations, o => o.Contains("Secondary", StringComparison.Ordinal));
        await panel.JogEndCommand.ExecuteAsync("NE");

        panel.JogRateText = "50";
        await panel.JogStartCommand.ExecuteAsync("E");
        Assert.True(panel.HasError);
        Assert.False(panel.IsJogging);
    }

    // ---- Focuser panel

    [Fact]
    public async Task ARelativeFocuser_IsShownAsOne_AndCannotMoveToAPosition()
    {
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureFocuser = d => d.Absolute = false };
        var ascom = new AscomFocuser(new DeviceId("focuser.rel"), "Relative", "ASCOM.Test.Focuser", drivers, null, null, FastTimings.Create());
        var host = NewHost();
        host.AddDevice(ascom);
        var vm = new FocuserViewModel(ascom, host, a => a(), new SessionActivity());
        var panel = new FocuserControlViewModel(vm, ascom);
        _panels.Add(panel);

        await ascom.ConnectAsync();
        await Settle(() => panel.IsAvailable);
        vm.Refresh();

        Assert.True(panel.IsRelative);
        Assert.True(vm.IsRelative);
        Assert.Contains("relative", vm.PositionText, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.MoveCommand.CanExecute(null));
        await panel.MoveByCommand.ExecuteAsync("+");
        Assert.False(panel.HasError);
        Assert.Contains(drivers.Log.Names, n => n == "Move 100");
    }

    [Fact]
    public async Task AFocuserWithTemperatureCompensation_OffersIt_AndKeepsTheChoice()
    {
        var preferences = new MemoryPreferences();
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureFocuser = d => { d.TempCompAvailableValue = true; d.TemperatureValue = 9.5; } };
        var ascom = new AscomFocuser(new DeviceId("focuser.abs"), "Focuser", "ASCOM.Test.Focuser", drivers, null, null, FastTimings.Create());
        var host = NewHost();
        host.AddDevice(ascom);
        var vm = new FocuserViewModel(ascom, host, a => a(), new SessionActivity());
        var panel = new FocuserControlViewModel(vm, ascom, preferences);
        _panels.Add(panel);
        await ascom.ConnectAsync();
        await Settle(() => panel.IsAvailable);

        Assert.True(panel.ShowTempComp);
        Assert.True(panel.ShowTemperature);
        Assert.Equal("9.5 °C", panel.TemperatureText);
        panel.TempComp = true;
        await panel.ApplyTempCompCommand.ExecuteAsync(null);

        Assert.True(drivers.Focusers[0].TempCompValue);
        Assert.Equal("true", preferences.GetPreferences("focuser.abs")[DevicePreferences.TempComp]);
    }
}

using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// The observing site and the optical train of the rigs: what is stored (and what is not), how old files load, the settings page, the comparison
/// of the site of Sidera with the one of a mount and its three answers, and the optics form with the values that follow from it.
/// </summary>
public sealed class SiteAndOpticsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-site-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public SiteAndOpticsTests() => Directory.CreateDirectory(_directory);

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

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private string EquipmentFile => Path.Combine(_directory, "equipment.json");

    private sealed record App(MainViewModel Vm, EquipmentService Equipment, SiteService Site, SideraRuntimeHost Host);

    private App Create(EquipmentConfiguration? stored = null, ObservingSite? site = null)
    {
        if (stored is not null)
        {
            new EquipmentConfigurationStore(EquipmentFile).Save(stored);
        }

        if (site is not null)
        {
            new SideraSettingsStore(SettingsFile).Save(new SideraSettings(site));
        }

        var host = new SideraRuntimeHost();
        var options = new DemoOptions { ManualExposure = TimeSpan.FromMilliseconds(30) };
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]);
        var equipment = new EquipmentService(host, new EquipmentConfigurationStore(EquipmentFile), factories);
        equipment.Load();
        var siteService = new SiteService(new SideraSettingsStore(SettingsFile));
        siteService.Load();
        var vm = new MainViewModel(
            host, a => a(), options,
            equipmentManagement: new EquipmentManagement(equipment, new NoDiscovery(), new NoSetup(), siteService), withDemoSequence: false);
        _apps.Add((host, vm));
        return new App(vm, equipment, siteService, host);
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

    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }

    // ---- The settings file

    [Fact]
    public void AnInstallationWithoutASettingsFile_HasNoSite_NotZeroZeroZero()
    {
        var app = Create();

        Assert.Null(app.Site.Site);
        Assert.Null(app.Site.Problem);
        Assert.False(File.Exists(SettingsFile));
        Assert.False(app.Vm.Settings.Site!.IsConfigured);
        Assert.Equal(string.Empty, app.Vm.Settings.Site.LatitudeText);
    }

    [Fact]
    public void ASiteIsSavedAndLoadedAgain_WithTheNameAndTheSignedValues()
    {
        var first = Create();
        Assert.True(first.Site.Set(new ObservingSite(37.77, -122.42, 16, "Bay")).Succeeded);

        var second = new SiteService(new SideraSettingsStore(SettingsFile));
        second.Load();

        Assert.Equal(new ObservingSite(37.77, -122.42, 16, "Bay"), second.Site);
        var json = File.ReadAllText(SettingsFile);
        Assert.Contains("\"longitudeDegrees\": -122.42", json); // signed degrees, no letters
        Assert.Contains("sidera-settings", json);
    }

    [Fact]
    public void ASettingsFileWithoutASite_Loads_AsNoSite()
    {
        File.WriteAllText(SettingsFile, "{\"format\":\"sidera-settings\",\"version\":1}");

        Assert.Null(Create().Site.Site);
    }

    [Theory]
    [InlineData("{\"format\":\"sidera-settings\",\"version\":1,\"site\":{\"latitudeDegrees\":95,\"longitudeDegrees\":8,\"elevationMeters\":10}}", "latitude")]
    [InlineData("{\"format\":\"sidera-settings\",\"version\":1,\"site\":{\"latitudeDegrees\":45,\"longitudeDegrees\":181,\"elevationMeters\":10}}", "longitude")]
    [InlineData("{\"format\":\"sidera-settings\",\"version\":1,\"site\":{\"latitudeDegrees\":45}}", "longitudeDegrees")]
    [InlineData("not json", "JSON")]
    public void ASettingsFileThatIsNotValid_IsNotRepaired_ItIsKeptAndTheSiteStaysUnknown(string content, string expected)
    {
        File.WriteAllText(SettingsFile, content);

        var app = Create();

        Assert.Null(app.Site.Site);
        Assert.Contains(expected, app.Site.Problem);
        Assert.Equal(content, File.ReadAllText(SettingsFile));
        Assert.Contains(expected, app.Vm.Settings.Site!.ProblemText);
    }

    [Fact]
    public void ClearingTheSite_MakesItUnknownAgain()
    {
        var app = Create(site: new ObservingSite(47.7, 7.8, 410));

        Assert.True(app.Site.Clear().Succeeded);

        Assert.Null(app.Site.Site);
        var reloaded = new SiteService(new SideraSettingsStore(SettingsFile));
        reloaded.Load();
        Assert.Null(reloaded.Site);
    }

    [Fact]
    public void TheGlobalSite_IsWhatCounts_UntilARigHasItsOwn()
    {
        var app = Create(site: new ObservingSite(47.7, 7.8, 410));
        var rigsOwn = new ObservingSite(10, 20, 30);

        Assert.Equal(app.Site.Site, app.Site.ResolveFor());
        Assert.Equal(rigsOwn, app.Site.ResolveFor(rigsOwn));
    }

    // ---- The settings page

    [Fact]
    public void EnteringTheSite_WithLettersOrSigns_StoresSignedDecimalDegrees()
    {
        var app = Create();
        var page = app.Vm.Settings.Site!;

        page.NameText = "Home Observatory";
        page.LatitudeText = "47.7192° N";
        page.LongitudeText = "7.8231° E";
        page.ElevationText = "410 m";
        page.SaveCommand.Execute(null);

        Assert.False(page.HasProblem);
        Assert.Equal(new ObservingSite(47.7192, 7.8231, 410, "Home Observatory"), app.Site.Site);
        Assert.Equal("Home Observatory: 47.7192° N, 7.8231° E, 410 m", page.StatusText);

        page.LatitudeText = "-33.8688";
        page.LongitudeText = "122.4194 W";
        page.SaveCommand.Execute(null);

        Assert.Equal((-33.8688, -122.4194), (app.Site.Site!.LatitudeDegrees, app.Site.Site.LongitudeDegrees));
        Assert.Equal("33.8688° S", page.LatitudeText);
        Assert.Equal("122.4194° W", page.LongitudeText);
    }

    [Theory]
    [InlineData("95", "7.8", "410", "latitude")]
    [InlineData("47.7", "190", "410", "longitude")]
    [InlineData("47.7", "7.8", "99999", "elevation")]
    [InlineData("north", "7.8", "410", "latitude")]
    [InlineData("47.7", "7.8", "", "elevation")]
    public void AValueThatIsNotValid_IsRefused_AndNothingIsSaved(string latitude, string longitude, string elevation, string expected)
    {
        var app = Create();
        var page = app.Vm.Settings.Site!;
        page.LatitudeText = latitude;
        page.LongitudeText = longitude;
        page.ElevationText = elevation;

        page.SaveCommand.Execute(null);

        Assert.Contains(expected, page.ProblemText);
        Assert.Null(app.Site.Site);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void TheSettingsPageFollowsAChangeOfTheSiteThatCameFromElsewhere()
    {
        var app = Create();

        app.Site.Set(new ObservingSite(48.1372, 11.5756, 520, "Munich"));

        Assert.Equal("48.1372° N", app.Vm.Settings.Site!.LatitudeText);
        Assert.True(app.Vm.Settings.Site.IsConfigured);
    }

    // ---- The rig optics in the equipment file

    private static EquipmentConfiguration WithRig(OpticalTrain? optics) => new(
        [DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera)],
        [new RigConfiguration("rig.main", "Main", "camera.main", null, null, optics)]);

    [Fact]
    public void TheOpticsOfARig_AreSavedAndLoaded_AsInputsOnly()
    {
        var optics = new OpticalTrain(750, 150, 3.76, 3.8, 6248, 4176);
        var bytes = EquipmentConfigurationSerializer.Serialize(WithRig(optics));
        var text = Encoding.UTF8.GetString(bytes);

        var loaded = EquipmentConfigurationSerializer.Deserialize(bytes).Rigs.Single().Optics;

        Assert.Equal(optics, loaded);
        Assert.Contains("focalLengthMm", text);
        Assert.DoesNotContain("pixelScale", text);
        Assert.DoesNotContain("fieldOfView", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sensorWidthMm", text);
    }

    [Fact]
    public void OpticsWithOnlyAFocalLength_RoundTrip_WithTheRestUnknown()
    {
        var loaded = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(WithRig(new OpticalTrain(500)))).Rigs.Single().Optics!;

        Assert.Equal(500, loaded.FocalLengthMm);
        Assert.Null(loaded.PixelSizeXMicrons);
        Assert.Null(loaded.SensorWidthPixels);
    }

    [Fact]
    public void ARigOfAnOlderFile_WithOneNameForThePixelSizeAndTheResolution_StillLoads()
    {
        const string old = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main",
               "optics":{"focalLengthMm":750,"apertureMm":150,"pixelSizeMicrons":3.76,"sensorWidthMm":23.5,"sensorHeightMm":15.7,"resolutionWidth":6248,"resolutionHeight":4176}}]}
            """;

        var optics = EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(old)).Rigs.Single().Optics!;

        Assert.Equal((750.0, 150.0, 3.76, 3.76, 6248, 4176), (optics.FocalLengthMm, optics.ApertureMm, optics.PixelSizeXMicrons, optics.PixelSizeYMicrons, optics.SensorWidthPixels, optics.SensorHeightPixels));
    }

    [Fact]
    public void ARigWithoutOptics_Loads()
    {
        const string old = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main"}]}
            """;

        var rig = EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(old)).Rigs.Single();

        Assert.Null(rig.Optics);
    }

    [Fact]
    public void OpticsThatAreNotValid_AreRefusedWithTheRigNamed()
    {
        const string bad = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","optics":{"focalLengthMm":-5}}]}
            """;

        var ex = Assert.Throws<EquipmentConfigurationException>(() => EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(bad)));

        Assert.Contains("rig.main", ex.Message);
    }

    // ---- The optics of the rig of a camera

    [Fact]
    public void OpticsForACameraWithoutARig_MakeARigThatHoldsOnlyThem_AndTheCameraCanStillBeRemoved()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));

        Assert.True(app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750, 150)).Succeeded);

        var rig = Assert.Single(app.Host.RigRegistry.GetAll());
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.Equal(750, new EquipmentConfigurationStore(EquipmentFile).Load().Rigs.Single().Optics!.FocalLengthMm);
        Assert.Null(app.Equipment.WhyCannotRemove("camera.main"));

        Assert.True(app.Equipment.Remove("camera.main").Succeeded);
        Assert.Empty(app.Host.RigRegistry.GetAll());
        Assert.Empty(new EquipmentConfigurationStore(EquipmentFile).Load().Rigs);
    }

    [Fact]
    public void RemovingTheOptics_DropsTheRigThatOnlyHeldThem()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750));

        Assert.True(app.Equipment.SetCameraOptics("camera.main", null).Succeeded);

        Assert.Empty(app.Host.RigRegistry.GetAll());
        Assert.Empty(app.Equipment.Configuration.Rigs);
    }

    [Fact]
    public void TheOpticsOfARealRig_Change_AndTheRigStays_EvenWhenTheyAreRemoved()
    {
        var app = Create(WithRig(new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176)));

        Assert.True(app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(1000)).Succeeded);
        Assert.Equal(1000, app.Host.RigRegistry.GetAll().Single().Optics!.FocalLengthMm);

        Assert.True(app.Equipment.SetCameraOptics("camera.main", null).Succeeded);
        var rig = Assert.Single(app.Host.RigRegistry.GetAll());
        Assert.Null(rig.Optics);
    }

    // ---- The optics form

    private static OpticalTrainViewModel OpticsFormOf(App app) =>
        Assert.IsType<CameraDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Camera).Detail).Optics;

    [Fact]
    public void TheOpticsForm_ShowsWhatIsDerived_AndWhereTheCameraFillsIn()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        Assert.Equal("Not set", form.PixelScaleText);

        form.FocalLengthText = "750";
        form.ApertureText = "150";

        // Nothing from the camera yet (not connected): the pixel size is unknown, so the scale is too.
        Assert.Equal("Not set", form.PixelScaleText);
        Assert.Contains("Connect the camera", form.ReportedText);
    }

    [Fact]
    public async Task OnceTheCameraIsConnected_ItsPixelSizeAndSensorFillTheGaps()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";

        await app.Host.DeviceOperations.ConnectAsync(new DeviceId("camera.main"));
        await Wait(() => form.PixelScaleText != "Not set");

        Assert.Equal("1.03 \"/px", form.PixelScaleText);
        Assert.Contains("reported by the camera", form.SourceText);
        Assert.StartsWith("Camera reports: 3.76 µm pixels", form.ReportedText);
    }

    [Fact]
    public void AConfiguredPixelSize_WinsOverTheCamera_AndTheFieldOfViewFollowsTheFocalLength()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        form.PixelSizeXText = "3.76";
        form.PixelSizeYText = "3.76";
        form.SensorWidthText = "6248";
        form.SensorHeightText = "4176";

        form.FocalLengthText = "750";
        var first = form.FieldOfViewText;
        form.FocalLengthText = "1500";

        Assert.Equal("1.79° × 1.20°", first);
        Assert.NotEqual(first, form.FieldOfViewText);
        Assert.Equal("0.52 \"/px", form.PixelScaleText);
        Assert.Contains("Pixel size: configured", form.SourceText);
    }

    [Fact]
    public void SavingTheOptics_StoresTheInputs_AndTheFormComesBackWithThem()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";
        form.ApertureText = "150";
        form.PixelSizeXText = "3.76";
        form.PixelSizeYText = "3.76";
        form.SensorWidthText = "6248";
        form.SensorHeightText = "4176";

        form.SaveCommand.Execute(null);

        Assert.False(form.HasProblem);
        var stored = new EquipmentConfigurationStore(EquipmentFile).Load().Rigs.Single().Optics!;
        Assert.Equal((750.0, 150.0, 3.76, 3.76, 6248, 4176), (stored.FocalLengthMm, stored.ApertureMm, stored.PixelSizeXMicrons, stored.PixelSizeYMicrons, stored.SensorWidthPixels, stored.SensorHeightPixels));

        var again = OpticsFormOf(app);
        Assert.Equal("750", again.FocalLengthText);
        Assert.True(again.HasConfiguration);
        Assert.Equal("1.03 \"/px", again.PixelScaleText);
        Assert.Equal("1.79° × 1.20°", again.FieldOfViewText);
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("0", "greater than zero")]
    [InlineData("-5", "greater than zero")]
    [InlineData("abc", "greater than zero")]
    public void AFocalLengthThatIsNotValid_IsRefused(string text, string expected)
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        form.FocalLengthText = text;

        form.SaveCommand.Execute(null);

        Assert.Contains(expected, form.ProblemText);
        Assert.Empty(app.Equipment.Configuration.Rigs);
    }

    [Fact]
    public void AnotherFieldThatIsNotValid_IsNamed()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";
        form.SensorWidthText = "12.5";

        form.SaveCommand.Execute(null);

        Assert.Contains("sensor width", form.ProblemText);
    }

    // ---- The mount against the site of Sidera

    private async Task<(App App, MountSiteViewModel Card, SimulatedMount Mount)> ConnectedMountAsync(ObservingSite? site)
    {
        var app = Create(site: site);
        app.Equipment.Add(DeviceConfiguration.Simulator("mount.main", "Mount", DeviceType.Mount));
        var detail = Assert.IsType<MountDetailViewModel>(app.Vm.Equipment.Slots.Single(s => s.Type == DeviceType.Mount).Detail);
        var mount = (SimulatedMount)app.Host.DeviceRegistry.GetAll().OfType<SimulatedMount>().Single();
        await app.Host.DeviceOperations.ConnectAsync(new DeviceId("mount.main"));
        await Wait(() => detail.Mount.IsConnected);
        return (app, detail.Site, mount);
    }

    private static readonly ObservingSite MountsPlace = new(50.1, 8.6, 120);

    [Fact]
    public async Task AMountWithTheSameSite_ConnectsWithoutAPrompt()
    {
        var (_, card, _) = await ConnectedMountAsync(new ObservingSite(50.1002, 8.6001, 125, "Home"));

        Assert.False(card.IsVisible);
        Assert.Equal(SiteSituationOf(card), Sidera.Runtime.Location.SiteSituation.InSync);
    }

    private static Sidera.Runtime.Location.SiteSituation? SiteSituationOf(MountSiteViewModel card) => card.Assessment?.Situation;

    [Fact]
    public async Task ADifferentSite_ShowsBothPlaces_AndTheThreeChoices()
    {
        var (_, card, _) = await ConnectedMountAsync(new ObservingSite(47.7192, 7.8231, 410, "Home Observatory"));

        Assert.True(card.IsMismatch);
        Assert.Equal("LOCATION MISMATCH", card.Headline);
        Assert.Equal("Home Observatory\n47.7192° N\n7.8231° E\n410 m", card.SideraText);
        Assert.Equal("50.1000° N\n8.6000° E\n120 m", card.MountText);
        Assert.True(card.CanUseMount);
        Assert.True(card.CanSend);
        Assert.StartsWith("Differs by", card.DifferenceText);
    }

    [Fact]
    public async Task UsingTheMountLocation_UpdatesSideraAndItsFile_KeepsTheName_AndLeavesTheMountAlone()
    {
        var (app, card, mount) = await ConnectedMountAsync(new ObservingSite(47.7192, 7.8231, 410, "Home Observatory"));

        card.UseMountLocationCommand.Execute(null);

        Assert.Equal(new ObservingSite(50.1, 8.6, 120, "Home Observatory"), app.Site.Site);
        var reloaded = new SiteService(new SideraSettingsStore(SettingsFile));
        reloaded.Load();
        Assert.Equal(app.Site.Site, reloaded.Site);
        Assert.Equal(MountsPlace.LatitudeDegrees, mount.Site!.LatitudeDegrees);
        Assert.False(card.IsVisible); // the two agree now
        Assert.True(card.HasResult);
        Assert.Equal("50.1000° N, 8.6000° E, 120 m", app.Vm.Settings.Site!.StatusText.Replace("Home Observatory: ", string.Empty));
    }

    [Fact]
    public async Task SendingTheSideraLocation_WritesItToTheMount_ReadsItBack_AndLeavesSideraAlone()
    {
        var home = new ObservingSite(47.7192, 7.8231, 410, "Home Observatory");
        var (app, card, mount) = await ConnectedMountAsync(home);

        await card.SendSideraLocationCommand.ExecuteAsync(null);

        Assert.False(card.HasProblem);
        Assert.Equal(new MountSite(47.7192, 7.8231, 410), mount.Site);
        Assert.Equal(home, app.Site.Site);
        Assert.False(card.IsVisible);
        Assert.Contains("read back", card.ResultText);
    }

    [Fact]
    public async Task KeepingBothUnchanged_ChangesNothing_AndOnlyClosesTheCard()
    {
        var home = new ObservingSite(47.7192, 7.8231, 410, "Home Observatory");
        var (app, card, mount) = await ConnectedMountAsync(home);
        var fileBefore = File.ReadAllText(SettingsFile);

        card.KeepBothCommand.Execute(null);

        Assert.False(card.IsVisible);
        Assert.Equal(home, app.Site.Site);
        Assert.Equal(new MountSite(50.1, 8.6, 120), mount.Site);
        Assert.Equal(fileBefore, File.ReadAllText(SettingsFile));
    }

    [Fact]
    public async Task AReconnect_AsksAgain()
    {
        var (app, card, _) = await ConnectedMountAsync(new ObservingSite(47.7192, 7.8231, 410));
        card.KeepBothCommand.Execute(null);
        var id = new DeviceId("mount.main");

        await app.Host.DeviceOperations.DisconnectAsync(id);
        await Wait(() => card.Assessment is null);
        await app.Host.DeviceOperations.ConnectAsync(id);
        await Wait(() => card.IsVisible);

        Assert.True(card.IsMismatch);
    }

    [Fact]
    public async Task WithoutASiteInSidera_TheMountLocationIsOffered_AndNotTakenByItself()
    {
        var (app, card, _) = await ConnectedMountAsync(null);

        Assert.True(card.IsMountOffering);
        Assert.False(card.CanSend);
        Assert.Null(app.Site.Site);

        card.UseMountLocationCommand.Execute(null);

        Assert.Equal(new ObservingSite(50.1, 8.6, 120), app.Site.Site);
    }

    [Fact]
    public async Task AMountThatTakesNoSite_IsNotOfferedOne_ButTheMismatchIsStillShown()
    {
        var app = Create(site: new ObservingSite(47.7192, 7.8231, 410));
        var mount = new SimulatedMount(new DeviceId("mount.own"), "GPS Mount", app.Host.EventBus) { SiteWritable = false };
        app.Host.AddDevice(mount);
        var vm = new MountViewModel(mount, app.Host, a => a(), new SessionActivity());
        var card = new MountSiteViewModel(vm, app.Site);
        await app.Host.DeviceOperations.ConnectAsync(mount.Id);
        await Wait(() => card.IsVisible);

        Assert.True(card.IsMismatch);
        Assert.False(card.CanSend);
        Assert.True(card.CanUseMount);
    }

    [Fact]
    public async Task ARefusedWrite_IsShown_AndNotReportedAsSynchronized_AndNotRetried()
    {
        var app = Create(site: new ObservingSite(47.7192, 7.8231, 410));
        var mount = new SimulatedMount(new DeviceId("mount.picky"), "Picky Mount", app.Host.EventBus) { RefuseSiteProperty = "elevation" };
        app.Host.AddDevice(mount);
        var vm = new MountViewModel(mount, app.Host, a => a(), new SessionActivity());
        var card = new MountSiteViewModel(vm, app.Site);
        await app.Host.DeviceOperations.ConnectAsync(mount.Id);
        await Wait(() => card.IsVisible);

        await card.SendSideraLocationCommand.ExecuteAsync(null);

        Assert.True(card.HasProblem);
        Assert.Contains("elevation", card.ProblemText);
        Assert.False(card.HasResult);
        Assert.True(card.IsVisible); // still a mismatch: the latitude and longitude were taken, the elevation was not
        Assert.Equal(new MountSite(47.7192, 7.8231, 120), mount.Site);
    }

    [Fact]
    public async Task TheMountWorkspace_ShowsTheSiteOfTheMountWithTheRightLetters()
    {
        var app = Create(site: null);
        var mount = new SimulatedMount(new DeviceId("mount.west"), "West Mount", app.Host.EventBus) { SimulatedSite = new MountSite(-33.8688, -122.4194, 16) };
        app.Host.AddDevice(mount);
        await app.Host.DeviceOperations.ConnectAsync(mount.Id);
        var control = new MountControlViewModel(new MountViewModel(mount, app.Host, a => a(), new SessionActivity()), mount, null, null);

        await Wait(() => control.InfoLines.Any(l => l.Label == "Site"));

        Assert.Equal("33.8688° S, 122.4194° W, 16 m", control.InfoLines.Single(l => l.Label == "Site").Value);
    }

    // ---- The camera database in the optics form

    private const string KnownCameraName = "ZWO ASI2600MC Pro";

    private App AppWithKnownCamera()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", KnownCameraName, DeviceType.Camera));
        return app;
    }

    [Fact]
    public void AKnownCamera_ShowsItsGeometryWithTheDatabaseAsSource_BeforeItIsConnected()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);

        form.FocalLengthText = "750";

        Assert.Equal(KnownCameraName, form.CameraIdentityText);
        Assert.Equal("Sony IMX571", form.SensorNameText);
        Assert.Equal("6248 × 4176", form.ResolutionText);
        Assert.Equal("Sidera Camera Database", form.ResolutionSourceText);
        Assert.Equal("3.76 µm", form.PixelSizeText);
        Assert.Equal("Sidera Camera Database", form.PixelSizeSourceText);
        Assert.Equal("1.03 \"/px", form.PixelScaleText);
        Assert.Equal("1.79° × 1.20°", form.FieldOfViewText);
        Assert.False(form.HasConflict);
        Assert.Contains("Connect the camera", form.ReportedText); // what the camera itself says is still only what it says
    }

    [Fact]
    public async Task OnceConnected_TheDeviceIsTheSource_AndADifferenceFromTheDatabaseIsShown()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";

        await app.Host.DeviceOperations.ConnectAsync(new DeviceId("camera.main"));
        await Wait(() => form.ResolutionSourceText == "Device");

        Assert.Equal("Device", form.PixelSizeSourceText); // 3.76 µm reported, the same as the database's
        Assert.True(form.HasConflict); // the simulated sensor is not an ASI2600
        Assert.Contains("Resolution: the camera reports", form.ConflictText);
        Assert.Contains("6248 × 4176", form.ConflictText);
        Assert.DoesNotContain("Pixel size:", form.ConflictText);
        Assert.Contains("camera's values are used", form.ConflictText);
    }

    [Fact]
    public void ACameraThatIsNotInTheDatabase_ShowsNoIdentity_AndNothingIsInvented()
    {
        var app = Create();
        app.Equipment.Add(DeviceConfiguration.Simulator("camera.main", "Main Camera", DeviceType.Camera));
        var form = OpticsFormOf(app);

        form.FocalLengthText = "750";

        Assert.Equal(string.Empty, form.CameraIdentityText);
        Assert.Equal("Not set", form.ResolutionText);
        Assert.Equal("Not set", form.PixelSizeText);
        Assert.Equal("Not set", form.PixelSizeSourceText);
        Assert.Equal("Not set", form.PixelScaleText);
    }

    [Fact]
    public void AManualOverride_WinsAndIsLabelledManual_AndRevertingGoesBackToTheDatabase()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";

        form.PixelSizeXText = "4.5";
        form.PixelSizeYText = "4.5";
        form.SensorWidthText = "5000";
        form.SensorHeightText = "3000";

        Assert.Equal("4.5 µm", form.PixelSizeText);
        Assert.Equal("Manual", form.PixelSizeSourceText);
        Assert.Equal("5000 × 3000", form.ResolutionText);
        Assert.Equal("Manual", form.ResolutionSourceText);
        Assert.True(form.HasOverride);
        Assert.Equal("1.24 \"/px", form.PixelScaleText);

        form.RevertToAutomaticCommand.Execute(null);

        Assert.False(form.HasOverride);
        Assert.Equal(string.Empty, form.PixelSizeXText);
        Assert.Equal("3.76 µm", form.PixelSizeText);
        Assert.Equal("Sidera Camera Database", form.PixelSizeSourceText);
        Assert.Equal("6248 × 4176", form.ResolutionText);
        Assert.Equal("1.03 \"/px", form.PixelScaleText);
        Assert.False(form.RevertToAutomaticCommand.CanExecute(null)); // nothing to revert
    }

    [Fact]
    public void OneManualValue_OverridesOnlyThatValue()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";

        form.PixelSizeXText = "3.9";

        Assert.Equal("3.9 × 3.76 µm", form.PixelSizeText);
        Assert.Equal("Manual", form.PixelSizeSourceText);
        Assert.Equal("6248 × 4176", form.ResolutionText);
        Assert.Equal("Sidera Camera Database", form.ResolutionSourceText);
    }

    [Fact]
    public void RevertingASavedOverride_SavesTheRigWithoutIt()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";
        form.PixelSizeXText = "4.5";
        form.PixelSizeYText = "4.5";
        form.SaveCommand.Execute(null);
        Assert.Equal(4.5, app.Host.RigRegistry.GetAll().Single().Optics!.PixelSizeXMicrons);

        OpticsFormOf(app).RevertToAutomaticCommand.Execute(null);

        var rig = app.Host.RigRegistry.GetAll().Single();
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.Null(rig.Optics.PixelSizeXMicrons);
        Assert.Null(rig.Optics.PixelSizeYMicrons);
    }

    [Fact]
    public void TheDatabaseValues_AreNeverStored_OnlyTheUsersInputsAre()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";
        form.SaveCommand.Execute(null);

        var rig = app.Host.RigRegistry.GetAll().Single();
        var text = File.ReadAllText(EquipmentFile);

        Assert.Null(rig.Optics!.PixelSizeXMicrons);
        Assert.Null(rig.Optics.SensorWidthPixels);
        Assert.DoesNotContain("pixelSize", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("6248", text);
        Assert.DoesNotContain("3.76", text);
        Assert.DoesNotContain("IMX571", text);
    }

    [Fact]
    public void TheRigAndTheFraming_GetTheDatabaseGeometry_WithoutAnyManualEntry()
    {
        var app = AppWithKnownCamera();
        var form = OpticsFormOf(app);
        form.FocalLengthText = "750";
        form.SaveCommand.Execute(null);

        var geometry = app.Vm.Equipment.Rigs.Single().Geometry;
        app.Vm.Framing.RefreshEquipment();

        Assert.Equal(1.034, geometry.PixelScaleXArcsecPerPixel!.Value, 3);
        Assert.Equal(GeometrySource.Database, geometry.PixelSizeSource);
        Assert.Equal(1.79, app.Vm.Framing.Field!.WidthDegrees, 2);
        Assert.Equal(1.20, app.Vm.Framing.Field.HeightDegrees, 2);
    }
}

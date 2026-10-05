using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>Managing rigs: add, rename, remove, give a rig its devices, share a mount or guider; what is saved, and how files from before a rig had a mount and guider load.</summary>
public sealed class RigManagementTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-rigs-" + Guid.NewGuid().ToString("N"));
    private readonly List<SideraRuntimeHost> _hosts = [];

    public RigManagementTests() => Directory.CreateDirectory(_directory);

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

    private string EquipmentFile => Path.Combine(_directory, "equipment.json");

    private sealed record App(EquipmentService Equipment, SideraRuntimeHost Host);

    private App Create(EquipmentConfiguration? stored = null)
    {
        if (stored is not null)
        {
            new EquipmentConfigurationStore(EquipmentFile).Save(stored);
        }

        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(new DemoOptions())]);
        var equipment = new EquipmentService(host, new EquipmentConfigurationStore(EquipmentFile), factories);
        equipment.Load();
        return new App(equipment, host);
    }

    private static DeviceConfiguration Sim(string id, DeviceType type, string? name = null) => DeviceConfiguration.Simulator(id, name ?? id, type);

    private static App Fill(App app)
    {
        foreach (var (id, type) in new[]
                 {
                     ("camera.main", DeviceType.Camera), ("camera.wide", DeviceType.Camera), ("focuser.main", DeviceType.Focuser), ("focuser.wide", DeviceType.Focuser),
                     ("mount.am3", DeviceType.Mount), ("mount.heq5", DeviceType.Mount), ("guider.phd2", DeviceType.Guider), ("guider.second", DeviceType.Guider),
                     ("filterwheel.main", DeviceType.FilterWheel), ("rotator.main", DeviceType.Rotator),
                 })
        {
            Assert.True(app.Equipment.Add(Sim(id, type)).Succeeded, id);
        }

        return app;
    }

    private static Rig RigOf(App app, string id) => app.Host.RigRegistry.TryGet(new RigId(id), out var rig) ? rig! : throw new InvalidOperationException("no rig " + id);

    // ---- Add, rename, remove

    [Fact]
    public void ARigIsAddedAroundACamera_WithNothingElse_AndSaved()
    {
        var app = Fill(Create());

        var result = app.Equipment.AddRig("Main Rig", "camera.main");

        Assert.True(result.Succeeded, result.Problem);
        var rig = Assert.Single(app.Host.RigRegistry.GetAll());
        Assert.Equal("Main Rig", rig.Name);
        Assert.Equal(new DeviceId("camera.main"), rig.CameraId);
        Assert.Equal("rig.main-rig", rig.Id.Value);
        Assert.Null(rig.FocuserId);
        Assert.Null(rig.MountId);
        Assert.Null(rig.GuiderId);
        Assert.Null(rig.Optics);
        Assert.Contains("rig.main-rig", File.ReadAllText(EquipmentFile));
    }

    [Fact]
    public void ARigNeedsAName_ThatIsNotTakenAndNotTooLong_AndACameraThatIsFree()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");

        Assert.False(app.Equipment.AddRig("  ", "camera.wide").Succeeded);
        Assert.False(app.Equipment.AddRig("main rig", "camera.wide").Succeeded); // the name is taken, whatever its case
        Assert.False(app.Equipment.AddRig(new string('x', 49), "camera.wide").Succeeded);
        Assert.False(app.Equipment.AddRig("Second", "camera.main").Succeeded); // the camera is in a rig
        Assert.False(app.Equipment.AddRig("Second", "focuser.main").Succeeded); // not a camera
        Assert.False(app.Equipment.AddRig("Second", "camera.nothing").Succeeded);
        Assert.Single(app.Host.RigRegistry.GetAll());
    }

    [Fact]
    public void TheIdOfARig_IsMadeFromItsName_AndStaysUnique()
    {
        var app = Fill(Create());

        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.RenameRig("rig.main-rig", "Renamed");
        app.Equipment.AddRig("Main Rig", "camera.wide");

        Assert.Equal(["rig.main-rig", "rig.main-rig-2"], app.Host.RigRegistry.GetAll().Select(r => r.Id.Value).Order());
    }

    [Fact]
    public void ARigIsRenamed_AndEverythingElseStays()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");
        app.Equipment.SetRigOptics("rig.main-rig", new OpticalTrain(750));

        var result = app.Equipment.RenameRig("rig.main-rig", "Imaging Scope");

        Assert.True(result.Succeeded);
        var rig = RigOf(app, "rig.main-rig");
        Assert.Equal("Imaging Scope", rig.Name);
        Assert.Equal(new DeviceId("mount.am3"), rig.MountId);
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.False(app.Equipment.RenameRig("rig.nothing", "X").Succeeded);
        Assert.False(app.Equipment.RenameRig("rig.main-rig", " ").Succeeded);
    }

    [Fact]
    public void ARigIsRemoved_AndItsDevicesStayWhereTheyAre()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");

        var result = app.Equipment.RemoveRig("rig.main-rig");

        Assert.True(result.Succeeded);
        Assert.Empty(app.Host.RigRegistry.GetAll());
        Assert.NotNull(app.Equipment.Configuration.Find("camera.main"));
        Assert.NotNull(app.Equipment.Configuration.Find("mount.am3"));
        Assert.True(app.Host.DeviceRegistry.TryGet(new DeviceId("mount.am3"), out _));
        Assert.Empty(Create().Equipment.Configuration.Rigs); // saved: the next start has no rig
        Assert.False(app.Equipment.RemoveRig("rig.main-rig").Succeeded);
    }

    // ---- Devices

    [Fact]
    public void ARigGetsItsDevices_OnePerRole_AndTheyAreSaved()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");

        foreach (var (role, id) in new[]
                 {
                     (RigRole.Focuser, "focuser.main"), (RigRole.FilterWheel, "filterwheel.main"), (RigRole.Rotator, "rotator.main"), (RigRole.Mount, "mount.am3"),
                     (RigRole.Guider, "guider.phd2"),
                 })
        {
            Assert.True(app.Equipment.SetRigDevice("rig.main-rig", role, id).Succeeded, role.ToString());
        }

        var rig = RigOf(app, "rig.main-rig");
        Assert.Equal(
            ["camera.main", "focuser.main", "filterwheel.main", "rotator.main", "mount.am3", "guider.phd2"],
            rig.Devices().Select(d => d.Device.Value));
        var reloaded = Create().Host.RigRegistry.GetAll().Single();
        Assert.Equal(rig.Devices().Select(d => d.Device), reloaded.Devices().Select(d => d.Device));
    }

    [Fact]
    public void AnOptionalDevice_CanBeTakenAway_ButTheCameraCannot()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");

        Assert.True(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, null).Succeeded);

        Assert.Null(RigOf(app, "rig.main-rig").MountId);
        Assert.False(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Camera, null).Succeeded);
    }

    [Fact]
    public void TheCameraOfARigCanBeChanged_ToAFreeCamera()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");

        Assert.True(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Camera, "camera.wide").Succeeded);

        Assert.Equal(new DeviceId("camera.wide"), RigOf(app, "rig.main-rig").CameraId);
    }

    [Fact]
    public void ADeviceOfTheWrongKindOrNotInTheEquipment_IsRefused()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");

        Assert.False(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "guider.phd2").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Guider, "mount.am3").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Focuser, "camera.wide").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.nothing").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.nothing", RigRole.Mount, "mount.am3").Succeeded);
        Assert.Null(RigOf(app, "rig.main-rig").MountId);
    }

    [Fact]
    public void AMountAndAGuider_AreSharedByNamingThemInTwoRigs()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.AddRig("Wide Rig", "camera.wide");

        Assert.True(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3").Succeeded);
        Assert.True(app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Mount, "mount.am3").Succeeded);
        Assert.True(app.Equipment.SetRigDevice("rig.main-rig", RigRole.Guider, "guider.phd2").Succeeded);
        Assert.True(app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Guider, "guider.phd2").Succeeded);

        Assert.Equal(2, app.Equipment.RigsOf("mount.am3").Count);
        Assert.Equal(2, app.Equipment.RigsOf("guider.phd2").Count);
    }

    [Fact]
    public void TwoRigs_CanHaveTheirOwnMountsAndGuiders()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.AddRig("Wide Rig", "camera.wide");

        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");
        app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Mount, "mount.heq5");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Guider, "guider.phd2");
        app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Guider, "guider.second");

        Assert.NotEqual(RigOf(app, "rig.main-rig").MountId, RigOf(app, "rig.wide-rig").MountId);
        Assert.NotEqual(RigOf(app, "rig.main-rig").GuiderId, RigOf(app, "rig.wide-rig").GuiderId);
        Assert.Single(app.Equipment.RigsOf("mount.am3"));
    }

    [Fact]
    public void ACameraFocuserFilterWheelOrRotator_IsNeverTakenFromAnotherRig()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.AddRig("Wide Rig", "camera.wide");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Focuser, "focuser.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Rotator, "rotator.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.FilterWheel, "filterwheel.main");

        var focuser = app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Focuser, "focuser.main");

        Assert.False(focuser.Succeeded);
        Assert.Contains("already the focuser of the rig 'Main Rig'", focuser.Problem);
        Assert.False(app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Rotator, "rotator.main").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.wide-rig", RigRole.FilterWheel, "filterwheel.main").Succeeded);
        Assert.False(app.Equipment.SetRigDevice("rig.wide-rig", RigRole.Camera, "camera.main").Succeeded);
        Assert.Null(RigOf(app, "rig.wide-rig").FocuserId);
    }

    [Fact]
    public void TheCandidatesOfARole_AreOfTheRightKind_AndExcludeWhatAnotherRigHas()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.AddRig("Wide Rig", "camera.wide");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Focuser, "focuser.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");

        Assert.Equal(["focuser.wide"], app.Equipment.CandidatesFor("rig.wide-rig", RigRole.Focuser).Select(d => d.Id));
        Assert.Equal(["mount.am3", "mount.heq5"], app.Equipment.CandidatesFor("rig.wide-rig", RigRole.Mount).Select(d => d.Id).Order()); // a mount can be shared
        Assert.Equal(["focuser.main", "focuser.wide"], app.Equipment.CandidatesFor("rig.main-rig", RigRole.Focuser).Select(d => d.Id).Order());
    }

    [Fact]
    public void ANewRotator_StartsWithoutACalibration()
    {
        var app = Fill(Create());
        app.Equipment.Add(Sim("rotator.second", DeviceType.Rotator));
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Rotator, "rotator.main");
        app.Equipment.SetRotatorModel("rig.main-rig", new Sidera.Core.Rotators.RotatorSkyModel(30));

        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Rotator, "rotator.second");

        Assert.Null(RigOf(app, "rig.main-rig").RotatorModel);
    }

    [Fact]
    public void TheOpticsOfARig_AreSet_AndKeepTheRestOfTheRig()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");

        Assert.True(app.Equipment.SetRigOptics("rig.main-rig", new OpticalTrain(750, 150)).Succeeded);

        var rig = RigOf(app, "rig.main-rig");
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.Equal(new DeviceId("mount.am3"), rig.MountId);
        Assert.True(app.Equipment.SetRigOptics("rig.main-rig", null).Succeeded);
        Assert.Null(RigOf(app, "rig.main-rig").Optics);
    }

    // ---- Removing a device

    [Fact]
    public void RemovingAMountOrGuider_TakesItOffTheRigs_AndTheRigsStay()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.AddRig("Wide Rig", "camera.wide");
        foreach (var rig in new[] { "rig.main-rig", "rig.wide-rig" })
        {
            app.Equipment.SetRigDevice(rig, RigRole.Mount, "mount.am3");
            app.Equipment.SetRigDevice(rig, RigRole.Guider, "guider.phd2");
        }

        Assert.True(app.Equipment.Remove("mount.am3").Succeeded);
        Assert.True(app.Equipment.Remove("guider.phd2").Succeeded);

        Assert.Equal(2, app.Host.RigRegistry.GetAll().Count);
        Assert.All(app.Host.RigRegistry.GetAll(), r =>
        {
            Assert.Null(r.MountId);
            Assert.Null(r.GuiderId);
        });
        var reloaded = Create().Host.RigRegistry.GetAll();
        Assert.All(reloaded, r => Assert.Null(r.MountId));
    }

    [Fact]
    public void ARigThatHoldsAMount_IsNotEmpty_SoTheOpticsRigOfACameraIsKept()
    {
        var app = Fill(Create());
        app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750)); // Sidera makes a rig that holds only these optics
        var implicitRig = Assert.Single(app.Host.RigRegistry.GetAll());
        app.Equipment.SetRigDevice(implicitRig.Id.Value, RigRole.Mount, "mount.am3");

        app.Equipment.SetCameraOptics("camera.main", null);

        var rig = Assert.Single(app.Host.RigRegistry.GetAll());
        Assert.Equal(new DeviceId("mount.am3"), rig.MountId);
        Assert.Null(rig.Optics);
    }

    [Fact]
    public void ACameraThatAlreadyHasAnOpticsRig_CannotGetASecondRig()
    {
        var app = Fill(Create());
        app.Equipment.SetCameraOptics("camera.main", new OpticalTrain(750));

        Assert.False(app.Equipment.AddRig("Another", "camera.main").Succeeded);
    }

    // ---- Persistence and migration

    [Fact]
    public void TheMountAndGuiderOfARig_AreStoredAsIds_AndNothingElseAboutThem()
    {
        var app = Fill(Create());
        app.Equipment.AddRig("Main Rig", "camera.main");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Mount, "mount.am3");
        app.Equipment.SetRigDevice("rig.main-rig", RigRole.Guider, "guider.phd2");

        var text = File.ReadAllText(EquipmentFile);

        Assert.Contains("\"mountId\": \"mount.am3\"", text);
        Assert.Contains("\"guiderId\": \"guider.phd2\"", text);
        Assert.DoesNotContain("shared", text, StringComparison.OrdinalIgnoreCase); // sharing is two rigs naming one device, not a setting
    }

    [Fact]
    public void AnEquipmentFileFromBeforeRigsHadAMountAndGuider_StillLoads_WithoutInventingOne()
    {
        const string old = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"},
                        {"id":"mount.am3","name":"Mount","type":"Mount","backend":"Simulator"},
                        {"id":"guider.phd2","name":"Guider","type":"Guider","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","optics":{"focalLengthMm":500}}]}
            """;

        var app = Create();
        File.WriteAllText(EquipmentFile, old, new UTF8Encoding(false));
        var loaded = Create();

        Assert.Empty(loaded.Equipment.Problems);
        var rig = Assert.Single(loaded.Host.RigRegistry.GetAll());
        Assert.Null(rig.MountId); // a mount exists, but the old rig never said it was this rig's: nothing is assumed
        Assert.Null(rig.GuiderId);
        Assert.Equal(500, rig.Optics!.FocalLengthMm);
        Assert.NotNull(app);
    }

    [Fact]
    public void AFileWhoseRigNamesAMountThatIsNotInIt_IsRefused()
    {
        const string bad = """
            {"format":"astra-equipment","version":1,
             "devices":[{"id":"camera.main","name":"Camera","type":"Camera","backend":"Simulator"}],
             "rigs":[{"id":"rig.main","name":"Main","cameraId":"camera.main","mountId":"mount.gone"}]}
            """;

        var ex = Assert.Throws<EquipmentConfigurationException>(() => EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(bad)));

        Assert.Contains("mount.gone", ex.Message);
    }

    [Fact]
    public void TheDemoRigs_ShareTheDemoMountAndGuider_ByNamingThem()
    {
        var app = Create();

        Assert.True(app.Equipment.AddDemoEquipment().Succeeded);

        var rigs = app.Host.RigRegistry.GetAll();
        Assert.Equal(3, rigs.Count);
        Assert.All(rigs, r =>
        {
            Assert.Equal(DemoSetup.MountId, r.MountId);
            Assert.Equal(DemoSetup.GuiderId, r.GuiderId);
        });
        Assert.Equal(3, app.Equipment.RigsOf(DemoSetup.MountId.Value).Count);
    }
}

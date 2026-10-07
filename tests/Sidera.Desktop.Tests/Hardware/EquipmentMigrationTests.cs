using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// An equipment file and a session from before the device-centred model: nothing is rewritten and nothing is lost. The imaging setup is the rig that was stored (same ids, same devices, same shared
/// mount and guider), so what refers to a rig by id still finds it; what refers to one that is gone says so instead of finding another.
/// </summary>
public sealed class EquipmentMigrationTests : IDisposable
{
    // A file as Sidera wrote it before: two cameras with focusers on one mount and one guider, as two rigs; version 1 of the format.
    private const string OldFile = """
        {
          "format": "astra-equipment",
          "version": 1,
          "devices": [
            { "id": "camera.main", "name": "ASI2600MM", "type": "Camera", "backend": "Simulator" },
            { "id": "camera.wide", "name": "ASI533MC", "type": "Camera", "backend": "Simulator" },
            { "id": "focuser.main", "name": "EAF", "type": "Focuser", "backend": "Simulator" },
            { "id": "focuser.wide", "name": "Wide Focuser", "type": "Focuser", "backend": "Simulator" },
            { "id": "mount.am3", "name": "AM3", "type": "Mount", "backend": "Simulator" },
            { "id": "guider.phd2", "name": "PHD2", "type": "Guider", "backend": "Simulator" }
          ],
          "rigs": [
            { "id": "rig.main", "name": "Main 750", "cameraId": "camera.main", "focuserId": "focuser.main", "mountId": "mount.am3", "guiderId": "guider.phd2", "optics": { "focalLengthMm": 750, "apertureMm": 150 } },
            { "id": "rig.wide", "name": "Wide 400", "cameraId": "camera.wide", "focuserId": "focuser.wide", "mountId": "mount.am3", "guiderId": "guider.phd2" }
          ]
        }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-migration-" + Guid.NewGuid().ToString("N"));
    private readonly List<SideraRuntimeHost> _hosts = [];

    public EquipmentMigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        foreach (var host in _hosts)
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

    private string FilePath => Path.Combine(_directory, "equipment.json");

    private (SideraRuntimeHost Host, EquipmentService Service) Load(string content)
    {
        File.WriteAllText(FilePath, content);
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var options = new DemoOptions();
        var service = new EquipmentService(host, new EquipmentConfigurationStore(FilePath), new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]));
        service.Load();
        return (host, service);
    }

    [Fact]
    public void AnOldFile_LoadsWithItsDevicesAndItsRigsAsImagingSetups_WithTheSameIds()
    {
        var (host, service) = Load(OldFile);

        Assert.Empty(service.Problems);
        Assert.Equal(["camera.main", "camera.wide", "focuser.main", "focuser.wide", "guider.phd2", "mount.am3"], service.Configuration.Devices.Select(d => d.Id).Order());
        Assert.Equal(["rig.main", "rig.wide"], host.RigRegistry.GetAll().Select(r => r.Id.Value).Order());
        var main = host.RigRegistry.GetAll().Single(r => r.Id.Value == "rig.main");
        Assert.Equal("Main 750", main.Name);
        Assert.Equal(750, main.Optics!.FocalLengthMm);
        // The shared mount and guider are the same device on both setups: sharing is the same id, nothing else.
        Assert.Equal(host.RigRegistry.GetAll().Select(r => r.MountId).Distinct().Single(), new Sidera.Core.Devices.DeviceId("mount.am3"));
        Assert.Equal(host.RigRegistry.GetAll().Select(r => r.GuiderId).Distinct().Single(), new Sidera.Core.Devices.DeviceId("guider.phd2"));
    }

    [Fact]
    public void ASavedChange_KeepsEveryOldDeviceAndSetup_AndTheFormatStaysVersionOne()
    {
        var (_, service) = Load(OldFile);

        Assert.True(service.AddRig("Third", "camera.wide").Succeeded is false); // the camera is in a setup already: refused, nothing written
        Assert.True(service.RenameRig("rig.wide", "Wide 400 mm").Succeeded);

        var again = new EquipmentConfigurationStore(FilePath).Load();
        Assert.Equal(1, EquipmentConfiguration.CurrentVersion);
        Assert.Equal(6, again.Devices.Count);
        Assert.Equal(["rig.main", "rig.wide"], again.Rigs.Select(r => r.Id).Order());
        Assert.Equal("Wide 400 mm", again.Rigs.Single(r => r.Id == "rig.wide").Name);
        var main = again.Rigs.Single(r => r.Id == "rig.main");
        Assert.Equal(("camera.main", "focuser.main", "mount.am3", "guider.phd2"), (main.CameraId, main.FocuserId, main.MountId, main.GuiderId));
        Assert.Equal(750, main.Optics!.FocalLengthMm);
    }

    [Fact]
    public void AFileWithAByteOrderMark_LoadsLikeAnyOther()
    {
        File.WriteAllBytes(FilePath, [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. System.Text.Encoding.UTF8.GetBytes(OldFile)]);
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(FilePath), new DeviceFactoryRegistry([new SimulatorDeviceFactory(new DemoOptions())]));

        service.Load();

        Assert.Empty(service.Problems);
        Assert.Equal(6, service.Configuration.Devices.Count);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsNotOverwritten_AndSaysSo()
    {
        var (host, service) = Load("{ \"format\": \"astra-equipment\", \"version\": 7, \"devices\": [] }");

        Assert.NotEmpty(service.Problems);
        Assert.Contains("version 7", string.Join(" ", service.Problems), StringComparison.Ordinal);
        Assert.Empty(host.RigRegistry.GetAll());
        Assert.Contains("\"version\": 7", File.ReadAllText(FilePath), StringComparison.Ordinal); // the file is as it was; nothing was silently replaced
    }

    // ---- a session that names a setup

    [Fact]
    public void ASessionThatNamesASetup_FindsItById_AndOneThatIsGone_IsRefusedInsteadOfBoundToAnother()
    {
        var (host, _) = Load(OldFile);
        var catalog = new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);
        var lane = Sessions.SessionFixture.Lane(new RigId("rig.wide"), Sessions.SessionFixture.Block(null, 60, 3));
        var found = SessionCompiler.Compile(Sessions.SessionFixture.Session(Sessions.SessionFixture.Target("M31", [lane])), catalog);

        Assert.DoesNotContain(found.Problems, p => p.ElementId == lane.Id);

        var missing = lane with { Setup = new RigId("rig.removed") };
        var refused = SessionCompiler.Compile(Sessions.SessionFixture.Session(Sessions.SessionFixture.Target("M31", [missing])), catalog);

        var problem = Assert.Single(refused.Problems, p => p.ElementId == lane.Id);
        Assert.Contains("'rig.removed' does not exist any more", problem.Message, StringComparison.Ordinal);
        Assert.Empty(refused.Steps.OfType<MultiRigStepDraft>()); // it did not image with the other setup instead
    }
}

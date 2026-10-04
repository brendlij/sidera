using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Hardware;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>The equipment file: its format, what it refuses, and the store that keeps it.</summary>
public sealed class EquipmentConfigurationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astra-equipment-tests-" + Guid.NewGuid().ToString("N"));

    public EquipmentConfigurationTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private string PathOf(string name) => Path.Combine(_directory, name);

    private static EquipmentConfiguration Sample() => new(
        [
            DeviceConfiguration.Ascom("camera.main", "Main Camera", DeviceType.Camera, "ASCOM.Simulator.Camera", "Camera V3 simulator"),
            DeviceConfiguration.Ascom("focuser.main", "Main Focuser", DeviceType.Focuser, "ASCOM.Simulator.Focuser"),
            DeviceConfiguration.Ascom("mount.eq6", "EQ6", DeviceType.Mount, "ASCOM.Simulator.Telescope"),
            DeviceConfiguration.Simulator("filterwheel.main", "Wheel", DeviceType.FilterWheel, new Dictionary<string, string> { ["filters"] = "L,R,G" }),
            DeviceConfiguration.Simulator("guider.main", "Guider", DeviceType.Guider),
        ],
        [
            new RigConfiguration(
                "rig.main", "Main Rig", "camera.main", "focuser.main", "filterwheel.main",
                new OpticalTrain(750, 150, 3.76, 23.5, 15.7, 6248, 4176)),
        ]);

    private static EquipmentConfigurationException Reject(string json) =>
        Assert.Throws<EquipmentConfigurationException>(() => EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(json)));

    private static string Minimal(string devices, string version = "1", string format = "astra-equipment") =>
        $$"""{ "format": "{{format}}", "version": {{version}}, "devices": [ {{devices}} ] }""";

    // The format

    [Fact]
    public void TheFile_HasTheFormatTheVersionAndTheDevices_InTheShapeOfTheSpecification()
    {
        var text = EquipmentConfigurationSerializer.ToText(Sample());

        Assert.Contains("\"format\": \"astra-equipment\"", text);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"id\": \"camera.main\"", text);
        Assert.Contains("\"type\": \"Camera\"", text);
        Assert.Contains("\"backend\": \"ASCOM\"", text);
        Assert.Contains("\"progId\": \"ASCOM.Simulator.Camera\"", text);
        Assert.Contains("\"driverName\": \"Camera V3 simulator\"", text);
        Assert.Contains("\"backend\": \"Simulator\"", text);
    }

    [Fact]
    public void TheFile_KeepsNoStateOfTheDevices()
    {
        var text = EquipmentConfigurationSerializer.ToText(Sample()).ToLowerInvariant();

        Assert.DoesNotContain("connected", text);
        Assert.DoesNotContain("position", text);
        Assert.DoesNotContain("tracking", text);
    }

    [Fact]
    public void WhatIsWritten_IsReadBackExactly()
    {
        var original = Sample();

        var read = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(original));

        Assert.Equal(original.Devices.Select(Describe), read.Devices.Select(Describe));
        var rig = Assert.Single(read.Rigs);
        Assert.Equal(("rig.main", "Main Rig", "camera.main", "focuser.main", "filterwheel.main"), (rig.Id, rig.Name, rig.CameraId, rig.FocuserId, rig.FilterWheelId));
        Assert.Equal(original.Rigs[0].Optics, rig.Optics);
    }

    private static string Describe(DeviceConfiguration d) =>
        $"{d.Id}|{d.Name}|{d.Type}|{d.Backend}|{string.Join(",", d.Settings.OrderBy(s => s.Key).Select(s => $"{s.Key}={s.Value}"))}";

    [Fact]
    public void ASimulatedBestFocus_IsKeptOnTheRig()
    {
        var configuration = new EquipmentConfiguration(
            Sample().Devices,
            [new RigConfiguration("rig.main", "Main Rig", "camera.main", null, null, new OpticalTrain(750, 150, 3.76, 23.5, 15.7, 6248, 4176), 20000)]);

        var read = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(configuration));

        Assert.Equal(20000, read.Rigs[0].SimulatedBestFocus);
        Assert.Null(read.Rigs[0].FocuserId);
    }

    [Fact]
    public void AnEmptyEquipment_IsAValidFile()
    {
        var read = EquipmentConfigurationSerializer.Deserialize(EquipmentConfigurationSerializer.Serialize(EquipmentConfiguration.Empty));

        Assert.Empty(read.Devices);
        Assert.Empty(read.Rigs);
    }

    [Fact]
    public void PropertiesThatAreNotKnown_AreIgnored_SoALaterVersionCanAddSome()
    {
        var read = EquipmentConfigurationSerializer.Deserialize(Encoding.UTF8.GetBytes(
            """{ "format": "astra-equipment", "version": 1, "future": 1, "devices": [ { "id": "camera.a", "name": "A", "type": "Camera", "backend": "Simulator", "colour": "red" } ] }"""));

        Assert.Equal("camera.a", Assert.Single(read.Devices).Id);
    }

    // What is refused

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "not an Sidera equipment file")]
    [InlineData("""{ "format": "something-else", "version": 1 }""", "not an Sidera equipment file")]
    [InlineData("""{ "format": "astra-equipment" }""", "no valid version")]
    [InlineData("""{ "format": "astra-equipment", "version": 0 }""", "no valid version")]
    [InlineData("""{ "format": "astra-equipment", "version": "1" }""", "no valid version")]
    public void ANonEquipmentFile_IsRefusedWithASentence(string json, string expected)
    {
        Assert.Contains(expected, Reject(json).Message);
    }

    [Fact]
    public void AFileOfANewerVersion_IsRefused_NotHalfRead()
    {
        var failure = Reject(Minimal("", version: "2"));

        Assert.Contains("version 2", failure.Message);
        Assert.Contains("up to version 1", failure.Message);
    }

    [Theory]
    [InlineData("""{ "id": "", "name": "A", "type": "Camera", "backend": "Simulator" }""", "has no 'id'")]
    [InlineData("""{ "id": "bad id", "name": "A", "type": "Camera", "backend": "Simulator" }""", "not valid")]
    [InlineData("""{ "id": "camera.a", "type": "Camera", "backend": "Simulator" }""", "has no 'name'")]
    [InlineData("""{ "id": "camera.a", "name": "A", "type": "Telescope", "backend": "Simulator" }""", "unknown type 'Telescope'")]
    [InlineData("""{ "id": "camera.a", "name": "A", "type": "Camera", "backend": "Alpaca" }""", "unknown backend 'Alpaca'")]
    [InlineData("""{ "id": "camera.a", "name": "A", "type": "Camera", "backend": "ASCOM" }""", "has no ProgId")]
    [InlineData("""{ "id": "camera.a", "name": "A", "type": "Camera", "backend": "ASCOM", "settings": { "progId": " " } }""", "has no ProgId")]
    [InlineData("""{ "id": "wheel.a", "name": "A", "type": "FilterWheel", "backend": "ASCOM", "settings": { "progId": "X.Wheel" } }""", "does not support yet")]
    [InlineData("""{ "id": "camera.a", "name": "A", "type": "Camera", "backend": "Simulator", "settings": { "k": 5 } }""", "must be text")]
    public void ABrokenDevice_IsRefused_WithTheReason(string device, string expected)
    {
        Assert.Contains(expected, Reject(Minimal(device)).Message);
    }

    [Fact]
    public void TwoDevicesWithOneId_AreRefused_EvenWhenOnlyTheCaseDiffers()
    {
        var failure = Reject(Minimal(
            """{ "id": "camera.a", "name": "A", "type": "Camera", "backend": "Simulator" }, { "id": "Camera.A", "name": "B", "type": "Camera", "backend": "Simulator" }"""));

        Assert.Contains("used twice", failure.Message);
    }

    [Fact]
    public void ARigWithADeviceThatIsNotInTheFile_IsRefused()
    {
        var failure = Reject(
            """
            { "format": "astra-equipment", "version": 1, "devices": [ { "id": "camera.a", "name": "A", "type": "Camera", "backend": "Simulator" } ],
               "rigs": [ { "id": "rig.a", "name": "R", "cameraId": "camera.a", "focuserId": "focuser.zzz", "optics": { "focalLengthMm": 750, "apertureMm": 150, "pixelSizeMicrons": 3.76, "sensorWidthMm": 23.5, "sensorHeightMm": 15.7, "resolutionWidth": 6248, "resolutionHeight": 4176 } } ] }
            """);

        Assert.Contains("'focuser.zzz', which is not in the file", failure.Message);
    }

    [Fact]
    public void ARigWithInvalidOptics_IsRefused()
    {
        var failure = Reject(
            """
            { "format": "astra-equipment", "version": 1, "devices": [ { "id": "camera.a", "name": "A", "type": "Camera", "backend": "Simulator" } ],
               "rigs": [ { "id": "rig.a", "name": "R", "cameraId": "camera.a", "optics": { "focalLengthMm": -1, "apertureMm": 150, "pixelSizeMicrons": 3.76, "sensorWidthMm": 23.5, "sensorHeightMm": 15.7, "resolutionWidth": 6248, "resolutionHeight": 4176 } } ] }
            """);

        Assert.Contains("optics of the rig 'rig.a' are not valid", failure.Message);
    }

    // The ids

    [Theory]
    [InlineData("camera.main", null)]
    [InlineData("focuser_2-b", null)]
    [InlineData("A1", null)]
    [InlineData("", "required")]
    [InlineData("  ", "required")]
    [InlineData(".camera", "starts with a letter or digit")]
    [InlineData("camera main", "can only contain")]
    [InlineData("kamera/ü", "can only contain")]
    public void TheRulesForAnId(string id, string? expected)
    {
        var problem = EquipmentIds.Problem(id);

        if (expected is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(expected, problem);
        }
    }

    [Fact]
    public void AnIdThatIsTooLong_IsRefused()
    {
        Assert.Contains("at most 64", EquipmentIds.Problem(new string('a', 65)));
        Assert.Null(EquipmentIds.Problem(new string('a', 64)));
    }

    [Theory]
    [InlineData(DeviceType.Camera, "Main Camera", "camera.main-camera")]
    [InlineData(DeviceType.FilterWheel, "ZWO EFW 7", "filterwheel.zwo-efw-7")]
    [InlineData(DeviceType.Mount, "  EQ6-R  Pro!! ", "mount.eq6-r-pro")]
    [InlineData(DeviceType.Focuser, "", "focuser")]
    [InlineData(DeviceType.Focuser, "Äpfel", "focuser.pfel")]
    public void TheSuggestedId_IsMadeOfTheKindAndTheName(DeviceType type, string name, string expected)
    {
        var suggestion = EquipmentIds.Suggest(type, name);

        Assert.Equal(expected, suggestion);
        Assert.Null(EquipmentIds.Problem(suggestion));
    }

    // The store

    [Fact]
    public void AMissingFile_IsAnEmptyInstallation()
    {
        var store = new EquipmentConfigurationStore(PathOf("none.json"));

        Assert.Empty(store.Load().Devices);
    }

    [Fact]
    public void SavingCreatesTheFolder_WritesTheFile_AndLeavesNothingElseBehind()
    {
        var path = Path.Combine(_directory, "sub", "equipment.json");
        var store = new EquipmentConfigurationStore(path);

        store.Save(Sample());

        Assert.True(File.Exists(path));
        Assert.Equal([path], Directory.GetFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories));
        Assert.Equal(Sample().Devices.Select(Describe), store.Load().Devices.Select(Describe));
    }

    [Fact]
    public void SavingAgain_KeepsThePreviousFileAsABackup()
    {
        var store = new EquipmentConfigurationStore(PathOf("equipment.json"));
        store.Save(Sample());
        var first = File.ReadAllBytes(store.Path);

        store.Save(EquipmentConfiguration.Empty);

        Assert.Empty(store.Load().Devices);
        Assert.Equal(first, File.ReadAllBytes(store.Path + ".bak"));
        Assert.Equal(2, Directory.GetFiles(_directory).Length); // the file and its backup: no temporary file
    }

    [Fact]
    public void AnUnreadableFile_IsReported_AndLeftAlone()
    {
        var store = new EquipmentConfigurationStore(PathOf("equipment.json"));
        File.WriteAllText(store.Path, "{ not json");

        var failure = Assert.Throws<EquipmentConfigurationException>(store.Load);

        Assert.Contains("not valid JSON", failure.Message);
        Assert.Equal("{ not json", File.ReadAllText(store.Path));
    }

    [Fact]
    public void SavingOverAnUnreadableFile_KeepsItsContent_AsTheBackup()
    {
        var store = new EquipmentConfigurationStore(PathOf("equipment.json"));
        File.WriteAllText(store.Path, "{ not json");

        store.Save(Sample());

        Assert.Equal("{ not json", File.ReadAllText(store.Path + ".bak"));
        Assert.Equal(5, store.Load().Devices.Count);
    }

    [Fact]
    public void ASaveThatCannotBeDone_SaysSo_AndChangesNothing()
    {
        var blocker = PathOf("blocker");
        File.WriteAllText(blocker, "a file where the folder should be");
        var store = new EquipmentConfigurationStore(Path.Combine(blocker, "equipment.json"));

        var failure = Assert.Throws<EquipmentConfigurationException>(() => store.Save(Sample()));

        Assert.Contains("could not be saved", failure.Message);
        Assert.Equal("a file where the folder should be", File.ReadAllText(blocker));
    }

    [Fact]
    public void TheDefaultPath_IsInTheApplicationDataFolder_AsEquipmentJson()
    {
        var path = EquipmentConfigurationStore.DefaultPath();

        Assert.Equal("equipment.json", Path.GetFileName(path));
        Assert.Equal("Sidera", Path.GetFileName(Path.GetDirectoryName(path)));
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), path);
    }
}

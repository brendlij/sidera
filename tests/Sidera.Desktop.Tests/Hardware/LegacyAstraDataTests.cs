using System.Text;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Hardware;

namespace Sidera.Desktop.Tests.Hardware;

/// <summary>
/// What Sidera takes over from the time it was called Astra: the equipment file moves to the new folder once, nothing is overwritten
/// or deleted, and the formats of the files keep their identifiers and versions, so that old files load unchanged.
/// </summary>
public sealed class LegacyAstraDataTests : IDisposable
{
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "sidera-legacy-" + Guid.NewGuid().ToString("N"));

    private const string LegacyEquipment =
        "{\"format\":\"astra-equipment\",\"version\":1,\"devices\":[{\"id\":\"camera.asi\",\"name\":\"ASI\",\"type\":\"Camera\"," +
        "\"backend\":\"ASCOM\",\"settings\":{\"progId\":\"ASCOM.ASICamera2.Camera\"}}]}";

    private string Legacy => LegacyAstraData.LegacyEquipmentFile(_appData);
    private string Current => LegacyAstraData.EquipmentFile(_appData);

    public void Dispose()
    {
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void TheCanonicalFolderIsSidera_AndTheLegacyOneIsAstra()
    {
        Assert.EndsWith(Path.Combine("Sidera", "equipment.json"), Current);
        Assert.EndsWith(Path.Combine("Astra", "equipment.json"), Legacy);
    }

    [Fact]
    public void OnlyTheAstraFolder_IsCopiedToSidera_AndTheOldFilesStay()
    {
        Write(Legacy, LegacyEquipment);
        Write(Legacy + ".bak", "backup");

        var result = LegacyAstraData.MigrateEquipmentFile(Current, Legacy);

        Assert.Equal(LegacyMigrationOutcome.Copied, result.Outcome);
        Assert.Equal(LegacyEquipment, File.ReadAllText(Current));
        Assert.Equal("backup", File.ReadAllText(Current + ".bak"));
        Assert.Equal(LegacyEquipment, File.ReadAllText(Legacy));
        Assert.True(File.Exists(Legacy + ".bak"));
    }

    [Fact]
    public void OnlyTheSideraFolder_IsUsedAsItIs()
    {
        Write(Current, "{}");

        var result = LegacyAstraData.MigrateEquipmentFile(Current, Legacy);

        Assert.Equal(LegacyMigrationOutcome.NotNeeded, result.Outcome);
        Assert.Equal("{}", File.ReadAllText(Current));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Legacy)));
    }

    [Fact]
    public void WhenBothExist_SideraIsKept_AndAstraIsLeftUntouched()
    {
        Write(Current, "sidera content");
        Write(Legacy, LegacyEquipment);

        var result = LegacyAstraData.MigrateEquipmentFile(Current, Legacy);

        Assert.Equal(LegacyMigrationOutcome.NotNeeded, result.Outcome);
        Assert.Equal("sidera content", File.ReadAllText(Current));
        Assert.Equal(LegacyEquipment, File.ReadAllText(Legacy));
    }

    [Fact]
    public void WhenThereIsNoEquipmentAtAll_NothingIsCreated()
    {
        var result = LegacyAstraData.MigrateEquipmentFile(Current, Legacy);

        Assert.Equal(LegacyMigrationOutcome.NotNeeded, result.Outcome);
        Assert.False(Directory.Exists(_appData));
    }

    [Fact]
    public void TheMigration_HappensOnce_AndALaterChangeOfTheNewFileIsNotUndone()
    {
        Write(Legacy, LegacyEquipment);
        LegacyAstraData.MigrateEquipmentFile(Current, Legacy);
        File.WriteAllText(Current, "changed in Sidera");

        var again = LegacyAstraData.MigrateEquipmentFile(Current, Legacy);

        Assert.Equal(LegacyMigrationOutcome.NotNeeded, again.Outcome);
        Assert.Equal("changed in Sidera", File.ReadAllText(Current));
    }

    [Fact]
    public void AnEquipmentFileOfTheAstraTime_LoadsAfterTheMigration_AndIsWrittenBackWithTheSameFormatAndVersion()
    {
        Write(Legacy, LegacyEquipment);
        LegacyAstraData.MigrateEquipmentFile(Current, Legacy);
        var store = new EquipmentConfigurationStore(Current);

        var loaded = store.Load();
        store.Save(loaded);

        var device = Assert.Single(loaded.Devices);
        Assert.Equal(("camera.asi", "ASCOM.ASICamera2.Camera"), (device.Id, device.ProgId));
        var written = File.ReadAllText(Current);
        Assert.Contains("\"format\": \"astra-equipment\"", written); // a format name, not the product name
        Assert.Contains("\"version\": 1", written);
        Assert.Equal(("astra-equipment", 1), (EquipmentConfiguration.FormatId, EquipmentConfiguration.CurrentVersion));
    }

    [Fact]
    public async Task AnAstraseqFileOfTheAstraTime_LoadsUnchanged_AndTheFormatIsNotBumped()
    {
        const string text = "{\"format\":\"astra-sequence\",\"version\":5,\"steps\":[{\"type\":\"exposure\"," +
            "\"id\":\"00000000-0000-0000-0000-00000000000a\",\"cameraId\":\"camera.main\",\"exposureSeconds\":300}]}";

        var document = await new JsonSequenceDocumentSerializer().LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

        Assert.Single(document.Steps);
        Assert.Equal(".astraseq", SequenceDocumentFiles.Extension);
        Assert.Equal(("astra-sequence", 6), (SequenceDocument.FormatId, SequenceDocument.CurrentVersion));
    }
}

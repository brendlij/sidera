using System;
using System.IO;

namespace Sidera.Desktop.Hardware;

/// <summary>What <see cref="LegacyAstraData.MigrateEquipmentFile"/> did.</summary>
public enum LegacyMigrationOutcome
{
    /// <summary>There was nothing to migrate: the Sidera file exists, or there is no file of the earlier name.</summary>
    NotNeeded,

    /// <summary>The equipment file of the earlier name was copied to the Sidera folder.</summary>
    Copied,

    /// <summary>The copy failed; the file of the earlier name is untouched and Sidera starts without equipment.</summary>
    Failed,
}

/// <param name="Problem">Why it failed, for the log; <c>null</c> otherwise.</param>
public sealed record LegacyMigrationResult(LegacyMigrationOutcome Outcome, string? Problem = null);

/// <summary>
/// The data Sidera finds from when it was called Astra. The equipment file lived in <c>%APPDATA%\Astra</c>; its place is now
/// <c>%APPDATA%\Sidera</c>. The first start of Sidera copies it, when there is no equipment file of Sidera yet: a file of Sidera is never
/// overwritten, and nothing of the earlier folder is changed or deleted. The kept copy of the file (<c>equipment.json.bak</c>) comes along.
/// </summary>
public static class LegacyAstraData
{
    public const string FolderName = "Astra";

    public static string LegacyEquipmentFile(string applicationData) =>
        Path.Combine(applicationData, FolderName, EquipmentConfigurationStore.FileName);

    public static string EquipmentFile(string applicationData) =>
        Path.Combine(applicationData, "Sidera", EquipmentConfigurationStore.FileName);

    /// <summary>Copies the equipment file of the earlier name to <paramref name="file"/>, once; see the type.</summary>
    public static LegacyMigrationResult MigrateEquipmentFile(string file, string legacyFile)
    {
        try
        {
            if (File.Exists(file) || !File.Exists(legacyFile))
            {
                return new LegacyMigrationResult(LegacyMigrationOutcome.NotNeeded);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            File.Copy(legacyFile, file, overwrite: false);
            var backup = legacyFile + ".bak";
            if (File.Exists(backup) && !File.Exists(file + ".bak"))
            {
                File.Copy(backup, file + ".bak", overwrite: false);
            }

            return new LegacyMigrationResult(LegacyMigrationOutcome.Copied);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LegacyMigrationResult(LegacyMigrationOutcome.Failed, ex.Message);
        }
    }
}

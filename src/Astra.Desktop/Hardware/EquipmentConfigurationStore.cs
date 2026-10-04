using System;
using System.IO;

namespace Astra.Desktop.Hardware;

/// <summary>
/// Where the equipment is kept: one JSON file in the application data folder of the user
/// (<c>%APPDATA%\Astra\equipment.json</c>), separate from every sequence file. A missing file is an empty installation.
/// Saving is atomic: the new content is written beside the file and replaces it in one step, and the file it replaces is
/// kept as <c>equipment.json.bak</c>, so that neither a crash nor a mistake loses the equipment.
/// </summary>
public sealed class EquipmentConfigurationStore(string path)
{
    public const string FileName = "equipment.json";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Astra", FileName);

    public static EquipmentConfigurationStore CreateDefault() => new(DefaultPath());

    /// <summary>The stored equipment; empty when there is no file.</summary>
    /// <exception cref="EquipmentConfigurationException">The file exists and cannot be used. It is left as it is.</exception>
    public EquipmentConfiguration Load()
    {
        byte[] content;
        try
        {
            if (!File.Exists(Path))
            {
                return EquipmentConfiguration.Empty;
            }

            content = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new EquipmentConfigurationException($"The equipment file {Path} could not be read.", ex);
        }

        return EquipmentConfigurationSerializer.Deserialize(content);
    }

    /// <exception cref="EquipmentConfigurationException">The file could not be written; the previous file is untouched.</exception>
    public void Save(EquipmentConfiguration configuration)
    {
        // Everything is encoded before the destination is touched.
        var content = EquipmentConfigurationSerializer.Serialize(configuration);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temporary = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(content);
                file.Flush(flushToDisk: true);
            }

            if (File.Exists(Path))
            {
                File.Replace(temporary, Path, Path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, Path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw new EquipmentConfigurationException($"The equipment could not be saved to {Path}.", ex);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is harmless; the original error is what matters.
        }
    }
}

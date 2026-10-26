using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sidera.Core.Focusing;

namespace Sidera.Desktop.Settings;

/// <summary>
/// The positions that autofocus found in focus, saved in a file next to the settings, so that the next night starts from them. A file that cannot be read is ignored (focus is searched from where
/// the focuser stands then); one that cannot be written is told to the caller, who says so once.
/// </summary>
public sealed class FocusHistoryStore(string path)
{
    public const string FileName = "focus-history.json";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>The file beside a settings file.</summary>
    public static FocusHistoryStore BesideSettings(string settingsPath) =>
        new(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(settingsPath)) ?? string.Empty, FileName));

    public IReadOnlyDictionary<string, IReadOnlyList<FocusHistoryEntry>> Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return new Dictionary<string, IReadOnlyList<FocusHistoryEntry>>();
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(Path));
            var result = new Dictionary<string, IReadOnlyList<FocusHistoryEntry>>(StringComparer.Ordinal);
            foreach (var rig in document.RootElement.GetProperty("rigs").EnumerateObject())
            {
                result[rig.Name] = rig.Value.EnumerateArray()
                    .Select(e => new FocusHistoryEntry(e.GetProperty("position").GetInt32(), e.GetProperty("hfr").GetDouble(), e.GetProperty("when").GetDateTimeOffset()))
                    .ToList();
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UnauthorizedAccessException)
        {
            return new Dictionary<string, IReadOnlyList<FocusHistoryEntry>>();
        }
    }

    /// <exception cref="IOException">The file cannot be written.</exception>
    public void Save(IReadOnlyDictionary<string, IReadOnlyList<FocusHistoryEntry>> entries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temporary = Path + ".tmp";
        using (var stream = File.Create(temporary))
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartObject("rigs");
            foreach (var (rig, list) in entries)
            {
                w.WriteStartArray(rig);
                foreach (var entry in list)
                {
                    w.WriteStartObject();
                    w.WriteNumber("position", entry.Position);
                    w.WriteNumber("hfr", entry.Hfr);
                    w.WriteString("when", entry.When);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
        }

        File.Move(temporary, Path, overwrite: true);
    }
}

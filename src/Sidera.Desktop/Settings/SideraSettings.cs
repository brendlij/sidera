using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Sidera.Core.Location;

namespace Sidera.Desktop.Settings;

/// <summary>
/// The settings of the application, as one document: today the observing site. A setting that was never made is <c>null</c>, not a default:
/// a site that nobody entered is unknown, never 0° 0° 0 m.
/// </summary>
public sealed record SideraSettings(ObservingSite? Site)
{
    public const string FormatId = "sidera-settings";
    public const int CurrentVersion = 1;

    public static SideraSettings Empty { get; } = new((ObservingSite?)null);
}

public sealed class SideraSettingsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Reads and writes the settings document; backend-neutral, nothing in it belongs to a device.</summary>
public static class SideraSettingsSerializer
{
    public static byte[] Serialize(SideraSettings settings)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", SideraSettings.FormatId);
            w.WriteNumber("version", SideraSettings.CurrentVersion);
            if (settings.Site is { } site)
            {
                w.WriteStartObject("site");
                if (site.Name is { } name)
                {
                    w.WriteString("name", name);
                }

                w.WriteNumber("latitudeDegrees", site.LatitudeDegrees);
                w.WriteNumber("longitudeDegrees", site.LongitudeDegrees);
                w.WriteNumber("elevationMeters", site.ElevationMeters);
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <exception cref="SideraSettingsException">The document is not valid; a site that is out of range is refused, not repaired.</exception>
    public static SideraSettings Deserialize(ReadOnlySpan<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.ToArray());
        }
        catch (JsonException ex)
        {
            throw new SideraSettingsException("The settings file is not valid JSON.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.GetString() != SideraSettings.FormatId)
            {
                throw new SideraSettingsException("The file is not a Sidera settings file.");
            }

            if (!root.TryGetProperty("site", out var site) || site.ValueKind == JsonValueKind.Null)
            {
                return SideraSettings.Empty;
            }

            if (site.ValueKind != JsonValueKind.Object)
            {
                throw new SideraSettingsException("The site in the settings file must be an object.");
            }

            double Number(string name) =>
                site.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                    ? number
                    : throw new SideraSettingsException($"The site in the settings file has no number '{name}'.");

            var latitude = Number("latitudeDegrees");
            var longitude = Number("longitudeDegrees");
            var elevation = Number("elevationMeters");
            if (ObservingSite.Problem(latitude, longitude, elevation) is { } problem)
            {
                throw new SideraSettingsException($"The site in the settings file is not valid: {problem}");
            }

            var name = site.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String ? nameElement.GetString() : null;
            return new SideraSettings(new ObservingSite(latitude, longitude, elevation, name));
        }
    }
}

/// <summary>
/// Where the settings are kept: <c>%APPDATA%\Sidera\settings.json</c>, beside the equipment file and apart from it. A missing file is a
/// installation with no settings. Saving is atomic and keeps the file it replaces as <c>settings.json.bak</c>.
/// </summary>
public sealed class SideraSettingsStore(string path)
{
    public const string FileName = "settings.json";

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sidera", FileName);

    public static SideraSettingsStore CreateDefault() => new(DefaultPath());

    /// <exception cref="SideraSettingsException">The file exists and cannot be used. It is left as it is.</exception>
    public SideraSettings Load()
    {
        byte[] content;
        try
        {
            if (!File.Exists(Path))
            {
                return SideraSettings.Empty;
            }

            content = File.ReadAllBytes(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SideraSettingsException($"The settings file {Path} could not be read.", ex);
        }

        return SideraSettingsSerializer.Deserialize(content);
    }

    /// <exception cref="SideraSettingsException">The file could not be written; the previous file is untouched.</exception>
    public void Save(SideraSettings settings)
    {
        var content = SideraSettingsSerializer.Serialize(settings);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temporary = System.IO.Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporary, content);
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
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // A leftover temporary file is harmless.
            }

            throw new SideraSettingsException($"The settings could not be saved to {Path}.", ex);
        }
    }
}

/// <summary>How a change of the site ended.</summary>
public sealed record SiteResult(bool Succeeded, string? Problem = null)
{
    public static SiteResult Ok() => new(true);

    public static SiteResult Fail(string problem) => new(false, problem);
}

/// <summary>
/// The global observing site of Sidera: one place for the whole application, independent of any rig or device, and unknown until it is
/// configured. A later rig may override it; the rule then is "the rig's site, else this one" (<see cref="ResolveFor"/>), so that the global
/// site is never copied into rigs.
/// </summary>
public sealed class SiteService
{
    private readonly SideraSettingsStore _store;
    private SideraSettings _settings = SideraSettings.Empty;

    public SiteService(SideraSettingsStore store)
    {
        _store = store;
    }

    /// <summary>The configured site; <c>null</c> when none was entered.</summary>
    public ObservingSite? Site => _settings.Site;

    /// <summary>What went wrong while loading (an unreadable file); <c>null</c> when all is well.</summary>
    public string? Problem { get; private set; }

    public string FilePath => _store.Path;

    /// <summary>Raised on the calling thread after the site was changed and saved.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the settings. A file that cannot be used leaves the site unknown and stays as it is until the next change.</summary>
    public void Load()
    {
        try
        {
            _settings = _store.Load();
            Problem = null;
        }
        catch (SideraSettingsException ex)
        {
            _settings = SideraSettings.Empty;
            Problem = ex.Message;
        }
    }

    public SiteResult Set(ObservingSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return Save(_settings with { Site = site });
    }

    public SiteResult Clear() => Save(_settings with { Site = null });

    /// <summary>The site that counts for something: the override of a rig when it has one, else the global site. Rigs have no override yet.</summary>
    public ObservingSite? ResolveFor(ObservingSite? rigOverride = null) => rigOverride ?? Site;

    private SiteResult Save(SideraSettings next)
    {
        try
        {
            _store.Save(next);
        }
        catch (SideraSettingsException ex)
        {
            return SiteResult.Fail(ex.Message);
        }

        _settings = next;
        Problem = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return SiteResult.Ok();
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Desktop.Documents;

namespace Sidera.Desktop.Settings;

/// <summary>
/// The settings of the application, as one document: today the observing site. A setting that was never made is <c>null</c>, not a default:
/// a site that nobody entered is unknown, never 0° 0° 0 m.
/// </summary>
public sealed record SideraSettings(ObservingSite? Site)
{
    public PlateSolvingSettings PlateSolving { get; init; } = new();
    public SkyAtlasSettings SkyAtlas { get; init; } = new();
    public SequencerSettings Sequencer { get; init; } = new();
    public ImagingSettings Imaging { get; init; } = new();
    public AutofocusDefaults Autofocus { get; init; } = new();
    public GuidingDefaults Guiding { get; init; } = new();

    /// <summary>The meridian flip that a workflow uses when it does not set its own (see <see cref="MeridianFlipSettings"/>); off until it is enabled here.</summary>
    public MeridianFlipSettings MeridianFlip { get; init; } = new();

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
            w.WritePropertyName("plateSolving");
            JsonSerializer.Serialize(w, settings.PlateSolving);
            w.WritePropertyName("skyAtlas");
            JsonSerializer.Serialize(w, settings.SkyAtlas);

            // The sections that came later are written by hand, with names that do not depend on a class: a file that has none of them means the defaults.
            w.WriteStartObject("sequencer");
            w.WriteString("defaultSessionMode", settings.Sequencer.DefaultSessionMode == SessionMode.Advanced ? "advanced" : "workflow");
            w.WriteEndObject();

            w.WriteStartObject("imaging");
            if (settings.Imaging.SaveDirectory is { } directory)
            {
                w.WriteString("saveDirectory", directory);
            }

            w.WriteBoolean("autoStretch", settings.Imaging.AutoStretch);
            w.WriteBoolean("fitOnCapture", settings.Imaging.FitOnCapture);
            w.WriteNumber("manualExposureSeconds", settings.Imaging.ManualExposureSeconds);
            w.WriteEndObject();

            w.WriteStartObject("autofocus");
            w.WriteNumber("exposureSeconds", settings.Autofocus.ExposureSeconds);
            w.WriteNumber("stepSize", settings.Autofocus.StepSize);
            w.WriteNumber("sampleCount", settings.Autofocus.SampleCount);
            w.WriteBoolean("policyEnabled", settings.Autofocus.PolicyEnabled);
            w.WriteBoolean("policyAtStart", settings.Autofocus.PolicyAtStart);
            w.WriteNumber("policyIntervalMinutes", settings.Autofocus.PolicyIntervalMinutes);
            w.WriteBoolean("policyAfterFilterChange", settings.Autofocus.PolicyAfterFilterChange);
            w.WriteEndObject();

            w.WriteStartObject("guiding");
            w.WriteBoolean("startBeforeImaging", settings.Guiding.StartBeforeImaging);
            w.WriteBoolean("stopWhenDone", settings.Guiding.StopWhenDone);
            w.WriteBoolean("ditherByDefault", settings.Guiding.DitherByDefault);
            w.WriteNumber("ditherEveryNFrames", settings.Guiding.DitherEveryNFrames);
            w.WriteNumber("ditherAmplitudePixels", settings.Guiding.DitherAmplitudePixels);
            w.WriteNumber("settleThresholdPixels", settings.Guiding.SettleThresholdPixels);
            w.WriteNumber("settleStableSeconds", settings.Guiding.SettleStableSeconds);
            w.WriteNumber("settleTimeoutSeconds", settings.Guiding.SettleTimeoutSeconds);
            w.WriteEndObject();

            w.WriteStartObject("meridianFlip");
            WorkflowJson.WriteSettingsBody(w, settings.MeridianFlip);
            w.WriteEndObject();
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

            var solving = new PlateSolvingSettings();
            if (root.TryGetProperty("plateSolving", out var solveElement))
            {
                try { solving = solveElement.Deserialize<PlateSolvingSettings>() ?? new(); }
                catch (JsonException ex) { throw new SideraSettingsException("The plate solving settings are invalid.", ex); }
                if (solving.Problem is { } solveProblem) throw new SideraSettingsException(solveProblem);
            }
            var atlas = new SkyAtlasSettings();
            if (root.TryGetProperty("skyAtlas", out var atlasElement))
            {
                try { atlas = atlasElement.Deserialize<SkyAtlasSettings>() ?? new(); }
                catch (JsonException ex) { throw new SideraSettingsException("The sky atlas settings are invalid.", ex); }
                if (atlas.Problem is { } atlasProblem) throw new SideraSettingsException(atlasProblem);
            }
            var product = ReadProductSections(root);
            if (!root.TryGetProperty("site", out var site) || site.ValueKind == JsonValueKind.Null)
            {
                return product with { PlateSolving = solving, SkyAtlas = atlas };
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
            return product with { Site = new ObservingSite(latitude, longitude, elevation, name), PlateSolving = solving, SkyAtlas = atlas };
        }
    }

    // The sections of the settings that a file written before them does not have: each is optional, and a value that is missing is its default. A value that is there and wrong is refused,
    // with the section named, and the file is left as it is.
    private static SideraSettings ReadProductSections(JsonElement root)
    {
        var settings = SideraSettings.Empty;

        JsonElement? Section(string name, string what)
        {
            if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return element.ValueKind == JsonValueKind.Object ? element : throw new SideraSettingsException($"The {what} settings must be an object.");
        }

        bool Flag(JsonElement e, string name, bool fallback, string what) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new SideraSettingsException($"'{name}' of the {what} settings must be true or false.");
        double Num(JsonElement e, string name, double fallback, string what) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw new SideraSettingsException($"'{name}' of the {what} settings must be a number.");
        int Int(JsonElement e, string name, int fallback, string what) =>
            !e.TryGetProperty(name, out var v) ? fallback : v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw new SideraSettingsException($"'{name}' of the {what} settings must be a whole number.");

        if (Section("sequencer", "sequencer") is { } sequencer)
        {
            var mode = sequencer.TryGetProperty("defaultSessionMode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (sequencer.TryGetProperty("defaultSessionMode", out _) && mode is not ("workflow" or "advanced"))
            {
                throw new SideraSettingsException("The default session mode must be 'workflow' or 'advanced'.");
            }

            settings = settings with { Sequencer = new SequencerSettings { DefaultSessionMode = mode == "advanced" ? SessionMode.Advanced : SessionMode.Workflow } };
        }

        if (Section("imaging", "imaging") is { } imaging)
        {
            var d = new ImagingSettings();
            var next = new ImagingSettings
            {
                SaveDirectory = imaging.TryGetProperty("saveDirectory", out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString() : null,
                AutoStretch = Flag(imaging, "autoStretch", d.AutoStretch, "imaging"),
                FitOnCapture = Flag(imaging, "fitOnCapture", d.FitOnCapture, "imaging"),
                ManualExposureSeconds = Num(imaging, "manualExposureSeconds", d.ManualExposureSeconds, "imaging"),
            };
            settings = settings with { Imaging = next.Problem is { } problem ? throw new SideraSettingsException(problem) : next };
        }

        if (Section("autofocus", "autofocus") is { } autofocus)
        {
            var d = new AutofocusDefaults();
            var next = new AutofocusDefaults
            {
                ExposureSeconds = Num(autofocus, "exposureSeconds", d.ExposureSeconds, "autofocus"),
                StepSize = Int(autofocus, "stepSize", d.StepSize, "autofocus"),
                SampleCount = Int(autofocus, "sampleCount", d.SampleCount, "autofocus"),
                PolicyEnabled = Flag(autofocus, "policyEnabled", d.PolicyEnabled, "autofocus"),
                PolicyAtStart = Flag(autofocus, "policyAtStart", d.PolicyAtStart, "autofocus"),
                PolicyIntervalMinutes = Num(autofocus, "policyIntervalMinutes", d.PolicyIntervalMinutes, "autofocus"),
                PolicyAfterFilterChange = Flag(autofocus, "policyAfterFilterChange", d.PolicyAfterFilterChange, "autofocus"),
            };
            settings = settings with { Autofocus = next.Problem is { } problem ? throw new SideraSettingsException(problem) : next };
        }

        if (Section("guiding", "guiding") is { } guiding)
        {
            var d = new GuidingDefaults();
            var next = new GuidingDefaults
            {
                StartBeforeImaging = Flag(guiding, "startBeforeImaging", d.StartBeforeImaging, "guiding"),
                StopWhenDone = Flag(guiding, "stopWhenDone", d.StopWhenDone, "guiding"),
                DitherByDefault = Flag(guiding, "ditherByDefault", d.DitherByDefault, "guiding"),
                DitherEveryNFrames = Int(guiding, "ditherEveryNFrames", d.DitherEveryNFrames, "guiding"),
                DitherAmplitudePixels = Num(guiding, "ditherAmplitudePixels", d.DitherAmplitudePixels, "guiding"),
                SettleThresholdPixels = Num(guiding, "settleThresholdPixels", d.SettleThresholdPixels, "guiding"),
                SettleStableSeconds = Num(guiding, "settleStableSeconds", d.SettleStableSeconds, "guiding"),
                SettleTimeoutSeconds = Num(guiding, "settleTimeoutSeconds", d.SettleTimeoutSeconds, "guiding"),
            };
            settings = settings with { Guiding = next.Problem is { } problem ? throw new SideraSettingsException(problem) : next };
        }

        if (Section("meridianFlip", "meridian flip") is { } flip)
        {
            try
            {
                var next = WorkflowJson.ReadSettingsBody(flip);
                settings = settings with { MeridianFlip = next.Problems().FirstOrDefault() is { } problem ? throw new SideraSettingsException(problem) : next };
            }
            catch (SequenceDocumentException ex)
            {
                throw new SideraSettingsException("The meridian flip settings are invalid: " + ex.Message, ex);
            }
        }

        return settings;
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
    public PlateSolvingSettings PlateSolving => _settings.PlateSolving;

    public SkyAtlasSettings SkyAtlas => _settings.SkyAtlas;

    public SequencerSettings Sequencer => _settings.Sequencer;
    public ImagingSettings Imaging => _settings.Imaging;
    public AutofocusDefaults Autofocus => _settings.Autofocus;
    public GuidingDefaults Guiding => _settings.Guiding;

    /// <summary>The meridian flip that a workflow follows when it uses the application defaults.</summary>
    public MeridianFlipSettings MeridianFlip => _settings.MeridianFlip;

    public SiteResult SetSequencer(SequencerSettings settings) => settings.Problem is { } problem ? SiteResult.Fail(problem) : Save(_settings with { Sequencer = settings });
    public SiteResult SetImaging(ImagingSettings settings) => settings.Problem is { } problem ? SiteResult.Fail(problem) : Save(_settings with { Imaging = settings });
    public SiteResult SetAutofocus(AutofocusDefaults settings) => settings.Problem is { } problem ? SiteResult.Fail(problem) : Save(_settings with { Autofocus = settings });
    public SiteResult SetGuiding(GuidingDefaults settings) => settings.Problem is { } problem ? SiteResult.Fail(problem) : Save(_settings with { Guiding = settings });

    /// <summary>Saves the meridian flip defaults; settings that make no sense are refused with the first problem.</summary>
    public SiteResult SetMeridianFlip(MeridianFlipSettings settings) =>
        settings.Problems().FirstOrDefault() is { } problem ? SiteResult.Fail(problem) : Save(_settings with { MeridianFlip = settings });

    public SiteResult SetSkyAtlas(SkyAtlasSettings settings) => settings.Problem is { } problem
        ? SiteResult.Fail(problem) : Save(_settings with { SkyAtlas = settings });

    public SiteResult SetPlateSolving(PlateSolvingSettings settings) => settings.Problem is { } problem
        ? SiteResult.Fail(problem) : Save(_settings with { PlateSolving = settings });

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

using System.Reflection;
using System.Text.Json;

namespace Sidera.Core.Cameras;

public enum CameraColorType
{
    Unknown,
    Mono,
    Bayer,
}

/// <summary>
/// What Sidera knows about the sensor of a known camera: the geometry only. It is reference data, never configuration: nothing of it is copied into a rig, so
/// it cannot go stale there.
/// </summary>
public sealed record CameraDatabaseEntry(
    string Manufacturer,
    string Model,
    IReadOnlyList<string> Aliases,
    string? SensorName,
    int WidthPixels,
    int HeightPixels,
    double PixelSizeXMicrometers,
    double PixelSizeYMicrometers,
    CameraColorType ColorType = CameraColorType.Unknown,
    string? BayerPattern = null)
{
    /// <summary>The camera as a person reads it: the manufacturer and the model.</summary>
    public string DisplayName => $"{Manufacturer} {Model}";
}

public sealed class CameraDatabaseException(string message) : Exception(message);

/// <summary>
/// A small, versioned list of well-known cameras and the geometry of their sensors, read from an embedded JSON resource (<see cref="Default"/>). A camera is
/// found by its manufacturer and model, or one of its aliases, as a driver or a person may write it: case, repeated whitespace, punctuation, a trailing "(1)" and a
/// leading manufacturer name do not matter. Nothing fuzzier than that: a name must be the model or an alias, not something that merely contains it, so similar models are not
/// mistaken for each other, and a name that fits more than one entry finds none.
/// </summary>
public sealed class CameraDatabase
{
    public const string Format = "sidera-camera-database";
    public const int SupportedVersion = 1;
    private const string ResourceName = "Sidera.Core.Cameras.camera-database.json";

    private static readonly Lazy<CameraDatabase> DefaultDatabase = new(LoadEmbedded);

    // Manufacturer names that a driver puts before the model; only ever removed from the start of a name.
    private static readonly HashSet<string> ManufacturerWords = ["zwo", "zwoptical", "optical"];

    private readonly Dictionary<string, List<CameraDatabaseEntry>> _byKey = [];

    public CameraDatabase(IEnumerable<CameraDatabaseEntry> entries, int version = SupportedVersion)
    {
        Version = version;
        Entries = [.. entries];
        foreach (var entry in Entries)
        {
            foreach (var name in new[] { entry.Model, entry.DisplayName }.Concat(entry.Aliases))
            {
                var key = Normalize(name);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!_byKey.TryGetValue(key, out var list))
                {
                    _byKey[key] = list = [];
                }

                if (!list.Contains(entry))
                {
                    list.Add(entry);
                }
            }
        }
    }

    /// <summary>The database that ships with Sidera.</summary>
    public static CameraDatabase Default => DefaultDatabase.Value;

    public static CameraDatabase Empty { get; } = new([]);

    public int Version { get; }

    public IReadOnlyList<CameraDatabaseEntry> Entries { get; }

    /// <summary>
    /// The entry that the given names (a display name, the description and the name that a driver reports, ...) point to, or <c>null</c>: no name matches, or the names
    /// together fit more than one entry.
    /// </summary>
    public CameraDatabaseEntry? Find(IEnumerable<string?> names)
    {
        CameraDatabaseEntry? found = null;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !_byKey.TryGetValue(Normalize(name), out var matches))
            {
                continue;
            }

            foreach (var match in matches)
            {
                if (found is not null && !ReferenceEquals(found, match))
                {
                    return null;
                }

                found = match;
            }
        }

        return found;
    }

    public CameraDatabaseEntry? Find(string? name) => Find([name]);

    /// <summary>The name as it is compared: lower case, without a "(n)" or other bracket, only letters and digits, without a manufacturer in front, and without spaces.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        var depth = 0;
        foreach (var c in name.ToLowerInvariant())
        {
            if (c == '(')
            {
                depth++;
                text.Append(' ');
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
                text.Append(' ');
            }
            else if (depth > 0)
            {
                continue;
            }
            else
            {
                text.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
        }

        var tokens = text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 1 && ManufacturerWords.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        return string.Concat(tokens);
    }

    /// <summary>Reads a database from its JSON.</summary>
    /// <exception cref="CameraDatabaseException">The text is not a database of a version that is known, or an entry is not valid.</exception>
    public static CameraDatabase Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != Format)
            {
                throw new CameraDatabaseException($"This is not a Sidera camera database (format '{Format}').");
            }

            var version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out var number) ? number : 0;
            if (version is < 1 or > SupportedVersion)
            {
                throw new CameraDatabaseException($"The camera database has version {version}; this Sidera reads version {SupportedVersion}.");
            }

            if (!root.TryGetProperty("cameras", out var cameras) || cameras.ValueKind != JsonValueKind.Array)
            {
                throw new CameraDatabaseException("The camera database has no list of cameras.");
            }

            return new CameraDatabase(cameras.EnumerateArray().Select(ReadEntry), version);
        }
        catch (JsonException ex)
        {
            throw new CameraDatabaseException("The camera database is not valid JSON: " + ex.Message);
        }
    }

    private static CameraDatabaseEntry ReadEntry(JsonElement element)
    {
        var manufacturer = Text(element, "manufacturer");
        var model = Text(element, "model");
        if (string.IsNullOrWhiteSpace(manufacturer) || string.IsNullOrWhiteSpace(model))
        {
            throw new CameraDatabaseException("A camera of the database needs a manufacturer and a model.");
        }

        var name = $"{manufacturer} {model}";
        var aliases = element.TryGetProperty("aliases", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.String).Select(a => a.GetString()!).ToList()
            : [];
        var color = Text(element, "color") switch
        {
            null => CameraColorType.Unknown,
            var c when c.Equals("Mono", StringComparison.OrdinalIgnoreCase) => CameraColorType.Mono,
            var c when c.Equals("Bayer", StringComparison.OrdinalIgnoreCase) => CameraColorType.Bayer,
            var c when c.Equals("Unknown", StringComparison.OrdinalIgnoreCase) => CameraColorType.Unknown,
            var c => throw new CameraDatabaseException($"{name}: the color type '{c}' is not known."),
        };
        return new CameraDatabaseEntry(
            manufacturer, model, aliases, Text(element, "sensor"),
            Count(element, "widthPixels", name), Count(element, "heightPixels", name),
            Size(element, "pixelSizeXMicrometers", name), Size(element, "pixelSizeYMicrometers", name),
            color, Text(element, "bayerPattern"));
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Count(JsonElement element, string property, string camera) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) && number > 0
            ? number
            : throw new CameraDatabaseException($"{camera}: '{property}' must be a whole number greater than zero.");

    private static double Size(JsonElement element, string property, string camera) =>
        element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number) && double.IsFinite(number) && number > 0
            ? number
            : throw new CameraDatabaseException($"{camera}: '{property}' must be a number greater than zero.");

    private static CameraDatabase LoadEmbedded()
    {
        using var stream = typeof(CameraDatabase).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new CameraDatabaseException("The camera database is missing from Sidera.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}

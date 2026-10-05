using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;

namespace Sidera.Sky;

/// <summary>An astronomical object found by name.</summary>
/// <param name="Name">The name to show: "M31".</param>
/// <param name="Aliases">The other names it is known by ("NGC 224", "Andromeda Galaxy").</param>
/// <param name="Type">What it is ("Galaxy", "Nebula"), where the catalog says; <c>null</c> otherwise.</param>
/// <param name="SizeArcminutes">The larger angular size, where known.</param>
/// <param name="Source">The catalog that found it.</param>
public sealed record CelestialObject(string Name, IReadOnlyList<string> Aliases, CelestialCoordinates Position, string? Type, double? SizeArcminutes, string Source)
{
    /// <summary>A line for a list of suggestions: the other names, then where it was found.</summary>
    public string Subtitle => string.Join(" · ", Aliases.Take(2).Append(Source));
}

/// <summary>A catalog of objects that can be searched by name. The framing asks it and never knows which catalog answers: a local list, an online resolver or both.</summary>
public interface ICelestialObjectCatalog
{
    string Name { get; }

    /// <summary>The objects that match a name, the best match first; empty when there is none or the catalog cannot answer (no network, no file). Never throws except for cancelling.</summary>
    Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default);
}

/// <summary>How names are compared: case, spaces, underscores and hyphens do not matter, so "NGC 7000", "ngc7000" and "NGC-7000" are one name.</summary>
public static class CatalogNames
{
    public static string Normalize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static readonly Regex Designation = new(@"^(?<letters>[A-Za-z]+)\s*(?<digits>\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>"NGC7000" as "NGC 7000", "M31" as "M31" (Messier is written without a space); other names as they are with underscores as spaces.</summary>
    public static string Display(string name)
    {
        var text = name.Replace('_', ' ').Trim();
        var match = Designation.Match(text);
        if (!match.Success)
        {
            return text;
        }

        var letters = match.Groups["letters"].Value;
        return letters.Equals("M", StringComparison.OrdinalIgnoreCase) ? letters.ToUpperInvariant() + match.Groups["digits"].Value : $"{letters} {match.Groups["digits"].Value}";
    }
}

/// <summary>
/// A catalog in the format of the deep sky list that ASTAP installs (<c>deep_sky.csv</c>: two header lines, then
/// <c>right ascension, declination, names separated by "/", length, width, orientation</c>): right ascension 0 to 864000 for 24 hours and declination
/// -324000 to 324000 for -90° to +90°. Read from where it is on this computer; Sidera does not ship it. It answers without a network, with about thirty thousand objects.
/// </summary>
public sealed class DeepSkyCsvCatalog : ICelestialObjectCatalog
{
    private readonly string _path;
    private readonly Lazy<Task<IReadOnlyList<Entry>>> _entries;

    private sealed record Entry(string[] Names, string[] Normalized, double RightAscensionHours, double DeclinationDegrees, double? SizeArcminutes);

    public DeepSkyCsvCatalog(string path)
    {
        _path = path;
        _entries = new Lazy<Task<IReadOnlyList<Entry>>>(() => Task.Run(Load));
    }

    public string Name => "Deep sky list";

    public bool Exists => File.Exists(_path);

    public async Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default)
    {
        var wanted = CatalogNames.Normalize(query);
        if (wanted.Length < 2 || !Exists)
        {
            return [];
        }

        IReadOnlyList<Entry> entries;
        try
        {
            entries = await _entries.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        // Exact names first, then names that begin with the text, then names that contain it (only for longer texts, or "M3" would find half the list).
        var exact = new List<Entry>();
        var prefix = new List<Entry>();
        var contains = new List<Entry>();
        foreach (var entry in entries)
        {
            var best = 3;
            foreach (var normalized in entry.Normalized)
            {
                if (normalized == wanted)
                {
                    best = 0;
                    break;
                }

                if (normalized.StartsWith(wanted, StringComparison.Ordinal))
                {
                    best = Math.Min(best, 1);
                }
                else if (wanted.Length >= 4 && normalized.Contains(wanted, StringComparison.Ordinal))
                {
                    best = Math.Min(best, 2);
                }
            }

            (best == 0 ? exact : best == 1 ? prefix : best == 2 ? contains : null)?.Add(entry);
        }

        return
        [
            .. exact.Concat(prefix.OrderBy(e => e.Normalized[0].Length)).Concat(contains).Take(maxResults).Select(e => new CelestialObject(
                CatalogNames.Display(e.Names[0]), [.. e.Names.Skip(1).Select(CatalogNames.Display)],
                new CelestialCoordinates(e.RightAscensionHours, e.DeclinationDegrees), null, e.SizeArcminutes, Name)),
        ];
    }

    private IReadOnlyList<Entry> Load()
    {
        var entries = new List<Entry>(32000);
        foreach (var raw in File.ReadLines(_path).Skip(2))
        {
            var parts = raw.Split(',');
            if (parts.Length < 3
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            {
                continue;
            }

            var hours = ra / 36000.0;
            var degrees = dec / 3600.0;
            if (hours is < 0 or >= 24 || degrees is < -90 or > 90)
            {
                continue;
            }

            var names = parts[2].Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (names.Length == 0)
            {
                continue;
            }

            double? size = parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var length) ? length / 10.0 : null;
            entries.Add(new Entry(names, [.. names.Select(CatalogNames.Normalize)], hours, degrees, size));
        }

        return entries;
    }
}

/// <summary>The name resolver of the CDS (Sesame: Simbad, NED and VizieR). It needs the network, answers with the position and the type, and is asked only when a name is searched.</summary>
public sealed class SesameCatalog(HttpClient http, TimeSpan? timeout = null) : ICelestialObjectCatalog
{
    private const string Endpoint = "https://cds.unistra.fr/cgi-bin/nph-sesame/-oxp/SNV?";

    public string Name => "CDS Sesame";

    public async Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            var xml = await http.GetStringAsync(Endpoint + Uri.EscapeDataString(query.Trim()), limit.Token).ConfigureAwait(false);
            return Parse(xml, query.Trim(), Name);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>The first resolver that gave a position, as one object; empty when the name is unknown.</summary>
    public static IReadOnlyList<CelestialObject> Parse(string xml, string query, string source)
    {
        var document = XDocument.Parse(xml);
        foreach (var resolver in document.Descendants("Resolver"))
        {
            if (!double.TryParse(resolver.Element("jradeg")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ra)
                || !double.TryParse(resolver.Element("jdedeg")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec)
                || dec is < -90 or > 90)
            {
                continue;
            }

            var official = resolver.Element("oname")?.Value;
            var name = CatalogNames.Display(string.IsNullOrWhiteSpace(official) ? query : Regex.Replace(official.Trim(), @"\s+", " "));
            var type = resolver.Element("otype")?.Value;
            return [new CelestialObject(name, [], SkyMath.FromDegrees(ra, dec), string.IsNullOrWhiteSpace(type) ? null : type.Trim(), null, source)];
        }

        return [];
    }
}

/// <summary>Several catalogs in order: the first one that finds something answers. A local list first, so that a known name needs no network.</summary>
public sealed class CompositeObjectCatalog(params ICelestialObjectCatalog[] catalogs) : ICelestialObjectCatalog
{
    public string Name => string.Join(" + ", catalogs.Select(c => c.Name));

    public async Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default)
    {
        foreach (var catalog in catalogs)
        {
            var found = await catalog.SearchAsync(query, maxResults, cancellationToken).ConfigureAwait(false);
            if (found.Count > 0)
            {
                return found;
            }
        }

        return [];
    }
}

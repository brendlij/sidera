using System.Text.Json;

namespace Sidera.Sky;

/// <summary>
/// A disk cache of the tiles of sky surveys, kept in <c>&lt;root&gt;/&lt;provider&gt;/&lt;survey&gt;/Norder{k}/Dir{d}/Npix{n}.{ext}</c> (the layout of HiPS itself), with the published
/// information of the survey in <c>survey.json</c> beside them. It holds what a person looked at, nothing more: it never fills itself, never leaves the user's
/// machine and is not Sidera's imagery. It is bounded: when the tiles together pass the limit, the ones used longest ago go first. An item that is not a picture (empty,
/// cut short, wrong bytes) counts as not there and is removed.
/// </summary>
public sealed class SkyTileCache
{
    public const long DefaultMaxBytes = 512L * 1024 * 1024;
    private const string InfoFileName = "survey.json";

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly object _gate = new();
    private long _bytes = -1;

    public SkyTileCache(string root, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1024);
        _root = Path.GetFullPath(root);
        _maxBytes = maxBytes;
    }

    public static string DefaultRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sidera", "cache", "sky");

    public string Root => _root;

    public long MaxBytes => _maxBytes;

    /// <summary>The folder of a survey of a provider; the names are made safe as folder names.</summary>
    public string SurveyFolder(string providerId, string surveyId) => Path.Combine(_root, Safe(providerId), Safe(surveyId));

    public string TilePath(string providerId, string surveyId, int order, long tile, string extension) =>
        Path.Combine(SurveyFolder(providerId, surveyId), $"Norder{order}", $"Dir{tile / 10000 * 10000}", $"Npix{tile}.{extension}");

    /// <summary>The bytes of a cached tile, or <c>null</c> when it is not there or is not a picture (it is removed then). A hit counts as use.</summary>
    public byte[]? TryGet(string providerId, string surveyId, int order, long tile, string extension)
    {
        var path = TilePath(providerId, surveyId, order, tile, extension);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (!LooksLikeImage(bytes))
            {
                Remove(path, bytes.Length);
                return null;
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keeps a tile. A failure to write (a full disk, a locked file) is not an error of the framing: the tile is just not cached.</summary>
    public void Put(string providerId, string surveyId, int order, long tile, string extension, byte[] bytes)
    {
        if (!LooksLikeImage(bytes))
        {
            return;
        }

        var path = TilePath(providerId, surveyId, order, tile, extension);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var previous = File.Exists(path) ? new FileInfo(path).Length : 0;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            lock (_gate)
            {
                _bytes = Math.Max(0, TotalBytes() - previous + bytes.Length);
            }

            if (TotalBytes() > _maxBytes)
            {
                Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cached; the tile was still shown.
        }
    }

    /// <summary>Removes a tile that turned out not to decode, so that the next request loads it again.</summary>
    public void Invalidate(string providerId, string surveyId, int order, long tile, string extension) =>
        Remove(TilePath(providerId, surveyId, order, tile, extension), null);

    public void SaveInfo(string providerId, SkySurveyInfo info)
    {
        try
        {
            var folder = SurveyFolder(providerId, info.Id);
            Directory.CreateDirectory(folder);
            var document = new CachedSurvey(providerId, info, DateTimeOffset.UtcNow,
                "Imagery in this folder belongs to its source, under the terms in this file; it is a local cache for the person who looked at it and is not to be redistributed.");
            File.WriteAllText(Path.Combine(folder, InfoFileName), JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The information is shown anyway; it is just not kept.
        }
    }

    /// <summary>What the survey said about itself the last time it was loaded, with the rights as published then; <c>null</c> when there is none or it cannot be read.</summary>
    public SkySurveyInfo? TryLoadInfo(string providerId, string surveyId)
    {
        try
        {
            var path = Path.Combine(SurveyFolder(providerId, surveyId), InfoFileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<CachedSurvey>(File.ReadAllText(path), JsonOptions)?.Survey : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The bytes of all the cached tiles.</summary>
    public long TotalBytes()
    {
        lock (_gate)
        {
            if (_bytes >= 0)
            {
                return _bytes;
            }

            _bytes = Directory.Exists(_root) ? Tiles().Sum(f => f.Length) : 0;
            return _bytes;
        }
    }

    /// <summary>Removes the tiles used longest ago until the cache is at nine tenths of its limit. Information files stay.</summary>
    public void Trim()
    {
        lock (_gate)
        {
            var target = _maxBytes / 10 * 9;
            var files = Tiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (total <= target)
                {
                    break;
                }

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use; it stays for now.
                }
            }

            _bytes = total;
        }
    }

    /// <summary>Removes everything the cache holds.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            _bytes = 0;
        }
    }

    private IEnumerable<FileInfo> Tiles() =>
        Directory.Exists(_root)
            ? new DirectoryInfo(_root).EnumerateFiles("Npix*", SearchOption.AllDirectories).Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal))
            : [];

    private void Remove(string path, long? length)
    {
        try
        {
            var size = length ?? (File.Exists(path) ? new FileInfo(path).Length : 0);
            File.Delete(path);
            lock (_gate)
            {
                if (_bytes >= 0)
                {
                    _bytes = Math.Max(0, _bytes - size);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left as it is.
        }
    }

    // A JPEG starts with FF D8 FF and ends with FF D9; a PNG with its eight byte signature. Whatever else (empty, an error page, cut short) is not a tile.
    internal static bool LooksLikeImage(byte[] bytes)
    {
        if (bytes.Length < 16)
        {
            return false;
        }

        var jpeg = bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF && bytes[^2] == 0xFF && bytes[^1] == 0xD9;
        var png = bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
        return jpeg || png;
    }

    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c).ToArray();
        var text = new string(chars).Trim('.', ' ');
        return text.Length == 0 ? "_" : text;
    }

    private sealed record CachedSurvey(string Provider, SkySurveyInfo Survey, DateTimeOffset CachedAt, string Notice);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}

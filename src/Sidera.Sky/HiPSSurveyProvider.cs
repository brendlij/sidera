using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Astrometry;
using Sidera.Core.Framing;

namespace Sidera.Sky;

/// <summary>How the remote provider behaves on the network.</summary>
public sealed record HiPSOptions
{
    /// <summary>The time one request (the properties, a tile) may take.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many tiles are requested at the same time: a view needs a handful, and a server is not to be flooded.</summary>
    public int MaxConcurrentRequests { get; init; } = 6;

    /// <summary>How many decoded tiles are kept in memory (each is a tile width squared times four bytes: about a megabyte).</summary>
    public int MaxDecodedTiles { get; init; } = 48;

    /// <summary>The most tiles one view may need; a view that would need more is drawn at a coarser order instead of loading them all.</summary>
    public int MaxTilesPerView { get; init; } = 40;

    /// <summary>The identifier of the provider in the cache folders.</summary>
    public string ProviderId { get; init; } = "hips";
}

/// <summary>
/// A HiPS survey served over HTTP. It reads the <c>properties</c> of the survey once, then draws a view by finding the tiles that the view touches (and no others), taking each
/// from the disk cache or the network, and sampling them into a picture. Requests are limited in number and in time, run off the calling thread, and end when the token is
/// cancelled; a tile that cannot be had leaves a transparent part and is counted, so a missing tile or no network at all gives a smaller picture and never an error.
/// What the cache holds stays where it is and carries the published rights of the survey.
/// </summary>
public sealed class HiPSSurveyProvider : ISkySurveyProvider, IDisposable
{
    private readonly SkySurveyDescriptor _survey;
    private readonly HttpClient _http;
    private readonly SkyTileCache _cache;
    private readonly ITileDecoder _decoder;
    private readonly HiPSOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _requests;
    private readonly object _gate = new();
    private readonly Dictionary<string, DecodedTile> _decoded = new();
    private readonly LinkedList<string> _decodedOrder = new();
    private SkySurveyInfo? _info;

    public HiPSSurveyProvider(
        SkySurveyDescriptor survey, HttpClient http, SkyTileCache cache, ITileDecoder decoder, HiPSOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(survey);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(decoder);
        _survey = survey;
        _http = http;
        _cache = cache;
        _decoder = decoder;
        _options = options ?? new HiPSOptions();
        _logger = logger ?? NullLogger.Instance;
        _requests = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentRequests));
    }

    public string Id => _survey.Id;

    public string DisplayName => _survey.Title;

    public async Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        if (_info is { } known)
        {
            return known;
        }

        try
        {
            var text = await GetTextAsync(Url("properties"), cancellationToken).ConfigureAwait(false);
            if (text is not null)
            {
                _info = ParseProperties(text, _survey);
                _cache.SaveInfo(_options.ProviderId, _info);
                return _info;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The request timed out: the cached information below is as good.
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("The properties of {Survey} could not be loaded: {Reason}", _survey.Id, ex.Message);
        }

        // Offline, or the server did not answer: the rights and the layout as they were published the last time.
        _info = _cache.TryLoadInfo(_options.ProviderId, _survey.Id);
        return _info;
    }

    public async Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var info = await GetInfoAsync(cancellationToken).ConfigureAwait(false);
        var empty = new byte[viewport.WidthPixels * viewport.HeightPixels * 4];
        if (info is null)
        {
            return new SkyImage(viewport.WidthPixels, viewport.HeightPixels, empty, 0, 0, 0, null, "The survey is not reachable and nothing of it is cached.");
        }

        var tileWidthLog2 = (int)Math.Round(Math.Log2(info.TileWidth));
        if (1 << tileWidthLog2 != info.TileWidth)
        {
            return new SkyImage(viewport.WidthPixels, viewport.HeightPixels, empty, 0, 0, 0, info, "The survey has a tile size that Sidera cannot read.");
        }

        var (order, tiles, pixelTiles, pixelLocal) = PlanTiles(viewport, info, tileWidthLog2, cancellationToken);
        var extension = info.TileFormat.Contains("png", StringComparison.OrdinalIgnoreCase) && !info.TileFormat.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var results = new Dictionary<long, (DecodedTile? Tile, bool FromCache)>();
        var fetch = tiles.Select(async tile =>
        {
            var result = await LoadTileAsync(info, order, tile, extension, cancellationToken).ConfigureAwait(false);
            lock (results)
            {
                results[tile] = result;
            }
        }).ToList();
        await Task.WhenAll(fetch).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var width = info.TileWidth;
        var rgba = empty;
        for (var i = 0; i < pixelTiles.Length; i++)
        {
            if (!results.TryGetValue(pixelTiles[i], out var entry) || entry.Tile is not { } tile)
            {
                continue;
            }

            var local = pixelLocal[i];
            var column = local % width;
            var row = local / width;
            var source = (row * tile.Width + column) * 4;
            var target = i * 4;
            Buffer.BlockCopy(tile.Rgba, source, rgba, target, 4);
        }

        var missing = results.Count(r => r.Value.Tile is null);
        var fromCache = results.Count(r => r.Value.Tile is not null && r.Value.FromCache);
        var note = missing == 0 ? null : missing == results.Count ? "No imagery for this view (offline, or not cached)." : $"{missing} of {results.Count} tiles are missing.";
        return new SkyImage(viewport.WidthPixels, viewport.HeightPixels, rgba, results.Count, missing, fromCache, info, note);
    }

    // The order whose pixels are about as small as the view's, the tiles that the view needs at that order, and for each output pixel its tile and its place in the tile image.
    private (int Order, IReadOnlyList<long> Tiles, long[] PixelTiles, int[] PixelLocal) PlanTiles(
        SkyViewport viewport, SkySurveyInfo info, int tileWidthLog2, CancellationToken cancellationToken)
    {
        var order = ChooseOrder(viewport.DegreesPerPixel, info.TileWidth, info.MaxOrder);
        while (true)
        {
            var count = viewport.WidthPixels * viewport.HeightPixels;
            var pixelTiles = new long[count];
            var pixelLocal = new int[count];
            var distinct = new HashSet<long>();
            var width = info.TileWidth;
            for (var y = 0; y < viewport.HeightPixels; y++)
            {
                if ((y & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                for (var x = 0; x < viewport.WidthPixels; x++)
                {
                    var sky = viewport.ToSky(x + 0.5, y + 0.5);
                    var (tile, column, row) = HealpixNested.ToTilePixel(order, tileWidthLog2, SkyMath.HoursToDegrees(sky.RightAscensionHours), sky.DeclinationDegrees);
                    var index = y * viewport.WidthPixels + x;
                    pixelTiles[index] = tile;
                    pixelLocal[index] = row * width + column;
                    distinct.Add(tile);
                }
            }

            // A view that needs more tiles than allowed is drawn from a coarser order instead: fewer, larger-pixel tiles.
            if (distinct.Count <= _options.MaxTilesPerView || order == 0)
            {
                return (order, [.. distinct], pixelTiles, pixelLocal);
            }

            order--;
        }
    }

    /// <summary>
    /// The order whose pixels are no larger than a pixel of the view (the finest that is useful), at most the finest the survey has. A HiPS pixel of order <c>k + log2(width)</c> is about
    /// <c>58.6 / 2^(k + log2(width))</c> degrees across.
    /// </summary>
    public static int ChooseOrder(double degreesPerViewPixel, int tileWidth, int maxOrder)
    {
        var tileBits = (int)Math.Round(Math.Log2(tileWidth));
        for (var order = 0; order <= maxOrder; order++)
        {
            var pixel = 58.6 / (1L << (order + tileBits));
            if (pixel <= degreesPerViewPixel)
            {
                return order;
            }
        }

        return maxOrder;
    }

    private async Task<(DecodedTile? Tile, bool FromCache)> LoadTileAsync(SkySurveyInfo info, int order, long tile, string extension, CancellationToken cancellationToken)
    {
        var key = $"{_survey.Id}|{order}|{tile}";
        if (TryGetDecoded(key) is { } kept)
        {
            return (kept, true);
        }

        var cached = _cache.TryGet(_options.ProviderId, _survey.Id, order, tile, extension);
        if (cached is not null)
        {
            if (_decoder.Decode(cached) is { } decoded)
            {
                Remember(key, decoded);
                return (decoded, true);
            }

            _cache.Invalidate(_options.ProviderId, _survey.Id, order, tile, extension); // it was not a picture after all
        }

        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await GetBytesAsync(Url($"Norder{order}/Dir{tile / 10000 * 10000}/Npix{tile}.{extension}"), cancellationToken).ConfigureAwait(false);
            if (bytes is null || _decoder.Decode(bytes) is not { } fresh)
            {
                return (null, false);
            }

            _cache.Put(_options.ProviderId, _survey.Id, order, tile, extension, bytes);
            Remember(key, fresh);
            return (fresh, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("The tile {Tile} of {Survey} timed out", tile, _survey.Id);
            return (null, false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("The tile {Tile} of {Survey} could not be loaded: {Reason}", tile, _survey.Id, ex.Message);
            return (null, false);
        }
        finally
        {
            _requests.Release();
        }
    }

    private string Url(string relative) => _survey.BaseUrl.TrimEnd('/') + "/" + relative;

    // A request is limited by the timeout of the options and by the token of the caller; a missing file (404) is no tile and no error.
    private async Task<byte[]?> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private DecodedTile? TryGetDecoded(string key)
    {
        lock (_gate)
        {
            if (!_decoded.TryGetValue(key, out var tile))
            {
                return null;
            }

            _decodedOrder.Remove(key);
            _decodedOrder.AddFirst(key);
            return tile;
        }
    }

    private void Remember(string key, DecodedTile tile)
    {
        lock (_gate)
        {
            if (_decoded.TryAdd(key, tile))
            {
                _decodedOrder.AddFirst(key);
            }

            while (_decoded.Count > _options.MaxDecodedTiles && _decodedOrder.Last is { } oldest)
            {
                _decoded.Remove(oldest.Value);
                _decodedOrder.RemoveLast();
            }
        }
    }

    /// <summary>The information from a HiPS <c>properties</c> file (<c>key = value</c> lines).</summary>
    public static SkySurveyInfo ParseProperties(string text, SkySurveyDescriptor survey)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals > 0)
            {
                values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
            }
        }

        string? Get(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        int Number(string key, int fallback) => int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;

        return new SkySurveyInfo
        {
            Id = survey.Id,
            Title = Get("obs_title") ?? survey.Title,
            SourceUrl = survey.BaseUrl,
            Copyright = Get("obs_copyright") ?? Get("hips_copyright"),
            CopyrightUrl = Get("obs_copyright_url"),
            License = Get("hips_license"),
            Acknowledgement = Get("obs_ack"),
            Creator = Get("hips_creator"),
            Status = Get("hips_status"),
            TileWidth = Number("hips_tile_width", 512),
            MaxOrder = Number("hips_order", 9),
            TileFormat = (Get("hips_tile_format") ?? "jpeg").ToLowerInvariant(),
            Frame = Get("hips_frame") ?? "equatorial",
        };
    }

    public void Dispose() => _requests.Dispose();
}

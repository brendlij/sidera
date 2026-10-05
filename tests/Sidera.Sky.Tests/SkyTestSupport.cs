using System.Net;
using Sidera.Sky;

namespace Sidera.Sky.Tests;

/// <summary>A tile that passes for a JPEG: the right first and last bytes, and the number of the tile in its middle, so that the decoder can paint it.</summary>
internal static class FakeTiles
{
    public static byte[] Jpeg(byte shade)
    {
        var bytes = new byte[32];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        bytes[10] = shade;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    public const string Properties = """
        obs_title            = Test sky
        obs_copyright        = Test Observatory
        obs_copyright_url    = http://example.org/copyright
        hips_license         = CC-BY-4.0
        obs_ack              = Thanks to the test observatory
        hips_creator         = The Tester
        hips_status          = public master unclonable
        hips_tile_width      = 8
        hips_order           = 6
        hips_tile_format     = jpeg
        hips_frame           = equatorial
        """;
}

/// <summary>Decodes a fake tile into a flat color: the shade byte of the tile in every pixel.</summary>
internal sealed class FakeDecoder : ITileDecoder
{
    public int Decoded;

    public DecodedTile? Decode(byte[] bytes)
    {
        Interlocked.Increment(ref Decoded);
        if (bytes.Length < 16 || bytes[0] != 0xFF)
        {
            return null;
        }

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 64; i++)
        {
            rgba[i * 4] = bytes[10];
            rgba[i * 4 + 1] = bytes[10];
            rgba[i * 4 + 2] = bytes[10];
            rgba[i * 4 + 3] = 255;
        }

        return new DecodedTile(8, 8, rgba);
    }
}

/// <summary>The network of a test: it answers by path, can fail, can be slow, and counts what was asked.</summary>
internal sealed class FakeServer : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    public bool Offline { get; set; }

    public TimeSpan Delay { get; set; }

    public HashSet<string> Missing { get; } = [];

    public string Properties { get; set; } = FakeTiles.Properties;

    public int InFlight;
    public int MaxInFlight;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        lock (Requests)
        {
            Requests.Add(path);
        }

        var now = Interlocked.Increment(ref InFlight);
        InterlockedMax(ref MaxInFlight, now);
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Offline)
            {
                throw new HttpRequestException("No network.");
            }

            if (path.EndsWith("/properties", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Properties) };
            }

            if (Missing.Any(path.Contains))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FakeTiles.Jpeg(100)) };
        }
        finally
        {
            Interlocked.Decrement(ref InFlight);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    public int TileRequests => Requests.Count(r => r.Contains("/Norder", StringComparison.Ordinal));
}

internal static class SkyTestPaths
{
    public static string NewTemp() => Path.Combine(Path.GetTempPath(), "sidera-sky-tests", Guid.NewGuid().ToString("N"));
}

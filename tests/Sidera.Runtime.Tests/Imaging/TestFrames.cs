using Sidera.Core.Devices;

namespace Sidera.Runtime.Tests.Imaging;

/// <summary>
/// Frames made for the tests of the analysis, independently of the simulator and of everything that measures: a flat
/// background with optional noise, and stars that are circular Gaussians of a given sigma and total flux. The half flux
/// radius a star of sigma s has is known from the mathematics of the Gaussian (s · sqrt(2 ln 2)), which is what the tests
/// compare the measurements with; nothing here computes a half flux radius.
/// </summary>
internal sealed class TestFrame
{
    /// <summary>The half flux radius of a circular Gaussian with this sigma.</summary>
    public static double ExpectedHfr(double sigma) => sigma * Math.Sqrt(2 * Math.Log(2));

    private readonly int _width;
    private readonly int _height;
    private readonly double[] _values;

    public TestFrame(int width = 400, int height = 300, double background = 1000)
    {
        _width = width;
        _height = height;
        _values = new double[width * height];
        Array.Fill(_values, background);
    }

    /// <summary>Gaussian noise with a fixed seed.</summary>
    public TestFrame Noise(double sigma, int seed = 1)
    {
        var random = new Random(seed);
        for (var i = 0; i < _values.Length; i += 2)
        {
            var radius = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * sigma;
            var angle = 2 * Math.PI * random.NextDouble();
            _values[i] += radius * Math.Cos(angle);
            if (i + 1 < _values.Length)
            {
                _values[i + 1] += radius * Math.Sin(angle);
            }
        }

        return this;
    }

    /// <summary>A star at (x, y) with the given total flux and sigma, sampled at the pixel centres.</summary>
    public TestFrame Star(double x, double y, double flux, double sigma)
    {
        var peak = flux / (2 * Math.PI * sigma * sigma);
        var reach = (int)Math.Ceiling(6 * sigma);
        for (var py = Math.Max(0, (int)y - reach); py <= Math.Min(_height - 1, (int)y + reach); py++)
        {
            for (var px = Math.Max(0, (int)x - reach); px <= Math.Min(_width - 1, (int)x + reach); px++)
            {
                var dx = px - x;
                var dy = py - y;
                _values[py * _width + px] += peak * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            }
        }

        return this;
    }

    public TestFrame Pixel(int x, int y, double add)
    {
        _values[y * _width + x] += add;
        return this;
    }

    public CameraFrame Build()
    {
        var pixels = new ushort[_values.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)Math.Clamp(Math.Round(_values[i]), 0, ushort.MaxValue);
        }

        return new CameraFrame(_width, _height, pixels, TimeSpan.FromSeconds(1));
    }

    /// <summary>A frame from raw values, for the tests of statistics.</summary>
    public static CameraFrame From(int width, int height, Func<int, int, int> pixel)
    {
        var pixels = new ushort[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (ushort)pixel(x, y);
            }
        }

        return new CameraFrame(width, height, pixels, TimeSpan.FromSeconds(1));
    }
}

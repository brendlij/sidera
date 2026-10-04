using Astra.Core.Devices;

namespace Astra.Runtime.Devices;

/// <summary>Generates a dark noisy background with randomly placed Gaussian stars.</summary>
internal sealed class SyntheticFrameGenerator
{
    public const int Width = 800;
    public const int Height = 600;

    private const int StarCount = 150;
    private const int Background = 500;
    private const int Noise = 40;
    private const double FaintestPeak = 2000;
    private const double BrightnessRange = 30; // brightest peak = 60000

    private readonly Random _random;

    public SyntheticFrameGenerator(Random random)
    {
        _random = random;
    }

    public CameraFrame Generate(TimeSpan exposureDuration)
    {
        var pixels = new ushort[Width * Height];

        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(Background + _random.Next(-Noise, Noise + 1));
        }

        for (var star = 0; star < StarCount; star++)
        {
            var x = _random.NextDouble() * Width;
            var y = _random.NextDouble() * Height;
            var peak = FaintestPeak * Math.Pow(BrightnessRange, _random.NextDouble());
            var sigma = 1.0 + _random.NextDouble() * 1.5;
            AddStar(pixels, x, y, peak, sigma);
        }

        return new CameraFrame(Width, Height, pixels, exposureDuration);
    }

    /// <summary>A frame with the shutter closed: the background and its noise, no stars.</summary>
    public CameraFrame GenerateDark(TimeSpan exposureDuration)
    {
        var pixels = new ushort[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(Background + _random.Next(-Noise, Noise + 1));
        }

        return new CameraFrame(Width, Height, pixels, exposureDuration);
    }

    /// <summary>An evenly lit frame, about half way up the scale.</summary>
    public CameraFrame GenerateFlat(TimeSpan exposureDuration)
    {
        var pixels = new ushort[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(30000 + _random.Next(-Noise, Noise + 1));
        }

        return new CameraFrame(Width, Height, pixels, exposureDuration);
    }

    private static void AddStar(ushort[] pixels, double x, double y, double peak, double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 4);
        var twoSigmaSquared = 2 * sigma * sigma;

        for (var py = Math.Max(0, (int)y - radius); py <= Math.Min(Height - 1, (int)y + radius); py++)
        {
            for (var px = Math.Max(0, (int)x - radius); px <= Math.Min(Width - 1, (int)x + radius); px++)
            {
                var dx = px - x;
                var dy = py - y;
                var value = peak * Math.Exp(-(dx * dx + dy * dy) / twoSigmaSquared);
                var index = py * Width + px;
                pixels[index] = (ushort)Math.Min(ushort.MaxValue, pixels[index] + value);
            }
        }
    }
}

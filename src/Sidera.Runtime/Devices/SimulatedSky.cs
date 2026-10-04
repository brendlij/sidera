using Sidera.Core.Devices;

namespace Sidera.Runtime.Devices;

/// <summary>
/// What a simulated sky looks like. The defaults are an easy field for autofocus: a flat background with a little
/// noise, and forty stars that are bright enough to measure but not saturated.
/// </summary>
/// <param name="StarCount">How many stars there are.</param>
/// <param name="Background">The sky level, in ADU.</param>
/// <param name="NoiseSigma">The standard deviation of the noise, in ADU; 0 for none. Deterministic: the same exposure number gives the same noise.</param>
/// <param name="FaintestFlux">The total flux of the faintest star, in ADU·pixels.</param>
/// <param name="BrightnessRange">The brightest star is this many times as bright as the faintest.</param>
/// <param name="SaturatedStars">Extra stars that are so bright that their cores reach 65535 when in focus.</param>
/// <param name="HotPixels">Single pixels that are bright in every frame.</param>
/// <param name="Margin">No star is put closer than this to the edge of the frame.</param>
public sealed record SimulatedSkyOptions(
    int StarCount = 40,
    double Background = 800,
    double NoiseSigma = 5,
    double FaintestFlux = 6000,
    double BrightnessRange = 20,
    int SaturatedStars = 0,
    int HotPixels = 0,
    int Margin = 40
);

/// <summary>
/// A fixed star field that renders frames of a simulated camera. The stars are at the same places in every frame (they
/// are made once from a seed), and what changes with focus is their width: each one is a circular Gaussian whose sigma is
/// given for the exposure, with its total flux the same however wide it is, so a defocused star is broad and dim.
/// <para>
/// The rendering knows Gaussian sigmas and nothing about half flux radii: measuring the stars is the job of the frame
/// analysis, which works from the pixels alone.
/// </para>
/// </summary>
public sealed class SimulatedSky
{
    public const int Width = 800;
    public const int Height = 600;

    private readonly int _seed;
    private readonly SimulatedSkyOptions _options;
    private readonly (double X, double Y, double Flux)[] _stars;
    private readonly (int X, int Y)[] _hotPixels;

    public SimulatedSky(int seed, SimulatedSkyOptions? options = null)
    {
        _seed = seed;
        _options = options ?? new SimulatedSkyOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(_options.StarCount);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.NoiseSigma);

        var random = new Random(seed);
        double X() => _options.Margin + random.NextDouble() * (Width - 2 * _options.Margin);
        double Y() => _options.Margin + random.NextDouble() * (Height - 2 * _options.Margin);

        var stars = new List<(double, double, double)>();
        for (var i = 0; i < _options.StarCount; i++)
        {
            stars.Add((X(), Y(), _options.FaintestFlux * Math.Pow(_options.BrightnessRange, random.NextDouble())));
        }

        // Saturated stars: enough flux for a peak far above 65535 at a width of a pixel or two.
        for (var i = 0; i < _options.SaturatedStars; i++)
        {
            stars.Add((X(), Y(), 4_000_000));
        }

        _stars = [.. stars];
        _hotPixels = Enumerable.Range(0, _options.HotPixels).Select(_ => ((int)X(), (int)Y())).ToArray();
    }

    public SimulatedSkyOptions Options => _options;

    /// <summary>The positions and total fluxes of the stars, as the sky was made.</summary>
    public IReadOnlyList<(double X, double Y, double Flux)> Stars => _stars;

    /// <summary>A frame of the sky with every star a Gaussian of <paramref name="psfSigma"/> pixels.</summary>
    /// <param name="exposureIndex">Which exposure this is; it decides the noise, so that a run is the same every time.</param>
    public CameraFrame Render(double psfSigma, TimeSpan exposure, int exposureIndex)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(psfSigma, 0);

        var values = new double[Width * Height];
        Array.Fill(values, _options.Background);

        if (_options.NoiseSigma > 0)
        {
            var noise = new Random(HashCode.Combine(_seed, exposureIndex));
            for (var i = 0; i < values.Length; i += 2)
            {
                // Two normally distributed numbers from two uniform ones (Box-Muller).
                var radius = Math.Sqrt(-2 * Math.Log(1 - noise.NextDouble())) * _options.NoiseSigma;
                var angle = 2 * Math.PI * noise.NextDouble();
                values[i] += radius * Math.Cos(angle);
                if (i + 1 < values.Length)
                {
                    values[i + 1] += radius * Math.Sin(angle);
                }
            }
        }

        var twoSigmaSquared = 2 * psfSigma * psfSigma;
        var reach = (int)Math.Ceiling(4.5 * psfSigma);
        foreach (var (x, y, flux) in _stars)
        {
            var peak = flux / (Math.PI * twoSigmaSquared);
            for (var py = Math.Max(0, (int)y - reach); py <= Math.Min(Height - 1, (int)y + reach); py++)
            {
                for (var px = Math.Max(0, (int)x - reach); px <= Math.Min(Width - 1, (int)x + reach); px++)
                {
                    var dx = px - x;
                    var dy = py - y;
                    values[py * Width + px] += peak * Math.Exp(-(dx * dx + dy * dy) / twoSigmaSquared);
                }
            }
        }

        foreach (var (x, y) in _hotPixels)
        {
            values[y * Width + x] += 30_000;
        }

        var pixels = new ushort[values.Length];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)Math.Clamp(Math.Round(values[i]), 0, ushort.MaxValue);
        }

        return new CameraFrame(Width, Height, pixels, exposure);
    }
}

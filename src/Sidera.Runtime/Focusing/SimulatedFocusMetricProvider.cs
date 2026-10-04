using Sidera.Core.Focusing;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Focusing;

/// <summary>
/// The focus of a simulated optical train: where its true best focus is, and how fast the star size grows away from it.
/// The HFR is a hyperbola in the focuser position, sqrt(best² + (slope · distance)²): smooth, symmetrical, and
/// never below <see cref="BestHfr"/>.
/// </summary>
/// <param name="BestPosition">The focuser position of perfect focus.</param>
/// <param name="BestHfr">The HFR at perfect focus, in pixels.</param>
/// <param name="SlopePixelsPerStep">
/// How many pixels of HFR one focuser step away from the best position adds, in the far field. Zero makes a curve that
/// is flat: no focus can be found in it.
/// </param>
public sealed record SimulatedFocusModel(int BestPosition, double BestHfr = 1.8, double SlopePixelsPerStep = 0.0025)
{
    public double BestHfr { get; } = double.IsFinite(BestHfr) && BestHfr > 0
        ? BestHfr
        : throw new ArgumentOutOfRangeException(nameof(BestHfr), BestHfr, "The best HFR must be a finite number greater than 0.");

    public double SlopePixelsPerStep { get; } = double.IsFinite(SlopePixelsPerStep) && SlopePixelsPerStep >= 0
        ? SlopePixelsPerStep
        : throw new ArgumentOutOfRangeException(nameof(SlopePixelsPerStep), SlopePixelsPerStep, "The slope cannot be negative.");

    /// <summary>The HFR a frame exposed at <paramref name="position"/> has.</summary>
    public double HfrAt(int position)
    {
        var distance = (double)position - BestPosition;
        return Math.Sqrt(BestHfr * BestHfr + SlopePixelsPerStep * SlopePixelsPerStep * distance * distance);
    }
}

/// <summary>
/// A focus metric that does not look at the pixels: it knows, for each rig, where that rig is in focus
/// (<see cref="SimulatedFocusModel"/>), and answers with the HFR of that curve at the focuser position of the exposure.
/// That knowledge is that of the simulated optical train (rig, camera and focuser together). It is deliberately not part
/// of the focuser, which does not know where focus is.
/// <para>
/// There is no noise unless asked for. Noise is a uniform error of at most the given amplitude from a random generator
/// with a fixed seed, so a run is still the same every time.
/// </para>
/// </summary>
public sealed class SimulatedFocusMetricProvider : IFocusMetricProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<RigId, SimulatedFocusModel> _models = new();
    private readonly Random _random;
    private readonly double _noiseAmplitude;

    /// <param name="noiseAmplitudePixels">The largest error added to an HFR, in pixels; 0 for none.</param>
    /// <param name="noiseSeed">The seed of the noise.</param>
    public SimulatedFocusMetricProvider(double noiseAmplitudePixels = 0, int noiseSeed = 1)
    {
        if (!double.IsFinite(noiseAmplitudePixels) || noiseAmplitudePixels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(noiseAmplitudePixels), noiseAmplitudePixels, "Noise cannot be negative.");
        }

        _noiseAmplitude = noiseAmplitudePixels;
        _random = new Random(noiseSeed);
    }

    /// <summary>Sets where the optical train of a rig is in focus, replacing what was set before.</summary>
    public void SetModel(RigId rig, SimulatedFocusModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            _models[rig] = model;
        }
    }

    public bool TryGetModel(RigId rig, out SimulatedFocusModel? model)
    {
        lock (_gate)
        {
            return _models.TryGetValue(rig, out model);
        }
    }

    public Task<FocusMeasurement> MeasureAsync(FocusMetricInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();

        SimulatedFocusModel? model;
        double noise = 0;
        lock (_gate)
        {
            if (!_models.TryGetValue(input.Rig, out model))
            {
                throw new InvalidOperationException($"There is no simulated focus for rig '{input.Rig}'.");
            }

            if (_noiseAmplitude > 0)
            {
                noise = (_random.NextDouble() * 2 - 1) * _noiseAmplitude;
            }
        }

        // The simulation can never produce an HFR below a tenth of a pixel, however much noise there is.
        var hfr = Math.Max(model.HfrAt(input.FocuserPosition) + noise, 0.1);
        return Task.FromResult(new FocusMeasurement(input.FocuserPosition, hfr));
    }
}

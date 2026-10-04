namespace Sidera.Core.Guiding;

/// <summary>
/// The recent guide samples of a guider, bounded twice: by age (older samples are dropped) and by count. For a graph and for a rolling
/// RMS; never persisted and never part of the application state. Safe to use from the thread that receives samples and from the thread
/// that draws them.
/// </summary>
public sealed class GuidingHistory
{
    /// <summary>What a history keeps unless it is told otherwise: five minutes, which holds the largest window the graph offers.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(5);

    public const int DefaultMaxSamples = 6000;

    private readonly object _gate = new();
    private readonly Queue<GuidingSample> _samples = new();
    private readonly TimeSpan _maxAge;
    private readonly int _maxSamples;
    private long _version;

    public GuidingHistory(TimeSpan? maxAge = null, int maxSamples = DefaultMaxSamples)
    {
        _maxAge = maxAge ?? DefaultMaxAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_maxAge, TimeSpan.Zero, nameof(maxAge));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, 1);
        _maxSamples = maxSamples;
    }

    /// <summary>Raised after a sample was added or the history was cleared; on the thread that did it, and cheap to handle.</summary>
    public event EventHandler? Changed;

    /// <summary>Counts every change; a drawing control compares it to know whether there is something new.</summary>
    public long Version
    {
        get { lock (_gate) { return _version; } }
    }

    public int Count
    {
        get { lock (_gate) { return _samples.Count; } }
    }

    public void Add(GuidingSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > _maxSamples || (_samples.Count > 1 && sample.Time - _samples.Peek().Time > _maxAge))
            {
                _samples.Dequeue();
            }

            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
            _version++;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The samples of the last <paramref name="window"/> (all of them when <c>null</c>), oldest first, as a copy.</summary>
    public GuidingSample[] Snapshot(TimeSpan? window = null)
    {
        lock (_gate)
        {
            if (window is null || _samples.Count == 0)
            {
                return [.. _samples];
            }

            var from = _samples.Last().Time - window.Value;
            return [.. _samples.Where(s => s.Time >= from)];
        }
    }

    /// <summary>
    /// Copies the samples of the last <paramref name="window"/>, oldest first, into <paramref name="into"/> (which is not cleared): for a
    /// drawing that keeps one list and so allocates nothing per frame.
    /// </summary>
    public void CopyRecent(List<GuidingSample> into, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(into);
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return;
            }

            var from = _samples.Last().Time - window;
            foreach (var sample in _samples)
            {
                if (sample.Time >= from)
                {
                    into.Add(sample);
                }
            }
        }
    }

    /// <summary>
    /// The rolling RMS over the last <paramref name="window"/>, in arcseconds: right ascension and declination by themselves, and the
    /// total as the square root of the sum of their squares. A sample without a value for an axis does not count for that axis; with no
    /// sample at all for an axis, its RMS is <c>null</c>, not zero.
    /// </summary>
    public GuidingRms Rms(TimeSpan window) => Rms(Snapshot(window));

    public static GuidingRms Rms(IReadOnlyCollection<GuidingSample> samples)
    {
        double raSum = 0, decSum = 0;
        int raCount = 0, decCount = 0;
        foreach (var s in samples)
        {
            if (s.RaErrorArcsec is { } ra && double.IsFinite(ra))
            {
                raSum += ra * ra;
                raCount++;
            }

            if (s.DecErrorArcsec is { } dec && double.IsFinite(dec))
            {
                decSum += dec * dec;
                decCount++;
            }
        }

        double? raRms = raCount > 0 ? Math.Sqrt(raSum / raCount) : null;
        double? decRms = decCount > 0 ? Math.Sqrt(decSum / decCount) : null;
        double? total = raRms is { } r && decRms is { } d ? Math.Sqrt(r * r + d * d) : null;
        return new GuidingRms(raRms, decRms, total, Math.Max(raCount, decCount));
    }
}

/// <summary>A rolling RMS in arcseconds, with how many samples it is made of.</summary>
public sealed record GuidingRms(double? RaArcsec, double? DecArcsec, double? TotalArcsec, int Samples);

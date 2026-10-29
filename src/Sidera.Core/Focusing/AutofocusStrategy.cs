using Sidera.Core.Rigs;

namespace Sidera.Core.Focusing;

/// <summary>How an autofocus run finds the focus.</summary>
public enum AutofocusMode
{
    /// <summary>A symmetrical pattern of positions around the current one, fitted once; a pattern is put around a minimum that lies outside it. Fails when a sample cannot be measured.</summary>
    Pattern,

    /// <summary>
    /// A search: it starts where the focuser stands, steps toward the side where the stars get smaller and turns round when they get larger, until it has samples on both sides of the minimum. A sample
    /// that cannot be measured (stars too blurred) is skipped, not fatal. The curve is fitted through what was measured, and a focus that is worse than the best sample falls back to that sample.
    /// </summary>
    Search,
}

/// <summary>How the focus curve is fitted through the samples.</summary>
public enum FocusFitMethod
{
    /// <summary>The HFR is a hyperbola in the focuser position, so its square is a parabola: a least-squares parabola through HFR², leaving out the samples that spoil the fit.</summary>
    Hyperbola,

    /// <summary>A straight line through the samples on each side of the minimum; the focus is where the lines cross. Robust when the curve is far from a hyperbola (a seeing floor, a bad outer sample).</summary>
    TrendLines,
}

/// <summary>Where autofocus remembers what it found: the positions that were in focus, for a rig, newest last.</summary>
public interface IFocusHistory
{
    /// <summary>The last position in focus of the rig, or <c>null</c> when there is none.</summary>
    int? LastPosition(RigId rig);

    /// <summary>What was found in focus last (position and HFR there), or <c>null</c>.</summary>
    FocusHistoryEntry? Last(RigId rig);

    void Record(RigId rig, int position, double hfr, DateTimeOffset when);
}

/// <summary>What was found in focus once: the position and the HFR there, and when.</summary>
public sealed record FocusHistoryEntry(int Position, double Hfr, DateTimeOffset When);

/// <summary>
/// The positions that autofocus found in focus, by rig, kept in memory (the application saves and loads them). A search that finds nothing at the position where the focuser stands starts from the last
/// position that was in focus.
/// </summary>
public sealed class FocusHistory : IFocusHistory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<FocusHistoryEntry>> _entries = new(StringComparer.Ordinal);

    /// <summary>How many positions are kept for a rig.</summary>
    public int Capacity { get; init; } = 10;

    /// <summary>Raised after a position was recorded (the application saves them then).</summary>
    public event EventHandler? Changed;

    public FocusHistoryEntry? Last(RigId rig)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(rig.Value, out var list) && list.Count > 0 ? list[^1] : null;
        }
    }

    public int? LastPosition(RigId rig)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(rig.Value, out var list) && list.Count > 0 ? list[^1].Position : null;
        }
    }

    public IReadOnlyList<FocusHistoryEntry> Of(RigId rig)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(rig.Value, out var list) ? [.. list] : [];
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<FocusHistoryEntry>> All()
    {
        lock (_gate)
        {
            return _entries.ToDictionary(e => e.Key, e => (IReadOnlyList<FocusHistoryEntry>)[.. e.Value], StringComparer.Ordinal);
        }
    }

    public void Record(RigId rig, int position, double hfr, DateTimeOffset when)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(rig.Value, out var list))
            {
                _entries[rig.Value] = list = [];
            }

            list.Add(new FocusHistoryEntry(position, hfr, when));
            if (list.Count > Capacity)
            {
                list.RemoveRange(0, list.Count - Capacity);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Puts back what was saved; whatever is there is replaced.</summary>
    public void Load(IReadOnlyDictionary<string, IReadOnlyList<FocusHistoryEntry>> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (var (rig, list) in entries)
            {
                _entries[rig] = [.. list.TakeLast(Capacity)];
            }
        }
    }
}

/// <summary>
/// How autofocus searches, as the application chooses it (Settings → Autofocus). It is not part of a session: every run reads it when its options are made.
/// </summary>
public sealed record AutofocusStrategy
{
    /// <summary>The strategy of the application. The default is the pattern; the application sets its own at start and when the settings change.</summary>
    public static AutofocusStrategy Application { get; set; } = new();

    public AutofocusMode Mode { get; init; } = AutofocusMode.Pattern;
    public FocusFitMethod FitMethod { get; init; } = FocusFitMethod.Hyperbola;

    /// <summary>The most samples a search takes; 0 is as many as a good search needs (four times the samples of a pattern, and a few more).</summary>
    public int MaxSamples { get; init; }

    /// <summary>How many samples in a row that cannot be measured a search puts up with before it gives up.</summary>
    public int MaxFailedSamples { get; init; } = 3;

    /// <summary>A search that finds no star where the focuser stands goes to the last position that was in focus and tries there.</summary>
    public bool UseRememberedPosition { get; init; } = true;

    /// <summary>
    /// A second, finer pass around the position the first one found: a quarter of the step size, fewer samples. The first pass finds the focus roughly, the second one exactly.
    /// </summary>
    public bool FinePass { get; init; } = true;

    /// <summary>How far from where the search started it may go, in focuser steps, to either side; 0 is ten steps of the step size.</summary>
    public int MaxTravelSteps { get; init; }

    /// <summary>Where the positions that were in focus are kept; <c>null</c> remembers nothing.</summary>
    public IFocusHistory? History { get; init; }
}

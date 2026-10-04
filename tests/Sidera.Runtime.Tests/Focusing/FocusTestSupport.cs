using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Runtime.Focusing;

namespace Sidera.Runtime.Tests.Focusing;

/// <summary>Measures without a camera: the HFR comes from a function of the focuser position.</summary>
internal sealed class FunctionMeasurer(IFocuser focuser, Func<int, double> hfr) : IFocusMeasurer
{
    private readonly object _gate = new();
    private readonly List<int> _positions = [];

    public Func<int, CancellationToken, Task>? BeforeMeasure { get; init; }

    public IReadOnlyList<int> Positions
    {
        get { lock (_gate) { return _positions.ToArray(); } }
    }

    public async Task<FocusMeasurement> MeasureAsync(TimeSpan exposureDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var position = focuser.Position;
        if (BeforeMeasure is not null)
        {
            await BeforeMeasure(position, cancellationToken);
        }

        await Task.Yield();
        lock (_gate)
        {
            _positions.Add(position);
        }

        return new FocusMeasurement(position, hfr(position));
    }
}

/// <summary>The focus curve of the simulation, as a plain function.</summary>
internal static class Curves
{
    public static Func<int, double> Hyperbola(int best, double bestHfr = 1.8, double slope = 0.0025) =>
        new SimulatedFocusModel(best, bestHfr, slope).HfrAt;
}

/// <summary>Records what a focuser did, in order: the positions it arrived at.</summary>
internal sealed class FocuserTrace
{
    private readonly object _gate = new();
    private readonly List<int> _arrivals = [];

    public FocuserTrace(Sidera.Runtime.Events.EventBus bus)
    {
        bus.Subscribe<FocuserPositionChanged>((e, _) =>
        {
            lock (_gate)
            {
                _arrivals.Add(e.Position);
            }

            return Task.CompletedTask;
        });
    }

    public IReadOnlyList<int> Arrivals
    {
        get { lock (_gate) { return _arrivals.ToArray(); } }
    }
}

/// <summary>A focuser that can be made to fail, and that tells the test what it was asked to do.</summary>
internal sealed class ScriptedFocuser(string id, int position = 10000) : IFocuser
{
    private int _moves;

    public DeviceId Id { get; } = new(id);
    public string Name => "Scripted focuser";
    public DeviceType Type => DeviceType.Focuser;
    public DeviceConnectionState ConnectionState { get; set; } = DeviceConnectionState.Connected;
    public FocuserMotionState MotionState { get; private set; }
    public int Position { get; private set; } = position;
    public int MinPosition => 0;
    public int MaxPosition => 50000;

    /// <summary>The move (1-based) that fails; 0 for none.</summary>
    public int FailOnMove { get; init; }

    public int Moves => _moves;

    public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task MoveToAsync(int target, CancellationToken cancellationToken = default)
    {
        var move = Interlocked.Increment(ref _moves);
        MotionState = FocuserMotionState.Moving;
        try
        {
            await Task.Yield();
            if (move == FailOnMove)
            {
                throw new InvalidOperationException("The focuser motor stalled.");
            }

            Position = target;
        }
        finally
        {
            MotionState = FocuserMotionState.Idle;
        }
    }
}

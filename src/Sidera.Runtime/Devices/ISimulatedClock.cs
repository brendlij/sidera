using System.Diagnostics;

namespace Sidera.Runtime.Devices;

/// <summary>Monotonic time and waiting for simulated guiding telemetry; tests replace it to control time.</summary>
internal interface ISimulatedClock
{
    /// <summary>Monotonic time since an arbitrary, fixed origin.</summary>
    TimeSpan Now { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemSimulatedClock : ISimulatedClock
{
    public static readonly SystemSimulatedClock Instance = new();

    private readonly long _origin = Stopwatch.GetTimestamp();

    public TimeSpan Now => Stopwatch.GetElapsedTime(_origin);

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

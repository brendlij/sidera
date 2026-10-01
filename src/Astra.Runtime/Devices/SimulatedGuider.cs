using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Guiding;

namespace Astra.Runtime.Devices;

/// <summary>
/// A guider that only pretends: starting and stopping guiding take a fixed time each, and there is no
/// guiding loop, telemetry or background work behind the <see cref="GuidingState"/>.
/// <para>
/// One lifecycle operation (connect, disconnect, start or stop) runs at a time; an overlapping call is
/// rejected with <see cref="InvalidOperationException"/>. A cancelled or failed operation restores the
/// stable state it started from: start → <c>Idle</c>, stop → <c>Guiding</c>, connect → disconnected.
/// A disconnect always ends disconnected and idle; active guiding becomes idle before the disconnect is
/// published. Calls that find the device already in their target state do nothing.
/// </para>
/// </summary>
public sealed class SimulatedGuider : IGuider
{
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _startDuration;
    private readonly TimeSpan _stopDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private GuidingState _guidingState = GuidingState.Idle;
    private bool _busy;

    public SimulatedGuider(
        DeviceId id,
        string name = "Simulated Guider",
        IEventPublisher? events = null,
        TimeSpan? startDuration = null,
        TimeSpan? stopDuration = null
    )
    {
        Id = id;
        Name = name;
        _events = events;
        _startDuration = startDuration ?? DefaultTransitionDuration;
        _stopDuration = stopDuration ?? DefaultTransitionDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_startDuration, TimeSpan.Zero, nameof(startDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_stopDuration, TimeSpan.Zero, nameof(stopDuration));
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Guider;

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public GuidingState GuidingState
    {
        get { lock (_gate) { return _guidingState; } }
    }

    /// <exception cref="InvalidOperationException">Another operation is in progress.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfBusy();
            if (_connectionState == DeviceConnectionState.Connected)
            {
                return;
            }

            _busy = true;
            _connectionState = DeviceConnectionState.Connecting;
        }

        try
        {
            try
            {
                await PublishConnectionAsync(
                    DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                await SetConnectionStateAsync(DeviceConnectionState.Connected, cancellationToken);
                // A subscriber may cancel the token and still return normally.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ReleaseGuard();
        }
    }

    /// <summary>
    /// Disconnects; active guiding is stopped first. Cancellation cuts the transition short, but the guider
    /// still ends disconnected and idle before the cancellation is propagated.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another operation (such as starting or stopping guiding) is in progress.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfBusy();
            if (_connectionState == DeviceConnectionState.Disconnected)
            {
                return;
            }

            _busy = true;
            _connectionState = DeviceConnectionState.Disconnecting;
        }

        try
        {
            try
            {
                await PublishConnectionAsync(
                    DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, cancellationToken);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
            finally
            {
                try
                {
                    await SetGuidingStateAsync(GuidingState.Idle, CancellationToken.None);
                }
                finally
                {
                    await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
                }
            }

            // Cancellation requested during cleanup is still reported, now that the guider is disconnected.
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            ReleaseGuard();
        }
    }

    /// <exception cref="InvalidOperationException">The guider is not connected or another operation is in progress.</exception>
    public Task StartGuidingAsync(CancellationToken cancellationToken = default) =>
        TransitionGuidingAsync(
            GuidingState.Idle, GuidingState.Starting, GuidingState.Guiding, _startDuration, "start", cancellationToken);

    /// <exception cref="InvalidOperationException">The guider is not connected or another operation is in progress.</exception>
    public Task StopGuidingAsync(CancellationToken cancellationToken = default) =>
        TransitionGuidingAsync(
            GuidingState.Guiding, GuidingState.Stopping, GuidingState.Idle, _stopDuration, "stop", cancellationToken);

    private async Task TransitionGuidingAsync(
        GuidingState from,
        GuidingState transitional,
        GuidingState to,
        TimeSpan duration,
        string verb,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfBusy();
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Cannot {verb} guiding: guider '{Id}' is not connected.");
            }

            if (_guidingState == to)
            {
                return;
            }

            _busy = true;
            _guidingState = transitional;
        }

        try
        {
            try
            {
                await PublishGuidingAsync(from, transitional, cancellationToken);
                await Task.Delay(duration, cancellationToken);
                await SetGuidingStateAsync(to, cancellationToken);
                // A subscriber may cancel the token and still return normally.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await SetGuidingStateAsync(from, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ReleaseGuard();
        }
    }

    // Called while holding _gate.
    private void ThrowIfBusy()
    {
        if (_busy)
        {
            throw new InvalidOperationException(
                $"Guider '{Id}' is busy: another connect, disconnect, start or stop operation is still in progress.");
        }
    }

    private void ReleaseGuard()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    private async Task SetConnectionStateAsync(DeviceConnectionState state, CancellationToken cancellationToken)
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            previous = _connectionState;
            _connectionState = state;
        }

        await PublishConnectionAsync(previous, state, cancellationToken);
    }

    private async Task SetGuidingStateAsync(GuidingState state, CancellationToken cancellationToken)
    {
        GuidingState previous;
        lock (_gate)
        {
            previous = _guidingState;
            _guidingState = state;
        }

        await PublishGuidingAsync(previous, state, cancellationToken);
    }

    private Task PublishConnectionAsync(
        DeviceConnectionState previous,
        DeviceConnectionState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);
    }

    private Task PublishGuidingAsync(GuidingState previous, GuidingState current, CancellationToken cancellationToken)
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(new GuidingStateChanged(Id, previous, current), cancellationToken);
    }
}

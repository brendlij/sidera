using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Guiding;

namespace Astra.Runtime.Devices;

/// <summary>
/// A guider that only pretends: starting, stopping guiding and dithering take a fixed time each, and there is no
/// guiding loop, telemetry, settling or background work behind the <see cref="GuidingState"/>.
/// <para>
/// One lifecycle operation (connect, disconnect, start, stop or dither) runs at a time; an overlapping call is
/// rejected with <see cref="InvalidOperationException"/>. A cancelled or failed operation restores the
/// stable state it started from: start → <c>Idle</c>, stop and dither → <c>Guiding</c>, connect → disconnected.
/// A disconnect always ends disconnected and idle; active guiding becomes idle before the disconnect is
/// published. Calls that find the device already in their target state do nothing.
/// </para>
/// </summary>
public sealed class SimulatedGuider : IDitherGuider
{
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _startDuration;
    private readonly TimeSpan _stopDuration;
    private readonly TimeSpan _ditherDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private GuidingState _guidingState = GuidingState.Idle;
    private bool _busy;

    public SimulatedGuider(
        DeviceId id,
        string name = "Simulated Guider",
        IEventPublisher? events = null,
        TimeSpan? startDuration = null,
        TimeSpan? stopDuration = null,
        TimeSpan? ditherDuration = null
    )
    {
        Id = id;
        Name = name;
        _events = events;
        _startDuration = startDuration ?? DefaultTransitionDuration;
        _stopDuration = stopDuration ?? DefaultTransitionDuration;
        _ditherDuration = ditherDuration ?? DefaultTransitionDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_startDuration, TimeSpan.Zero, nameof(startDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_stopDuration, TimeSpan.Zero, nameof(stopDuration));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_ditherDuration, TimeSpan.Zero, nameof(ditherDuration));
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

        await RunGuidingTransitionAsync(from, transitional, to, duration, cancellationToken);
    }

    /// <summary>
    /// Publishes <c>Guiding → Dithering → Guiding</c>. Completion means only that the simulated dither command has
    /// finished; nothing about settling is simulated or implied.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amplitudePixels"/> is not a finite, positive number.</exception>
    /// <exception cref="InvalidOperationException">The guider is not connected, not guiding, or another operation is in progress.</exception>
    public async Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(amplitudePixels) || amplitudePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amplitudePixels), amplitudePixels, "Dither amplitude must be a finite, positive number of guider pixels.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ThrowIfBusy();
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Cannot dither: guider '{Id}' is not connected.");
            }

            if (_guidingState != GuidingState.Guiding)
            {
                throw new InvalidOperationException($"Cannot dither: guider '{Id}' is not guiding.");
            }

            _busy = true;
            _guidingState = GuidingState.Dithering;
        }

        await RunGuidingTransitionAsync(
            GuidingState.Guiding, GuidingState.Dithering, GuidingState.Guiding, _ditherDuration, cancellationToken);
    }

    // Called with the guard taken and the transitional state already set; restores `from` unless it succeeds.
    // The original failure or cancellation is propagated even if publishing the restored state fails as well.
    private async Task RunGuidingTransitionAsync(
        GuidingState from,
        GuidingState transitional,
        GuidingState to,
        TimeSpan duration,
        CancellationToken cancellationToken
    )
    {
        var targetPublished = false;
        try
        {
            try
            {
                await PublishGuidingAsync(from, transitional, cancellationToken);
                await Task.Delay(duration, cancellationToken);
                await SetGuidingStateAsync(to, cancellationToken);
                targetPublished = true;
                // A subscriber may cancel the token and still return normally.
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await RestoreGuidingStateAsync(from, transitional, targetPublished);
                throw;
            }
        }
        finally
        {
            ReleaseGuard();
        }
    }

    private async Task RestoreGuidingStateAsync(GuidingState from, GuidingState transitional, bool targetPublished)
    {
        GuidingState previous;
        lock (_gate)
        {
            previous = _guidingState;
            _guidingState = from;
        }

        // A dither ends where it began, so a failed final publication already left the local state at `from`
        // while observers may still see the transitional state; republish the restored state in that case.
        if (previous == from && !targetPublished)
        {
            previous = transitional;
        }

        try
        {
            await PublishGuidingAsync(previous, from, CancellationToken.None);
        }
        catch
        {
            // The local state is restored; the caller propagates the original failure instead of this one.
        }
    }

    // Called while holding _gate.
    private void ThrowIfBusy()
    {
        if (_busy)
        {
            throw new InvalidOperationException(
                $"Guider '{Id}' is busy: another connect, disconnect, start, stop or dither operation is still in progress.");
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

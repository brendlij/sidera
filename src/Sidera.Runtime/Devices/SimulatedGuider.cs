using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Guiding;

namespace Sidera.Runtime.Devices;

/// <summary>
/// A guider that only pretends: starting, stopping guiding and dithering take a fixed time each, and there is no
/// guiding loop or background work behind the <see cref="GuidingState"/>.
/// <para>
/// For settling it models a deterministic guide error, sampled every <see cref="SampleInterval"/> while connected
/// and guiding: <see cref="NormalGuideErrorPixels"/> in steady guiding, and after each completed dither a
/// disturbance of the dither amplitude on top of it that halves every <see cref="DisturbanceHalfLife"/>.
/// Observations older than two sample intervals count as stale. Settling only observes: it is not a lifecycle
/// operation, takes no part in the one-operation-at-a-time rule and never changes the guiding state.
/// </para>
/// <para>
/// One lifecycle operation (connect, disconnect, start, stop or dither) runs at a time; an overlapping call is
/// rejected with <see cref="InvalidOperationException"/>. A cancelled or failed operation restores the
/// stable state it started from: start → <c>Idle</c>, stop and dither → <c>Guiding</c>, connect → disconnected.
/// A disconnect always ends disconnected and idle; active guiding becomes idle before the disconnect is
/// published. Calls that find the device already in their target state do nothing.
/// </para>
/// </summary>
public sealed class SimulatedGuider : IDitherGuider, IGuidingSettler
{
    private static readonly TimeSpan DefaultTransitionDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>Guide error of steady, undisturbed guiding, in guide camera pixels.</summary>
    public const double NormalGuideErrorPixels = 0.3;

    /// <summary>How often the simulated guide error is sampled while settling.</summary>
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Time in which the disturbance after a dither halves.</summary>
    public static readonly TimeSpan DisturbanceHalfLife = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _startDuration;
    private readonly TimeSpan _stopDuration;
    private readonly TimeSpan _ditherDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private GuidingState _guidingState = GuidingState.Idle;
    private bool _busy;
    private readonly ISimulatedClock _clock;
    private TaskCompletionSource _guidingInterrupted = NewSignal();
    private TimeSpan? _disturbanceStart;
    private double _disturbancePixels;

    public SimulatedGuider(
        DeviceId id,
        string name = "Simulated Guider",
        IEventPublisher? events = null,
        TimeSpan? startDuration = null,
        TimeSpan? stopDuration = null,
        TimeSpan? ditherDuration = null
    )
        : this(id, SystemSimulatedClock.Instance, name, events, startDuration, stopDuration, ditherDuration)
    {
    }

    /// <param name="clock">Monotonic time for the guide-error model and the settle wait.</param>
    internal SimulatedGuider(
        DeviceId id,
        ISimulatedClock clock,
        string name = "Simulated Guider",
        IEventPublisher? events = null,
        TimeSpan? startDuration = null,
        TimeSpan? stopDuration = null,
        TimeSpan? ditherDuration = null
    )
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
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
            InterruptGuidingLocked();
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
            SetGuidingStateLocked(transitional);
        }

        await RunGuidingTransitionAsync(from, transitional, to, duration, cancellationToken);
    }

    /// <summary>
    /// Publishes <c>Guiding → Dithering → Guiding</c>. Completion means only that the simulated dither command has
    /// finished and the guide-error disturbance has begun; it does not mean that guiding has settled.
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
            SetGuidingStateLocked(GuidingState.Dithering);
        }

        await RunGuidingTransitionAsync(
            GuidingState.Guiding, GuidingState.Dithering, GuidingState.Guiding, _ditherDuration, cancellationToken);

        lock (_gate)
        {
            // The lock position has moved: the guide star starts off by the dither amplitude.
            _disturbanceStart = _clock.Now;
            _disturbancePixels = amplitudePixels;
        }
    }

    /// <summary>
    /// Waits until the simulated guide error has stayed at or below the threshold for the stable duration,
    /// sampling it every <see cref="SampleInterval"/>. See <see cref="IGuidingSettler.SettleAsync"/> for the
    /// outcomes; none of them changes the guiding state.
    /// </summary>
    /// <exception cref="InvalidOperationException">The guider is not connected or not guiding, or stopped guiding while waiting.</exception>
    /// <exception cref="GuidingSettleTimeoutException">Guiding did not settle within the timeout.</exception>
    public async Task SettleAsync(GuidingSettleOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        Task interrupted;
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"Cannot settle: guider '{Id}' is not connected.");
            }

            if (_guidingState != GuidingState.Guiding)
            {
                throw new InvalidOperationException($"Cannot settle: guider '{Id}' is not guiding.");
            }

            interrupted = _guidingInterrupted.Task;
        }

        var start = _clock.Now;
        var tracker = new GuidingSettleTracker(options, start, 2 * SampleInterval);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (interrupted.IsCompleted)
            {
                throw new InvalidOperationException($"Guider '{Id}' stopped guiding while settling.");
            }

            var now = _clock.Now;
            switch (tracker.Evaluate(now, ObserveGuideError(now)))
            {
                case GuidingSettleProgress.Settled:
                    return;
                case GuidingSettleProgress.TimedOut:
                    throw new GuidingSettleTimeoutException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Guider '{Id}' did not settle within {options.Timeout.TotalSeconds:0.##} s."));
            }

            // The next sample, or the timeout if that comes first; a stop or disconnect ends the wait at once.
            var wait = TimeSpan.FromTicks(Math.Min(SampleInterval.Ticks, (options.Timeout - (now - start)).Ticks));
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = _clock.DelayAsync(wait, delayCancellation.Token);
            if (await Task.WhenAny(delay, interrupted) != delay)
            {
                await delayCancellation.CancelAsync();
            }

            try
            {
                await delay;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Cut short because guiding was interrupted; reported at the top of the loop.
            }
        }
    }

    /// <summary>Replaces the guide-error model in tests: maps the current monotonic time to the latest observation, if any.</summary>
    internal Func<TimeSpan, GuideErrorObservation?>? ObservationSource { get; set; }

    /// <summary>The latest guide-error observation at <paramref name="now"/>; none unless connected and guiding.</summary>
    internal GuideErrorObservation? ObserveGuideError(TimeSpan now)
    {
        if (ObservationSource is { } source)
        {
            return source(now);
        }

        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected || _guidingState != GuidingState.Guiding)
            {
                return null;
            }

            var error = NormalGuideErrorPixels;
            if (_disturbanceStart is { } disturbed && now >= disturbed)
            {
                error += _disturbancePixels * Math.Pow(0.5, (now - disturbed) / DisturbanceHalfLife);
            }

            return new GuideErrorObservation(now, error);
        }
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
            SetGuidingStateLocked(from);
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

    // Called while holding _gate. Leaving Guiding ends every settle wait that is in progress.
    private void SetGuidingStateLocked(GuidingState state)
    {
        if (_guidingState == GuidingState.Guiding && state != GuidingState.Guiding)
        {
            InterruptGuidingLocked();
        }

        _guidingState = state;
    }

    // Called while holding _gate. The signal runs its continuations asynchronously, never under the lock.
    private void InterruptGuidingLocked()
    {
        _guidingInterrupted.TrySetResult();
        _guidingInterrupted = NewSignal();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            SetGuidingStateLocked(state);
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

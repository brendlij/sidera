using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Focusers;

namespace Astra.Runtime.Devices;

/// <summary>
/// A focuser that only pretends: a move takes a time that grows with the distance, and then the position jumps to
/// the target. Nothing in it is random, and there is no focus quality: it is the hardware that autofocus will later
/// drive, not a simulation of the sky.
/// <para>
/// State machine of <see cref="MotionState"/>: <c>Idle</c> → <c>Moving</c> during a move → <c>Idle</c> when it
/// ended. Events of a move that arrived: motion Idle→Moving (at the start position), position changed, motion
/// Moving→Idle (at the target). A move that was cancelled or failed ends in <c>Idle</c> at the position it started
/// from, without a position event: the target is never reported as reached. A move to the current position
/// completes at once and publishes nothing.
/// </para>
/// </summary>
public sealed partial class SimulatedFocuser : IFocuserControl
{
    public const int DefaultStartPosition = 10000;
    public const int DefaultMinPosition = 0;
    public const int DefaultMaxPosition = 50000;
    public const int DefaultStepsPerSecond = 10000;

    private static readonly TimeSpan DefaultMinimumMoveDuration = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly int _stepsPerSecond;
    private readonly TimeSpan _minimumMoveDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private FocuserMotionState _motionState = FocuserMotionState.Idle;
    private int _position;

    /// <param name="stepsPerSecond">How fast it moves; a move of N steps takes N / this, but at least <paramref name="minimumMoveDuration"/>.</param>
    /// <param name="minimumMoveDuration">The shortest a move that has to go anywhere takes.</param>
    public SimulatedFocuser(
        DeviceId id,
        string name = "Simulated Focuser",
        IEventPublisher? events = null,
        int startPosition = DefaultStartPosition,
        int minPosition = DefaultMinPosition,
        int maxPosition = DefaultMaxPosition,
        int stepsPerSecond = DefaultStepsPerSecond,
        TimeSpan? minimumMoveDuration = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPosition, minPosition);
        ArgumentOutOfRangeException.ThrowIfLessThan(startPosition, minPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startPosition, maxPosition);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stepsPerSecond, 0);

        Id = id;
        Name = name;
        _events = events;
        _position = startPosition;
        MinPosition = minPosition;
        MaxPosition = maxPosition;
        _stepsPerSecond = stepsPerSecond;
        _minimumMoveDuration = minimumMoveDuration ?? DefaultMinimumMoveDuration;
        ArgumentOutOfRangeException.ThrowIfLessThan(_minimumMoveDuration, TimeSpan.Zero);
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Focuser;
    public int MinPosition { get; }
    public int MaxPosition { get; }

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public FocuserMotionState MotionState
    {
        get { lock (_gate) { return _motionState; } }
    }

    public int Position
    {
        get { lock (_gate) { return _position; } }
    }

    /// <summary>How long a move from <paramref name="from"/> to <paramref name="to"/> takes.</summary>
    public TimeSpan MoveDuration(int from, int to)
    {
        var travel = TimeSpan.FromSeconds((double)Math.Abs((long)to - from) / _stepsPerSecond);
        return travel > _minimumMoveDuration ? travel : _minimumMoveDuration;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransition(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting))
        {
            return;
        }

        try
        {
            await PublishConnectionAsync(
                DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            await SetConnectionStateAsync(DeviceConnectionState.Connected, cancellationToken);
        }
        catch
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
            throw;
        }
    }

    /// <exception cref="InvalidOperationException">A move is running.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool disconnecting;
        lock (_gate)
        {
            if (_connectionState == DeviceConnectionState.Connected && _motionState == FocuserMotionState.Moving)
            {
                throw new InvalidOperationException("Cannot disconnect while the focuser is moving.");
            }

            disconnecting = _connectionState == DeviceConnectionState.Connected;
            if (disconnecting)
            {
                _connectionState = DeviceConnectionState.Disconnecting;
            }
        }

        if (!disconnecting)
        {
            return;
        }

        try
        {
            await PublishConnectionAsync(
                DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }
        finally
        {
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
        }
    }

    /// <exception cref="ArgumentOutOfRangeException">The target is outside the range of the focuser.</exception>
    /// <exception cref="InvalidOperationException">The focuser is not connected or is already moving.</exception>
    public async Task MoveToAsync(int target, CancellationToken cancellationToken = default)
    {
        if (target < MinPosition || target > MaxPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target), target, $"Focuser position must be between {MinPosition} and {MaxPosition}.");
        }

        int startedAt;
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Focuser is not connected.");
            }

            if (_motionState == FocuserMotionState.Moving)
            {
                throw new InvalidOperationException("The focuser is already moving.");
            }

            startedAt = _position;
            if (startedAt == target)
            {
                return;
            }

            _motionState = FocuserMotionState.Moving;
        }

        var arrived = false;
        try
        {
            await PublishMotionAsync(FocuserMotionState.Idle, FocuserMotionState.Moving, startedAt, cancellationToken);
            await Task.Delay(MoveDuration(startedAt, target), cancellationToken);

            lock (_gate)
            {
                _position = target;
                _motionState = FocuserMotionState.Idle;
            }

            arrived = true;
            // Arrived: report it even if cancellation was requested in the meantime.
            await PublishPositionAsync(startedAt, target);
            await PublishMotionAsync(FocuserMotionState.Moving, FocuserMotionState.Idle, target, CancellationToken.None);
        }
        finally
        {
            if (!arrived)
            {
                // Cancelled or failed on the way: stopped where it started.
                lock (_gate)
                {
                    _motionState = FocuserMotionState.Idle;
                }

                await PublishMotionAsync(FocuserMotionState.Moving, FocuserMotionState.Idle, startedAt, CancellationToken.None);
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool TryTransition(DeviceConnectionState from, DeviceConnectionState to)
    {
        lock (_gate)
        {
            if (_connectionState != from)
            {
                return false;
            }

            _connectionState = to;
            return true;
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

        OnConnectionStateSet(state);
        await PublishConnectionAsync(previous, state, cancellationToken);
    }

    private Task PublishConnectionAsync(
        DeviceConnectionState previous, DeviceConnectionState current, CancellationToken cancellationToken)
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);
    }

    private Task PublishMotionAsync(
        FocuserMotionState previous, FocuserMotionState current, int position, CancellationToken cancellationToken)
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new FocuserMotionStateChanged(Id, previous, current, position), cancellationToken);
    }

    private Task PublishPositionAsync(int previous, int current)
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new FocuserPositionChanged(Id, previous, current), CancellationToken.None);
    }
}

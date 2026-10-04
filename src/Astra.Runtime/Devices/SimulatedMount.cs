using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Mounts;

namespace Astra.Runtime.Devices;

/// <summary>
/// A mount that only pretends: slewing takes a fixed time and then the coordinates jump to the target.
/// <para>
/// State machine of <see cref="MotionState"/>: <c>Idle</c> at start → <c>Slewing</c> during a slew →
/// <c>Tracking</c> when it arrived. A cancelled slew ends in <c>Idle</c> (stopped, not tracking) and the
/// coordinates stay at the last position that was actually reached; the target is never reported as reached.
/// </para>
/// </summary>
public sealed partial class SimulatedMount : IMountControl
{
    /// <summary>Where a new mount points.</summary>
    public static CelestialCoordinates DefaultCoordinates { get; } = new(0, 0);

    private static readonly TimeSpan DefaultSlewDuration = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _slewDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private MountMotionState _motionState = MountMotionState.Idle;
    private CelestialCoordinates _coordinates = DefaultCoordinates;

    public SimulatedMount(
        DeviceId id,
        string name = "Simulated Mount",
        IEventPublisher? events = null,
        TimeSpan? slewDuration = null
    )
    {
        Id = id;
        Name = name;
        _events = events;
        _slewDuration = slewDuration ?? DefaultSlewDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_slewDuration, TimeSpan.Zero);
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Mount;

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public MountMotionState MotionState
    {
        get { lock (_gate) { return _motionState; } }
    }

    public CelestialCoordinates Coordinates
    {
        get { lock (_gate) { return _coordinates; } }
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

    /// <exception cref="InvalidOperationException">A slew is running.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool disconnecting;
        lock (_gate)
        {
            if (_connectionState == DeviceConnectionState.Connected && _motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("Cannot disconnect while the mount is slewing.");
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

    /// <exception cref="InvalidOperationException">The mount is not connected or is already slewing.</exception>
    public async Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        CelestialCoordinates startedAt;
        bool wasTracking;
        using var motion = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Mount is not connected.");
            }

            if (_motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("The mount is already slewing.");
            }

            if (_parked)
            {
                throw new InvalidOperationException($"{Name} is parked. Unpark it first.");
            }

            wasTracking = _motionState == MountMotionState.Tracking;
            _motionState = MountMotionState.Slewing;
            startedAt = _coordinates;
            _motionStop = motion;
        }

        var previous = MountMotionState.Idle;
        var arrived = false;

        try
        {
            await PublishMotionAsync(previous, MountMotionState.Slewing, startedAt, cancellationToken);
            await Task.Delay(_slewDuration, motion.Token);

            lock (_gate)
            {
                _coordinates = target;
                _motionState = MountMotionState.Tracking;
            }

            arrived = true;
            lock (_gate)
            {
                _motionStop = null;
            }

            // Arrived: report it even if cancellation was requested in the meantime.
            await PublishMotionAsync(MountMotionState.Slewing, MountMotionState.Tracking, target, CancellationToken.None);
        }
        finally
        {
            if (!arrived)
            {
                // Cancelled or failed on the way: stopped where it was. Stop only stops the movement: whether the mount tracks is
                // what it was before; a slew that was cancelled otherwise leaves it not tracking.
                MountMotionState next;
                lock (_gate)
                {
                    next = _stopRequested && wasTracking ? MountMotionState.Tracking : MountMotionState.Idle;
                    _motionState = next;
                    _stopRequested = false;
                    _motionStop = null;
                }

                await PublishMotionAsync(MountMotionState.Slewing, next, startedAt, CancellationToken.None);
            }
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
        DeviceConnectionState previous,
        DeviceConnectionState current,
        CancellationToken cancellationToken
    )
    {
        return _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);
    }

    private Task PublishMotionAsync(
        MountMotionState previous,
        MountMotionState current,
        CelestialCoordinates coordinates,
        CancellationToken cancellationToken
    )
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new MountMotionStateChanged(Id, previous, current, coordinates), cancellationToken);
    }
}

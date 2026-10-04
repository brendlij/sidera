using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;

namespace Sidera.Runtime.Devices;

/// <summary>
/// A filter wheel that only pretends: turning to another slot takes a fixed time, and then that slot is current.
/// The slots are given by the caller; nothing about their names is built in.
/// <para>
/// State machine of <see cref="MotionState"/>: <c>Idle</c> → <c>Moving</c> while turning → <c>Idle</c> when it
/// ended. Events of a move that arrived: motion Idle→Moving (at the start slot), slot changed, motion Moving→Idle
/// (at the target slot). A move that was cancelled or failed ends in <c>Idle</c> at the slot it started from,
/// without a slot event. A move to the current slot completes at once and publishes nothing.
/// </para>
/// </summary>
public sealed class SimulatedFilterWheel : IFilterWheel
{
    private static readonly TimeSpan DefaultMoveDuration = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _moveDuration;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private FilterWheelMotionState _motionState = FilterWheelMotionState.Idle;
    private FilterSlot _current;

    /// <param name="slots">At least one slot; indexes must be 0, 1, 2, … in order.</param>
    public SimulatedFilterWheel(
        DeviceId id,
        IEnumerable<FilterSlot> slots,
        string name = "Simulated Filter Wheel",
        IEventPublisher? events = null,
        int startSlotIndex = 0,
        TimeSpan? moveDuration = null
    )
    {
        ArgumentNullException.ThrowIfNull(slots);

        Slots = slots.ToArray();
        if (Slots.Count == 0)
        {
            throw new ArgumentException("A filter wheel needs at least one slot.", nameof(slots));
        }

        for (var i = 0; i < Slots.Count; i++)
        {
            if (Slots[i].Index != i)
            {
                throw new ArgumentException("The slots of a filter wheel must have the indexes 0, 1, 2, … in order.", nameof(slots));
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startSlotIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startSlotIndex, Slots.Count);

        Id = id;
        Name = name;
        _events = events;
        _current = Slots[startSlotIndex];
        _moveDuration = moveDuration ?? DefaultMoveDuration;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_moveDuration, TimeSpan.Zero);
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.FilterWheel;
    public IReadOnlyList<FilterSlot> Slots { get; }

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public FilterWheelMotionState MotionState
    {
        get { lock (_gate) { return _motionState; } }
    }

    public FilterSlot CurrentSlot
    {
        get { lock (_gate) { return _current; } }
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

    /// <exception cref="InvalidOperationException">The wheel is turning.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        bool disconnecting;
        lock (_gate)
        {
            if (_connectionState == DeviceConnectionState.Connected && _motionState == FilterWheelMotionState.Moving)
            {
                throw new InvalidOperationException("Cannot disconnect while the filter wheel is moving.");
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

    /// <exception cref="ArgumentOutOfRangeException">There is no slot with this index.</exception>
    /// <exception cref="InvalidOperationException">The wheel is not connected or is already moving.</exception>
    public async Task MoveToSlotAsync(int slotIndex, CancellationToken cancellationToken = default)
    {
        if (slotIndex < 0 || slotIndex >= Slots.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slotIndex), slotIndex, $"Filter slot must be between 0 and {Slots.Count - 1}.");
        }

        var target = Slots[slotIndex];
        FilterSlot startedAt;
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Filter wheel is not connected.");
            }

            if (_motionState == FilterWheelMotionState.Moving)
            {
                throw new InvalidOperationException("The filter wheel is already moving.");
            }

            startedAt = _current;
            if (startedAt.Index == slotIndex)
            {
                return;
            }

            _motionState = FilterWheelMotionState.Moving;
        }

        var arrived = false;
        try
        {
            await PublishMotionAsync(FilterWheelMotionState.Idle, FilterWheelMotionState.Moving, startedAt, cancellationToken);
            await Task.Delay(_moveDuration, cancellationToken);

            lock (_gate)
            {
                _current = target;
                _motionState = FilterWheelMotionState.Idle;
            }

            arrived = true;
            // Arrived: report it even if cancellation was requested in the meantime.
            await PublishSlotAsync(startedAt, target);
            await PublishMotionAsync(FilterWheelMotionState.Moving, FilterWheelMotionState.Idle, target, CancellationToken.None);
        }
        finally
        {
            if (!arrived)
            {
                lock (_gate)
                {
                    _motionState = FilterWheelMotionState.Idle;
                }

                await PublishMotionAsync(FilterWheelMotionState.Moving, FilterWheelMotionState.Idle, startedAt, CancellationToken.None);
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
        FilterWheelMotionState previous, FilterWheelMotionState current, FilterSlot slot, CancellationToken cancellationToken)
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new FilterWheelMotionStateChanged(Id, previous, current, slot), cancellationToken);
    }

    private Task PublishSlotAsync(FilterSlot previous, FilterSlot current)
    {
        return _events is null
            ? Task.CompletedTask
            : _events.PublishAsync(new FilterWheelSlotChanged(Id, previous, current), CancellationToken.None);
    }
}

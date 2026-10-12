using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;

namespace Sidera.Ascom.FilterWheels;

/// <summary>
/// An ASCOM filter wheel as Sidera's <see cref="IFilterWheel"/>. The slots are the names the driver reports (a slot without a name is called "Filter 1", "Filter 2", …);
/// the index of a slot is its ASCOM position (zero-based). The names belong to the driver and are changed in its setup dialog, not in Sidera.
/// <para>
/// A move is setting <c>Position</c> and then reading it until it is no longer -1 ("moving" in ASCOM). It is never retried. ASCOM has no Halt for a filter wheel: a move that
/// is cancelled cannot be stopped, so the adapter waits for the wheel to arrive, reports where it ended, and then throws the cancellation, so that the position Sidera
/// shows is the one the wheel has. A move that does not end in time says honestly that the wheel may still be turning. Everything runs on the dispatcher thread of the device.
/// </para>
/// </summary>
public sealed class AscomFilterWheel : AscomDevice<IAscomFilterWheelDriver>, IFilterWheel
{
    private const int MovingPosition = -1;

    private readonly IAscomDriverFactory _drivers;
    private readonly object _state = new();
    private IReadOnlyList<FilterSlot> _slots = [];
    private FilterWheelMotionState _motion = FilterWheelMotionState.Idle;
    private FilterSlot? _current;

    public AscomFilterWheel(
        DeviceId id, string name, string progId, IAscomDriverFactory drivers, IEventPublisher? events = null, ILogger? logger = null, AscomTimings? timings = null)
        : base(id, name, DeviceType.FilterWheel, progId, events, logger, timings)
    {
        _drivers = drivers;
    }

    public IReadOnlyList<FilterSlot> Slots
    {
        get { lock (_state) { return _slots; } }
    }

    public FilterWheelMotionState MotionState
    {
        get { lock (_state) { return _motion; } }
    }

    /// <summary>The slot in the light path: the one the wheel was at when it connected, or the last one it reached.</summary>
    /// <exception cref="InvalidOperationException">The wheel is not connected.</exception>
    public FilterSlot CurrentSlot
    {
        get
        {
            lock (_state)
            {
                return _current ?? throw new InvalidOperationException($"{Name} is not connected.");
            }
        }
    }

    protected override bool IsBusy => MotionState == FilterWheelMotionState.Moving;

    protected override string BusyDescription => "the filter wheel is moving";

    protected override IAscomFilterWheelDriver CreateDriver() => _drivers.CreateFilterWheel(ProgId);

    protected override void OnConnected(IAscomFilterWheelDriver driver)
    {
        var names = driver.Names;
        if (names is null || names.Count == 0)
        {
            throw new InvalidOperationException($"{Name} ({ProgId}) reports no filters, so it has no slots to turn to.");
        }

        var slots = names.Select((n, i) => new FilterSlot(i, string.IsNullOrWhiteSpace(n) ? $"Filter {i + 1}" : n)).ToList();

        // A wheel that is turning when it connects says -1; it is given a moment to arrive, and rejected when it does not, rather than assumed to be at some slot.
        var position = driver.Position;
        var clock = Stopwatch.StartNew();
        while (position == MovingPosition && clock.Elapsed < Timings.StopWait)
        {
            Thread.Sleep(Timings.FilterWheelPollInterval);
            position = driver.Position;
        }

        if (position < 0 || position >= slots.Count)
        {
            throw new InvalidOperationException(
                position == MovingPosition
                    ? $"{Name} ({ProgId}) is still turning and does not say where it is."
                    : $"{Name} ({ProgId}) reports position {position}, but it has {slots.Count} filters.");
        }

        lock (_state)
        {
            _slots = slots;
            _current = slots[position];
            _motion = FilterWheelMotionState.Idle;
        }

        Logger.LogInformation("{Device}: filter wheel with {Count} filters ({Names}), at {Slot}", Name, slots.Count, string.Join(", ", slots.Select(s => s.Name)), slots[position].Name);
    }

    protected override void OnDisconnected()
    {
        lock (_state)
        {
            _slots = [];
            _current = null;
            _motion = FilterWheelMotionState.Idle;
        }
    }

    /// <exception cref="ArgumentOutOfRangeException">There is no slot with this index.</exception>
    /// <exception cref="InvalidOperationException">The wheel is not connected or is already moving.</exception>
    public async Task MoveToSlotAsync(int slotIndex, CancellationToken cancellationToken = default)
    {
        Session();
        FilterSlot started;
        FilterSlot target;
        lock (_state)
        {
            if (slotIndex < 0 || slotIndex >= _slots.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex), slotIndex, $"Filter slot must be between 0 and {_slots.Count - 1}.");
            }

            if (_motion == FilterWheelMotionState.Moving)
            {
                throw new InvalidOperationException("The filter wheel is already moving.");
            }

            _motion = FilterWheelMotionState.Moving;
            started = _current!;
            target = _slots[slotIndex];
        }

        using var scope = BeginScope();
        var announced = false;
        var reached = started;
        try
        {
            // Where the wheel is now, not where it was left: it may have been turned by hand or by another program.
            var position = await ReadSlotAsync(cancellationToken);
            if (position is { } now)
            {
                started = reached = Slots[now];
                SetCurrent(started);
            }

            if (started.Index == slotIndex)
            {
                return;
            }

            Logger.LogInformation("Turning {Device} from {From} to {To}", Name, started.Name, target.Name);
            announced = true;
            await PublishAsync(new FilterWheelMotionStateChanged(Id, FilterWheelMotionState.Idle, FilterWheelMotionState.Moving, started), cancellationToken);
            await CallAsync("move", d => d.Position = slotIndex, cancellationToken);

            var final = await WaitForArrivalAsync(slotIndex, cancellationToken);
            reached = Slots[final];
            SetCurrent(reached);
            Logger.LogInformation("{Device} arrived at {Slot}", Name, reached.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A filter wheel cannot be stopped: wait for it, so that what is shown is where it is.
            Logger.LogInformation("Move of {Device} cancelled; ASCOM has no halt for a filter wheel, so waiting for it to stop turning", Name);
            if (announced && await SettleQuietlyAsync() is { } settled)
            {
                reached = Slots[settled];
                SetCurrent(reached);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "Move of {Device} failed", Name);
            if (announced && await SettleQuietlyAsync() is { } settled)
            {
                reached = Slots[settled];
                SetCurrent(reached);
            }

            throw;
        }
        finally
        {
            lock (_state)
            {
                _motion = FilterWheelMotionState.Idle;
            }

            if (announced)
            {
                if (reached.Index != started.Index)
                {
                    await PublishAsync(new FilterWheelSlotChanged(Id, started, reached));
                }

                await PublishAsync(new FilterWheelMotionStateChanged(Id, FilterWheelMotionState.Moving, FilterWheelMotionState.Idle, reached));
            }

        }
    }

    // Reads Position until the wheel says it is no longer moving; the wheel is at the target when it does, anything else is an error. Two reads of the same other
    // position in a row are needed to say so, because a driver may still show the old position for a moment after it was told to turn.
    private async Task<int> WaitForArrivalAsync(int target, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var other = 0;
        while (true)
        {
            await Task.Delay(Timings.FilterWheelPollInterval, cancellationToken);
            var position = await CallAsync("poll", d => d.Position, cancellationToken);
            if (position == target)
            {
                return position;
            }

            other = position == MovingPosition ? 0 : other + 1;
            if (other >= 2)
            {
                var wheelAt = position >= 0 && position < Slots.Count ? Slots[position].Name : $"position {position}";
                throw new AscomDeviceException(
                    Id, ProgId, "move", $"{Name} ({ProgId}) stopped at {wheelAt} instead of {Slots[target].Name}.");
            }

            if (clock.Elapsed > Timings.FilterWheelMoveTimeout)
            {
                throw new AscomTimeoutException(
                    Id, ProgId, "move",
                    $"{Name} did not arrive at {Slots[target].Name} within {Timings.FilterWheelMoveTimeout.TotalSeconds:0} s. " +
                    "A filter wheel cannot be stopped: it may still be turning, and the filter in the light path is unknown until it stops.");
            }
        }
    }

    private async Task<int?> ReadSlotAsync(CancellationToken cancellationToken)
    {
        var position = await CallAsync("read the position of", d => d.Position, cancellationToken);
        return position >= 0 && position < Slots.Count ? position : null;
    }

    // After a cancelled or failed move: waits for the wheel to stop turning (bounded) and says where it is, or null when that cannot be told.
    private async Task<int?> SettleQuietlyAsync()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < Timings.FilterWheelMoveTimeout)
            {
                var position = await CallAsync("poll", d => d.Position, CancellationToken.None).WaitAsync(Timings.StopWait);
                if (position != MovingPosition)
                {
                    return position >= 0 && position < Slots.Count ? position : null;
                }

                await Task.Delay(Timings.FilterWheelPollInterval);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: could not tell where the filter wheel stopped: {Reason}", Name, ex.Message);
        }

        return null;
    }

    private void SetCurrent(FilterSlot slot)
    {
        lock (_state)
        {
            _current = slot;
        }
    }
}

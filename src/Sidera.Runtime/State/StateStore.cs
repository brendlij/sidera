using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Runtime.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.State;

/// <summary>
/// The latest known state of every device, kept up to date from the events on the bus. Every change is logged at Trace
/// only: state changes are frequent (positions, motion) and the interesting ones are logged where they happen.
/// </summary>
public sealed class StateStore : IDisposable
{
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, DeviceState> _states = new();
    private readonly IDisposable _connectionSubscription;
    private readonly IDisposable _exposureSubscription;
    private readonly IDisposable _mountSubscription;
    private readonly IDisposable _guidingSubscription;
    private readonly IDisposable _focuserMotionSubscription;
    private readonly IDisposable _focuserPositionSubscription;
    private readonly IDisposable _filterWheelMotionSubscription;
    private readonly IDisposable _filterWheelSlotSubscription;

    public StateStore(EventBus eventBus, ILogger<StateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _logger = logger ?? NullLogger<StateStore>.Instance;

        _connectionSubscription = eventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
        _exposureSubscription = eventBus.Subscribe<CameraExposureStateChanged>(OnExposureStateChanged);
        _mountSubscription = eventBus.Subscribe<MountMotionStateChanged>(OnMountMotionStateChanged);
        _guidingSubscription = eventBus.Subscribe<GuidingStateChanged>(OnGuidingStateChanged);
        _focuserMotionSubscription = eventBus.Subscribe<FocuserMotionStateChanged>(OnFocuserMotionStateChanged);
        _focuserPositionSubscription = eventBus.Subscribe<FocuserPositionChanged>(OnFocuserPositionChanged);
        _filterWheelMotionSubscription = eventBus.Subscribe<FilterWheelMotionStateChanged>(OnFilterWheelMotionStateChanged);
        _filterWheelSlotSubscription = eventBus.Subscribe<FilterWheelSlotChanged>(OnFilterWheelSlotChanged);
    }

    public bool TryGet(DeviceId id, out DeviceState? state)
    {
        lock (_gate)
        {
            return _states.TryGetValue(id, out state);
        }
    }

    /// <summary>Forgets what is known about a device that left the runtime, so that a device added later under the same id starts clean.</summary>
    public bool Remove(DeviceId id)
    {
        lock (_gate)
        {
            return _states.Remove(id);
        }
    }

    public IReadOnlyCollection<DeviceState> GetAll()
    {
        lock (_gate)
        {
            return _states.Values.ToArray();
        }
    }

    public void Dispose()
    {
        _connectionSubscription.Dispose();
        _exposureSubscription.Dispose();
        _mountSubscription.Dispose();
        _guidingSubscription.Dispose();
        _focuserMotionSubscription.Dispose();
        _focuserPositionSubscription.Dispose();
        _filterWheelMotionSubscription.Dispose();
        _filterWheelSlotSubscription.Dispose();
    }

    private Task OnConnectionStateChanged(
        DeviceConnectionStateChanged e,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace("State of {DeviceId}: connection {PreviousState} -> {NewState}", e.DeviceId, e.PreviousState, e.NewState);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, e.NewState);
            _states[e.DeviceId] = current with { ConnectionState = e.NewState };
        }

        return Task.CompletedTask;
    }

    private Task OnMountMotionStateChanged(
        MountMotionStateChanged e,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace("State of {DeviceId}: mount {NewState}", e.DeviceId, e.NewState);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { MotionState = e.NewState, Coordinates = e.Coordinates };
        }

        return Task.CompletedTask;
    }

    private Task OnFocuserMotionStateChanged(FocuserMotionStateChanged e, CancellationToken cancellationToken)
    {
        _logger.LogTrace("State of {DeviceId}: focuser {NewState} at {Position}", e.DeviceId, e.NewState, e.Position);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { FocuserMotionState = e.NewState, FocuserPosition = e.Position };
        }

        return Task.CompletedTask;
    }

    private Task OnFocuserPositionChanged(FocuserPositionChanged e, CancellationToken cancellationToken)
    {
        _logger.LogTrace("State of {DeviceId}: focuser position {Position}", e.DeviceId, e.Position);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { FocuserPosition = e.Position };
        }

        return Task.CompletedTask;
    }

    private Task OnFilterWheelMotionStateChanged(FilterWheelMotionStateChanged e, CancellationToken cancellationToken)
    {
        _logger.LogTrace("State of {DeviceId}: filter wheel {NewState}", e.DeviceId, e.NewState);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { FilterWheelMotionState = e.NewState, FilterSlot = e.Slot };
        }

        return Task.CompletedTask;
    }

    private Task OnFilterWheelSlotChanged(FilterWheelSlotChanged e, CancellationToken cancellationToken)
    {
        _logger.LogTrace("State of {DeviceId}: filter wheel slot changed", e.DeviceId);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { FilterSlot = e.Slot };
        }

        return Task.CompletedTask;
    }

    private Task OnGuidingStateChanged(
        GuidingStateChanged e,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace("State of {DeviceId}: guiding {NewState}", e.DeviceId, e.NewState);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { GuidingState = e.NewState };
        }

        return Task.CompletedTask;
    }

    private Task OnExposureStateChanged(
        CameraExposureStateChanged e,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace("State of {DeviceId}: exposure {NewState}", e.DeviceId, e.NewState);
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { ExposureState = e.NewState };
        }

        return Task.CompletedTask;
    }
}

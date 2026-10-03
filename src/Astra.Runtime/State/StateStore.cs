using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Focusers;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Runtime.Events;

namespace Astra.Runtime.State;

public sealed class StateStore : IDisposable
{
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

    public StateStore(EventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);

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
        lock (_gate)
        {
            var current = _states.GetValueOrDefault(e.DeviceId)
                ?? new DeviceState(e.DeviceId, DeviceConnectionState.Disconnected);
            _states[e.DeviceId] = current with { ExposureState = e.NewState };
        }

        return Task.CompletedTask;
    }
}

using Astra.Core.Devices;
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

    public StateStore(EventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);

        _connectionSubscription = eventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
        _exposureSubscription = eventBus.Subscribe<CameraExposureStateChanged>(OnExposureStateChanged);
        _mountSubscription = eventBus.Subscribe<MountMotionStateChanged>(OnMountMotionStateChanged);
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

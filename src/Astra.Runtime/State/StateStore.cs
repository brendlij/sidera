using Astra.Core.Devices;
using Astra.Runtime.Events;

namespace Astra.Runtime.State;

public sealed class StateStore : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, DeviceState> _states = new();
    private readonly IDisposable _subscription;

    public StateStore(EventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);

        _subscription = eventBus.Subscribe<DeviceConnectionStateChanged>(OnConnectionStateChanged);
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
        _subscription.Dispose();
    }

    private Task OnConnectionStateChanged(
        DeviceConnectionStateChanged e,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            _states[e.DeviceId] = new DeviceState(e.DeviceId, e.NewState);
        }

        return Task.CompletedTask;
    }
}

using Sidera.Core.Devices;

namespace Sidera.Runtime.Devices;

public sealed class DeviceRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<DeviceId, IDevice> _devices = new();

    public void Register(IDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);

        lock (_gate)
        {
            if (!_devices.TryAdd(device.Id, device))
            {
                throw new InvalidOperationException(
                    $"A device with ID '{device.Id}' is already registered."
                );
            }
        }
    }

    public bool Unregister(DeviceId id)
    {
        lock (_gate)
        {
            return _devices.Remove(id);
        }
    }

    public bool TryGet(DeviceId id, out IDevice? device)
    {
        lock (_gate)
        {
            return _devices.TryGetValue(id, out device);
        }
    }

    public IReadOnlyCollection<IDevice> GetAll()
    {
        lock (_gate)
        {
            return _devices.Values.ToArray();
        }
    }
}

using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

public sealed class DisconnectDeviceAction : ISequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _deviceId;

    public DisconnectDeviceAction(DeviceRegistry registry, DeviceId deviceId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _deviceId = deviceId;
    }

    public string Name => $"Disconnect {_deviceId}";

    public Task ExecuteAsync(CancellationToken cancellationToken)
    {
        return DeviceLookup.Resolve<IDevice>(_registry, _deviceId, "device").DisconnectAsync(cancellationToken);
    }
}

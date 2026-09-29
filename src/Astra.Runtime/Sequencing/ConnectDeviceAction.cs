using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

public sealed class ConnectDeviceAction : ISequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _deviceId;

    public ConnectDeviceAction(DeviceRegistry registry, DeviceId deviceId)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _deviceId = deviceId;
    }

    public string Name => $"Connect {_deviceId}";

    public async Task<SequenceStepResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        await DeviceLookup.Resolve<IDevice>(_registry, _deviceId, "device").ConnectAsync(cancellationToken);
        return new SequenceStepResult();
    }
}

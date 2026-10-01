using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

public sealed class ConnectDeviceAction : IResourceAwareSequenceStep
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

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_deviceId)];

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        await DeviceLookup.Resolve<IDevice>(_registry, _deviceId, "device").ConnectAsync(cancellationToken);
        return new SequenceStepResult();
    }
}

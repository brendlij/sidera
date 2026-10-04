using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Starts guiding on a connected guider; it never connects the guider. Requires exclusive use of the
/// guider device only while the start command runs: once guiding has started the step completes and its
/// lease is released, while guiding stays active until a <see cref="StopGuidingAction"/> or a disconnect.
/// Guiders are shared devices addressed by ID, not part of a rig.
/// </summary>
public sealed class StartGuidingAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _guiderId;

    public StartGuidingAction(DeviceRegistry registry, DeviceId guiderId)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _guiderId = guiderId;
    }

    public string Name => "Start guiding";

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_guiderId)];

    /// <exception cref="InvalidOperationException">The guider is unknown, not a guider, or not connected.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, _guiderId, "guider");

        if (guider.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Guider '{_guiderId}' is not connected.");
        }

        await guider.StartGuidingAsync(cancellationToken);
        return new SequenceStepResult();
    }
}

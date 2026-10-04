using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Stops guiding on a connected guider; it never connects or disconnects the guider. Requires exclusive
/// use of the guider device while the stop command runs.
/// </summary>
public sealed class StopGuidingAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _guiderId;

    public StopGuidingAction(DeviceRegistry registry, DeviceId guiderId)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _guiderId = guiderId;
    }

    public string Name => "Stop guiding";

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_guiderId)];

    /// <exception cref="InvalidOperationException">The guider is unknown, not a guider, or not connected.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, _guiderId, "guider");

        if (guider.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Guider '{_guiderId}' is not connected.");
        }

        await guider.StopGuidingAsync(cancellationToken);
        return new SequenceStepResult();
    }
}

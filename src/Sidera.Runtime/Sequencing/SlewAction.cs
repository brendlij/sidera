using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Slews a connected mount to a target; it never connects the mount. Requires exclusive use of the mount
/// device, so two slews on one mount never overlap, while slews on different mounts may.
/// <para>
/// The mount is a shared resource, not part of a rig. Note that it is only locked against other mount
/// operations: a camera exposing at the same time holds a different resource, so a slew can currently run
/// during an exposure. Keeping a slew (or later a dither) from disturbing running exposures is a question of
/// safe points between branches, not of this resource lock.
/// </para>
/// </summary>
public sealed class SlewAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _mountId;

    public SlewAction(DeviceRegistry registry, DeviceId mountId, CelestialCoordinates target)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(target);

        _registry = registry;
        _mountId = mountId;
        Target = target;
    }

    public CelestialCoordinates Target { get; }

    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"Slew to RA {Target.RightAscensionHours:0.###}h Dec {Target.DeclinationDegrees:+0.###;-0.###;0}°");

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_mountId)];

    /// <exception cref="InvalidOperationException">The mount is unknown, not a mount, or not connected.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var mount = DeviceLookup.Resolve<IMount>(_registry, _mountId, "mount");

        if (mount.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Mount '{_mountId}' is not connected.");
        }

        await mount.SlewToAsync(Target, cancellationToken);
        return new SequenceStepResult();
    }
}

using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Moves a connected focuser to an absolute position; it never connects the focuser. Requires exclusive use of the
/// focuser device and nothing else: a camera of the same rig can keep exposing, and other rigs are not touched.
/// </summary>
public sealed class MoveFocuserAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;

    public MoveFocuserAction(DeviceRegistry registry, DeviceId focuserId, int target)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        FocuserId = focuserId;
        Target = target;
    }

    public DeviceId FocuserId { get; }

    /// <summary>The absolute position in focuser steps.</summary>
    public int Target { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Move focuser to {Target}");

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(FocuserId)];

    /// <summary>Returns a result whose payload is the final position (an <see cref="int"/>).</summary>
    /// <exception cref="InvalidOperationException">The focuser is unknown, not a focuser, or not connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The target is outside the range of the focuser.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var focuser = DeviceLookup.ResolveAbsoluteFocuser(_registry, FocuserId);

        if (focuser.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Focuser '{FocuserId}' is not connected.");
        }

        await focuser.MoveToAsync(Target, cancellationToken);
        return new SequenceStepResult(focuser.Position);
    }
}

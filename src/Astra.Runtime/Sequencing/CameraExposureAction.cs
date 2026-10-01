using System.Globalization;
using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Takes one exposure with a camera that is already connected; it never connects or disconnects.
/// </summary>
public sealed class CameraExposureAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _deviceId;

    public CameraExposureAction(DeviceRegistry registry, DeviceId deviceId, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        _registry = registry;
        _deviceId = deviceId;
        Duration = duration;
    }

    public TimeSpan Duration { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Exposure {Duration.TotalSeconds:0.##}s");

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_deviceId)];

    /// <summary>Returns a result whose payload is the <see cref="CameraFrame"/> of this execution.</summary>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, _deviceId, "camera");

        var frame = await camera.ExposeAsync(Duration, cancellationToken);
        return new SequenceStepResult(frame);
    }
}

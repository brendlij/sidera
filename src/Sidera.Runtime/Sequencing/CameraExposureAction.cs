using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Takes one exposure with a camera that is already connected; it never connects or disconnects.
/// </summary>
public sealed class CameraExposureAction : IResourceAwareSequenceStep, IClaimingSequenceStep
{
    private readonly DeviceRegistry _registry;
    private readonly DeviceId _deviceId;

    private readonly AcquisitionIntent _intent;
    private readonly IAcquisitionDefaultsSource? _defaults;
    private readonly ILogger? _logger;

    /// <param name="intent">What the exposure asks of the camera besides its duration; nothing set means the defaults of the camera.</param>
    public CameraExposureAction(
        DeviceRegistry registry,
        DeviceId deviceId,
        TimeSpan duration,
        AcquisitionIntent? intent = null,
        IAcquisitionDefaultsSource? defaults = null,
        ILogger? logger = null,
        DeviceId? mountId = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

        MountId = mountId;
        _registry = registry;
        _deviceId = deviceId;
        Duration = duration;
        _intent = intent ?? AcquisitionIntent.Default;
        _defaults = defaults;
        _logger = logger;
    }

    public AcquisitionIntent Intent => _intent;

    public TimeSpan Duration { get; }

    /// <summary>The camera this action exposes with.</summary>
    public DeviceId CameraId => _deviceId;

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Exposure {Duration.TotalSeconds:0.##}s");

    /// <summary>The mount this camera is carried by, when it is known: while the exposure runs the mount must stand still.</summary>
    public DeviceId? MountId { get; }

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_deviceId)];

    /// <summary>The camera, alone; and the stability of its mount, shared with every other exposure on that mount (many cameras can expose together; nothing that moves the mount can).</summary>
    public IReadOnlyCollection<ResourceClaim> Claims =>
        MountId is { } mount
            ? [ResourceClaim.Exclusive(ResourceId.ForDevice(_deviceId)), ResourceClaim.Shared(ResourceId.ForMountStability(mount))]
            : [ResourceClaim.Exclusive(ResourceId.ForDevice(_deviceId))];

    /// <summary>Returns a result whose payload is the <see cref="CameraFrame"/> of this execution.</summary>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, _deviceId, "camera");

        var frame = await AcquisitionExposer.ExposeAsync(camera, Duration, _intent, _defaults, _logger, cancellationToken);
        return new SequenceStepResult(frame);
    }
}

using System.Globalization;
using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using Microsoft.Extensions.Logging;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Takes one exposure with a camera that is already connected; it never connects or disconnects.
/// </summary>
public sealed class CameraExposureAction : IResourceAwareSequenceStep
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
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);

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

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_deviceId)];

    /// <summary>Returns a result whose payload is the <see cref="CameraFrame"/> of this execution.</summary>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, _deviceId, "camera");

        var frame = await AcquisitionExposer.ExposeAsync(camera, Duration, _intent, _defaults, _logger, cancellationToken);
        return new SequenceStepResult(frame);
    }
}

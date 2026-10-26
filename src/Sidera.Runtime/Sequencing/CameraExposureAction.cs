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
    private readonly IFrameSink? _frameSink;
    private readonly FrameLabel? _label;

    /// <param name="intent">What the exposure asks of the camera besides its duration; nothing set means the defaults of the camera.</param>
    public CameraExposureAction(
        DeviceRegistry registry,
        DeviceId deviceId,
        TimeSpan duration,
        AcquisitionIntent? intent = null,
        IAcquisitionDefaultsSource? defaults = null,
        ILogger? logger = null,
        DeviceId? mountId = null,
        IFrameSink? frameSink = null,
        FrameLabel? label = null)
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
        _frameSink = frameSink;
        _label = label;
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

        var capture = BeginCapture();
        CameraFrame frame;
        try
        {
            frame = await AcquisitionExposer.ExposeAsync(camera, Duration, _intent, _defaults, _logger, cancellationToken);
        }
        catch
        {
            Guard(() => capture?.Abandon());
            throw;
        }

        Guard(() => capture?.Complete(frame));
        return new SequenceStepResult(frame);
    }

    // What a sink does with a frame never decides whether the exposure worked.
    private IFrameCapture? BeginCapture()
    {
        try
        {
            return _frameSink?.Begin(_deviceId, _label);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The frame sink could not start a capture; this frame is not saved");
            return null;
        }
    }

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "The frame sink failed; this frame may not be saved");
        }
    }
}

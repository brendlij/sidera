using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Takes one exposure with a camera that is already connected; it never connects or disconnects.
/// </summary>
public sealed class CameraExposureAction : ISequenceStep
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

    /// <summary>The frame of the last successful execution; <c>null</c> until then, and again while a new execution runs.</summary>
    public CameraFrame? Frame { get; private set; }

    public string Name => $"Exposure {Duration.TotalSeconds:0.##}s";

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, _deviceId, "camera");

        Frame = null;
        Frame = await camera.ExposeAsync(Duration, cancellationToken);
    }
}

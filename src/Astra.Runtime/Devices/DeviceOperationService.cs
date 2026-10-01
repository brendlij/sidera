using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Runtime.Resources;
using Astra.Runtime.Sequencing;

namespace Astra.Runtime.Devices;

/// <summary>
/// Direct (manual) device operations. Each call takes the device's resource from the shared
/// <see cref="ResourceManager"/> for the duration of the operation, so it coordinates with sequences and
/// other direct calls on the same device even if the UI does not stop them.
/// <para>
/// Only for the direct path. A sequence step must not call this service: the <see cref="SequenceRunner"/>
/// already holds the step's resources while it runs, and acquiring them a second time would block forever.
/// </para>
/// </summary>
public sealed class DeviceOperationService
{
    private readonly DeviceRegistry _registry;
    private readonly ResourceManager _resources;

    public DeviceOperationService(DeviceRegistry registry, ResourceManager resources)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resources);

        _registry = registry;
        _resources = resources;
    }

    /// <exception cref="InvalidOperationException">The device is not registered.</exception>
    public async Task ConnectAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var device = DeviceLookup.Resolve<IDevice>(_registry, deviceId, "device");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(deviceId)], cancellationToken))
        {
            await device.ConnectAsync(cancellationToken);
        }
    }

    /// <exception cref="InvalidOperationException">The device is not registered.</exception>
    public async Task DisconnectAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var device = DeviceLookup.Resolve<IDevice>(_registry, deviceId, "device");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(deviceId)], cancellationToken))
        {
            await device.DisconnectAsync(cancellationToken);
        }
    }

    /// <exception cref="InvalidOperationException">The device is not registered or is not a camera.</exception>
    public async Task<CameraFrame> ExposeAsync(
        DeviceId cameraId,
        TimeSpan duration,
        CancellationToken cancellationToken = default
    )
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, cameraId, "camera");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(cameraId)], cancellationToken))
        {
            return await camera.ExposeAsync(duration, cancellationToken);
        }
    }
}

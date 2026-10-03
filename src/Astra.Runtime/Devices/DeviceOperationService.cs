using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Focusers;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
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

    /// <exception cref="InvalidOperationException">The device is not registered or is not a mount.</exception>
    public async Task SlewToAsync(
        DeviceId mountId,
        CelestialCoordinates target,
        CancellationToken cancellationToken = default
    )
    {
        var mount = DeviceLookup.Resolve<IMount>(_registry, mountId, "mount");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(mountId)], cancellationToken))
        {
            await mount.SlewToAsync(target, cancellationToken);
        }
    }

    /// <summary>
    /// Moves a focuser to an absolute position. Takes the focuser resource only: a move needs no camera.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a focuser.</exception>
    public async Task MoveFocuserToAsync(DeviceId focuserId, int target, CancellationToken cancellationToken = default)
    {
        var focuser = DeviceLookup.Resolve<IFocuser>(_registry, focuserId, "focuser");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(focuserId)], cancellationToken))
        {
            await focuser.MoveToAsync(target, cancellationToken);
        }
    }

    /// <summary>
    /// Turns a filter wheel to the slot with the given index. Takes the wheel resource only: it needs no camera.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a filter wheel.</exception>
    public async Task MoveFilterWheelToAsync(DeviceId filterWheelId, int slotIndex, CancellationToken cancellationToken = default)
    {
        var wheel = DeviceLookup.Resolve<IFilterWheel>(_registry, filterWheelId, "filter wheel");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(filterWheelId)], cancellationToken))
        {
            await wheel.MoveToSlotAsync(slotIndex, cancellationToken);
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

    /// <summary>
    /// Starts guiding. The guider's resource is held only while the command runs: once guiding has started,
    /// the call completes and releases it while guiding stays active.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a guider.</exception>
    public async Task StartGuidingAsync(DeviceId guiderId, CancellationToken cancellationToken = default)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, guiderId, "guider");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(guiderId)], cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await guider.StartGuidingAsync(cancellationToken);
        }
    }

    /// <exception cref="InvalidOperationException">The device is not registered or is not a guider.</exception>
    public async Task StopGuidingAsync(DeviceId guiderId, CancellationToken cancellationToken = default)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, guiderId, "guider");

        using (await _resources.AcquireAsync([ResourceId.ForDevice(guiderId)], cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await guider.StopGuidingAsync(cancellationToken);
        }
    }
}

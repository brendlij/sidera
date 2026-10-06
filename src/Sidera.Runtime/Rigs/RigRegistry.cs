using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Rigs;

/// <summary>
/// In-memory registry of logical rigs. A rig may only be registered if the devices it refers to
/// are known to the <see cref="DeviceRegistry"/>. Devices are not owned by rigs: several rigs may
/// refer to the same device.
/// </summary>
public sealed class RigRegistry : ISetupSource
{
    private readonly object _gate = new();
    private readonly Dictionary<RigId, Rig> _rigs = new();
    private readonly DeviceRegistry _devices;

    public RigRegistry(DeviceRegistry devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _devices = devices;
    }

    /// <exception cref="InvalidOperationException">
    /// The rig refers to an unknown device, the camera is not an <see cref="ICamera"/>, or the rig ID is taken.
    /// </exception>
    public void Register(Rig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);

        Validate(rig);

        lock (_gate)
        {
            if (!_rigs.TryAdd(rig.Id, rig))
            {
                throw new InvalidOperationException($"A rig with ID '{rig.Id}' is already registered.");
            }
        }
    }

    public bool Unregister(RigId id)
    {
        lock (_gate)
        {
            return _rigs.Remove(id);
        }
    }

    public bool TryGet(RigId id, out Rig? rig)
    {
        lock (_gate)
        {
            return _rigs.TryGetValue(id, out rig);
        }
    }

    public IReadOnlyCollection<Rig> GetAll()
    {
        lock (_gate)
        {
            return _rigs.Values.ToArray();
        }
    }

    private void Validate(Rig rig)
    {
        if (!_devices.TryGet(rig.CameraId, out var camera) || camera is null)
        {
            throw new InvalidOperationException($"Camera device '{rig.CameraId}' is not registered.");
        }

        if (camera is not ICamera)
        {
            throw new InvalidOperationException(
                $"Device '{rig.CameraId}' assigned as camera does not implement ICamera.");
        }

        if (rig.FocuserId is { } focuserId)
        {
            if (!_devices.TryGet(focuserId, out var focuser) || focuser is null)
            {
                throw new InvalidOperationException($"Focuser device '{focuserId}' is not registered.");
            }

            if (focuser is not IFocuser)
            {
                throw new InvalidOperationException(
                    $"Device '{focuserId}' assigned as focuser does not implement IFocuser.");
            }
        }

        if (rig.RotatorId is { } rotatorId)
        {
            if (!_devices.TryGet(rotatorId, out var rotator) || rotator is null)
            {
                throw new InvalidOperationException($"Rotator device '{rotatorId}' is not registered.");
            }

            if (rotator is not Sidera.Core.Rotators.IRotator)
            {
                throw new InvalidOperationException($"Device '{rotatorId}' assigned as rotator does not implement IRotator.");
            }
        }

        if (rig.MountId is { } mountId)
        {
            if (!_devices.TryGet(mountId, out var mount) || mount is null)
            {
                throw new InvalidOperationException($"Mount device '{mountId}' is not registered.");
            }

            if (mount is not Sidera.Core.Mounts.IMount)
            {
                throw new InvalidOperationException($"Device '{mountId}' assigned as mount does not implement IMount.");
            }
        }

        if (rig.GuiderId is { } guiderId)
        {
            if (!_devices.TryGet(guiderId, out var guider) || guider is null)
            {
                throw new InvalidOperationException($"Guider device '{guiderId}' is not registered.");
            }

            if (guider is not Sidera.Core.Guiding.IGuider)
            {
                throw new InvalidOperationException($"Device '{guiderId}' assigned as guider does not implement IGuider.");
            }
        }

        if (rig.FilterWheelId is { } filterWheelId)
        {
            if (!_devices.TryGet(filterWheelId, out var wheel) || wheel is null)
            {
                throw new InvalidOperationException($"Filter wheel device '{filterWheelId}' is not registered.");
            }

            if (wheel is not IFilterWheel)
            {
                throw new InvalidOperationException(
                    $"Device '{filterWheelId}' assigned as filter wheel does not implement IFilterWheel.");
            }
        }
    }
}

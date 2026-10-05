using Sidera.Core.Devices;

namespace Sidera.Core.Rigs;

/// <summary>What a device is for in a rig.</summary>
public enum RigRole
{
    Camera,
    Focuser,
    FilterWheel,
    Rotator,
    Mount,
    Guider,
}

/// <summary>
/// A logical, addressable imaging unit: which devices make up this optical train.
/// It holds device IDs only, never live devices, and no runtime state of any kind.
/// <para>
/// A rig is a camera with what sits on its optical train (focuser, filter wheel, rotator, optics) and, optionally, the mount that carries it and the guider that guides it. The mount and
/// the guider are not global: a rig may have its own, share them with another rig, or have none. "Shared" is not a setting: two rigs share a device when they name the same
/// <see cref="DeviceId"/>, and the resource manager then serializes what they do with it.
/// </para>
/// </summary>
public sealed class Rig
{
    public Rig(
        RigId id,
        string name,
        DeviceId cameraId,
        OpticalTrain? optics = null,
        DeviceId? focuserId = null,
        DeviceId? filterWheelId = null,
        DeviceId? rotatorId = null,
        Rotators.RotatorSkyModel? rotatorModel = null,
        DeviceId? mountId = null,
        DeviceId? guiderId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Name = name.Trim();
        CameraId = cameraId;
        FocuserId = focuserId;
        FilterWheelId = filterWheelId;
        Optics = optics;
        RotatorId = rotatorId;
        RotatorModel = rotatorId is null ? null : rotatorModel;
        MountId = mountId;
        GuiderId = guiderId;
    }

    public RigId Id { get; }
    public string Name { get; }
    public DeviceId CameraId { get; }
    public DeviceId? FocuserId { get; }
    public DeviceId? FilterWheelId { get; }

    /// <summary>The motorized rotator of the rig; <c>null</c> for a rig without one, which is complete as it is.</summary>
    public DeviceId? RotatorId { get; }

    /// <summary>How the position of the rotator relates to the rotation of the sky in the image (a calibration); <c>null</c> until it was calibrated, and never a guess.</summary>
    public Rotators.RotatorSkyModel? RotatorModel { get; }

    /// <summary>The mount that carries this rig; <c>null</c> for a rig without one (it can still image). Another rig may name the same mount: that is what sharing it means.</summary>
    public DeviceId? MountId { get; }

    /// <summary>The guider of this rig; <c>null</c> for a rig that is not guided. Another rig may name the same guider.</summary>
    public DeviceId? GuiderId { get; }

    /// <summary>The configured optics; <c>null</c> for a rig that has none yet.</summary>
    public OpticalTrain? Optics { get; }

    /// <summary>The same rig under another name (everything else is the same).</summary>
    public Rig WithName(string name) => new(Id, name, CameraId, Optics, FocuserId, FilterWheelId, RotatorId, RotatorModel, MountId, GuiderId);

    /// <summary>The same rig with another camera.</summary>
    public Rig WithCamera(DeviceId cameraId) => new(Id, Name, cameraId, Optics, FocuserId, FilterWheelId, RotatorId, RotatorModel, MountId, GuiderId);

    /// <summary>The same rig with other optics (<c>null</c> removes them).</summary>
    public Rig WithOptics(OpticalTrain? optics) => new(Id, Name, CameraId, optics, FocuserId, FilterWheelId, RotatorId, RotatorModel, MountId, GuiderId);

    public Rig WithFocuser(DeviceId? focuserId) => new(Id, Name, CameraId, Optics, focuserId, FilterWheelId, RotatorId, RotatorModel, MountId, GuiderId);

    public Rig WithFilterWheel(DeviceId? filterWheelId) => new(Id, Name, CameraId, Optics, FocuserId, filterWheelId, RotatorId, RotatorModel, MountId, GuiderId);

    /// <summary>The same rig with another rotator (<c>null</c> removes it, and its calibration with it).</summary>
    public Rig WithRotator(DeviceId? rotatorId, Rotators.RotatorSkyModel? model = null) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, rotatorId, model, MountId, GuiderId);

    /// <summary>The same rig with another calibration of its rotator.</summary>
    public Rig WithRotatorModel(Rotators.RotatorSkyModel? model) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, RotatorId, model, MountId, GuiderId);

    /// <summary>The same rig on another mount (<c>null</c> takes it off any mount).</summary>
    public Rig WithMount(DeviceId? mountId) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, RotatorId, RotatorModel, mountId, GuiderId);

    /// <summary>The same rig with another guider (<c>null</c> makes it unguided).</summary>
    public Rig WithGuider(DeviceId? guiderId) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, RotatorId, RotatorModel, MountId, guiderId);

    /// <summary>The devices of this rig and what each is for, in the order they are shown: camera, focuser, filter wheel, rotator, mount, guider. A role without a device is left out.</summary>
    public IEnumerable<(RigRole Role, DeviceId Device)> Devices()
    {
        yield return (RigRole.Camera, CameraId);
        if (FocuserId is { } focuser) yield return (RigRole.Focuser, focuser);
        if (FilterWheelId is { } wheel) yield return (RigRole.FilterWheel, wheel);
        if (RotatorId is { } rotator) yield return (RigRole.Rotator, rotator);
        if (MountId is { } mount) yield return (RigRole.Mount, mount);
        if (GuiderId is { } guider) yield return (RigRole.Guider, guider);
    }

    /// <summary>The device the rig has for a role; <c>null</c> when it has none.</summary>
    public DeviceId? DeviceFor(RigRole role) => role switch
    {
        RigRole.Camera => CameraId,
        RigRole.Focuser => FocuserId,
        RigRole.FilterWheel => FilterWheelId,
        RigRole.Rotator => RotatorId,
        RigRole.Mount => MountId,
        RigRole.Guider => GuiderId,
        _ => null,
    };

    /// <summary>The same rig with the device of a role replaced (<c>null</c> clears an optional role; the camera cannot be cleared).</summary>
    public Rig WithDevice(RigRole role, DeviceId? device) => role switch
    {
        RigRole.Camera => device is { } camera ? WithCamera(camera) : throw new ArgumentException("A rig needs a camera.", nameof(device)),
        RigRole.Focuser => WithFocuser(device),
        RigRole.FilterWheel => WithFilterWheel(device),
        RigRole.Rotator => RotatorId == device ? this : WithRotator(device),
        RigRole.Mount => WithMount(device),
        RigRole.Guider => WithGuider(device),
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };
}

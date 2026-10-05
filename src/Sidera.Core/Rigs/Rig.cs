using Sidera.Core.Devices;

namespace Sidera.Core.Rigs;

/// <summary>
/// A logical, addressable imaging unit: which devices make up this optical train.
/// It holds device IDs only, never live devices, and no runtime state of any kind.
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
        Rotators.RotatorSkyModel? rotatorModel = null
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
    /// <summary>The configured optics; <c>null</c> for a rig that has none yet.</summary>
    public OpticalTrain? Optics { get; }

    /// <summary>The same rig with other optics (<c>null</c> removes them).</summary>
    public Rig WithOptics(OpticalTrain? optics) => new(Id, Name, CameraId, optics, FocuserId, FilterWheelId, RotatorId, RotatorModel);

    /// <summary>The same rig with another rotator (<c>null</c> removes it, and its calibration with it).</summary>
    public Rig WithRotator(DeviceId? rotatorId, Rotators.RotatorSkyModel? model = null) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, rotatorId, model);

    /// <summary>The same rig with another calibration of its rotator.</summary>
    public Rig WithRotatorModel(Rotators.RotatorSkyModel? model) => new(Id, Name, CameraId, Optics, FocuserId, FilterWheelId, RotatorId, model);
}

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
        DeviceId? filterWheelId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = id;
        Name = name.Trim();
        CameraId = cameraId;
        FocuserId = focuserId;
        FilterWheelId = filterWheelId;
        Optics = optics;
    }

    public RigId Id { get; }
    public string Name { get; }
    public DeviceId CameraId { get; }
    public DeviceId? FocuserId { get; }
    public DeviceId? FilterWheelId { get; }
    /// <summary>The configured optics; <c>null</c> for a rig that has none yet.</summary>
    public OpticalTrain? Optics { get; }

    /// <summary>The same rig with other optics (<c>null</c> removes them).</summary>
    public Rig WithOptics(OpticalTrain? optics) => new(Id, Name, CameraId, optics, FocuserId, FilterWheelId);
}

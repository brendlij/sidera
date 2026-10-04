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
        OpticalTrain optics,
        DeviceId? focuserId = null,
        DeviceId? filterWheelId = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(optics);

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
    public OpticalTrain Optics { get; }
}

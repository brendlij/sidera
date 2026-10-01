using Astra.Core.Mounts;

namespace Astra.Core.Devices;

/// <param name="ExposureState">Only set for cameras, <c>null</c> for other devices.</param>
/// <param name="MotionState">Only set for mounts, <c>null</c> for other devices.</param>
/// <param name="Coordinates">Only set for mounts, <c>null</c> for other devices.</param>
public sealed record DeviceState(
    DeviceId DeviceId,
    DeviceConnectionState ConnectionState,
    CameraExposureState? ExposureState = null,
    MountMotionState? MotionState = null,
    CelestialCoordinates? Coordinates = null
);

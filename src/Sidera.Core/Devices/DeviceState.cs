using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;

namespace Sidera.Core.Devices;

/// <param name="ExposureState">Only set for cameras, <c>null</c> for other devices.</param>
/// <param name="MotionState">Only set for mounts, <c>null</c> for other devices.</param>
/// <param name="Coordinates">Only set for mounts, <c>null</c> for other devices.</param>
/// <param name="GuidingState">Only set for guiders whose guiding state has been observed, <c>null</c> otherwise.</param>
/// <param name="FocuserMotionState">Only set for focusers whose motion has been observed, <c>null</c> otherwise.</param>
/// <param name="FocuserPosition">Only set for focusers whose position has been observed, <c>null</c> otherwise.</param>
/// <param name="FilterWheelMotionState">Only set for filter wheels whose motion has been observed, <c>null</c> otherwise.</param>
/// <param name="FilterSlot">Only set for filter wheels whose slot has been observed, <c>null</c> otherwise.</param>
public sealed record DeviceState(
    DeviceId DeviceId,
    DeviceConnectionState ConnectionState,
    CameraExposureState? ExposureState = null,
    MountMotionState? MotionState = null,
    CelestialCoordinates? Coordinates = null,
    GuidingState? GuidingState = null,
    FocuserMotionState? FocuserMotionState = null,
    int? FocuserPosition = null,
    FilterWheelMotionState? FilterWheelMotionState = null,
    FilterSlot? FilterSlot = null
);

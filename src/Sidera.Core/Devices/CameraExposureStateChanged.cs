using Sidera.Core.Events;

namespace Sidera.Core.Devices;

public sealed record CameraExposureStateChanged(
    DeviceId DeviceId,
    CameraExposureState PreviousState,
    CameraExposureState NewState
) : ISideraEvent;

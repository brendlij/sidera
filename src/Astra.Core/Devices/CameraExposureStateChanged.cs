using Astra.Core.Events;

namespace Astra.Core.Devices;

public sealed record CameraExposureStateChanged(
    DeviceId DeviceId,
    CameraExposureState PreviousState,
    CameraExposureState NewState
) : IAstraEvent;

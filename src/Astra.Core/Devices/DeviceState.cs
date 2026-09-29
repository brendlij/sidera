namespace Astra.Core.Devices;

/// <param name="ExposureState">Only set for cameras, <c>null</c> for other devices.</param>
public sealed record DeviceState(
    DeviceId DeviceId,
    DeviceConnectionState ConnectionState,
    CameraExposureState? ExposureState = null
);

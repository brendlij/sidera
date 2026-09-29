using Astra.Core.Events;

namespace Astra.Core.Devices;

public sealed record DeviceConnectionStateChanged(
    DeviceId DeviceId,
    DeviceConnectionState PreviousState,
    DeviceConnectionState NewState
) : IAstraEvent;

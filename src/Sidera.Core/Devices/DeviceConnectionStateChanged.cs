using Sidera.Core.Events;

namespace Sidera.Core.Devices;

public sealed record DeviceConnectionStateChanged(
    DeviceId DeviceId,
    DeviceConnectionState PreviousState,
    DeviceConnectionState NewState
) : ISideraEvent;

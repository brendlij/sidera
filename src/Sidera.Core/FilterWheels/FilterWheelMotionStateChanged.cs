using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.FilterWheels;

/// <summary>A filter wheel started or stopped turning. <paramref name="Slot"/> is the slot in the light path at the time of the change.</summary>
public sealed record FilterWheelMotionStateChanged(
    DeviceId DeviceId,
    FilterWheelMotionState PreviousState,
    FilterWheelMotionState NewState,
    FilterSlot Slot
) : ISideraEvent;

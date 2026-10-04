using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.FilterWheels;

/// <summary>A filter wheel arrived at another slot. Published once when a move has reached its target.</summary>
public sealed record FilterWheelSlotChanged(
    DeviceId DeviceId,
    FilterSlot PreviousSlot,
    FilterSlot Slot
) : ISideraEvent;

using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Focusers;

/// <summary>A focuser started or stopped moving. <paramref name="Position"/> is where it stands at the time of the change.</summary>
public sealed record FocuserMotionStateChanged(
    DeviceId DeviceId,
    FocuserMotionState PreviousState,
    FocuserMotionState NewState,
    int Position
) : ISideraEvent;

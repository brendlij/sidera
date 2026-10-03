using Astra.Core.Devices;
using Astra.Core.Events;

namespace Astra.Core.Focusers;

/// <summary>A focuser started or stopped moving. <paramref name="Position"/> is where it stands at the time of the change.</summary>
public sealed record FocuserMotionStateChanged(
    DeviceId DeviceId,
    FocuserMotionState PreviousState,
    FocuserMotionState NewState,
    int Position
) : IAstraEvent;

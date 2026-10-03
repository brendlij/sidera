using Astra.Core.Devices;
using Astra.Core.Events;

namespace Astra.Core.Focusers;

/// <summary>
/// A focuser arrived at a new position. Low-frequency by design: published once when a move has reached its target,
/// not as telemetry while moving.
/// </summary>
public sealed record FocuserPositionChanged(
    DeviceId DeviceId,
    int PreviousPosition,
    int Position
) : IAstraEvent;

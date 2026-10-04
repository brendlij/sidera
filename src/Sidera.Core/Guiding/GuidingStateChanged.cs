using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Guiding;

/// <summary>
/// A guider changed its guiding state (for example Idle → Starting → Guiding). Low-frequency by design:
/// no guiding telemetry is carried.
/// </summary>
public sealed record GuidingStateChanged(
    DeviceId DeviceId,
    GuidingState PreviousState,
    GuidingState NewState
) : ISideraEvent;

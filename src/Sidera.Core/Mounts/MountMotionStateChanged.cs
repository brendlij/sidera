using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Mounts;

/// <summary>
/// A mount changed its motion state (for example Idle → Slewing → Tracking). Low-frequency by design:
/// there is no position telemetry while slewing.
/// </summary>
/// <param name="Coordinates">Where the mount points at the time of the change (the start position while slewing).</param>
public sealed record MountMotionStateChanged(
    DeviceId DeviceId,
    MountMotionState PreviousState,
    MountMotionState NewState,
    CelestialCoordinates Coordinates
) : ISideraEvent;

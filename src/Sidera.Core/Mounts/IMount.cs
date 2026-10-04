using Sidera.Core.Devices;

namespace Sidera.Core.Mounts;

public interface IMount : IDevice
{
    MountMotionState MotionState { get; }

    /// <summary>Where the mount points: its start position, or the target of the last slew it completed.</summary>
    CelestialCoordinates Coordinates { get; }

    /// <summary>
    /// Moves to <paramref name="target"/>. Completes when the mount has arrived and is tracking.
    /// Cancelling throws <see cref="OperationCanceledException"/>; the target was then not reached.
    /// </summary>
    Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default);
}

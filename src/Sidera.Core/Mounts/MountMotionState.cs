namespace Sidera.Core.Mounts;

public enum MountMotionState
{
    /// <summary>Stopped and not tracking.</summary>
    Idle,

    /// <summary>Moving to a target.</summary>
    Slewing,

    /// <summary>At its coordinates and following the sky.</summary>
    Tracking
}

namespace Sidera.Core.Guiding;

/// <summary>
/// Lifecycle of a guider's guiding loop, independent of its connection state. <c>Guiding</c> only says that
/// the loop is running: not that the RMS is acceptable, that calibration succeeded or that it is safe to expose.
/// </summary>
public enum GuidingState
{
    /// <summary>Not guiding.</summary>
    Idle,

    /// <summary>Guiding is being started.</summary>
    Starting,

    /// <summary>The guiding loop is active.</summary>
    Guiding,

    /// <summary>Guiding is being stopped.</summary>
    Stopping,

    /// <summary>
    /// A dither command is running; guiding returns to <c>Guiding</c> when it has finished. Leaving this state
    /// does not mean that guiding has settled.
    /// </summary>
    Dithering,

    /// <summary>The guider takes exposures and shows the sky, without a star selected for guiding or guiding corrections.</summary>
    Looping,

    /// <summary>A guide star is selected; the guider neither loops, calibrates nor guides.</summary>
    StarSelected,

    /// <summary>The guider is calibrating: it learns how the mount answers to guide pulses. Owned by the guider, not by Sidera.</summary>
    Calibrating,

    /// <summary>Guiding is paused: the guider does not correct.</summary>
    Paused,

    /// <summary>After a dither or the start of guiding the guider waits for the guide error to settle; the telemetry says how far it is.</summary>
    Settling,

    /// <summary>The guide star was lost while guiding; the guider is looking for it again.</summary>
    StarLost
}

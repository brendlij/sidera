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
    Dithering
}

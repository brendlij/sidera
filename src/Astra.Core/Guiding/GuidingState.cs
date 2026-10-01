namespace Astra.Core.Guiding;

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
    Stopping
}

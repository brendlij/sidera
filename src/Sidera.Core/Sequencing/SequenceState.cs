namespace Sidera.Core.Sequencing;

public enum SequenceState
{
    Idle,
    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>A pause was requested; branches finish what they are doing and stop at their next boundary.</summary>
    Pausing,

    /// <summary>Every active branch waits at a boundary; nothing new starts until the run is resumed.</summary>
    Paused
}

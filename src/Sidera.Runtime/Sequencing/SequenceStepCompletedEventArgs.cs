using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Sequencing;

/// <summary>One successfully completed execution of a step, top-level or nested.</summary>
public sealed class SequenceStepCompletedEventArgs(SequenceExecutionPosition position, SequenceStepResult result)
    : EventArgs
{
    public SequenceExecutionPosition Position { get; } = position;
    public SequenceStepResult Result { get; } = result;

    /// <summary>Index within the parent (the sequence for top-level steps, the repeat for its child).</summary>
    public int StepIndex => Position.Index;

    public string StepName => Position.StepName;
}

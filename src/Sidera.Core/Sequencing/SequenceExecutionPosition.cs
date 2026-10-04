namespace Sidera.Core.Sequencing;

/// <summary>
/// Identifies one execution of a step inside a running sequence: the step, its position within its
/// parent (<see cref="Index"/> of <see cref="Count"/>) and the parent's own position up to the top level.
/// For a top-level step that is its index in the sequence; for the child of a repeat it is the iteration.
/// Execution-specific, so it never lives on the reusable step definition.
/// </summary>
/// <param name="StepName">Name of the step being executed.</param>
/// <param name="Index">Zero-based position within the parent.</param>
/// <param name="Count">Number of positions in the parent (steps in the sequence, or repeat count).</param>
/// <param name="Parent">The enclosing execution, or <c>null</c> for a top-level step.</param>
public sealed record SequenceExecutionPosition(
    string StepName,
    int Index,
    int Count,
    SequenceExecutionPosition? Parent = null
)
{
    /// <summary>The top-level ancestor of this execution (itself if it has no parent).</summary>
    public SequenceExecutionPosition Root => Parent?.Root ?? this;
}

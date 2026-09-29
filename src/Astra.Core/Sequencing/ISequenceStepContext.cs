namespace Astra.Core.Sequencing;

/// <summary>
/// Given to a step by whatever executes it. Container steps run their children through it so that
/// every child execution is tracked, reported and cancellable like a top-level step.
/// </summary>
public interface ISequenceStepContext
{
    /// <summary>
    /// Executes <paramref name="child"/> once as position <paramref name="index"/> of <paramref name="count"/>
    /// within the calling step, and returns the result of that execution.
    /// </summary>
    Task<SequenceStepResult> ExecuteChildAsync(
        ISequenceStep child,
        int index,
        int count,
        CancellationToken cancellationToken
    );
}

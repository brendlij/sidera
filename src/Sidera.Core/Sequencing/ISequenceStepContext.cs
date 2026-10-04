using Sidera.Core.Coordination;

namespace Sidera.Core.Sequencing;

/// <summary>
/// Given to a step by whatever executes it. Container steps run their children through it so that
/// every child execution is tracked, reported and cancellable like a top-level step. It also lets a step
/// take part in coordination between parallel branches without knowing who coordinates.
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

    /// <summary>
    /// Like <see cref="ExecuteChildAsync"/>, for a child that runs next to its siblings. With a
    /// <paramref name="group"/> the child becomes a participant of that coordination group for as long as it
    /// runs (so does everything nested in it). All <paramref name="count"/> branches of one calling step must be
    /// started through this method with the same group; without a group it is the same as ExecuteChildAsync.
    /// </summary>
    Task<SequenceStepResult> ExecuteBranchAsync(
        ISequenceStep child,
        int index,
        int count,
        CoordinationGroupId? group,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Declares the calling step's branch safe to pause. Returns at once if no coordinated operation is
    /// pending (or the step is not inside a coordination group); otherwise waits until it is done.
    /// </summary>
    Task ReachSafePointAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="operation"/> once all other branches of the calling step's coordination group are at
    /// a safe point, then lets them continue. Outside a coordination group the operation just runs.
    /// </summary>
    Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken);
}

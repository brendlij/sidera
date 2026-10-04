using Sidera.Core.Coordination;
using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>
/// Context for executing a leaf step directly, outside a runner. Leaf steps never use it for children;
/// as outside any coordination group, a safe point does nothing and a coordinated operation just runs.
/// </summary>
internal sealed class NoContext : ISequenceStepContext
{
    public static NoContext Instance { get; } = new();

    public Task<SequenceStepResult> ExecuteChildAsync(
        ISequenceStep child,
        int index,
        int count,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public Task<SequenceStepResult> ExecuteBranchAsync(
        ISequenceStep child,
        int index,
        int count,
        CoordinationGroupId? group,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();

    public Task ReachSafePointAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        operation(cancellationToken);
}

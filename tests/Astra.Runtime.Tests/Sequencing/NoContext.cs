using Astra.Core.Sequencing;

namespace Astra.Runtime.Tests.Sequencing;

/// <summary>Context for executing a leaf step directly, outside a runner. Leaf steps never use it.</summary>
internal sealed class NoContext : ISequenceStepContext
{
    public static NoContext Instance { get; } = new();

    public Task<SequenceStepResult> ExecuteChildAsync(
        ISequenceStep child,
        int index,
        int count,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException();
}

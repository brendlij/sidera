using System.Runtime.ExceptionServices;
using Sidera.Core.Coordination;

namespace Sidera.Core.Sequencing;

/// <summary>
/// Lets its children progress concurrently. It decides only that the branches may run side by side;
/// whether two operations may really overlap is left to the resource coordination of each child
/// (a child needing a busy resource simply waits for it). Holds no execution state.
/// </summary>
public sealed class ParallelStep : ISequenceStep
{
    /// <param name="coordinationGroup">
    /// When set, the branches become participants of this coordination group while they run, so that they can
    /// use safe points and coordinated operations. Without it the branches simply run side by side.
    /// </param>
    public ParallelStep(
        string name,
        IEnumerable<ISequenceStep> children,
        CoordinationGroupId? coordinationGroup = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(children);

        var list = children.ToArray();

        if (list.Length < 2)
        {
            throw new ArgumentException("A parallel step needs at least two children.", nameof(children));
        }

        if (list.Any(child => child is null))
        {
            throw new ArgumentException("A parallel step cannot contain null children.", nameof(children));
        }

        Name = name;
        Children = list;
        CoordinationGroup = coordinationGroup;
    }

    public string Name { get; }
    public IReadOnlyList<ISequenceStep> Children { get; }
    public CoordinationGroupId? CoordinationGroup { get; }

    /// <summary>
    /// Starts every child through <paramref name="context"/> and waits for all of them.
    /// <list type="bullet">
    /// <item>All succeed: the result's payload is the list of child results in definition order
    /// (completion notifications still arrive in real completion order).</item>
    /// <item>A child fails: the siblings are cancelled through an internal token, and every branch is awaited
    /// before this method returns. A single failure is rethrown as it was; if several branches failed on their
    /// own, an <see cref="AggregateException"/> lists them in definition order. Cancellation caused by the
    /// failure is never reported as a failure.</item>
    /// <item>The caller cancels: all branches are cancelled and awaited, and an
    /// <see cref="OperationCanceledException"/> is thrown.</item>
    /// </list>
    /// </summary>
    public async Task<SequenceStepResult> ExecuteAsync(
        ISequenceStepContext context,
        CancellationToken cancellationToken
    )
    {
        using var branchesCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var branches = new Task<SequenceStepResult>[Children.Count];
        for (var i = 0; i < branches.Length; i++)
        {
            branches[i] = RunBranchAsync(context, i, branchesCancellation);
        }

        // Wait for every branch, whatever happens to the others; outcomes are inspected below.
        try
        {
            await Task.WhenAll(branches);
        }
        catch
        {
        }

        var failures = branches
            .Where(branch => branch.IsFaulted)
            .Select(branch => branch.Exception!.InnerException ?? branch.Exception!)
            .ToList();

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException(failures);
        }

        if (branches.Any(branch => branch.IsCanceled))
        {
            throw new OperationCanceledException(branchesCancellation.Token);
        }

        return new SequenceStepResult(branches.Select(branch => branch.Result).ToList().AsReadOnly());
    }

    private async Task<SequenceStepResult> RunBranchAsync(
        ISequenceStepContext context,
        int index,
        CancellationTokenSource branchesCancellation
    )
    {
        try
        {
            return await context.ExecuteBranchAsync(
                Children[index], index, Children.Count, CoordinationGroup, branchesCancellation.Token);
        }
        catch (OperationCanceledException) when (branchesCancellation.IsCancellationRequested)
        {
            // Cancelled by the caller or because a sibling failed; not a failure of this branch.
            throw;
        }
        catch
        {
            branchesCancellation.Cancel();
            throw;
        }
    }
}

namespace Sidera.Core.Sequencing;

/// <summary>
/// Executes its children strictly one after another, in the given order. Holds no execution state:
/// each execution of the group produces its own results.
/// </summary>
public sealed class SequenceGroup : ISequenceStep
{
    public SequenceGroup(string name, IEnumerable<ISequenceStep> children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(children);

        var list = children.ToArray();

        if (list.Length == 0)
        {
            throw new ArgumentException("A sequence group needs at least one child.", nameof(children));
        }

        if (list.Any(child => child is null))
        {
            throw new ArgumentException("A sequence group cannot contain null children.", nameof(children));
        }

        Name = name;
        Children = list;
    }

    public string Name { get; }
    public IReadOnlyList<ISequenceStep> Children { get; }

    /// <summary>
    /// Runs the children in order through <paramref name="context"/>, so each one is tracked and reported
    /// like any other execution. Cancellation is checked before every child. A failing or cancelled child
    /// stops the group: later children do not run and the exception propagates.
    /// The result's payload is the list of the child results, in order.
    /// </summary>
    public async Task<SequenceStepResult> ExecuteAsync(
        ISequenceStepContext context,
        CancellationToken cancellationToken
    )
    {
        var results = new List<SequenceStepResult>(Children.Count);

        for (var i = 0; i < Children.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await context.ExecuteChildAsync(Children[i], i, Children.Count, cancellationToken));
        }

        return new SequenceStepResult(results.AsReadOnly());
    }
}

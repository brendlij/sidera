using System.Globalization;

namespace Sidera.Core.Sequencing;

/// <summary>
/// Executes its child step <see cref="Count"/> times, one after another. The same child definition is
/// reused for every iteration; each iteration produces its own result. Holds no execution state.
/// </summary>
public sealed class RepeatStep : ISequenceStep
{
    public RepeatStep(int count, ISequenceStep child, IRepeatControl? control = null)
    {
        Control = control;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);
        ArgumentNullException.ThrowIfNull(child);

        Count = count;
        Child = child;
    }

    /// <summary>What ends the repetition earlier than <see cref="Count"/>, if anything (conditions of a workflow); <c>null</c> for a plain repeat.</summary>
    public IRepeatControl? Control { get; }

    public int Count { get; }
    public ISequenceStep Child { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Repeat × {Count}");

    /// <summary>
    /// Runs the child <see cref="Count"/> times. Cancellation is checked before every iteration and
    /// passed to the child. Any failure or cancellation stops the remaining iterations and propagates.
    /// The result's payload is the list of the child results, in iteration order.
    /// </summary>
    public async Task<SequenceStepResult> ExecuteAsync(
        ISequenceStepContext context,
        CancellationToken cancellationToken
    )
    {
        var results = new List<SequenceStepResult>(Count);
        Control?.Begin();
        try
        {
            for (var i = 0; i < Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Control?.ShouldStop(i) == true)
                {
                    break;
                }

                results.Add(await context.ExecuteChildAsync(Child, i, Count, cancellationToken));
                if (Control?.AfterIteration(i + 1) == true)
                {
                    break;
                }
            }
        }
        finally
        {
            Control?.End(results.Count);
        }

        return new SequenceStepResult(results.AsReadOnly());
    }
}

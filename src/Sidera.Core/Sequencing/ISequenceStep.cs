namespace Sidera.Core.Sequencing;

public interface ISequenceStep
{
    string Name { get; }

    /// <summary>
    /// Executes the step once and returns the result of that execution. Leaf steps ignore
    /// <paramref name="context"/>; container steps run their children through it.
    /// </summary>
    Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken);
}

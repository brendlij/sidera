namespace Astra.Core.Sequencing;

public interface ISequenceStep
{
    string Name { get; }

    /// <summary>Executes the step once and returns the result of that execution.</summary>
    Task<SequenceStepResult> ExecuteAsync(CancellationToken cancellationToken);
}

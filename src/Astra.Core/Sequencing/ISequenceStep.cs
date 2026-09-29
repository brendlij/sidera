namespace Astra.Core.Sequencing;

public interface ISequenceStep
{
    string Name { get; }

    Task ExecuteAsync(CancellationToken cancellationToken);
}

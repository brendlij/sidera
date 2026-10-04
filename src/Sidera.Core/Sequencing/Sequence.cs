namespace Sidera.Core.Sequencing;

public sealed class Sequence
{
    public Sequence(string name, IEnumerable<ISequenceStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);

        Name = name;
        Steps = steps.ToArray();

        if (Steps.Any(step => step is null))
        {
            throw new ArgumentException("A sequence cannot contain null steps.", nameof(steps));
        }
    }

    public string Name { get; }
    public IReadOnlyList<ISequenceStep> Steps { get; }
}

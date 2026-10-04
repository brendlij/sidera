namespace Sidera.Core.Coordination;

/// <summary>
/// Names a set of execution branches that coordinate with each other, for example "session.mount.eq6".
/// Deliberately independent of rigs: a group may later span several rigs, a mount, guiding or a dome.
/// </summary>
public readonly record struct CoordinationGroupId
{
    public string Value { get; }

    public CoordinationGroupId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Coordination group ID cannot be empty or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    public override string ToString()
    {
        return Value;
    }
}

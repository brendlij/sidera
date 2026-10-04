namespace Sidera.Core.Coordination;

/// <summary>
/// Identifies one execution branch inside a coordination group. Created at run time for each execution of a
/// branch (never stored on a sequence definition), so two branches with the same name stay distinct.
/// </summary>
public readonly record struct ParticipantId
{
    public string Value { get; }

    public ParticipantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Participant ID cannot be empty or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    public override string ToString()
    {
        return Value;
    }
}

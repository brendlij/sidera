namespace Astra.Core.Rigs;

/// <summary>Identifies a logical imaging rig inside Astra, for example "rig.main".</summary>
public readonly record struct RigId
{
    public string Value { get; }

    public RigId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Rig ID cannot be empty or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    public override string ToString()
    {
        return Value;
    }
}

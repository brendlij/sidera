namespace Sidera.Core.FilterWheels;

/// <summary>One position of a filter wheel: its index (the identity) and the name the user knows it by.</summary>
public sealed record FilterSlot
{
    public FilterSlot(int index, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Index = index;
        Name = name.Trim();
    }

    /// <summary>Zero-based position on the wheel; the stable identity of the slot.</summary>
    public int Index { get; }

    /// <summary>A free text such as "L", "Ha" or "Clear"; not unique by itself, and never interpreted.</summary>
    public string Name { get; }
}

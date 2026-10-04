namespace Sidera.Core.Devices;

public readonly record struct DeviceId
{
    public string Value { get; }

    public DeviceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Device ID cannot be empty or whitespace.",
                nameof(value)
            );
        }

        Value = value.Trim();
    }

    public override string ToString()
    {
        return Value;
    }
}
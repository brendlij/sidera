using Sidera.Core.Devices;

namespace Sidera.Core.Resources;

/// <summary>
/// Names something an action must control exclusively while it runs, for example "device:camera.main"
/// or "guiding". Resources, not rigs, decide what may run concurrently.
/// </summary>
public readonly record struct ResourceId
{
    public string Value { get; }

    public ResourceId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Resource ID cannot be empty or whitespace.", nameof(value));
        }

        Value = value.Trim();
    }

    /// <summary>The resource representing exclusive use of a device: "camera.main" becomes "device:camera.main".</summary>
    public static ResourceId ForDevice(DeviceId deviceId)
    {
        return new ResourceId($"device:{deviceId.Value}");
    }

    public override string ToString()
    {
        return Value;
    }
}

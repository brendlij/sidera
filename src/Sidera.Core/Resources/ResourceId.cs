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

    /// <summary>
    /// The resource "the mount does not move": exposures of every camera on that mount hold it shared, and whatever moves or shakes the mount (a slew, centering, a flip, a dither) holds it
    /// exclusively, which makes it wait for the exposures that are running and keeps new ones from starting. The mount is a device; its stability is a resource of its own because holding the device
    /// (to slew it) and relying on it standing still (to expose) are different things.
    /// </summary>
    public static ResourceId ForMountStability(DeviceId mountId)
    {
        return new ResourceId($"mountstability:{mountId.Value}");
    }

    public override string ToString()
    {
        return Value;
    }
}

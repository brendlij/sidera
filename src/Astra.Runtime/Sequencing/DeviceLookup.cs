using Astra.Core.Devices;
using Astra.Runtime.Devices;

namespace Astra.Runtime.Sequencing;

internal static class DeviceLookup
{
    public static T Resolve<T>(DeviceRegistry registry, DeviceId id, string kind)
        where T : class, IDevice
    {
        if (!registry.TryGet(id, out var device) || device is null)
        {
            throw new InvalidOperationException($"Device '{id}' is not registered.");
        }

        return device as T
            ?? throw new InvalidOperationException($"Device '{id}' is not a {kind}.");
    }
}

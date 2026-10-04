using Astra.Core.Devices;
using Astra.Core.Focusers;
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

    /// <summary>
    /// A focuser that works with positions. A relative focuser is refused here, before anything moves: its position is not
    /// known, and a position that is invented is worse than an error.
    /// </summary>
    public static IFocuser ResolveAbsoluteFocuser(DeviceRegistry registry, DeviceId id)
    {
        var focuser = Resolve<IFocuser>(registry, id, "focuser");
        return focuser.IsAbsolute
            ? focuser
            : throw new InvalidOperationException(
                $"Focuser '{id}' is a relative focuser: it has no positions to move to, so this cannot be done with it.");
    }
}

using Sidera.Ascom.Cameras;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Focusers;
using Sidera.Ascom.Mounts;
using Sidera.Ascom.Rotators;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom;

/// <summary>
/// Creates the ASCOM adapter for a device of Sidera: a camera, a mount, a focuser or a rotator. Creating one touches nothing: no
/// driver is instantiated until the device is connected. Other kinds of device have no ASCOM adapter yet.
/// </summary>
public sealed class AscomDeviceFactory(
    IAscomDriverFactory drivers, ILoggerFactory? loggers = null, AscomTimings? timings = null)
{
    public static bool Supports(DeviceType type) => type is DeviceType.Camera or DeviceType.Mount or DeviceType.Focuser or DeviceType.Rotator;

    /// <exception cref="NotSupportedException">There is no ASCOM adapter for the kind of device.</exception>
    public IDevice Create(DeviceType type, DeviceId id, string name, string progId, IEventPublisher? events = null)
    {
        var logger = loggers?.CreateLogger($"Sidera.Ascom.{type}");
        return type switch
        {
            DeviceType.Camera => new AscomCamera(id, name, progId, drivers, events, logger, timings),
            DeviceType.Mount => new AscomMount(id, name, progId, drivers, events, logger, timings),
            DeviceType.Focuser => new AscomFocuser(id, name, progId, drivers, events, logger, timings),
            DeviceType.Rotator => new AscomRotator(id, name, progId, drivers, events, logger, timings),
            _ => throw new NotSupportedException($"ASCOM {type} devices are not supported yet."),
        };
    }
}

using Sidera.Core.Devices;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Diagnostics;

/// <summary>
/// Logs the connection life of every device, from the one place all devices report it: the connection state events on the
/// event bus. A device class, simulated or real, only has to publish its state changes to be part of the log. Connecting,
/// connected, disconnecting and disconnected are Information; a device that fell from connected to disconnected without
/// having been asked to disconnect is a Warning (an unexpected disconnect); a faulted device is an Error. A failed connect
/// attempt is logged with its exception where it was requested (see <see cref="DeviceOperationService"/>).
/// </summary>
public sealed class DeviceLifecycleLogger : IDisposable
{
    private readonly DeviceRegistry _registry;
    private readonly ILogger _logger;
    private readonly IDisposable _subscription;

    public DeviceLifecycleLogger(EventBus events, DeviceRegistry registry, ILogger<DeviceLifecycleLogger> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
        _subscription = events.Subscribe<DeviceConnectionStateChanged>(OnChanged);
    }

    public void Dispose() => _subscription.Dispose();

    private Task OnChanged(DeviceConnectionStateChanged e, CancellationToken cancellationToken)
    {
        var known = _registry.TryGet(e.DeviceId, out var device);
        var type = known ? device!.Type.ToString() : "device";
        var name = known ? device!.Name : string.Empty;

        using var scope = _logger.BeginScope(new KeyValuePair<string, object?>[] { new("DeviceId", e.DeviceId.Value) });
        switch (e.NewState)
        {
            case DeviceConnectionState.Connecting:
                _logger.LogInformation("{DeviceType} {DeviceId} ({DeviceName}) connecting", type, e.DeviceId, name);
                break;
            case DeviceConnectionState.Connected:
                _logger.LogInformation("{DeviceType} {DeviceId} ({DeviceName}) connected", type, e.DeviceId, name);
                break;
            case DeviceConnectionState.Disconnecting:
                _logger.LogInformation("{DeviceType} {DeviceId} ({DeviceName}) disconnecting", type, e.DeviceId, name);
                break;
            case DeviceConnectionState.Disconnected when e.PreviousState == DeviceConnectionState.Connected:
                _logger.LogWarning(
                    "{DeviceType} {DeviceId} ({DeviceName}) disconnected unexpectedly: it was connected and nobody asked it to disconnect",
                    type, e.DeviceId, name);
                break;
            case DeviceConnectionState.Disconnected:
                _logger.LogInformation("{DeviceType} {DeviceId} ({DeviceName}) disconnected", type, e.DeviceId, name);
                break;
            case DeviceConnectionState.Faulted:
                _logger.LogError(
                    "{DeviceType} {DeviceId} ({DeviceName}) faulted (it was {PreviousState})", type, e.DeviceId, name, e.PreviousState);
                break;
        }

        return Task.CompletedTask;
    }
}

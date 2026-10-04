namespace Sidera.Core.Devices;

public interface IDevice
{
    DeviceId Id { get; }
    string Name { get; }
    DeviceType Type { get; }
    DeviceConnectionState ConnectionState { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
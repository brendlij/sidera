using Astra.Core.Devices;
using Astra.Runtime.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.State;

namespace Astra.Runtime;

/// <summary>
/// Owns the runtime components (event bus, state store, device registry) and their lifecycle.
/// Devices are added explicitly; on <see cref="StopAsync"/> the host disconnects those still connected.
/// </summary>
public sealed class AstraRuntimeHost : IAsyncDisposable
{
    private enum HostState
    {
        Created,
        Started,
        Stopped,
        Disposed
    }

    private readonly object _gate = new();
    private HostState _state = HostState.Created;

    public AstraRuntimeHost()
    {
        EventBus = new EventBus();
        // Created here so the store always subscribes before any other consumer of the bus.
        StateStore = new StateStore(EventBus);
        DeviceRegistry = new DeviceRegistry();
    }

    public EventBus EventBus { get; }
    public StateStore StateStore { get; }
    public DeviceRegistry DeviceRegistry { get; }

    /// <summary>Registers a device with the host. The host disconnects it on shutdown.</summary>
    public void AddDevice(IDevice device)
    {
        ThrowIfDisposed();
        DeviceRegistry.Register(device);
    }

    /// <summary>Creates a simulated camera wired to this host's event bus and registers it.</summary>
    public SimulatedCamera AddSimulatedCamera(DeviceId id, string name, int? seed = null)
    {
        var camera = new SimulatedCamera(id, name, EventBus, seed);
        AddDevice(camera);
        return camera;
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();

            if (_state != HostState.Created)
            {
                throw new InvalidOperationException("The runtime host has already been started.");
            }

            _state = HostState.Started;
        }
    }

    /// <summary>
    /// Disconnects every device that is currently connected, one after another. Devices in any other
    /// connection state are left alone. A device that fails does not stop the cleanup of the others;
    /// all failures are reported afterwards as one <see cref="AggregateException"/>.
    /// Does nothing if the host is not running.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state != HostState.Started)
            {
                return;
            }

            _state = HostState.Stopped;
        }

        var failures = new List<Exception>();

        foreach (var device in DeviceRegistry.GetAll())
        {
            if (device.ConnectionState != DeviceConnectionState.Connected)
            {
                continue;
            }

            try
            {
                await device.DisconnectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new InvalidOperationException(
                    $"Device '{device.Id}' could not be disconnected.", ex));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException(failures);
        }
    }

    /// <summary>
    /// Stops the host if it is still running and releases its subscriptions.
    /// Never throws; call <see cref="StopAsync"/> first to observe shutdown failures.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_state == HostState.Disposed)
            {
                return;
            }
        }

        try
        {
            await StopAsync();
        }
        catch
        {
            // Disposal must not throw. StopAsync already tried every device.
        }

        lock (_gate)
        {
            _state = HostState.Disposed;
        }

        StateStore.Dispose();
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == HostState.Disposed, this);
        }
    }
}

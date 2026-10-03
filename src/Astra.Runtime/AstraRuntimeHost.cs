using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Rigs;
using Astra.Runtime.Coordination;
using Astra.Runtime.Devices;
using Astra.Runtime.Events;
using Astra.Runtime.Focusing;
using Astra.Runtime.Resources;
using Astra.Runtime.Rigs;
using Astra.Runtime.State;

namespace Astra.Runtime;

/// <summary>
/// Owns the runtime components (event bus, state store, device registry, rig registry, resource manager, device operations) and their lifecycle.
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
        RigRegistry = new RigRegistry(DeviceRegistry);
        ResourceManager = new ResourceManager();
        DeviceOperations = new DeviceOperationService(DeviceRegistry, ResourceManager);
        SafePointCoordinator = new SafePointCoordinator();
        FocusMetrics = new SimulatedFocusMetricProvider();
    }

    public EventBus EventBus { get; }
    public StateStore StateStore { get; }
    public DeviceRegistry DeviceRegistry { get; }
    public RigRegistry RigRegistry { get; }

    /// <summary>The one manager that all sequence runners of this runtime share.</summary>
    public ResourceManager ResourceManager { get; }

    /// <summary>Direct device operations, coordinated through <see cref="ResourceManager"/>.</summary>
    public DeviceOperationService DeviceOperations { get; }

    /// <summary>Coordinates the branches of parallel steps; shared by all sequence runners of this runtime.</summary>
    public SafePointCoordinator SafePointCoordinator { get; }

    /// <summary>
    /// What measures focus for autofocus. Today it is the simulation: it knows, for each rig that was given a model with
    /// <see cref="AddSimulatedFocusModel"/>, where that rig is in focus.
    /// </summary>
    public SimulatedFocusMetricProvider FocusMetrics { get; }

    /// <summary>Registers a device with the host. The host disconnects it on shutdown.</summary>
    public void AddDevice(IDevice device)
    {
        ThrowIfDisposed();
        DeviceRegistry.Register(device);
    }

    /// <summary>
    /// Registers a rig. The devices it refers to must already have been added to the host.
    /// Devices are not owned by the rig and several rigs may share one.
    /// </summary>
    public void AddRig(Rig rig)
    {
        ThrowIfDisposed();
        RigRegistry.Register(rig);
    }

    /// <summary>Creates a simulated camera wired to this host's event bus and registers it.</summary>
    public SimulatedCamera AddSimulatedCamera(DeviceId id, string name, int? seed = null)
    {
        var camera = new SimulatedCamera(id, name, EventBus, seed);
        AddDevice(camera);
        return camera;
    }

    /// <summary>
    /// Creates a simulated mount wired to this host's event bus and registers it. The mount is a shared
    /// device: it is not part of any rig.
    /// </summary>
    public SimulatedMount AddSimulatedMount(DeviceId id, string name, TimeSpan? slewDuration = null)
    {
        var mount = new SimulatedMount(id, name, EventBus, slewDuration);
        AddDevice(mount);
        return mount;
    }

    /// <summary>
    /// Creates a simulated guider wired to this host's event bus and registers it. Like a mount, the guider is
    /// a shared device addressed by its ID: it is not part of any rig, and several guiders may coexist.
    /// </summary>
    public SimulatedGuider AddSimulatedGuider(
        DeviceId id,
        string name,
        TimeSpan? startDuration = null,
        TimeSpan? stopDuration = null,
        TimeSpan? ditherDuration = null
    )
    {
        var guider = new SimulatedGuider(id, name, EventBus, startDuration, stopDuration, ditherDuration);
        AddDevice(guider);
        return guider;
    }

    /// <summary>
    /// Creates a simulated focuser wired to this host's event bus and registers it. A focuser belongs to a rig
    /// only by the rig naming its ID; it can be used on its own just the same.
    /// </summary>
    public SimulatedFocuser AddSimulatedFocuser(
        DeviceId id,
        string name,
        int startPosition = SimulatedFocuser.DefaultStartPosition,
        int minPosition = SimulatedFocuser.DefaultMinPosition,
        int maxPosition = SimulatedFocuser.DefaultMaxPosition,
        int stepsPerSecond = SimulatedFocuser.DefaultStepsPerSecond,
        TimeSpan? minimumMoveDuration = null
    )
    {
        var focuser = new SimulatedFocuser(
            id, name, EventBus, startPosition, minPosition, maxPosition, stepsPerSecond, minimumMoveDuration);
        AddDevice(focuser);
        return focuser;
    }

    /// <summary>Creates a simulated filter wheel with the given slots, wired to this host's event bus, and registers it.</summary>
    public SimulatedFilterWheel AddSimulatedFilterWheel(
        DeviceId id,
        string name,
        IEnumerable<FilterSlot> slots,
        int startSlotIndex = 0,
        TimeSpan? moveDuration = null
    )
    {
        var wheel = new SimulatedFilterWheel(id, slots, name, EventBus, startSlotIndex, moveDuration);
        AddDevice(wheel);
        return wheel;
    }

    /// <summary>
    /// Says where a simulated rig is in focus (see <see cref="SimulatedFocusModel"/>). The knowledge belongs to the rig
    /// (its camera, focuser and optics together), not to the focuser device.
    /// </summary>
    public void AddSimulatedFocusModel(RigId rig, SimulatedFocusModel model)
    {
        ThrowIfDisposed();
        FocusMetrics.SetModel(rig, model);
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

using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Focusers;
using Astra.Core.Focusing;
using Astra.Core.Imaging;
using Astra.Core.Rigs;
using Astra.Runtime.Coordination;
using Astra.Runtime.Devices;
using Astra.Runtime.Diagnostics;
using Astra.Runtime.Events;
using Astra.Runtime.Focusing;
using Astra.Runtime.Imaging;
using Astra.Runtime.Resources;
using Astra.Runtime.Rigs;
using Astra.Runtime.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;
    private readonly DeviceLifecycleLogger _lifecycleLogger;
    private HostState _state = HostState.Created;

    /// <param name="loggerFactory">
    /// Where the runtime writes its diagnostics. The application configures it (providers, levels) once, in its
    /// composition root; the host never does. Without one nothing is logged. The host wraps it so that every entry of this
    /// runtime carries the <see cref="SessionId"/>, and does not dispose it.
    /// </param>
    /// <param name="sessionId">The id of this run of the application in the logs; a new short id when not given.</param>
    public AstraRuntimeHost(
        FrameAnalysisOptions? analysisOptions = null, ILoggerFactory? loggerFactory = null, string? sessionId = null)
    {
        SessionId = sessionId ?? SessionIds.New();
        LoggerFactory = new SessionLoggerFactory(loggerFactory ?? NullLoggerFactory.Instance, SessionId);
        _logger = LoggerFactory.CreateLogger<AstraRuntimeHost>();

        EventBus = new EventBus();
        // Created here so the store always subscribes before any other consumer of the bus.
        StateStore = new StateStore(EventBus, LoggerFactory.CreateLogger<StateStore>());
        DeviceRegistry = new DeviceRegistry();
        _lifecycleLogger = new DeviceLifecycleLogger(EventBus, DeviceRegistry, LoggerFactory.CreateLogger<DeviceLifecycleLogger>());
        RigRegistry = new RigRegistry(DeviceRegistry);
        ResourceManager = new ResourceManager(LoggerFactory.CreateLogger<ResourceManager>());
        AcquisitionDefaults = new AcquisitionDefaultsRegistry();
        DeviceOperations = new DeviceOperationService(
            DeviceRegistry, ResourceManager, LoggerFactory.CreateLogger<DeviceOperationService>(), AcquisitionDefaults);
        SafePointCoordinator = new SafePointCoordinator(LoggerFactory.CreateLogger<SafePointCoordinator>());
        FocusMetrics = new SimulatedFocusMetricProvider();
        FrameAnalyzer = new FrameAnalyzer(analysisOptions, logger: LoggerFactory.CreateLogger<FrameAnalyzer>());
        FocusMetricProvider = new StarHfrFocusMetricProvider(FrameAnalyzer);
    }

    /// <summary>The id of this run of the application: on every log entry of this runtime, and in the log file name's session.</summary>
    public string SessionId { get; }

    /// <summary>
    /// The loggers of everything that runs on this host (sequence runners, steps, view models). Every entry carries the
    /// <see cref="SessionId"/>. Owned by the application, not by the host.
    /// </summary>
    public ILoggerFactory LoggerFactory { get; }

    public EventBus EventBus { get; }
    public StateStore StateStore { get; }
    public DeviceRegistry DeviceRegistry { get; }
    public RigRegistry RigRegistry { get; }

    /// <summary>The one manager that all sequence runners of this runtime share.</summary>
    public ResourceManager ResourceManager { get; }

    /// <summary>
    /// What each camera normally takes frames with (gain, binning, region ...), as the application keeps it; exposures without
    /// settings of their own resolve to these.
    /// </summary>
    public AcquisitionDefaultsRegistry AcquisitionDefaults { get; }

    /// <summary>Direct device operations, coordinated through <see cref="ResourceManager"/>.</summary>
    public DeviceOperationService DeviceOperations { get; }

    /// <summary>Coordinates the branches of parallel steps; shared by all sequence runners of this runtime.</summary>
    public SafePointCoordinator SafePointCoordinator { get; }

    /// <summary>
    /// What measures focus for autofocus. Today it is the simulation: it knows, for each rig that was given a model with
    /// <see cref="AddSimulatedFocusModel"/>, where that rig is in focus.
    /// </summary>
    public SimulatedFocusMetricProvider FocusMetrics { get; }

    /// <summary>
    /// The one analysis of frames (statistics, stars, HFR): what autofocus measures with and what the imaging page shows,
    /// so that a frame is analysed once.
    /// </summary>
    public IFrameAnalyzer FrameAnalyzer { get; }

    /// <summary>
    /// What autofocus measures focus with: the HFR of the stars in the frame that the camera exposed. It does not know
    /// where focus is. <see cref="FocusMetrics"/> is the direct simulated alternative, kept for tests of the algorithm.
    /// </summary>
    public IFocusMetricProvider FocusMetricProvider { get; }

    /// <summary>Registers a device with the host. The host disconnects it on shutdown.</summary>
    public void AddDevice(IDevice device)
    {
        ThrowIfDisposed();
        DeviceRegistry.Register(device);
    }

    /// <summary>
    /// Takes a device out of the host: it leaves the registry and the state store forgets it. The device must not be
    /// connected (the caller disconnects it first), and no rig may refer to it. The device itself is not disposed here;
    /// whoever created it ends it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is connected or busy connecting, or a rig refers to it.</exception>
    /// <returns>Whether there was such a device.</returns>
    public bool RemoveDevice(DeviceId id)
    {
        ThrowIfDisposed();
        if (!DeviceRegistry.TryGet(id, out var device) || device is null)
        {
            return false;
        }

        if (device.ConnectionState != DeviceConnectionState.Disconnected)
        {
            throw new InvalidOperationException($"Device '{id}' is {device.ConnectionState.ToString().ToLowerInvariant()}; disconnect it before removing it.");
        }

        if (RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == id || r.FocuserId == id || r.FilterWheelId == id) is { } rig)
        {
            throw new InvalidOperationException($"Device '{id}' is part of the rig '{rig.Id}'.");
        }

        DeviceRegistry.Unregister(id);
        StateStore.Remove(id);
        return true;
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

        // Stars at fixed places whose width follows the focus of the rig the camera is part of, when that rig has a
        // simulated focus (see AddSimulatedFocusModel); otherwise the camera keeps its random sky.
        camera.Sky = new SimulatedSky(seed ?? StableHash(id.Value));
        camera.PsfSigmaSource = () => PsfSigmaOf(id);
        AddDevice(camera);
        return camera;
    }

    // The relation between the HFR of a Gaussian star and its sigma: HFR = sigma * sqrt(2 ln 2).
    private static readonly double HfrPerSigma = Math.Sqrt(2 * Math.Log(2));

    // What focus does to the stars of a camera: from the focuser position of its rig, through the simulated focus of
    // that rig. The sky is drawn from the width only; nothing of the HFR it will be measured as is handed over.
    private double? PsfSigmaOf(DeviceId cameraId)
    {
        var rig = RigRegistry.GetAll().FirstOrDefault(r => r.CameraId == cameraId && r.FocuserId is not null);
        if (rig?.FocuserId is not { } focuserId
            || !FocusMetrics.TryGetModel(rig.Id, out var model) || model is null
            || !DeviceRegistry.TryGet(focuserId, out var device) || device is not IFocuser focuser)
        {
            return null;
        }

        return model.HfrAt(focuser.Position) / HfrPerSigma;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var c in text)
            {
                hash = (hash ^ c) * 16777619;
            }

            return hash;
        }
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

        _logger.LogInformation(
            "Runtime started with {DeviceCount} devices and {RigCount} rigs",
            DeviceRegistry.GetAll().Count, RigRegistry.GetAll().Count);
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

        _logger.LogInformation("Runtime stopping: disconnecting the devices that are still connected");
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
                _logger.LogInformation("Runtime stop was cancelled while disconnecting {DeviceId}", device.Id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Device {DeviceId} could not be disconnected while the runtime stopped", device.Id);
                failures.Add(new InvalidOperationException(
                    $"Device '{device.Id}' could not be disconnected.", ex));
            }
        }

        if (failures.Count > 0)
        {
            _logger.LogWarning("Runtime stopped; {FailureCount} devices could not be disconnected", failures.Count);
            throw new AggregateException(failures);
        }

        _logger.LogInformation("Runtime stopped");
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
        catch (Exception ex)
        {
            // Disposal must not throw. StopAsync already tried every device, and logged each failure.
            _logger.LogError(ex, "The runtime could not be stopped cleanly while it was disposed");
        }

        lock (_gate)
        {
            _state = HostState.Disposed;
        }

        _lifecycleLogger.Dispose();
        StateStore.Dispose();
        _logger.LogDebug("Runtime disposed");
    }

    private void ThrowIfDisposed()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == HostState.Disposed, this);
        }
    }
}

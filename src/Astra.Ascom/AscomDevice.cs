using Astra.Ascom.Drivers;
using Astra.Ascom.Infrastructure;
using Astra.Core.Devices;
using Astra.Core.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Ascom;

/// <summary>
/// What every ASCOM device of Astra shares: its connection state and events, the dispatcher thread that owns the
/// driver, and the ownership of the COM object.
/// <para>
/// <b>Lifetime.</b> A connection owns one dispatcher thread and one driver instance, both created by
/// <see cref="ConnectAsync"/> and both gone after <see cref="DisconnectAsync"/>: the driver is created, connected,
/// used, disconnected and disposed on that one thread, and the reference is dropped before anything is released, so
/// that it can never be used afterwards. Nothing exists while the device is disconnected, which is why a device
/// created from the configuration at startup costs nothing and touches no hardware.
/// </para>
/// <para>
/// A failed or abandoned connection attempt releases what it created. Connecting is not retried.
/// </para>
/// </summary>
public abstract class AscomDevice<TDriver> : IDevice, IBackendDescribed, IAsyncDisposable
    where TDriver : class, IAscomDriver
{
    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private AscomDispatcher? _dispatcher;
    private TDriver? _driver;
    private bool _disposed;

    protected AscomDevice(
        DeviceId id,
        string name,
        DeviceType type,
        string progId,
        IEventPublisher? events,
        ILogger? logger,
        AscomTimings? timings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);
        Id = id;
        Name = name;
        Type = type;
        ProgId = progId;
        _events = events;
        Logger = logger ?? NullLogger.Instance;
        Timings = timings ?? AscomTimings.Default;
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type { get; }

    /// <summary>The stable identifier of the ASCOM driver: what the configuration stores.</summary>
    public string ProgId { get; }

    string IBackendDescribed.BackendName => "ASCOM";
    string? IBackendDescribed.DriverId => ProgId;

    protected ILogger Logger { get; }
    protected AscomTimings Timings { get; }

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    /// <summary>Creates the driver instance. Runs on the dispatcher thread, which then owns the instance.</summary>
    protected abstract TDriver CreateDriver();

    /// <summary>
    /// Reads what the adapter needs to know once, right after the driver connected. Runs on the dispatcher thread.
    /// Throwing rejects the device: the connection is rolled back.
    /// </summary>
    protected virtual void OnConnected(TDriver driver)
    {
    }

    /// <summary>Called after the connection was rolled back or closed, to forget what was read from the driver.</summary>
    protected virtual void OnDisconnected()
    {
    }

    /// <summary>The device is in the middle of an operation that a disconnect must not interrupt.</summary>
    protected virtual bool IsBusy => false;

    /// <summary>What the device is doing, for "Cannot disconnect while ...".</summary>
    protected virtual string BusyDescription => "an operation is running";

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTransition(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting))
        {
            return;
        }

        var dispatcher = new AscomDispatcher(Id.Value);
        var attempt = new ConnectAttempt();
        using var scope = BeginScope();
        try
        {
            await PublishConnectionAsync(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken);
            Logger.LogInformation("Connecting {Device} through ASCOM driver {ProgId}", Name, ProgId);

            var driver = await dispatcher
                .InvokeAsync(
                    () =>
                    {
                        var created = CreateDriver();
                        attempt.Driver = created;
                        created.Connected = true;
                        OnConnected(created);
                        return created;
                    })
                .WaitAsync(Timings.ConnectTimeout, cancellationToken);

            lock (_gate)
            {
                _dispatcher = dispatcher;
                _driver = driver;
            }

            await SetConnectionStateAsync(DeviceConnectionState.Connected, cancellationToken);
            Logger.LogInformation("{Device} is connected", Name);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Connecting {Device} failed; releasing what the attempt created", Name);
            await ReleaseAsync(dispatcher, () => attempt.Driver);
            OnDisconnected();
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);

            if (ex is TimeoutException)
            {
                throw new AscomTimeoutException(
                    Id, ProgId, "connect",
                    $"{Name} ({ProgId}) did not connect within {Timings.ConnectTimeout.TotalSeconds:0} s. " +
                    "The driver may still be starting; the attempt was released.");
            }

            throw Translate(ex, "connect");
        }
    }

    /// <exception cref="InvalidOperationException">An operation is running.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        AscomDispatcher? dispatcher;
        TDriver? driver;
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                return;
            }

            if (IsBusy)
            {
                throw new InvalidOperationException($"Cannot disconnect while {BusyDescription}.");
            }

            _connectionState = DeviceConnectionState.Disconnecting;
            (dispatcher, driver) = (_dispatcher, _driver);

            // From here on nothing can reach the driver any more: it is only released.
            (_dispatcher, _driver) = (null, null);
        }

        using var scope = BeginScope();
        try
        {
            await PublishConnectionAsync(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, cancellationToken);
            Logger.LogInformation("Disconnecting {Device}", Name);
            await ReleaseAsync(dispatcher!, () => driver);
        }
        finally
        {
            OnDisconnected();
            await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
        }
    }

    /// <summary>
    /// Ends the device for good: whatever is still connected is released, even while an operation is running (a
    /// device that is being thrown away cannot be asked to finish first). Idempotent.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        AscomDispatcher? dispatcher;
        TDriver? driver;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (dispatcher, driver) = (_dispatcher, _driver);
            (_dispatcher, _driver) = (null, null);
            if (dispatcher is null)
            {
                return;
            }

            _connectionState = DeviceConnectionState.Disconnecting;
        }

        using var scope = BeginScope();
        Logger.LogInformation("Releasing {Device} while it is being removed", Name);
        await ReleaseAsync(dispatcher, () => driver);
        OnDisconnected();
        await SetConnectionStateAsync(DeviceConnectionState.Disconnected, CancellationToken.None);
    }

    /// <summary>
    /// Runs a call on the dispatcher thread against the connected driver. Cancelling stops the waiting; a call that is
    /// already running on the driver is not interrupted and may still complete.
    /// </summary>
    protected async Task<T> CallAsync<T>(string operation, Func<TDriver, T> work, CancellationToken cancellationToken)
    {
        var (dispatcher, driver) = Session();
        try
        {
            return await dispatcher.InvokeAsync(() => work(driver), cancellationToken).WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex, operation);
        }
    }

    protected Task CallAsync(string operation, Action<TDriver> work, CancellationToken cancellationToken) =>
        CallAsync<object?>(
            operation,
            driver =>
            {
                work(driver);
                return null;
            },
            cancellationToken);

    /// <exception cref="InvalidOperationException">The device is not connected.</exception>
    protected (AscomDispatcher Dispatcher, TDriver Driver) Session()
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected || _dispatcher is null || _driver is null)
            {
                throw new InvalidOperationException($"{Name} is not connected.");
            }

            return (_dispatcher, _driver);
        }
    }

    protected Exception Translate(Exception exception, string operation) =>
        AscomErrors.Translate(exception, Id, Name, ProgId, operation);

    protected IDisposable? BeginScope() =>
        Logger.BeginScope(new Dictionary<string, object> { ["DeviceId"] = Id.Value, ["ProgId"] = ProgId });

    protected Task PublishAsync<TEvent>(TEvent astraEvent, CancellationToken cancellationToken = default)
        where TEvent : IAstraEvent =>
        _events is null ? Task.CompletedTask : _events.PublishAsync(astraEvent, cancellationToken);

    // Disconnects the driver and releases it, on the thread that owns it, then ends the thread. Bounded: a driver that
    // never returns is given up on after the timeout instead of hanging Astra.
    private async Task ReleaseAsync(AscomDispatcher dispatcher, Func<TDriver?> driver)
    {
        try
        {
            await dispatcher.InvokeAsync(() => ReleaseDriver(driver())).WaitAsync(Timings.ReleaseTimeout);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Device} did not release cleanly; its thread is given up on", Name);
        }
        finally
        {
            dispatcher.Dispose();
        }
    }

    private void ReleaseDriver(TDriver? driver)
    {
        if (driver is null)
        {
            return;
        }

        try
        {
            driver.Connected = false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Device}: disconnecting the driver failed: {Reason}", Name, AscomErrors.Describe(ex));
        }

        try
        {
            driver.Dispose();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{Device}: releasing the driver failed: {Reason}", Name, AscomErrors.Describe(ex));
        }
    }

    private bool TryTransition(DeviceConnectionState from, DeviceConnectionState to)
    {
        lock (_gate)
        {
            if (_connectionState != from || _disposed)
            {
                return false;
            }

            _connectionState = to;
            return true;
        }
    }

    private async Task SetConnectionStateAsync(DeviceConnectionState state, CancellationToken cancellationToken)
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            previous = _connectionState;
            _connectionState = state;
        }

        await PublishConnectionAsync(previous, state, cancellationToken);
    }

    private Task PublishConnectionAsync(
        DeviceConnectionState previous, DeviceConnectionState current, CancellationToken cancellationToken) =>
        previous == current
            ? Task.CompletedTask
            : PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);

    private sealed class ConnectAttempt
    {
        private TDriver? _driver;

        public TDriver? Driver
        {
            get => Volatile.Read(ref _driver);
            set => Volatile.Write(ref _driver, value);
        }
    }
}

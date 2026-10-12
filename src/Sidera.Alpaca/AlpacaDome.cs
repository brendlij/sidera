using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Core.Events;

namespace Sidera.Alpaca;

/// <summary>The times an Alpaca dome waits for things; the defaults are those of real equipment, tests make them short.</summary>
public sealed record AlpacaDomeOptions
{
    /// <summary>How often the state is read while nothing is going on, so that a roof that was closed by its own rain sensor shows as closed.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often it is read while an operation of Sidera waits for the end of a move.</summary>
    public TimeSpan OperationPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a dome may take to start moving after it was told to. Alpaca calls that answer at once and then move are normal, so the state is watched for this long before Sidera says that
    /// the dome did not respond.
    /// </summary>
    public TimeSpan StartGrace { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ShutterTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How many reads in a row may fail before the dome is reported as faulted.</summary>
    public int MaxPollFailures { get; init; } = 3;
}

/// <summary>
/// An ASCOM Alpaca dome (or roll-off roof) as Sidera's <see cref="IDomeControl"/>, reached at one address. Connecting reads what the device supports and moves nothing. The shutter and the
/// azimuth move only on an explicit call; each of them waits until the device reports the end of the move, and cancelling it tells the device to stop. A state that a driver cannot report
/// (a roof has no azimuth) is unknown, not zero. While connected the state is read every few seconds, so that a roof that closes by itself is seen; when the device stops answering, it is
/// reported as faulted and nothing is assumed about where it stands.
/// </summary>
public sealed class AlpacaDome : IDomeControl, IBackendDescribed, IDisposable
{
    private readonly object _gate = new();
    private readonly AlpacaClient _client;
    private readonly IEventPublisher? _events;
    private readonly ILogger _logger;
    private readonly AlpacaDomeOptions _options;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private DeviceCapabilities<DomeCapabilities> _capabilities = DeviceCapabilities<DomeCapabilities>.Unknown;
    private DomeTelemetry? _telemetry;
    private string? _operation;
    private bool _halted;
    private CancellationTokenSource? _poller;
    private Task _pollTask = Task.CompletedTask;

    public AlpacaDome(
        DeviceId id, string name, AlpacaEndpoint endpoint, IEventPublisher? events = null, ILogger? logger = null, AlpacaDomeOptions? options = null, HttpClient? http = null)
    {
        Id = id;
        Name = name;
        Endpoint = endpoint;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new AlpacaDomeOptions();
        _client = new AlpacaClient(endpoint, "dome", http, _options.RequestTimeout);
    }

    public DeviceId Id { get; }

    public string Name { get; }

    public DeviceType Type => DeviceType.Dome;

    public AlpacaEndpoint Endpoint { get; }

    public string BackendName => "Alpaca";

    public string? DriverId => Endpoint.ToString();

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged;

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public DeviceCapabilities<DomeCapabilities> Capabilities
    {
        get { lock (_gate) { return _capabilities; } }
    }

    public DomeTelemetry? Telemetry
    {
        get { lock (_gate) { return _telemetry; } }
    }

    public DomeShutterState ShutterState => Telemetry?.ShutterState ?? DomeShutterState.Unknown;

    public double? Azimuth => Telemetry?.Azimuth;

    public bool IsMoving => Telemetry?.IsMoving ?? false;

    // ---- connection

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectionState is not (DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted))
            {
                return;
            }

            _connectionState = DeviceConnectionState.Connecting;
        }

        await PublishConnectionAsync(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken).ConfigureAwait(false);
        var told = false;
        try
        {
            _logger.LogInformation("Connecting the Alpaca dome {DeviceId} at {Endpoint}", Id, Endpoint);
            await _client.PutAsync("connected", cancellationToken, ("Connected", "true")).ConfigureAwait(false);
            told = true;
            if (!await _client.GetBoolAsync("connected", cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The device did not accept the connection.");
            }

            var capabilities = await ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var telemetry = await ReadTelemetryAsync(capabilities, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _capabilities = DeviceCapabilities<DomeCapabilities>.Of(capabilities);
                _telemetry = telemetry;
                _connectionState = DeviceConnectionState.Connected;
            }

            await PublishConnectionAsync(DeviceConnectionState.Connecting, DeviceConnectionState.Connected, cancellationToken).ConfigureAwait(false);
            CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            StartPolling();
        }
        catch
        {
            lock (_gate)
            {
                _connectionState = DeviceConnectionState.Disconnected;
                _capabilities = DeviceCapabilities<DomeCapabilities>.Unknown;
                _telemetry = null;
            }

            if (told)
            {
                await TryDisconnectQuietlyAsync().ConfigureAwait(false);
            }

            await PublishConnectionAsync(DeviceConnectionState.Connecting, DeviceConnectionState.Disconnected, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <exception cref="InvalidOperationException">An operation of Sidera (a shutter or a move) is running.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            if (_connectionState is DeviceConnectionState.Disconnected)
            {
                return;
            }

            if (_operation is { } running)
            {
                throw new InvalidOperationException($"Cannot disconnect while the dome is {running}.");
            }

            previous = _connectionState;
            _connectionState = DeviceConnectionState.Disconnecting;
        }

        await PublishConnectionAsync(previous, DeviceConnectionState.Disconnecting, cancellationToken).ConfigureAwait(false);
        await StopPollingAsync().ConfigureAwait(false);
        try
        {
            // A device that cannot be reached any more is disconnected on our side all the same.
            if (previous == DeviceConnectionState.Connected)
            {
                await _client.PutAsync("connected", cancellationToken, ("Connected", "false")).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
        {
            _logger.LogWarning(ex, "The Alpaca dome {DeviceId} could not be told to disconnect", Id);
        }
        finally
        {
            lock (_gate)
            {
                _connectionState = DeviceConnectionState.Disconnected;
                _capabilities = DeviceCapabilities<DomeCapabilities>.Unknown;
                _telemetry = null;
            }
        }

        await PublishConnectionAsync(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected, CancellationToken.None).ConfigureAwait(false);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task TryDisconnectQuietlyAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _client.PutAsync("connected", timeout.Token, ("Connected", "false")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The Alpaca dome {DeviceId} could not be disconnected after a failed connect", Id);
        }
    }

    // ---- reading

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionState != DeviceConnectionState.Connected)
        {
            return;
        }

        await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<DomeCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
    {
        async Task<bool> Can(string method) => await _client.TryGetBoolAsync(method, cancellationToken).ConfigureAwait(false) ?? false;

        async Task<string?> Text(string method)
        {
            try
            {
                var text = await _client.GetStringAsync(method, cancellationToken).ConfigureAwait(false);
                return text.Length == 0 ? null : text;
            }
            catch (AlpacaException)
            {
                return null;
            }
        }

        var driver = new DriverMetadata(
            await Text("name").ConfigureAwait(false), await Text("description").ConfigureAwait(false), await Text("driverinfo").ConfigureAwait(false),
            await Text("driverversion").ConfigureAwait(false), await _client.TryGetIntAsync("interfaceversion", cancellationToken).ConfigureAwait(false));
        var canSetShutter = await Can("cansetshutter").ConfigureAwait(false);
        var canSetAzimuth = await Can("cansetazimuth").ConfigureAwait(false);
        var notes = new List<string>();
        if (canSetShutter && !canSetAzimuth)
        {
            notes.Add("It has a shutter and no azimuth: Sidera treats it as a roll-off roof.");
        }
        else if (!canSetShutter && !canSetAzimuth)
        {
            notes.Add("The driver says that it can neither move the shutter nor turn the dome.");
        }

        return new DomeCapabilities
        {
            Driver = driver,
            CanSetShutter = canSetShutter,
            CanSetAzimuth = canSetAzimuth,
            CanSetAltitude = await Can("cansetaltitude").ConfigureAwait(false),
            CanPark = await Can("canpark").ConfigureAwait(false),
            CanSetPark = await Can("cansetpark").ConfigureAwait(false),
            CanFindHome = await Can("canfindhome").ConfigureAwait(false),
            CanSyncAzimuth = await Can("cansyncazimuth").ConfigureAwait(false),
            CanSlave = await Can("canslave").ConfigureAwait(false),
            Notes = notes,
        };
    }

    private async Task<DomeTelemetry> ReadTelemetryAsync(DomeCapabilities capabilities, CancellationToken cancellationToken)
    {
        // "Slewing" is read first on purpose: when it says that the move is over, everything read after it is the state after the move (a park that is read as "not at park" a moment before it
        // arrived would end an operation as failed).
        var moving = await _client.TryGetBoolAsync("slewing", cancellationToken).ConfigureAwait(false) ?? false;
        var shutter = DomeShutterState.Unknown;
        if (await _client.TryGetIntAsync("shutterstatus", cancellationToken).ConfigureAwait(false) is { } status)
        {
            shutter = status switch
            {
                0 => DomeShutterState.Open,
                1 => DomeShutterState.Closed,
                2 => DomeShutterState.Opening,
                3 => DomeShutterState.Closing,
                _ => DomeShutterState.Error,
            };
        }

        // A roof reports no azimuth (some drivers answer 0 for it): it is read only from a dome that turns.
        var azimuth = capabilities.CanSetAzimuth ? await _client.TryGetDoubleAsync("azimuth", cancellationToken).ConfigureAwait(false) : null;
        var altitude = capabilities.CanSetAltitude ? await _client.TryGetDoubleAsync("altitude", cancellationToken).ConfigureAwait(false) : null;
        return new DomeTelemetry
        {
            ShutterState = shutter,
            Azimuth = azimuth,
            Altitude = altitude,
            AtHome = await _client.TryGetBoolAsync("athome", cancellationToken).ConfigureAwait(false),
            AtPark = await _client.TryGetBoolAsync("atpark", cancellationToken).ConfigureAwait(false),
            IsMoving = moving,
            Slaved = capabilities.CanSlave ? await _client.TryGetBoolAsync("slaved", cancellationToken).ConfigureAwait(false) : null,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private async Task<DomeTelemetry> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        var telemetry = await ReadTelemetryAsync(capabilities, cancellationToken).ConfigureAwait(false);
        DomeTelemetry? previous;
        lock (_gate)
        {
            previous = _telemetry;
            _telemetry = telemetry;
        }

        if (previous is null || previous.ShutterState != telemetry.ShutterState)
        {
            await PublishAsync(new DomeShutterStateChanged(Id, previous?.ShutterState ?? DomeShutterState.Unknown, telemetry.ShutterState), CancellationToken.None).ConfigureAwait(false);
        }

        if (previous is null || Differs(previous, telemetry))
        {
            await PublishAsync(new DomeStateChanged(Id), CancellationToken.None).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return telemetry;
    }

    private static bool Differs(DomeTelemetry a, DomeTelemetry b) =>
        a.ShutterState != b.ShutterState || a.IsMoving != b.IsMoving || a.AtHome != b.AtHome || a.AtPark != b.AtPark || a.Slaved != b.Slaved
        || !Same(a.Azimuth, b.Azimuth) || !Same(a.Altitude, b.Altitude);

    private static bool Same(double? a, double? b) => a is null || b is null ? a is null && b is null : Math.Abs(a.Value - b.Value) < 0.05;

    // ---- polling

    private void StartPolling()
    {
        var source = new CancellationTokenSource();
        lock (_gate)
        {
            _poller = source;
        }

        _pollTask = Task.Run(() => PollAsync(source.Token));
    }

    private async Task StopPollingAsync()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _poller;
            _poller = null;
        }

        if (source is null)
        {
            return;
        }

        await source.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pollTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        source.Dispose();
    }

    private async Task PollAsync(CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollInterval, token).ConfigureAwait(false);
                bool busy;
                lock (_gate)
                {
                    busy = _operation is not null; // an operation reads the state itself while it waits
                }

                if (busy)
                {
                    continue;
                }

                await RefreshCoreAsync(token).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
            {
                failures++;
                _logger.LogWarning(ex, "Reading the Alpaca dome {DeviceId} failed ({Failures} in a row)", Id, failures);
                if (failures >= _options.MaxPollFailures || ex is AlpacaException { IsNotConnected: true })
                {
                    await FaultAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private async Task FaultAsync()
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                return;
            }

            _connectionState = DeviceConnectionState.Faulted;
        }

        _logger.LogError("The Alpaca dome {DeviceId} at {Endpoint} stopped answering", Id, Endpoint);
        await PublishConnectionAsync(DeviceConnectionState.Connected, DeviceConnectionState.Faulted, CancellationToken.None).ConfigureAwait(false);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- operations

    public Task OpenShutterAsync(CancellationToken cancellationToken = default) => MoveShutterAsync(open: true, cancellationToken);

    public Task CloseShutterAsync(CancellationToken cancellationToken = default) => MoveShutterAsync(open: false, cancellationToken);

    private async Task MoveShutterAsync(bool open, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = RequireConnected();
        if (!capabilities.CanSetShutter)
        {
            throw new InvalidOperationException($"{Name} cannot open or close its shutter.");
        }

        var target = open ? DomeShutterState.Open : DomeShutterState.Closed;
        var moving = open ? DomeShutterState.Opening : DomeShutterState.Closing;
        using var operation = Begin(open ? "opening the shutter" : "closing the shutter");
        try
        {
            var current = (await ReadTelemetryAsync(capabilities, cancellationToken).ConfigureAwait(false)).ShutterState;
            if (current == target)
            {
                return;
            }

            // Closing is the safe direction, so a shutter in the error state (a driver reports it after a halt in the middle of a move) can be closed, and only closed.
            if (current == DomeShutterState.Error && open)
            {
                throw new InvalidOperationException($"The shutter of {Name} reports an error. Close it, or check the dome, before opening it.");
            }

            _logger.LogInformation("{Action} of the Alpaca dome {DeviceId}: the shutter is {State}", open ? "Opening the shutter" : "Closing the shutter", Id, current);
            if (current != moving)
            {
                await _client.PutAsync(open ? "openshutter" : "closeshutter", cancellationToken).ConfigureAwait(false);
            }

            var clock = Stopwatch.StartNew();
            var moved = current == moving;
            while (true)
            {
                await Task.Delay(_options.OperationPollInterval, cancellationToken).ConfigureAwait(false);
                var state = (await RefreshCoreAsync(cancellationToken).ConfigureAwait(false)).ShutterState;
                ThrowIfHalted();
                if (state == target)
                {
                    return;
                }

                if (state == DomeShutterState.Error)
                {
                    throw new InvalidOperationException($"The shutter of {Name} reported an error while it moved. Check the dome.");
                }

                moved |= state == moving;
                if (!moved && clock.Elapsed > _options.StartGrace)
                {
                    throw new InvalidOperationException($"The shutter of {Name} did not start to {(open ? "open" : "close")}: it still reports {state}.");
                }

                if (clock.Elapsed > _options.ShutterTimeout)
                {
                    throw new TimeoutException($"The shutter of {Name} did not finish {(open ? "opening" : "closing")} within {_options.ShutterTimeout.TotalMinutes:0.#} minutes. It reports {state}.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAfterCancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task SlewToAzimuthAsync(double azimuthDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(azimuthDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(azimuthDegrees), azimuthDegrees, "The azimuth must be a finite number of degrees.");
        }

        var azimuth = ((azimuthDegrees % 360.0) + 360.0) % 360.0;
        return MoveAsync(
            "turning", "slewtoazimuth", capabilities => capabilities.CanSetAzimuth ? null : $"{Name} cannot be turned to an azimuth.",
            [("Azimuth", azimuth.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))],
            arrived: t => t.Azimuth is { } a && AzimuthDifference(a, azimuth) <= 1.0, mustArrive: false, cancellationToken);
    }

    public Task ParkAsync(CancellationToken cancellationToken = default) =>
        MoveAsync("parking", "park", c => c.CanPark ? null : $"{Name} cannot be parked.", [], t => t.AtPark == true, mustArrive: true, cancellationToken);

    public Task FindHomeAsync(CancellationToken cancellationToken = default) =>
        MoveAsync("finding home", "findhome", c => c.CanFindHome ? null : $"{Name} cannot find its home position.", [], t => t.AtHome == true, mustArrive: true, cancellationToken);

    private async Task MoveAsync(
        string description, string method, Func<DomeCapabilities, string?> refusal, (string Name, string Value)[] parameters, Func<DomeTelemetry, bool> arrived,
        bool mustArrive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = RequireConnected();
        if (refusal(capabilities) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        using var operation = Begin(description);
        try
        {
            var start = await ReadTelemetryAsync(capabilities, cancellationToken).ConfigureAwait(false);
            if (start.IsMoving)
            {
                throw new InvalidOperationException($"{Name} is already moving. Stop it first.");
            }

            _logger.LogInformation("The Alpaca dome {DeviceId} is {Action}", Id, description);
            await _client.PutAsync(method, cancellationToken, parameters).ConfigureAwait(false);
            var clock = Stopwatch.StartNew();
            var moved = false;
            while (true)
            {
                await Task.Delay(_options.OperationPollInterval, cancellationToken).ConfigureAwait(false);
                var state = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                ThrowIfHalted();
                moved |= state.IsMoving;
                if (!state.IsMoving && (moved || arrived(state)))
                {
                    if (mustArrive && !arrived(state))
                    {
                        throw new InvalidOperationException($"{Name} stopped {description} without arriving.");
                    }

                    return;
                }

                if (!moved && clock.Elapsed > _options.StartGrace)
                {
                    throw new InvalidOperationException($"{Name} did not start {description}.");
                }

                if (clock.Elapsed > _options.MoveTimeout)
                {
                    throw new TimeoutException($"{Name} did not finish {description} within {_options.MoveTimeout.TotalMinutes:0.#} minutes.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAfterCancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task HaltAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        _logger.LogInformation("Halting the Alpaca dome {DeviceId}", Id);
        lock (_gate)
        {
            _halted = _operation is not null; // an operation that waits for the end of a move ends as cancelled
        }

        await _client.PutAsync("abortslew", cancellationToken).ConfigureAwait(false);
        if (ConnectionState == DeviceConnectionState.Connected)
        {
            try
            {
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AlpacaCommunicationException or AlpacaException)
            {
                _logger.LogDebug(ex, "The state of the Alpaca dome {DeviceId} could not be read after a halt", Id);
            }
        }
    }

    // The caller cancelled: the dome is told to stop, and the cancellation goes on. If the dome cannot be told, that is said, because a shutter that may still move matters.
    private async Task StopAfterCancelAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _client.PutAsync("abortslew", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The operation was cancelled, but {Name} could not be told to stop and may still be moving: {ex.Message}", ex);
        }
    }

    private void ThrowIfHalted()
    {
        bool halted;
        lock (_gate)
        {
            halted = _halted;
        }

        if (halted)
        {
            throw new OperationCanceledException($"{Name} was stopped.");
        }
    }

    private static double AzimuthDifference(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360.0;
        return difference > 180.0 ? 360.0 - difference : difference;
    }

    private DomeCapabilities RequireConnected()
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected || _capabilities.Value is not { } capabilities)
            {
                throw new InvalidOperationException($"{Name} is not connected.");
            }

            return capabilities;
        }
    }

    private OperationScope Begin(string description)
    {
        lock (_gate)
        {
            if (_operation is { } running)
            {
                throw new InvalidOperationException($"{Name} is busy ({running}).");
            }

            _operation = description;
            _halted = false;
        }

        return new OperationScope(this);
    }

    private readonly struct OperationScope(AlpacaDome dome) : IDisposable
    {
        public void Dispose()
        {
            lock (dome._gate)
            {
                dome._operation = null;
            }
        }
    }

    private Task PublishConnectionAsync(DeviceConnectionState previous, DeviceConnectionState current, CancellationToken cancellationToken) =>
        _events is null || previous == current ? Task.CompletedTask : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);

    private Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : ISideraEvent =>
        _events is null ? Task.CompletedTask : _events.PublishAsync(@event, cancellationToken);

    public void Dispose()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _poller;
            _poller = null;
        }

        source?.Cancel();
        _client.Dispose();
    }
}

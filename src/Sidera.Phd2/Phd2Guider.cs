using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Guiding;

namespace Sidera.Phd2;

/// <summary>What the PHD2 guider waits for and how long; the values are the defaults of the integration, not settings of the device.</summary>
public sealed class Phd2GuiderOptions
{
    /// <summary>
    /// What counts as settled when the caller does not say (starting guiding, or a dither without a settle of its own). PHD2 needs a settle
    /// for <c>guide</c> and <c>dither</c> in every case; these are the values of the examples of its documentation, with a longer timeout
    /// because a start may follow a calibration.
    /// </summary>
    public GuidingSettleOptions DefaultSettle { get; init; } = new(1.5, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(120));

    /// <summary>Over how much of the recent samples the rolling RMS is calculated.</summary>
    public TimeSpan RmsWindow { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a start of guiding may take as a whole (selecting a star, calibrating, settling).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>How long PHD2 may take to say that it stopped.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long PHD2 may take, after accepting a dither, to report that the lock position moved.</summary>
    public TimeSpan DitherTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How much longer than its timeout a settle is waited for: PHD2 reports the end of every settle, this is only for a PHD2 that does not.</summary>
    public TimeSpan SettleGrace { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long PHD2 may take to confirm a pause or a resume.</summary>
    public TimeSpan PauseTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RequestTimeout { get; init; } = Phd2Connection.DefaultRequestTimeout;

    public TimeSpan HistoryMaxAge { get; init; } = GuidingHistory.DefaultMaxAge;
}

/// <summary>
/// PHD2 as a guider of Sidera. Sidera connects to the event server of PHD2; it does not connect PHD2's equipment, start looping, calibrate or
/// guide by connecting. Everything the guider reports is what PHD2 reports: the state follows the notifications of PHD2, not the success of a
/// request. Calibration, star selection, the guide algorithms and the guide pulses stay with PHD2; Sidera decides when guiding starts, when a
/// dither is allowed and what counts as settled.
/// </summary>
public sealed class Phd2Guider : IDitherGuider, IGuidingSettler, IGuiderControl, IBackendDescribed, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Phd2Endpoint _endpoint;
    private readonly IEventPublisher? _events;
    private readonly ILogger _logger;
    private readonly Phd2GuiderOptions _options;
    private readonly Func<CancellationToken, Task<Stream>>? _connector;
    private readonly TimeProvider _time;

    private DeviceConnectionState _connection = DeviceConnectionState.Disconnected;
    private GuidingState _state = GuidingState.Idle;
    private DeviceCapabilities<GuiderCapabilities> _capabilities = DeviceCapabilities<GuiderCapabilities>.Unknown;
    private Phd2Connection? _conn;
    private bool _phd2Guiding;
    private double? _pixelScale;
    private double? _exposureSeconds;
    private double? _starSnr;
    private double? _starMass;
    private string? _version;
    private string? _lastAlert;
    private GuiderInfo? _info;
    private Settle? _settle;
    private TaskCompletionSource? _ditherReported;
    private TaskCompletionSource? _stopped;
    private TaskCompletionSource? _pausedReported;
    private TaskCompletionSource? _resumedReported;
    private TaskCompletionSource _sampleSignal = NewSignal();
    private long _lastSampleTick;
    private double? _lastDistancePixels;
    private int _scaleRequested;

    public Phd2Guider(
        DeviceId id,
        string name,
        Phd2Endpoint endpoint,
        IEventPublisher? events = null,
        ILogger? logger = null,
        Phd2GuiderOptions? options = null,
        Func<CancellationToken, Task<Stream>>? connector = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Problem() is { } problem)
        {
            throw new ArgumentException(problem, nameof(endpoint));
        }

        Id = id;
        Name = name;
        _endpoint = endpoint;
        _events = events;
        _logger = logger ?? NullLogger.Instance;
        _options = options ?? new Phd2GuiderOptions();
        _connector = connector;
        _time = timeProvider ?? TimeProvider.System;
        History = new GuidingHistory(_options.HistoryMaxAge);
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Guider;
    public string BackendName => "PHD2";
    public string? DriverId => _endpoint.ToString();
    public Phd2Endpoint Endpoint => _endpoint;

    public GuidingHistory History { get; }

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connection; } }
    }

    public GuidingState GuidingState
    {
        get { lock (_gate) { return _state; } }
    }

    public DeviceCapabilities<GuiderCapabilities> Capabilities
    {
        get { lock (_gate) { return _capabilities; } }
    }

    public GuiderInfo? Info
    {
        get { lock (_gate) { return _info; } }
    }

    public GuidingTelemetry? Telemetry
    {
        get
        {
            lock (_gate)
            {
                if (_connection != DeviceConnectionState.Connected)
                {
                    return null;
                }

                return new GuidingTelemetry
                {
                    Rms = History.Rms(_options.RmsWindow),
                    StarSnr = _starSnr,
                    StarMass = _starMass,
                    ExposureSeconds = _exposureSeconds,
                    PixelScaleArcsecPerPixel = _pixelScale,
                    Settle = SettleStatus(),
                    Time = _time.GetUtcNow(),
                };
            }
        }
    }

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged;

    // ------------------------------------------------------------------ connection

    /// <summary>
    /// Connects to the event server of PHD2 and reads where PHD2 stands: its state, its profile and its equipment. Nothing of PHD2 is changed.
    /// </summary>
    /// <exception cref="Phd2ConnectionException">PHD2 cannot be reached.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connection == DeviceConnectionState.Connected)
            {
                return;
            }

            if (_connection is DeviceConnectionState.Connecting or DeviceConnectionState.Disconnecting)
            {
                throw new InvalidOperationException($"The guider is {_connection.ToString().ToLowerInvariant()}.");
            }
        }

        await SetConnectionAsync(DeviceConnectionState.Connecting, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation(Phd2Log.Connecting, "Connecting to PHD2 at {Host}:{Port} (device {DeviceId})", _endpoint.Host, _endpoint.Port, Id);
        var started = Stopwatch.GetTimestamp();
        var connection = _connector is null
            ? Phd2Connection.ToEndpoint(_endpoint, _logger, _options.ConnectTimeout)
            : new Phd2Connection(_connector, _logger);
        try
        {
            History.Clear();
            lock (_gate)
            {
                ResetStateForNewConnection();
            }

            connection.EventHandler = HandleEventAsync;
            connection.Closed += OnConnectionClosed;
            await connection.ConnectAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _conn = connection;
            }

            await ReadStartingPointAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _conn = null;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            await SetConnectionAsync(DeviceConnectionState.Disconnected, CancellationToken.None).ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            _logger.LogWarning(Phd2Log.ConnectFailed, ex, "Could not connect to PHD2 at {Host}:{Port} (device {DeviceId})", _endpoint.Host, _endpoint.Port, Id);
            throw ex is Phd2ConnectionException ? ex : new Phd2ConnectionException($"Could not connect to PHD2 at {_endpoint}: {ex.Message}", ex);
        }

        lock (_gate)
        {
            _capabilities = DeviceCapabilities<GuiderCapabilities>.Of(new GuiderCapabilities
            {
                Driver = new DriverMetadata("PHD2", "Guiding by PHD2", "PHD2 " + _version, _version, null),
                CanGuide = true,
                CanDither = true,
                CanSettle = true,
                CanPause = true,
                ProvidesGuideTelemetry = true,
            });
        }

        await SetConnectionAsync(DeviceConnectionState.Connected, cancellationToken).ConfigureAwait(false);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation(
            Phd2Log.Connected, "Connected to PHD2 at {Host}:{Port} (device {DeviceId}, profile {Profile}, state {State}) in {DurationMs:0} ms",
            _endpoint.Host, _endpoint.Port, Id, Info?.Profile, GuidingState, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    // What PHD2 reports is the truth; nothing is assumed of the earlier connection.
    private void ResetStateForNewConnection()
    {
        _state = GuidingState.Idle;
        _phd2Guiding = false;
        _pixelScale = null;
        _exposureSeconds = null;
        _starSnr = null;
        _starMass = null;
        _version = null;
        _lastAlert = null;
        _info = null;
        _settle = null;
        _lastDistancePixels = null;
        _scaleRequested = 0;
    }

    private async Task ReadStartingPointAsync(Phd2Connection connection, CancellationToken cancellationToken)
    {
        // The notifications of the first moments (Version, StarSelected ... AppState) are handled as they come; the answer of PHD2 to get_app_state
        // is the state in any case.
        var state = (await connection.CallAsync(Phd2Protocol.Methods.GetAppState, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false))
            ?.GetString();
        if (state is not null)
        {
            await ApplyAppStateAsync(state).ConfigureAwait(false);
        }

        await RefreshInfoAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshInfoAsync(Phd2Connection connection, CancellationToken cancellationToken)
    {
        async Task<JsonElement?> Try(string method)
        {
            try
            {
                return await connection.CallAsync(method, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
            }
            catch (Phd2RpcException ex)
            {
                _logger.LogDebug("PHD2 has no answer for {Method}: {Reason}", method, ex.Reason);
                return null;
            }
        }

        var profile = await Try(Phd2Protocol.Methods.GetProfile).ConfigureAwait(false);
        var equipment = await Try(Phd2Protocol.Methods.GetCurrentEquipment).ConfigureAwait(false);
        var calibrated = await Try(Phd2Protocol.Methods.GetCalibrated).ConfigureAwait(false);
        var connected = await Try(Phd2Protocol.Methods.GetConnected).ConfigureAwait(false);
        var exposure = await Try(Phd2Protocol.Methods.GetExposure).ConfigureAwait(false);
        var scale = await Try(Phd2Protocol.Methods.GetPixelScale).ConfigureAwait(false);

        lock (_gate)
        {
            _info = new GuiderInfo(
                profile is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("name", out var n) ? n.GetString() : null,
                DeviceName(equipment, "camera"),
                DeviceName(equipment, "mount"),
                calibrated is { } c && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null,
                connected is { } k && k.ValueKind is JsonValueKind.True or JsonValueKind.False ? k.GetBoolean() : null);
            _exposureSeconds = exposure is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var ms) && ms > 0 ? ms / 1000.0 : _exposureSeconds;
            _pixelScale = ValidScale(scale) ?? _pixelScale;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double? ValidScale(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } v && v.TryGetDouble(out var d) && double.IsFinite(d) && d > 0 ? d : null;

    private static string? DeviceName(JsonElement? equipment, string slot) =>
        equipment is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(slot, out var device) && device.ValueKind == JsonValueKind.Object
            && device.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        var state = (await connection.CallAsync(Phd2Protocol.Methods.GetAppState, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false))
            ?.GetString();
        if (state is not null && GuidingState is not (GuidingState.Starting or GuidingState.Stopping or GuidingState.Dithering or GuidingState.Settling))
        {
            await ApplyAppStateAsync(state).ConfigureAwait(false);
        }

        await RefreshInfoAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the connection to PHD2. PHD2 itself is left as it is: whatever it does goes on. Operations that wait for PHD2 fail.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Phd2Connection? connection;
        lock (_gate)
        {
            if (_connection is DeviceConnectionState.Disconnected or DeviceConnectionState.Disconnecting)
            {
                return;
            }

            connection = _conn;
            _conn = null;
        }

        await SetConnectionAsync(DeviceConnectionState.Disconnecting, CancellationToken.None).ConfigureAwait(false);
        FailWaiters(new Phd2ConnectionException("The guider was disconnected."));
        if (connection is not null)
        {
            connection.Closed -= OnConnectionClosed;
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        await EndConnectionAsync(DeviceConnectionState.Disconnected).ConfigureAwait(false);
        _logger.LogInformation(Phd2Log.Disconnected, "Disconnected from PHD2 at {Host}:{Port} (device {DeviceId})", _endpoint.Host, _endpoint.Port, Id);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Disconnecting the PHD2 guider while it is disposed failed");
        }
    }

    // PHD2 went away, or the network did: the device is faulted, everything that waits fails, nothing is restarted.
    private void OnConnectionClosed(Exception reason)
    {
        _ = Task.Run(async () =>
        {
            Phd2Connection? connection;
            lock (_gate)
            {
                if (_connection is DeviceConnectionState.Disconnected or DeviceConnectionState.Disconnecting)
                {
                    return;
                }

                connection = _conn;
                _conn = null;
            }

            _logger.LogWarning(
                Phd2Log.Disconnected, "The connection to PHD2 at {Host}:{Port} was lost (device {DeviceId}): {Reason}",
                _endpoint.Host, _endpoint.Port, Id, reason.Message);
            FailWaiters(new Phd2ConnectionException($"The connection to PHD2 at {_endpoint} was lost.", reason));
            await EndConnectionAsync(DeviceConnectionState.Faulted).ConfigureAwait(false);
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    // After the end of a connection no guiding goes on as far as Sidera knows, and no measurement is current.
    private async Task EndConnectionAsync(DeviceConnectionState final)
    {
        await SetStateAsync(GuidingState.Idle, force: true).ConfigureAwait(false);
        lock (_gate)
        {
            _phd2Guiding = false;
            _info = null;
            _settle = _settle is { IsDone: false } ? _settle.Finish(GuidingSettleOutcome.Failed, "The connection to PHD2 ended.", _time) : _settle;
            _capabilities = DeviceCapabilities<GuiderCapabilities>.Unknown;
        }

        await SetConnectionAsync(final, CancellationToken.None).ConfigureAwait(false);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SetConnectionAsync(DeviceConnectionState next, CancellationToken cancellationToken)
    {
        DeviceConnectionState previous;
        lock (_gate)
        {
            previous = _connection;
            _connection = next;
        }

        if (previous != next && _events is not null)
        {
            await _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, next), cancellationToken).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private Phd2Connection RequireConnection()
    {
        lock (_gate)
        {
            return _connection == DeviceConnectionState.Connected && _conn is { } connection
                ? connection
                : throw new InvalidOperationException($"The guider '{Id}' is not connected.");
        }
    }

    // ------------------------------------------------------------------ guiding

    public async Task StartGuidingAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        lock (_gate)
        {
            if (_state == GuidingState.Guiding)
            {
                return;
            }

            if (_state is GuidingState.Starting or GuidingState.Stopping or GuidingState.Dithering or GuidingState.Settling or GuidingState.Calibrating)
            {
                throw new InvalidOperationException($"Guiding cannot be started while the guider is {Describe(_state)}.");
            }
        }

        // PHD2 can only guide with its guide camera and mount connected; connecting them is the user's decision in PHD2.
        var equipment = await connection.CallAsync(Phd2Protocol.Methods.GetConnected, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        if (equipment is not { ValueKind: JsonValueKind.True })
        {
            throw new Phd2Exception("The equipment of PHD2 (guide camera and mount) is not connected. Connect it in PHD2, then start guiding.");
        }

        var settle = _options.DefaultSettle;
        Settle operation;
        lock (_gate)
        {
            operation = _settle = new Settle(settle, _time, isStart: true);
        }

        _logger.LogInformation(Phd2Log.GuidingRequested, "Guiding requested on PHD2 (device {DeviceId}, profile {Profile})", Id, Info?.Profile);
        var started = Stopwatch.GetTimestamp();
        await SetStateAsync(GuidingState.Starting).ConfigureAwait(false);
        try
        {
            await connection.CallAsync(Phd2Protocol.Methods.Guide, SettleParameter(settle), cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The request may have arrived before the cancellation did: PHD2 must not go on to guide on its own.
            lock (_gate)
            {
                _settle = null;
            }

            await TryStopAsync(connection).ConfigureAwait(false);
            await SetStateAsync(GuidingState.Idle, force: true).ConfigureAwait(false);
            throw;
        }
        catch
        {
            lock (_gate)
            {
                _settle = null;
            }

            await SetStateAsync(_phd2Guiding ? GuidingState.Guiding : GuidingState.Idle, force: true).ConfigureAwait(false);
            throw;
        }

        // PHD2 goes on by itself from here: selects a star, calibrates when it must, guides, settles. It reports the end of that with SettleDone.
        SettleResult result;
        try
        {
            result = await operation.Completion.Task.WaitAsync(_options.StartTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await TryStopAsync(connection).ConfigureAwait(false);
            throw new Phd2TimeoutException($"PHD2 did not finish starting guiding within {_options.StartTimeout.TotalMinutes:0} minutes; it was told to stop.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Starting guiding was cancelled; PHD2 is told to stop (device {DeviceId})", Id);
            await TryStopAsync(connection).ConfigureAwait(false);
            throw;
        }

        if (result.Outcome != GuidingSettleOutcome.Settled)
        {
            throw result.Outcome == GuidingSettleOutcome.TimedOut
                ? new GuidingSettleTimeoutException($"Guiding was started but did not settle within {settle.Timeout.TotalSeconds:0} s: {result.Reason}")
                : new Phd2SettleFailedException($"Guiding could not be started: {result.Reason}");
        }

        _logger.LogInformation(
            Phd2Log.GuidingStarted, "Guiding started on PHD2 (device {DeviceId}) after {DurationSeconds:0.0} s", Id, Stopwatch.GetElapsedTime(started).TotalSeconds);
    }

    private async Task TryStopAsync(Phd2Connection connection)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.CallAsync(Phd2Protocol.Methods.StopCapture, null, limit.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PHD2 could not be told to stop (device {DeviceId})", Id);
        }
    }

    public async Task StopGuidingAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        TaskCompletionSource stopped;
        lock (_gate)
        {
            if (_state is GuidingState.Idle or GuidingState.StarSelected)
            {
                return; // PHD2 is not capturing: there is nothing to stop
            }

            stopped = _stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await SetStateAsync(GuidingState.Stopping).ConfigureAwait(false);
        await connection.CallAsync(Phd2Protocol.Methods.StopCapture, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        try
        {
            await stopped.Task.WaitAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // PHD2 reports a stop with a notification; when none came, what PHD2 says about its state decides.
            var state = (await connection.CallAsync(Phd2Protocol.Methods.GetAppState, null, cancellationToken, _options.RequestTimeout).ConfigureAwait(false))?.GetString();
            if (state is not (Phd2Protocol.AppStates.Stopped or Phd2Protocol.AppStates.Selected))
            {
                await SetStateAsync(_phd2Guiding ? GuidingState.Guiding : GuidingState.Idle, force: true).ConfigureAwait(false);
                throw new Phd2TimeoutException($"PHD2 did not confirm that it stopped (it reports '{state}').");
            }

            await ApplyAppStateAsync(state).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop was asked for; PHD2 will report it. The state follows the notifications.
            throw;
        }
    }

    public async Task PauseGuidingAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        TaskCompletionSource reported;
        lock (_gate)
        {
            if (_state != GuidingState.Guiding)
            {
                throw new InvalidOperationException($"Guiding cannot be paused while the guider is {Describe(_state)}.");
            }

            reported = _pausedReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await connection.CallAsync(Phd2Protocol.Methods.SetPaused, new object[] { true }, cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        await WaitReportedAsync(reported, "pause", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeGuidingAsync(CancellationToken cancellationToken = default)
    {
        var connection = RequireConnection();
        TaskCompletionSource reported;
        lock (_gate)
        {
            if (_state != GuidingState.Paused)
            {
                throw new InvalidOperationException($"Guiding is not paused; the guider is {Describe(_state)}.");
            }

            reported = _resumedReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        await connection.CallAsync(Phd2Protocol.Methods.SetPaused, new object[] { false }, cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        await WaitReportedAsync(reported, "resume", cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitReportedAsync(TaskCompletionSource reported, string what, CancellationToken cancellationToken)
    {
        try
        {
            await reported.Task.WaitAsync(_options.PauseTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new Phd2TimeoutException($"PHD2 did not report that it did '{what}'.");
        }
    }

    // ------------------------------------------------------------------ dither and settle

    public Task DitherAsync(double amplitudePixels, CancellationToken cancellationToken = default) =>
        DitherAsync(new DitherRequest(amplitudePixels), cancellationToken);

    /// <summary>
    /// Asks PHD2 to dither. PHD2 moves the lock position by up to the amount (times its own dither scale), then settles by the tolerance, the
    /// stable time and the timeout of the request. This returns when PHD2 reported that the lock position moved; when the request carries no
    /// settle of its own, it returns only after PHD2 reported the end of the settle with the default settle. When the request has a settle,
    /// the outcome of it is what <see cref="SettleAsync"/> waits for next.
    /// </summary>
    public async Task DitherAsync(DitherRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.AmplitudePixels) || request.AmplitudePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.AmplitudePixels, "Dither amplitude must be a finite, positive number of guider pixels.");
        }

        var connection = RequireConnection();
        var settle = request.Settle ?? _options.DefaultSettle;
        Settle operation;
        TaskCompletionSource reported;
        lock (_gate)
        {
            if (_state != GuidingState.Guiding)
            {
                throw new InvalidOperationException($"The guider '{Id}' cannot dither while it is {Describe(_state)}; it has to be guiding.");
            }

            operation = _settle = new Settle(settle, _time, isStart: false);
            reported = _ditherReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _logger.LogInformation(
            Phd2Log.DitherRequested,
            "Dither requested on PHD2: {AmplitudePixels} px, RA only {RaOnly}, settle at most {TolerancePixels} px for {StableSeconds} s within {TimeoutSeconds} s (device {DeviceId})",
            request.AmplitudePixels, request.RaOnly, settle.MaximumErrorPixels, settle.StableDuration.TotalSeconds, settle.Timeout.TotalSeconds, Id);
        await SetStateAsync(GuidingState.Dithering).ConfigureAwait(false);
        try
        {
            await connection.CallAsync(
                Phd2Protocol.Methods.Dither,
                new { amount = request.AmplitudePixels, raOnly = request.RaOnly, settle = SettleObject(settle) },
                cancellationToken, _options.RequestTimeout).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                _settle = null;
            }

            await SetStateAsync(_phd2Guiding ? GuidingState.Guiding : GuidingState.Idle, force: true).ConfigureAwait(false);
            throw;
        }

        // PHD2 moves the lock position and tells so; a settle that ends before that (an error) ends the wait too.
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(_options.DitherTimeout);
            try
            {
                await Task.WhenAny(reported.Task, operation.Completion.Task).WaitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new Phd2TimeoutException("PHD2 did not report that the dither moved the lock position.");
            }
        }

        if (operation.Completion.Task.IsCompleted && operation.Completion.Task.Result.Outcome != GuidingSettleOutcome.Settled)
        {
            throw FailureOf(operation.Completion.Task.Result, settle);
        }

        _logger.LogInformation(Phd2Log.DitherStarted, "PHD2 moved the lock position (dither, device {DeviceId})", Id);
        if (request.Settle is null)
        {
            // Nobody else waits for the settle of this dither: the dither is over when the settle is.
            await AwaitSettleAsync(operation, settle, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SettleAsync(GuidingSettleOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        RequireConnection();
        Settle? operation;
        lock (_gate)
        {
            operation = _settle is { Consumed: false } ? _settle : null;
            if (operation is null && _state is not (GuidingState.Guiding or GuidingState.Settling or GuidingState.Dithering or GuidingState.StarLost))
            {
                throw new InvalidOperationException($"The guider '{Id}' is not guiding; there is no guiding to settle.");
            }

            if (operation is not null)
            {
                operation.Consumed = true;
            }
        }

        if (operation is not null)
        {
            // PHD2 settles by the settle of the dither or of the start that began it, and says how it ended.
            await AwaitSettleAsync(operation, new GuidingSettleOptions(operation.Tolerance, operation.RequiredStable, operation.Timeout), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await SettleFromSamplesAsync(options, cancellationToken).ConfigureAwait(false);
    }

    private async Task AwaitSettleAsync(Settle operation, GuidingSettleOptions settle, CancellationToken cancellationToken)
    {
        operation.Consumed = true;
        SettleResult result;
        try
        {
            result = await operation.Completion.Task.WaitAsync(settle.Timeout + _options.SettleGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_gate)
            {
                _settle = operation.Finish(GuidingSettleOutcome.TimedOut, "PHD2 did not report the end of the settle.", _time);
            }

            await SetStateAsync(_phd2Guiding ? GuidingState.Guiding : GuidingState.Idle, force: true).ConfigureAwait(false);
            throw new GuidingSettleTimeoutException($"Guiding did not settle within {settle.Timeout.TotalSeconds:0} s: PHD2 did not report the end of the settle.");
        }

        if (result.Outcome != GuidingSettleOutcome.Settled)
        {
            throw FailureOf(result, settle);
        }
    }

    private Exception FailureOf(SettleResult result, GuidingSettleOptions settle) => result.Outcome == GuidingSettleOutcome.TimedOut
        ? new GuidingSettleTimeoutException($"Guiding did not settle within {settle.Timeout.TotalSeconds:0} s: {result.Reason}")
        : new Phd2SettleFailedException($"Guiding did not settle: {result.Reason}");

    // Without a settle of PHD2 to wait for (a settle on request, long after a dither): the criterion is applied to the guide steps, on a
    // monotonic clock. Any step above the tolerance, or a step without a usable error, starts the stable time again.
    private async Task SettleFromSamplesAsync(GuidingSettleOptions options, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        long? stableSince = null;
        long lastTick = 0;
        var maximumGap = TimeSpan.FromSeconds(Math.Max(5, (_exposureSeconds ?? 1) * 3));
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (elapsed >= options.Timeout)
            {
                throw new GuidingSettleTimeoutException($"Guiding did not settle within {options.Timeout.TotalSeconds:0} s.");
            }

            if (GuidingState is GuidingState.Idle or GuidingState.Stopping || ConnectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException("Guiding stopped, or the guider was disconnected, while waiting for it to settle.");
            }

            Task signal;
            long tick;
            double? distance;
            lock (_gate)
            {
                signal = _sampleSignal.Task;
                (tick, distance) = (_lastSampleTick, _lastDistancePixels);
            }

            if (tick > start && tick != lastTick)
            {
                lastTick = tick;
                if (distance is { } d && d <= options.MaximumErrorPixels)
                {
                    if (stableSince is null || Stopwatch.GetElapsedTime(stableSince.Value, tick) > maximumGap)
                    {
                        stableSince = tick;
                    }

                    if (Stopwatch.GetElapsedTime(stableSince.Value, tick) >= options.StableDuration)
                    {
                        return;
                    }
                }
                else
                {
                    stableSince = null;
                }

                continue;
            }

            var remaining = options.Timeout - elapsed;
            try
            {
                await signal.WaitAsync(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No step in that second: the loop checks the time limit and the state again.
            }
        }
    }

    // ------------------------------------------------------------------ notifications of PHD2

    private async Task HandleEventAsync(Phd2Event e)
    {
        switch (e.Name)
        {
            case Phd2Protocol.Events.Version:
                lock (_gate)
                {
                    _version = e.Text("PHDVersion") is { } version ? version + e.Text("PHDSubver") : _version;
                }

                break;

            case Phd2Protocol.Events.AppState:
                if (e.Text("State") is { } state)
                {
                    await ApplyAppStateAsync(state).ConfigureAwait(false);
                }

                break;

            case Phd2Protocol.Events.StarSelected:
                await SetPhd2StateAsync(GuidingState.StarSelected, onlyFrom: [GuidingState.Idle]).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.LoopingExposures:
                await SetPhd2StateAsync(GuidingState.Looping, onlyFrom: [GuidingState.Idle, GuidingState.StarSelected, GuidingState.Starting, GuidingState.Looping]).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.LoopingExposuresStopped:
            case Phd2Protocol.Events.GuidingStopped:
                lock (_gate)
                {
                    _phd2Guiding = false;
                }

                _logger.LogInformation(Phd2Log.GuidingStopped, "PHD2 reports: {Event} (device {DeviceId})", e.Name, Id);
                await SetStateAsync(GuidingState.Idle, force: true).ConfigureAwait(false);
                FailStartingSettle("PHD2 stopped before guiding was running.");
                lock (_gate)
                {
                    _stopped?.TrySetResult();
                }

                break;

            case Phd2Protocol.Events.StartCalibration:
                lock (_gate)
                {
                    _phd2Guiding = false;
                }

                _logger.LogInformation(Phd2Log.CalibrationStarted, "PHD2 started to calibrate (mount {Mount}, device {DeviceId})", e.Text("Mount"), Id);
                await SetStateAsync(GuidingState.Calibrating, force: true).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.CalibrationComplete:
                lock (_gate)
                {
                    _info = _info is null ? null : _info with { IsCalibrated = true };
                }

                _logger.LogInformation(Phd2Log.CalibrationCompleted, "PHD2 completed the calibration (mount {Mount}, device {DeviceId})", e.Text("Mount"), Id);
                StateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case Phd2Protocol.Events.CalibrationFailed:
                var reason = e.Text("Reason") ?? "no reason given";
                _logger.LogWarning(Phd2Log.CalibrationFailed, "PHD2 failed to calibrate: {Reason} (device {DeviceId})", reason, Id);
                lock (_gate)
                {
                    _lastAlert = "Calibration failed: " + reason;
                }

                await SetStateAsync(GuidingState.Idle, force: true).ConfigureAwait(false);
                FailStartingSettle("Calibration failed: " + reason);
                break;

            case Phd2Protocol.Events.StartGuiding:
                lock (_gate)
                {
                    _phd2Guiding = true;
                }

                await SetPhd2StateAsync(GuidingState.Guiding).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.Paused:
                await SetStateAsync(GuidingState.Paused, force: true).ConfigureAwait(false);
                lock (_gate)
                {
                    _pausedReported?.TrySetResult();
                }

                break;

            case Phd2Protocol.Events.Resumed:
                await SetPhd2StateAsync(GuidingState.Guiding, onlyFrom: [GuidingState.Paused]).ConfigureAwait(false);
                lock (_gate)
                {
                    _phd2Guiding = true;
                    _resumedReported?.TrySetResult();
                }

                break;

            case Phd2Protocol.Events.StarLost:
                lock (_gate)
                {
                    _lastDistancePixels = null;
                }

                if (GuidingState is GuidingState.Guiding)
                {
                    await SetStateAsync(GuidingState.StarLost).ConfigureAwait(false);
                }

                break;

            case Phd2Protocol.Events.GuideStep:
                await HandleGuideStepAsync(e).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.GuidingDithered:
                lock (_gate)
                {
                    _ditherReported?.TrySetResult();
                }

                break;

            case Phd2Protocol.Events.SettleBegin:
                lock (_gate)
                {
                    _settle ??= new Settle(_options.DefaultSettle, _time, isStart: false);
                    _settle.Begin(_time);
                }

                _logger.LogInformation(Phd2Log.SettleStarted, "PHD2 began to settle (device {DeviceId})", Id);
                await SetStateAsync(GuidingState.Settling, force: true).ConfigureAwait(false);
                lock (_gate)
                {
                    _ditherReported?.TrySetResult();
                }

                break;

            case Phd2Protocol.Events.Settling:
                lock (_gate)
                {
                    _settle?.Update(e.Number("Distance"), e.Number("Time"), e.Number("SettleTime"), e.Flag("StarLocked"));
                }

                StateChanged?.Invoke(this, EventArgs.Empty);
                break;

            case Phd2Protocol.Events.SettleDone:
                await HandleSettleDoneAsync(e).ConfigureAwait(false);
                break;

            case Phd2Protocol.Events.Alert:
                lock (_gate)
                {
                    _lastAlert = e.Text("Msg");
                }

                _logger.LogInformation("PHD2 alert ({Type}): {Message}", e.Text("Type"), e.Text("Msg"));
                break;

            default:
                _logger.LogTrace("PHD2 notification {Event} is not used", e.Name);
                break;
        }
    }

    private async Task HandleSettleDoneAsync(Phd2Event e)
    {
        var status = e.Number("Status") ?? -1;
        var error = e.Text("Error");
        SettleResult result;
        TimeSpan duration;
        Settle finished;
        lock (_gate)
        {
            finished = _settle ??= new Settle(_options.DefaultSettle, _time, isStart: false);
            result = status == 0
                ? new SettleResult(GuidingSettleOutcome.Settled, string.Empty)
                : error is { } text && (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) || text.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                    ? new SettleResult(GuidingSettleOutcome.TimedOut, text)
                    : new SettleResult(GuidingSettleOutcome.Failed, error ?? "PHD2 gave no reason.");
            duration = finished.Elapsed(_time);
        }

        if (result.Outcome == GuidingSettleOutcome.Settled)
        {
            _logger.LogInformation(Phd2Log.SettleCompleted, "PHD2 settled after {DurationSeconds:0.0} s (device {DeviceId})", duration.TotalSeconds, Id);
        }
        else
        {
            _logger.LogWarning(
                Phd2Log.SettleFailed, "PHD2 did not settle after {DurationSeconds:0.0} s: {Reason} (device {DeviceId})", duration.TotalSeconds, result.Reason, Id);
        }

        bool guiding;
        lock (_gate)
        {
            guiding = _phd2Guiding || result.Outcome == GuidingSettleOutcome.Settled;
            if (result.Outcome == GuidingSettleOutcome.Settled)
            {
                _phd2Guiding = true;
            }

            _ditherReported?.TrySetResult();
        }

        // The state first, then whoever waits for the end of the settle is released: it finds the guider as it is after the settle.
        await SetStateAsync(guiding ? GuidingState.Guiding : GuidingState.Idle, force: true).ConfigureAwait(false);
        lock (_gate)
        {
            finished.Finish(result.Outcome, result.Reason, _time);
        }
    }

    private async Task HandleGuideStepAsync(Phd2Event e)
    {
        var ra = e.Number("RADistanceRaw");
        var dec = e.Number("DECDistanceRaw");
        var errorCode = e.Number("ErrorCode");
        var usable = errorCode is null or 0;
        double? scale;
        lock (_gate)
        {
            scale = _pixelScale;
        }

        // The error of a guide step is given in pixels; arcseconds are the pixels times the pixel scale of the guide camera, which PHD2
        // tells with get_pixel_scale. Without it the arcseconds stay unknown, and so does the RMS.
        var raPixels = usable ? ra : null;
        var decPixels = usable ? dec : null;
        var sample = new GuidingSample(
            _time.GetUtcNow(),
            raPixels,
            decPixels,
            raPixels * scale,
            decPixels * scale,
            Pulse(e.Number("RADuration"), e.Text("RADirection"), "East", "West"),
            Pulse(e.Number("DECDuration"), e.Text("DECDirection"), "North", "South"),
            e.Number("SNR"));
        lock (_gate)
        {
            _starSnr = sample.StarSnr ?? _starSnr;
            _starMass = e.Number("StarMass") ?? _starMass;
            _phd2Guiding = true;
            _lastSampleTick = Stopwatch.GetTimestamp();
            _lastDistancePixels = sample.TotalErrorPixels;
            var signal = _sampleSignal;
            _sampleSignal = NewSignal();
            signal.TrySetResult();
        }

        History.Add(sample);
        if (scale is null && Interlocked.Exchange(ref _scaleRequested, 1) == 0)
        {
            _ = RefreshPixelScaleAsync();
        }

        // A guide step means PHD2 is guiding, unless Sidera is in the middle of something that says more (a dither, a settle).
        await SetPhd2StateAsync(
            GuidingState.Guiding,
            onlyFrom: [GuidingState.Idle, GuidingState.StarSelected, GuidingState.Looping, GuidingState.Starting, GuidingState.StarLost, GuidingState.Calibrating, GuidingState.Guiding])
            .ConfigureAwait(false);
    }

    private async Task RefreshPixelScaleAsync()
    {
        try
        {
            Phd2Connection? connection;
            lock (_gate)
            {
                connection = _conn;
            }

            if (connection is null)
            {
                return;
            }

            var scale = ValidScale(await connection.CallAsync(Phd2Protocol.Methods.GetPixelScale, null, CancellationToken.None, _options.RequestTimeout).ConfigureAwait(false));
            lock (_gate)
            {
                _pixelScale = scale ?? _pixelScale;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The pixel scale of PHD2 could not be read");
            Interlocked.Exchange(ref _scaleRequested, 0);
        }
    }

    // A pulse as a number: positive towards the first direction, negative towards the second; null when there was none.
    private static double? Pulse(double? milliseconds, string? direction, string positive, string negative) =>
        milliseconds is { } ms && ms > 0
            ? direction == positive ? ms : direction == negative ? -ms : null
            : null;

    private void FailStartingSettle(string reason)
    {
        lock (_gate)
        {
            if (_settle is { IsStart: true, IsDone: false } operation)
            {
                _settle = operation.Finish(GuidingSettleOutcome.Failed, reason, _time);
            }
        }
    }

    private async Task ApplyAppStateAsync(string state)
    {
        var mapped = state switch
        {
            Phd2Protocol.AppStates.Stopped => GuidingState.Idle,
            Phd2Protocol.AppStates.Selected => GuidingState.StarSelected,
            Phd2Protocol.AppStates.Calibrating => GuidingState.Calibrating,
            Phd2Protocol.AppStates.Guiding => GuidingState.Guiding,
            Phd2Protocol.AppStates.LostLock => GuidingState.StarLost,
            Phd2Protocol.AppStates.Paused => GuidingState.Paused,
            Phd2Protocol.AppStates.Looping => GuidingState.Looping,
            _ => (GuidingState?)null,
        };
        if (mapped is null)
        {
            _logger.LogDebug("PHD2 reports the unknown state {State}", state);
            return;
        }

        lock (_gate)
        {
            _phd2Guiding = mapped is GuidingState.Guiding or GuidingState.StarLost;
        }

        await SetStateAsync(mapped.Value, force: true).ConfigureAwait(false);
    }

    // A state that PHD2 reports; it does not override a dither or a settle that Sidera is waiting for unless it is one of the listed.
    private async Task SetPhd2StateAsync(GuidingState next, GuidingState[]? onlyFrom = null)
    {
        lock (_gate)
        {
            if (onlyFrom is not null && !onlyFrom.Contains(_state))
            {
                return;
            }
        }

        await SetStateAsync(next).ConfigureAwait(false);
    }

    private async Task SetStateAsync(GuidingState next, bool force = false)
    {
        GuidingState previous;
        lock (_gate)
        {
            previous = _state;
            if (previous == next)
            {
                return;
            }

            _state = next;
        }

        _logger.LogDebug("Guiding state of {DeviceId}: {Previous} -> {Next}", Id, previous, next);
        if (_events is not null)
        {
            await _events.PublishAsync(new GuidingStateChanged(Id, previous, next)).ConfigureAwait(false);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Everything that waits for PHD2 stops waiting: the connection is gone.
    private void FailWaiters(Exception reason)
    {
        lock (_gate)
        {
            _settle = _settle is { IsDone: false } operation ? operation.Finish(GuidingSettleOutcome.Failed, reason.Message, _time) : _settle;
            foreach (var waiter in new[] { _ditherReported, _stopped, _pausedReported, _resumedReported })
            {
                waiter?.TrySetException(reason);
            }
        }
    }

    private GuidingSettleStatus SettleStatus() => _settle?.Status(_time) ?? GuidingSettleStatus.None;

    private static string Describe(GuidingState state) => state switch
    {
        GuidingState.Idle => "not guiding",
        _ => state.ToString().ToLowerInvariant(),
    };

    // The settle object of PHD2: pixels, time and timeout, the last two in seconds.
    private static object SettleObject(GuidingSettleOptions settle) => new
    {
        pixels = settle.MaximumErrorPixels,
        time = settle.StableDuration.TotalSeconds,
        timeout = settle.Timeout.TotalSeconds,
    };

    private static object SettleParameter(GuidingSettleOptions settle) => new { settle = SettleObject(settle) };

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ------------------------------------------------------------------ the settle that PHD2 is doing

    internal sealed record SettleResult(GuidingSettleOutcome Outcome, string Reason);

    // One settle of PHD2, after a dither or the start of guiding: what was asked, how far PHD2 says it is, and how it ended.
    private sealed class Settle
    {
        private long? _begun;
        private long? _ended;
        private double? _distance;
        private TimeSpan? _stableFor;
        private double? _requiredSeconds;

        public Settle(GuidingSettleOptions options, TimeProvider time, bool isStart)
        {
            Tolerance = options.MaximumErrorPixels;
            RequiredStable = options.StableDuration;
            Timeout = options.Timeout;
            IsStart = isStart;
            Created = time.GetTimestamp();
        }

        public double Tolerance { get; }
        public TimeSpan RequiredStable { get; }
        public TimeSpan Timeout { get; }
        public bool IsStart { get; }
        public long Created { get; }
        public bool Consumed { get; set; }
        public TaskCompletionSource<SettleResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public GuidingSettleOutcome Outcome { get; private set; }
        public string? Reason { get; private set; }
        public bool IsDone => Outcome != GuidingSettleOutcome.None;

        public void Begin(TimeProvider time) => _begun ??= time.GetTimestamp();

        public void Update(double? distance, double? stableSeconds, double? requiredSeconds, bool? starLocked)
        {
            _distance = starLocked == false ? null : distance;
            _stableFor = stableSeconds is { } s ? TimeSpan.FromSeconds(s) : _stableFor;
            _requiredSeconds = requiredSeconds ?? _requiredSeconds;
        }

        public TimeSpan Elapsed(TimeProvider time) => time.GetElapsedTime(_begun ?? Created, _ended ?? time.GetTimestamp());

        public Settle Finish(GuidingSettleOutcome outcome, string reason, TimeProvider time)
        {
            if (!IsDone)
            {
                Outcome = outcome;
                Reason = reason;
                _ended = time.GetTimestamp();
                Completion.TrySetResult(new SettleResult(outcome, reason));
            }

            return this;
        }

        public GuidingSettleStatus Status(TimeProvider time) => new(
            IsActive: !IsDone && _begun is not null,
            DistancePixels: IsDone ? null : _distance,
            StableFor: IsDone ? null : _stableFor,
            TolerancePixels: Tolerance,
            RequiredStable: _requiredSeconds is { } r ? TimeSpan.FromSeconds(r) : RequiredStable,
            Timeout: Timeout,
            Elapsed: _begun is null ? TimeSpan.Zero : Elapsed(time),
            LastOutcome: Outcome,
            LastDuration: IsDone ? Elapsed(time) : null,
            FailureReason: Outcome is GuidingSettleOutcome.TimedOut or GuidingSettleOutcome.Failed ? Reason : null);
    }
}

internal static class Phd2Log
{
    public static readonly EventId Connecting = new(2001, "Phd2Connecting");
    public static readonly EventId Connected = new(2002, "Phd2Connected");
    public static readonly EventId ConnectFailed = new(2003, "Phd2ConnectFailed");
    public static readonly EventId Disconnected = new(2004, "Phd2Disconnected");
    public static readonly EventId GuidingRequested = new(2010, "GuidingRequested");
    public static readonly EventId GuidingStarted = new(2011, "GuidingStarted");
    public static readonly EventId GuidingStopped = new(2012, "GuidingStopped");
    public static readonly EventId CalibrationStarted = new(2020, "CalibrationStarted");
    public static readonly EventId CalibrationCompleted = new(2021, "CalibrationCompleted");
    public static readonly EventId CalibrationFailed = new(2022, "CalibrationFailed");
    public static readonly EventId DitherRequested = new(2030, "DitherRequested");
    public static readonly EventId DitherStarted = new(2031, "DitherStarted");
    public static readonly EventId SettleStarted = new(2032, "SettleStarted");
    public static readonly EventId SettleCompleted = new(2033, "SettleCompleted");
    public static readonly EventId SettleFailed = new(2034, "SettleFailed");
}

using System.Diagnostics;
using Astra.Ascom.Drivers;
using Astra.Ascom.Infrastructure;
using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Focusers;
using Microsoft.Extensions.Logging;

namespace Astra.Ascom.Focusers;

/// <summary>
/// An ASCOM focuser as Astra's <see cref="IFocuser"/> and <see cref="IFocuserControl"/>. An absolute focuser's range is
/// 0 to the driver's MaxStep, read once at connect. A relative focuser connects too, but says <see cref="IsAbsolute"/>
/// false: it has no position, moving to a target is refused before anything moves, and it is only moved by steps.
/// <para>
/// A move is <c>Move</c>, then polling <c>IsMoving</c> until it is over; it is never retried. Events follow the
/// simulated focuser: motion Idle to Moving, position changed, motion back to Idle, with the position the focuser really
/// reports; a move that ended any other way is reported at the position it was found at, never at the target. A relative
/// focuser has no position to report, so its moves raise no motion events; <see cref="IObservableDevice.StateChanged"/>
/// covers them.
/// </para>
/// <para>
/// Cancelling (or a timeout) asks the driver to <c>Halt</c> and then watches whether the motion ended. Whether the
/// hardware really stopped is logged and stated in the error of a timeout; a driver without Halt, or one that fails it,
/// is never claimed to have stopped.
/// </para>
/// </summary>
public sealed class AscomFocuser : AscomDevice<IAscomFocuserDriver>, IFocuserControl
{
    private readonly IAscomDriverFactory _drivers;
    private readonly object _state = new();
    private readonly CapabilityHolder<FocuserCapabilities> _capabilities = new();
    private FocuserMotionState _motionState = FocuserMotionState.Idle;
    private FocuserTelemetry? _telemetry;
    private bool _absolute = true;
    private int _position;
    private int _maxPosition;

    public AscomFocuser(
        DeviceId id,
        string name,
        string progId,
        IAscomDriverFactory drivers,
        IEventPublisher? events = null,
        ILogger? logger = null,
        AscomTimings? timings = null)
        : base(id, name, DeviceType.Focuser, progId, events, logger, timings)
    {
        _drivers = drivers;
    }

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged
    {
        add => _capabilities.Changed += value;
        remove => _capabilities.Changed -= value;
    }

    public DeviceCapabilities<FocuserCapabilities> Capabilities => _capabilities.Current;

    public bool IsAbsolute
    {
        get { lock (_state) { return _absolute; } }
    }

    public FocuserTelemetry? Telemetry
    {
        get { lock (_state) { return _telemetry; } }
    }

    public FocuserMotionState MotionState
    {
        get { lock (_state) { return _motionState; } }
    }

    public int Position
    {
        get { lock (_state) { return _position; } }
    }

    public int MinPosition => 0;

    public int MaxPosition
    {
        get { lock (_state) { return _maxPosition; } }
    }

    protected override bool IsBusy => MotionState == FocuserMotionState.Moving;

    protected override string BusyDescription => "the focuser is moving";

    protected override IAscomFocuserDriver CreateDriver() => _drivers.CreateFocuser(ProgId);

    protected override void OnConnected(IAscomFocuserDriver driver)
    {
        var probe = new CapabilityProbe(Name);
        var identity = probe.Read("identity", () => driver.Identity, new DriverMetadata());
        var absolute = driver.Absolute;
        int? max = null;
        var position = 0;
        if (absolute)
        {
            max = driver.MaxStep;
            if (max <= 0)
            {
                throw new AscomUnsupportedException(
                    Id, ProgId, "connect", $"{Name} ({ProgId}) reports a maximum position of {max}; it cannot be used.");
            }

            position = driver.Position;
        }

        var maxIncrement = probe.Try("MaxIncrement", () => driver.MaxIncrement);
        var stepSize = probe.Try("StepSize", () => driver.StepSize);
        var temperature = probe.Try("Temperature", () => driver.Temperature);
        var hasTemperature = temperature is { } t && double.IsFinite(t);
        var tempCompAvailable = probe.Read("TempCompAvailable", () => driver.TempCompAvailable, false);
        var tempComp = tempCompAvailable ? probe.Try("TempComp", () => driver.TempComp) : null;
        var isMoving = probe.Read("IsMoving", () => driver.IsMoving, false);

        var capabilities = new FocuserCapabilities
        {
            Driver = identity,
            Absolute = absolute,
            MaxStep = max,
            MaxIncrement = maxIncrement is > 0 ? maxIncrement : null,
            StepSizeMicrons = stepSize is > 0 and < 1e6 ? stepSize : null,
            HasTemperature = hasTemperature,
            TempCompAvailable = tempCompAvailable,
            CanHalt = null,
            Notes = probe.Notes.ToList(),
        };

        lock (_state)
        {
            _absolute = absolute;
            _maxPosition = max ?? 0;
            _position = position;
            _motionState = FocuserMotionState.Idle;
            _telemetry = new FocuserTelemetry
            {
                Position = absolute ? position : null,
                IsMoving = isMoving,
                Temperature = hasTemperature ? temperature : null,
                TempComp = tempComp,
            };
        }

        _capabilities.Set(capabilities, this);
        foreach (var note in probe.Notes)
        {
            Logger.LogDebug("{Device}: capability probe: {Note}", Name, note);
        }

        Logger.LogInformation(
            "{Device}: {Kind} focuser, position {Position}, range 0 to {Max}, temperature {HasTemperature}, temperature compensation {TempComp}",
            Name, absolute ? "absolute" : "relative", absolute ? position : "none", max?.ToString() ?? "none", hasTemperature, tempCompAvailable);
    }

    protected override void OnDisconnected()
    {
        lock (_state)
        {
            _motionState = FocuserMotionState.Idle;
            _telemetry = null;
            _absolute = true;
        }

        _capabilities.Reset(this);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        var telemetry = await CallAsync(
            "read the state of",
            d => new FocuserTelemetry
            {
                IsMoving = d.IsMoving,
                Position = capabilities.Absolute ? TryRead(d) : null,
                Temperature = capabilities.HasTemperature ? TryTemperature(d) : null,
                TempComp = capabilities.TempCompAvailable ? TryTempComp(d) : null,
            },
            cancellationToken);
        lock (_state)
        {
            _telemetry = telemetry;
            if (telemetry.Position is { } p && _motionState != FocuserMotionState.Moving)
            {
                _position = p;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SetTempCompAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!capabilities.TempCompAvailable)
        {
            throw new AscomUnsupportedException(
                Id, ProgId, "set temperature compensation", $"{Name} ({ProgId}) has no temperature compensation.");
        }

        using var scope = BeginScope();
        await CallAsync("set temperature compensation of", d => d.TempComp = enabled, cancellationToken);
        Logger.LogInformation("{Device}: temperature compensation {State}", Name, enabled ? "on" : "off");
        await RefreshAsync(cancellationToken);
    }

    /// <exception cref="ArgumentOutOfRangeException">The move is longer than MaxIncrement, or leaves the range.</exception>
    public async Task MoveByAsync(int steps, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (steps == 0)
        {
            return;
        }

        if (capabilities.MaxIncrement is { } increment && Math.Abs((long)steps) > increment)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, $"One move of this focuser may be at most {increment} steps.");
        }

        if (!capabilities.Absolute)
        {
            await MoveRelativeAsync(steps, cancellationToken);
            return;
        }

        var current = await CallAsync("read the position of", d => d.Position, cancellationToken);
        var target = (long)current + steps;
        if (target < MinPosition || target > MaxPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, $"Moving by {steps} from {current} would leave the range {MinPosition} to {MaxPosition}.");
        }

        await MoveToAsync((int)target, cancellationToken);
    }

    /// <exception cref="ArgumentOutOfRangeException">The target is outside 0 to MaxPosition.</exception>
    /// <exception cref="InvalidOperationException">The focuser is not connected, is relative, or is already moving.</exception>
    public async Task MoveToAsync(int target, CancellationToken cancellationToken = default)
    {
        Session();
        if (!IsAbsolute)
        {
            throw new InvalidOperationException(
                $"{Name} is a relative focuser: it has no positions to move to. Move it by a number of steps instead.");
        }

        if (target < MinPosition || target > MaxPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(target), target, $"Focuser position must be between {MinPosition} and {MaxPosition}.");
        }

        int startedAt;
        lock (_state)
        {
            if (_motionState == FocuserMotionState.Moving)
            {
                throw new InvalidOperationException("The focuser is already moving.");
            }

            _motionState = FocuserMotionState.Moving;
            startedAt = _position;
        }

        using var scope = BeginScope();
        var ended = false;
        var announced = false;
        try
        {
            // The position now, not the one remembered: the focuser may have been moved by hand or by another program.
            startedAt = await CallAsync("read the position of", d => d.Position, cancellationToken);
            SetPosition(startedAt);
            if (startedAt == target)
            {
                Logger.LogDebug("{Device} is already at {Target}", Name, target);
                ended = true;
                SetIdle();
                return;
            }

            Logger.LogInformation("Moving {Device} from {From} to {Target}", Name, startedAt, target);
            announced = true;
            await PublishAsync(new FocuserMotionStateChanged(Id, FocuserMotionState.Idle, FocuserMotionState.Moving, startedAt), cancellationToken);
            await CallAsync("move", d => d.Move(target), cancellationToken);

            var clock = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(Timings.FocuserPollInterval, cancellationToken);
                var (moving, position) = await CallAsync("poll", d => (d.IsMoving, TryRead(d)), cancellationToken);
                if (position is { } seen)
                {
                    SetPosition(seen);
                }

                if (!moving)
                {
                    break;
                }

                if (clock.Elapsed > Timings.FocuserMoveTimeout)
                {
                    var stopped = await StopAsync();
                    throw new AscomTimeoutException(
                        Id, ProgId, "move",
                        $"{Name} did not finish moving to {target} within {Timings.FocuserMoveTimeout.TotalSeconds:0} s. " +
                        (stopped
                            ? "The driver accepted Halt and the focuser reports that it stopped."
                            : "The focuser could not be confirmed stopped: it may still be moving."));
                }
            }

            var final = await CallAsync("read the position of", d => d.Position, cancellationToken);
            SetPosition(final);
            if (final != target)
            {
                Logger.LogWarning("{Device} stopped at {Final}, not at the target {Target}", Name, final, target);
            }

            ended = true;
            SetIdle();
            await PublishAsync(new FocuserPositionChanged(Id, startedAt, final));
            await PublishAsync(new FocuserMotionStateChanged(Id, FocuserMotionState.Moving, FocuserMotionState.Idle, final));
            Logger.LogInformation("{Device} arrived at {Position}", Name, final);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogInformation("Move of {Device} cancelled; asking the driver to halt", Name);
            var stopped = await StopAsync();
            Logger.LogInformation(
                stopped
                    ? "{Device} confirmed stopped after the cancelled move"
                    : "{Device} could not be confirmed stopped after the cancelled move; it may still be moving",
                Name);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Failed on the way: what the focuser does now is not known; ask it to stop, then report where it is.
            Logger.LogError(ex, "Move of {Device} failed", Name);
            await StopAsync();
            throw;
        }
        finally
        {
            if (!ended)
            {
                var where = await ReadPositionQuietlyAsync() ?? Position;
                SetPosition(where);
                SetIdle();
                if (announced)
                {
                    if (where != startedAt)
                    {
                        await PublishAsync(new FocuserPositionChanged(Id, startedAt, where));
                    }

                    await PublishAsync(new FocuserMotionStateChanged(Id, FocuserMotionState.Moving, FocuserMotionState.Idle, where));
                }
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // A relative focuser: Move(steps), then IsMoving until it is over. No position exists, so none is reported.
    private async Task MoveRelativeAsync(int steps, CancellationToken cancellationToken)
    {
        Session();
        lock (_state)
        {
            if (_motionState == FocuserMotionState.Moving)
            {
                throw new InvalidOperationException("The focuser is already moving.");
            }

            _motionState = FocuserMotionState.Moving;
        }

        using var scope = BeginScope();
        try
        {
            Logger.LogInformation("Moving relative focuser {Device} by {Steps} steps", Name, steps);
            await CallAsync("move", d => d.Move(steps), cancellationToken);
            var clock = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(Timings.FocuserPollInterval, cancellationToken);
                if (!await CallAsync("poll", d => d.IsMoving, cancellationToken))
                {
                    break;
                }

                if (clock.Elapsed > Timings.FocuserMoveTimeout)
                {
                    var stopped = await StopAsync();
                    throw new AscomTimeoutException(
                        Id, ProgId, "move",
                        $"{Name} did not finish moving by {steps} steps within {Timings.FocuserMoveTimeout.TotalSeconds:0} s. " +
                        (stopped
                            ? "The driver accepted Halt and the focuser reports that it stopped."
                            : "The focuser could not be confirmed stopped: it may still be moving."));
                }
            }

            Logger.LogInformation("{Device} finished moving by {Steps} steps", Name, steps);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stopped = await StopAsync();
            Logger.LogInformation(
                stopped
                    ? "{Device} confirmed stopped after the cancelled move"
                    : "{Device} could not be confirmed stopped after the cancelled move; it may still be moving",
                Name);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "Move of {Device} failed", Name);
            await StopAsync();
            throw;
        }
        finally
        {
            SetIdle();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Halt, then watch IsMoving for a bounded time. True only when the focuser itself says it is no longer moving
    // after a Halt the driver accepted.
    private async Task<bool> StopAsync()
    {
        try
        {
            await CallAsync("halt", d => d.Halt(), CancellationToken.None).WaitAsync(Timings.StopWait);
            NoteHalt(true);
        }
        catch (Exception ex)
        {
            if (ex is AscomDeviceException { InnerException: { } inner } && inner.GetType().Name.Contains("NotImplemented", StringComparison.Ordinal))
            {
                NoteHalt(false);
            }

            Logger.LogWarning("{Device}: Halt did not work: {Reason}", Name, ex is AscomDeviceException ? ex.Message : AscomErrors.Describe(ex));
            return false;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timings.StopWait)
        {
            try
            {
                if (!await CallAsync("poll", d => d.IsMoving, CancellationToken.None).WaitAsync(Timings.StopWait))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("{Device}: could not tell whether the focuser stopped: {Reason}", Name, ex.Message);
                return false;
            }

            await Task.Delay(Timings.FocuserPollInterval);
        }

        return false;
    }

    private void NoteHalt(bool works)
    {
        var current = Capabilities.Value;
        if (current is null || current.CanHalt == works)
        {
            return;
        }

        _capabilities.Set(current with { CanHalt = works }, this);
    }

    private async Task<int?> ReadPositionQuietlyAsync()
    {
        try
        {
            return await CallAsync("read the position of", d => d.Position, CancellationToken.None).WaitAsync(Timings.StopWait);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: the position could not be read after the move: {Reason}", Name, ex.Message);
            return null;
        }
    }

    // A position read while the focuser moves is a courtesy; drivers may refuse it.
    private static int? TryRead(IAscomFocuserDriver driver)
    {
        try
        {
            return driver.Position;
        }
        catch
        {
            return null;
        }
    }

    private static double? TryTemperature(IAscomFocuserDriver driver)
    {
        try
        {
            var t = driver.Temperature;
            return double.IsFinite(t) ? t : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryTempComp(IAscomFocuserDriver driver)
    {
        try
        {
            return driver.TempComp;
        }
        catch
        {
            return null;
        }
    }

    private void SetPosition(int position)
    {
        lock (_state)
        {
            _position = position;
        }
    }

    private void SetIdle()
    {
        lock (_state)
        {
            _motionState = FocuserMotionState.Idle;
        }
    }
}

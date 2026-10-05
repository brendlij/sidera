using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Rotators;

namespace Sidera.Ascom.Rotators;

/// <summary>
/// An ASCOM rotator as Sidera's <see cref="IRotator"/> and <see cref="IRotatorControl"/>. The position is the ASCOM Position (degrees 0 to 360, in the frame that
/// MoveAbsolute commands, which may include a synchronization offset); the mechanical position is reported when the driver has one. Neither is the rotation of the
/// sky in an image: that relation is a calibration of Sidera's, kept with the rig.
/// <para>
/// A move is <c>MoveAbsolute</c> or <c>Move</c>, then polling <c>IsMoving</c> until it is over; it is never retried. Cancelling (or a timeout) asks the driver to
/// <c>Halt</c> and watches whether the motion ended, and says honestly when it could not be confirmed. Whether the driver implements Halt is only known after it was tried.
/// Everything runs on the dispatcher thread of the device, like the other ASCOM adapters.
/// </para>
/// </summary>
public sealed class AscomRotator : AscomDevice<IAscomRotatorDriver>, IRotatorControl
{
    private readonly IAscomDriverFactory _drivers;
    private readonly object _state = new();
    private readonly CapabilityHolder<RotatorCapabilities> _capabilities = new();
    private RotatorMotionState _motion = RotatorMotionState.Idle;
    private RotatorTelemetry? _telemetry;
    private double _position;
    private double? _mechanical;

    public AscomRotator(
        DeviceId id, string name, string progId, IAscomDriverFactory drivers, IEventPublisher? events = null, ILogger? logger = null, AscomTimings? timings = null)
        : base(id, name, DeviceType.Rotator, progId, events, logger, timings)
    {
        _drivers = drivers;
    }

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged
    {
        add => _capabilities.Changed += value;
        remove => _capabilities.Changed -= value;
    }

    public DeviceCapabilities<RotatorCapabilities> Capabilities => _capabilities.Current;

    public RotatorTelemetry? Telemetry
    {
        get { lock (_state) { return _telemetry; } }
    }

    public RotatorMotionState MotionState
    {
        get { lock (_state) { return _motion; } }
    }

    public double Position
    {
        get { lock (_state) { return _position; } }
    }

    public double? MechanicalPosition
    {
        get { lock (_state) { return _mechanical; } }
    }

    protected override bool IsBusy => MotionState == RotatorMotionState.Moving;

    protected override string BusyDescription => "the rotator is moving";

    protected override IAscomRotatorDriver CreateDriver() => _drivers.CreateRotator(ProgId);

    protected override void OnConnected(IAscomRotatorDriver driver)
    {
        var probe = new CapabilityProbe(Name);
        var identity = probe.Read("identity", () => driver.Identity, new DriverMetadata());
        var position = driver.Position;
        var mechanical = probe.Try("MechanicalPosition", () => driver.MechanicalPosition);
        var canReverse = probe.Read("CanReverse", () => driver.CanReverse, false);
        var reversed = canReverse ? probe.Try("Reverse", () => driver.Reverse) : null;
        var stepSize = probe.Try("StepSize", () => driver.StepSize);
        var isMoving = probe.Read("IsMoving", () => driver.IsMoving, false);

        // The standard has no flag for Sync: a driver of interface version 2 or later has the method, and only trying it says whether it works.
        var canSync = identity.InterfaceVersion is >= 2;
        var capabilities = new RotatorCapabilities
        {
            Driver = identity,
            AbsoluteMove = true,
            RelativeMove = true,
            CanHalt = null,
            CanSync = canSync,
            CanReverse = canReverse,
            HasMechanicalPosition = mechanical is { } m && double.IsFinite(m),
            StepSizeDegrees = stepSize is > 0 and < 360 ? stepSize : null,
            Notes = probe.Notes.ToList(),
        };

        lock (_state)
        {
            _position = Normalize(position);
            _mechanical = capabilities.HasMechanicalPosition ? Normalize(mechanical!.Value) : null;
            _motion = RotatorMotionState.Idle;
            _telemetry = new RotatorTelemetry { Position = _position, MechanicalPosition = _mechanical, IsMoving = isMoving, Reversed = reversed };
        }

        _capabilities.Set(capabilities, this);
        foreach (var note in probe.Notes)
        {
            Logger.LogDebug("{Device}: capability probe: {Note}", Name, note);
        }

        Logger.LogInformation(
            "{Device}: rotator at {Position:0.##}° (mechanical {Mechanical}), reverse {CanReverse}, sync {CanSync}",
            Name, position, capabilities.HasMechanicalPosition ? $"{mechanical:0.##}°" : "not reported", canReverse, canSync);
    }

    protected override void OnDisconnected()
    {
        lock (_state)
        {
            _motion = RotatorMotionState.Idle;
            _telemetry = null;
            _mechanical = null;
        }

        _capabilities.Reset(this);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        var telemetry = await CallAsync(
            "read the state of",
            d => new RotatorTelemetry
            {
                IsMoving = d.IsMoving,
                Position = Try(() => Normalize(d.Position)),
                MechanicalPosition = capabilities.HasMechanicalPosition ? Try(() => Normalize(d.MechanicalPosition)) : null,
                Reversed = capabilities.CanReverse ? TryBool(d) : null,
            },
            cancellationToken);
        lock (_state)
        {
            _telemetry = telemetry;
            if (_motion != RotatorMotionState.Moving)
            {
                if (telemetry.Position is { } p)
                {
                    _position = p;
                }

                _mechanical = telemetry.MechanicalPosition ?? _mechanical;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <exception cref="InvalidOperationException">The rotator is not connected or is already moving.</exception>
    public Task MoveToAsync(double positionDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(positionDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(positionDegrees), positionDegrees, "The position must be a finite number of degrees.");
        }

        var target = Normalize(positionDegrees);
        return MoveAsync($"to {target:0.##}°", d => d.MoveAbsolute(target), cancellationToken);
    }

    public Task MoveByAsync(double degrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The angle must be a finite number of degrees.");
        }

        return degrees == 0 ? Task.CompletedTask : MoveAsync($"by {degrees:+0.##;-0.##}°", d => d.Move(degrees), cancellationToken);
    }

    private async Task MoveAsync(string description, Action<IAscomRotatorDriver> start, CancellationToken cancellationToken)
    {
        Session();
        double startedAt;
        lock (_state)
        {
            if (_motion == RotatorMotionState.Moving)
            {
                throw new InvalidOperationException("The rotator is already moving.");
            }

            _motion = RotatorMotionState.Moving;
            startedAt = _position;
        }

        using var scope = BeginScope();
        var ended = false;
        var announced = false;
        try
        {
            // The position now, not the one remembered: it may have been turned by hand or by another program.
            startedAt = Normalize(await CallAsync("read the position of", d => d.Position, cancellationToken));
            SetPosition(startedAt);
            Logger.LogInformation("Moving {Device} {Move} from {From:0.##}°", Name, description, startedAt);
            announced = true;
            await PublishAsync(new RotatorMotionStateChanged(Id, RotatorMotionState.Idle, RotatorMotionState.Moving, startedAt), cancellationToken);
            await CallAsync("move", start, cancellationToken);

            var clock = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(Timings.RotatorPollInterval, cancellationToken);
                var (moving, position) = await CallAsync("poll", d => (d.IsMoving, Try(() => Normalize(d.Position))), cancellationToken);
                if (position is { } seen)
                {
                    SetPosition(seen);
                }

                if (!moving)
                {
                    break;
                }

                if (clock.Elapsed > Timings.RotatorMoveTimeout)
                {
                    var stopped = await StopAsync();
                    throw new AscomTimeoutException(
                        Id, ProgId, "move",
                        $"{Name} did not finish moving {description} within {Timings.RotatorMoveTimeout.TotalSeconds:0} s. " +
                        (stopped
                            ? "The driver accepted Halt and the rotator reports that it stopped."
                            : "The rotator could not be confirmed stopped: it may still be moving."));
                }
            }

            var final = Normalize(await CallAsync("read the position of", d => d.Position, cancellationToken));
            SetPosition(final);
            ended = true;
            SetIdle();
            await PublishAsync(new RotatorPositionChanged(Id, startedAt, final));
            await PublishAsync(new RotatorMotionStateChanged(Id, RotatorMotionState.Moving, RotatorMotionState.Idle, final));
            Logger.LogInformation("{Device} arrived at {Position:0.##}°", Name, final);
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
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
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
                    if (Math.Abs(where - startedAt) > 1e-6)
                    {
                        await PublishAsync(new RotatorPositionChanged(Id, startedAt, where));
                    }

                    await PublishAsync(new RotatorMotionStateChanged(Id, RotatorMotionState.Moving, RotatorMotionState.Idle, where));
                }
            }

            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task HaltAsync(CancellationToken cancellationToken = default)
    {
        _ = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        // Not under the scope of a move: a halt is allowed at any time, and it does not wait for the move that is running.
        if (!await StopAsync())
        {
            throw new AscomUnsupportedException(Id, ProgId, "halt", $"{Name} ({ProgId}) could not be confirmed stopped; it may still be moving.");
        }
    }

    public async Task SyncAsync(double positionDegrees, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!capabilities.CanSync)
        {
            throw new AscomUnsupportedException(Id, ProgId, "sync", $"{Name} ({ProgId}) cannot be synchronized.");
        }

        if (MotionState == RotatorMotionState.Moving)
        {
            throw new InvalidOperationException("The rotator is moving.");
        }

        var target = Normalize(positionDegrees);
        using var scope = BeginScope();
        await CallAsync("sync", d => d.Sync(target), cancellationToken);
        Logger.LogInformation("{Device}: position synchronized to {Position:0.##}°", Name, target);
        await RefreshAsync(cancellationToken);
    }

    public async Task SetReversedAsync(bool reversed, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!capabilities.CanReverse)
        {
            throw new AscomUnsupportedException(Id, ProgId, "reverse", $"{Name} ({ProgId}) cannot reverse its direction.");
        }

        using var scope = BeginScope();
        await CallAsync("reverse", d => d.Reverse = reversed, cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    // Halt, then watch IsMoving for a bounded time. True only when the rotator itself says it no longer moves after a Halt that the driver accepted.
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
                Logger.LogWarning("{Device}: could not tell whether the rotator stopped: {Reason}", Name, ex.Message);
                return false;
            }

            await Task.Delay(Timings.RotatorPollInterval);
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

    private async Task<double?> ReadPositionQuietlyAsync()
    {
        try
        {
            return Normalize(await CallAsync("read the position of", d => d.Position, CancellationToken.None).WaitAsync(Timings.StopWait));
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: the position could not be read after the move: {Reason}", Name, ex.Message);
            return null;
        }
    }

    private static double? Try(Func<double> read)
    {
        try
        {
            var value = read();
            return double.IsFinite(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? TryBool(IAscomRotatorDriver driver)
    {
        try
        {
            return driver.Reverse;
        }
        catch
        {
            return null;
        }
    }

    private void SetPosition(double position)
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
            _motion = RotatorMotionState.Idle;
        }
    }

    // ASCOM positions are 0 to 360 as floats: a value of 360.0 is 0.
    private static double Normalize(double degrees) => RotatorSkyModel.Normalize360(degrees);
}

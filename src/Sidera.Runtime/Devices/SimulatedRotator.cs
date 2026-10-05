using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Rotators;

namespace Sidera.Runtime.Devices;

/// <summary>
/// A rotator that only pretends: a move takes a time that grows with the angle, the way is the short one round the circle, and the position is a number from 0 to 360.
/// <para>
/// It also knows how the camera sits on it: <see cref="SkyOffsetDegrees"/> and <see cref="ReversedMounting"/> give the rotation of the sky in the image at every
/// position (<see cref="SkyRotationAt"/>), which is what a simulated plate solve of a test reports. So a test can check that the position and the sky rotation are two
/// different things (position 30° and sky 42° with an offset of 12°) and that the code keeps them apart.
/// </para>
/// A synchronized position keeps a mechanical zero of its own (<see cref="MechanicalPosition"/>). Cancelling a move halts it where it stands; <see cref="HaltAsync"/> does the same
/// from outside.
/// </summary>
public sealed class SimulatedRotator : IRotatorControl
{
    public const double DefaultDegreesPerSecond = 45;

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly double _degreesPerSecond;
    private readonly TimeSpan _minimumMove;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private RotatorMotionState _motion = RotatorMotionState.Idle;
    private double _mechanical;
    private double _syncOffset;
    private bool _reversed;
    private bool _haltRequested;
    private DeviceCapabilities<RotatorCapabilities> _capabilities = DeviceCapabilities<RotatorCapabilities>.Unknown;

    /// <param name="startPosition">The position it starts at.</param>
    /// <param name="degreesPerSecond">How fast it turns.</param>
    public SimulatedRotator(
        DeviceId id, string name = "Simulated Rotator", IEventPublisher? events = null, double startPosition = 0, double degreesPerSecond = DefaultDegreesPerSecond,
        TimeSpan? minimumMove = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(degreesPerSecond, 0);
        Id = id;
        Name = name;
        _events = events;
        _mechanical = RotatorSkyModel.Normalize360(startPosition);
        _degreesPerSecond = degreesPerSecond;
        _minimumMove = minimumMove ?? TimeSpan.FromMilliseconds(20);
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Rotator;

    /// <summary>The rotation of the sky in the image when the rotator is at position 0: how the camera is mounted.</summary>
    public double SkyOffsetDegrees { get; init; }

    /// <summary>The camera is mounted so that more position means less sky rotation.</summary>
    public bool ReversedMounting { get; init; }

    /// <summary>For tests of a failure: the move to start after this many moves fails with an error.</summary>
    public int FailMovesAfter { get; set; } = -1;

    /// <summary>How many moves were started.</summary>
    public int MovesStarted { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged;

    public DeviceConnectionState ConnectionState
    {
        get { lock (_gate) { return _connectionState; } }
    }

    public RotatorMotionState MotionState
    {
        get { lock (_gate) { return _motion; } }
    }

    public double Position
    {
        get { lock (_gate) { return RotatorSkyModel.Normalize360(_mechanical + _syncOffset); } }
    }

    public double? MechanicalPosition
    {
        get { lock (_gate) { return _capabilities.IsAvailable ? RotatorSkyModel.Normalize360(_mechanical) : null; } }
    }

    public DeviceCapabilities<RotatorCapabilities> Capabilities
    {
        get { lock (_gate) { return _capabilities; } }
    }

    public RotatorTelemetry? Telemetry
    {
        get
        {
            lock (_gate)
            {
                return _capabilities.IsAvailable
                    ? new RotatorTelemetry { Position = RotatorSkyModel.Normalize360(_mechanical + _syncOffset), MechanicalPosition = RotatorSkyModel.Normalize360(_mechanical), IsMoving = _motion == RotatorMotionState.Moving, Reversed = _reversed }
                    : null;
            }
        }
    }

    /// <summary>The rotation of the sky in the image at a position of this simulated rotator: what a plate solve of a frame taken there would report.</summary>
    public double SkyRotationAt(double positionDegrees) =>
        Core.Astrometry.SkyMath.NormalizeRotationDegrees((ReversedMounting ? -positionDegrees : positionDegrees) + SkyOffsetDegrees);

    /// <summary>The sky rotation of the image now.</summary>
    public double CurrentSkyRotation => SkyRotationAt(Position);

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Disconnected)
            {
                return;
            }

            _connectionState = DeviceConnectionState.Connecting;
        }

        try
        {
            await PublishConnectionAsync(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            lock (_gate)
            {
                _connectionState = DeviceConnectionState.Connected;
                _capabilities = DeviceCapabilities<RotatorCapabilities>.Of(new RotatorCapabilities
                {
                    Driver = new DriverMetadata("Sidera simulated rotator", "A rotator that only pretends", "Sidera.Runtime", "1.0", null),
                    AbsoluteMove = true,
                    RelativeMove = true,
                    CanHalt = true,
                    CanSync = true,
                    CanReverse = true,
                    HasMechanicalPosition = true,
                    StepSizeDegrees = 0.01,
                });
            }

            await PublishConnectionAsync(DeviceConnectionState.Connecting, DeviceConnectionState.Connected, cancellationToken);
            CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            lock (_gate)
            {
                _connectionState = DeviceConnectionState.Disconnected;
            }

            throw;
        }
    }

    /// <exception cref="InvalidOperationException">A move is running.</exception>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                return;
            }

            if (_motion == RotatorMotionState.Moving)
            {
                throw new InvalidOperationException("Cannot disconnect while the rotator is moving.");
            }

            _connectionState = DeviceConnectionState.Disconnecting;
        }

        await PublishConnectionAsync(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, cancellationToken);
        lock (_gate)
        {
            _connectionState = DeviceConnectionState.Disconnected;
            _capabilities = DeviceCapabilities<RotatorCapabilities>.Unknown;
        }

        await PublishConnectionAsync(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected, cancellationToken);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task MoveToAsync(double positionDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(positionDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(positionDegrees), positionDegrees, "The position must be a finite number of degrees.");
        }

        return MoveAsync(positionDegrees, relative: false, cancellationToken);
    }

    public Task MoveByAsync(double degrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The angle must be a finite number of degrees.");
        }

        return MoveAsync(degrees, relative: true, cancellationToken);
    }

    public Task HaltAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                throw new InvalidOperationException($"{Name} is not connected.");
            }

            _haltRequested = true;
        }

        return Task.CompletedTask;
    }

    public Task SyncAsync(double positionDegrees, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireConnected();
            if (_motion == RotatorMotionState.Moving)
            {
                throw new InvalidOperationException("The rotator is moving.");
            }

            _syncOffset = RotatorSkyModel.Normalize360(positionDegrees) - _mechanical;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetReversedAsync(bool reversed, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireConnected();
            _reversed = reversed;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task MoveAsync(double value, bool relative, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double start;
        double travel;
        lock (_gate)
        {
            RequireConnected();
            if (_motion == RotatorMotionState.Moving)
            {
                throw new InvalidOperationException("The rotator is already moving.");
            }

            if (FailMovesAfter >= 0 && MovesStarted >= FailMovesAfter)
            {
                MovesStarted++;
                throw new InvalidOperationException("The simulated rotator failed to move.");
            }

            MovesStarted++;
            start = Position;
            // The short way round, in (-180, 180]; a relative move is the angle that was asked for, however large.
            travel = relative ? value : Core.Astrometry.SkyMath.RotationDifferenceDegrees(start, RotatorSkyModel.Normalize360(value));
            _motion = RotatorMotionState.Moving;
            _haltRequested = false;
        }

        await PublishAsync(new RotatorMotionStateChanged(Id, RotatorMotionState.Idle, RotatorMotionState.Moving, start), cancellationToken);
        StateChanged?.Invoke(this, EventArgs.Empty);
        var duration = TimeSpan.FromSeconds(Math.Abs(travel) / _degreesPerSecond);
        if (duration < _minimumMove)
        {
            duration = _minimumMove;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = false;
        try
        {
            while (clock.Elapsed < duration)
            {
                bool halted;
                lock (_gate)
                {
                    halted = _haltRequested;
                }

                if (halted)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                // The position follows the move, so that a halt or a cancel stops it where it really was.
                var fraction = Math.Min(1, clock.Elapsed / duration);
                SetMechanicalFrom(start, travel * fraction);
                await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
            }

            lock (_gate)
            {
                if (!_haltRequested)
                {
                    SetMechanicalFrom(start, travel);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled: the rotator stops where it is, and the cancel goes on.
            cancelled = true;
            throw;
        }
        finally
        {
            double end;
            lock (_gate)
            {
                _motion = RotatorMotionState.Idle;
                _haltRequested = false;
                end = Position;
            }

            if (Math.Abs(Core.Astrometry.SkyMath.RotationDifferenceDegrees(start, end)) > 1e-9)
            {
                await PublishAsync(new RotatorPositionChanged(Id, start, end), CancellationToken.None);
            }

            await PublishAsync(new RotatorMotionStateChanged(Id, RotatorMotionState.Moving, RotatorMotionState.Idle, end), CancellationToken.None);
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = cancelled;
        }
    }

    private void SetMechanicalFrom(double startPosition, double travel)
    {
        lock (_gate)
        {
            _mechanical = RotatorSkyModel.Normalize360(startPosition + travel - _syncOffset);
        }
    }

    private void RequireConnected()
    {
        if (_connectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }
    }

    private Task PublishConnectionAsync(DeviceConnectionState previous, DeviceConnectionState current, CancellationToken cancellationToken) =>
        _events is null || previous == current
            ? Task.CompletedTask
            : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);

    private Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken) where TEvent : ISideraEvent =>
        _events is null ? Task.CompletedTask : _events.PublishAsync(@event, cancellationToken);
}

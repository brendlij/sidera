using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Core.Events;

namespace Sidera.Runtime.Devices;

/// <summary>
/// A dome that only pretends: a shutter that takes a time to open and close, an azimuth that turns at a speed, a park and a home position. With <c>shutterOnly</c> it is a roll-off roof: it has a
/// shutter and no azimuth. A halt stops whatever moves, a shutter in the middle of its way is then reported as an error (as real drivers do), and it can be closed from there. Cancelling an operation
/// halts it where it stands.
/// </summary>
public sealed class SimulatedDome : IDomeControl
{
    public const double DefaultDegreesPerSecond = 20;

    private readonly object _gate = new();
    private readonly IEventPublisher? _events;
    private readonly TimeSpan _shutterTime;
    private readonly double _degreesPerSecond;
    private readonly bool _shutterOnly;
    private DeviceConnectionState _connectionState = DeviceConnectionState.Disconnected;
    private DeviceCapabilities<DomeCapabilities> _capabilities = DeviceCapabilities<DomeCapabilities>.Unknown;
    private DomeShutterState _shutter = DomeShutterState.Closed;
    private double _azimuth;
    private bool _moving;
    private bool _atPark;
    private bool _atHome;
    private bool _halt;

    public SimulatedDome(
        DeviceId id, string name = "Simulated Dome", IEventPublisher? events = null, bool shutterOnly = false, TimeSpan? shutterTime = null, double degreesPerSecond = DefaultDegreesPerSecond,
        double startAzimuth = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(degreesPerSecond, 0);
        Id = id;
        Name = name;
        _events = events;
        _shutterOnly = shutterOnly;
        _shutterTime = shutterTime ?? TimeSpan.FromMilliseconds(400);
        _degreesPerSecond = degreesPerSecond;
        _azimuth = Normalize(startAzimuth);
        _atPark = true;
    }

    public DeviceId Id { get; }
    public string Name { get; }
    public DeviceType Type => DeviceType.Dome;

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

    public DomeShutterState ShutterState
    {
        get { lock (_gate) { return _capabilities.IsAvailable ? _shutter : DomeShutterState.Unknown; } }
    }

    public double? Azimuth
    {
        get { lock (_gate) { return _capabilities.IsAvailable && !_shutterOnly ? _azimuth : null; } }
    }

    public bool IsMoving
    {
        get { lock (_gate) { return _moving || _shutter is DomeShutterState.Opening or DomeShutterState.Closing; } }
    }

    public DomeTelemetry? Telemetry
    {
        get
        {
            lock (_gate)
            {
                return _capabilities.IsAvailable
                    ? new DomeTelemetry
                    {
                        ShutterState = _shutter, Azimuth = _shutterOnly ? null : _azimuth, AtHome = _shutterOnly ? null : _atHome, AtPark = _shutterOnly ? null : _atPark,
                        IsMoving = _moving || _shutter is DomeShutterState.Opening or DomeShutterState.Closing, Slaved = false,
                    }
                    : null;
            }
        }
    }

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

        await PublishConnectionAsync(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken);
        lock (_gate)
        {
            _connectionState = DeviceConnectionState.Connected;
            _capabilities = DeviceCapabilities<DomeCapabilities>.Of(new DomeCapabilities
            {
                Driver = new DriverMetadata("Sidera simulated dome", "A dome that only pretends", "Sidera.Runtime", "1.0", null),
                CanSetShutter = true,
                CanSetAzimuth = !_shutterOnly,
                CanPark = !_shutterOnly,
                CanFindHome = !_shutterOnly,
                Notes = _shutterOnly ? ["It has a shutter and no azimuth: Sidera treats it as a roll-off roof."] : [],
            });
        }

        await PublishConnectionAsync(DeviceConnectionState.Connecting, DeviceConnectionState.Connected, cancellationToken);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_connectionState != DeviceConnectionState.Connected)
            {
                return;
            }

            if (_moving || _shutter is DomeShutterState.Opening or DomeShutterState.Closing)
            {
                throw new InvalidOperationException("Cannot disconnect while the dome is moving.");
            }

            _connectionState = DeviceConnectionState.Disconnecting;
        }

        await PublishConnectionAsync(DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting, cancellationToken);
        lock (_gate)
        {
            _connectionState = DeviceConnectionState.Disconnected;
            _capabilities = DeviceCapabilities<DomeCapabilities>.Unknown;
        }

        await PublishConnectionAsync(DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected, cancellationToken);
        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task OpenShutterAsync(CancellationToken cancellationToken = default) => MoveShutterAsync(open: true, cancellationToken);

    public Task CloseShutterAsync(CancellationToken cancellationToken = default) => MoveShutterAsync(open: false, cancellationToken);

    private async Task MoveShutterAsync(bool open, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DomeShutterState start;
        lock (_gate)
        {
            RequireConnected();
            if (IsMovingUnlocked())
            {
                throw new InvalidOperationException($"{Name} is already moving.");
            }

            start = _shutter;
            if (start == (open ? DomeShutterState.Open : DomeShutterState.Closed))
            {
                return;
            }

            if (start == DomeShutterState.Error && open)
            {
                throw new InvalidOperationException($"The shutter of {Name} reports an error. Close it, or check the dome, before opening it.");
            }

            _shutter = open ? DomeShutterState.Opening : DomeShutterState.Closing;
            _halt = false;
        }

        await PublishShutterAsync(start, open ? DomeShutterState.Opening : DomeShutterState.Closing);
        StateChanged?.Invoke(this, EventArgs.Empty);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < _shutterTime)
            {
                bool halted;
                lock (_gate)
                {
                    halted = _halt;
                }

                if (halted)
                {
                    await EndShutterAsync(DomeShutterState.Error);
                    throw new OperationCanceledException($"{Name} was stopped.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
            }

            await EndShutterAsync(open ? DomeShutterState.Open : DomeShutterState.Closed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await EndShutterAsync(DomeShutterState.Error); // stopped in the middle of its way
            throw;
        }
    }

    public Task SlewToAzimuthAsync(double azimuthDegrees, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(azimuthDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(azimuthDegrees), azimuthDegrees, "The azimuth must be a finite number of degrees.");
        }

        return TurnAsync(Normalize(azimuthDegrees), park: false, home: false, cancellationToken);
    }

    public Task ParkAsync(CancellationToken cancellationToken = default) => TurnAsync(0, park: true, home: false, cancellationToken);

    public Task FindHomeAsync(CancellationToken cancellationToken = default) => TurnAsync(0, park: false, home: true, cancellationToken);

    private async Task TurnAsync(double target, bool park, bool home, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        double start;
        lock (_gate)
        {
            RequireConnected();
            if (_shutterOnly)
            {
                throw new InvalidOperationException($"{Name} has no azimuth.");
            }

            if (IsMovingUnlocked())
            {
                throw new InvalidOperationException($"{Name} is already moving.");
            }

            start = _azimuth;
            _moving = true;
            _halt = false;
            _atPark = false;
            _atHome = false;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        var travel = ShortestTravel(start, target);
        var duration = TimeSpan.FromSeconds(Math.Abs(travel) / _degreesPerSecond);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var arrived = true;
        try
        {
            while (clock.Elapsed < duration)
            {
                lock (_gate)
                {
                    if (_halt)
                    {
                        arrived = false;
                        break;
                    }

                    _azimuth = Normalize(start + travel * Math.Min(1, clock.Elapsed / duration));
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            arrived = false;
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _moving = false;
                _halt = false;
                if (arrived)
                {
                    _azimuth = target;
                    _atPark = park;
                    _atHome = home;
                }
            }

            await PublishAsync(new DomeStateChanged(Id));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task HaltAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RequireConnected();
            _halt = true;
        }

        return Task.CompletedTask;
    }

    private async Task EndShutterAsync(DomeShutterState end)
    {
        DomeShutterState previous;
        lock (_gate)
        {
            previous = _shutter;
            _shutter = end;
            _halt = false;
        }

        await PublishShutterAsync(previous, end);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsMovingUnlocked() => _moving || _shutter is DomeShutterState.Opening or DomeShutterState.Closing;

    private static double Normalize(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;

    private static double ShortestTravel(double from, double to)
    {
        var difference = Normalize(to - from);
        return difference > 180 ? difference - 360 : difference;
    }

    private void RequireConnected()
    {
        if (_connectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }
    }

    private Task PublishShutterAsync(DomeShutterState previous, DomeShutterState current) =>
        previous == current ? Task.CompletedTask : PublishAsync(new DomeShutterStateChanged(Id, previous, current));

    private Task PublishConnectionAsync(DeviceConnectionState previous, DeviceConnectionState current, CancellationToken cancellationToken) =>
        _events is null || previous == current ? Task.CompletedTask : _events.PublishAsync(new DeviceConnectionStateChanged(Id, previous, current), cancellationToken);

    private Task PublishAsync<TEvent>(TEvent @event) where TEvent : ISideraEvent => _events is null ? Task.CompletedTask : _events.PublishAsync(@event, CancellationToken.None);
}

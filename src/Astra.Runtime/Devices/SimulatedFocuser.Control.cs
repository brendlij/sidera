using Astra.Core.Devices;
using Astra.Core.Focusers;

namespace Astra.Runtime.Devices;

/// <summary>
/// The capability and telemetry side of the simulated focuser: an absolute focuser with a step size, a temperature that
/// stays where it is, and a temperature compensation switch that is remembered and does nothing else. Cancelling a move
/// really stops it, so <c>CanHalt</c> is true.
/// </summary>
public sealed partial class SimulatedFocuser
{
    public const double SimulatedTemperature = 12.5;

    private readonly object _control = new();
    private DeviceCapabilities<FocuserCapabilities> _capabilities = DeviceCapabilities<FocuserCapabilities>.Unknown;
    private bool _tempComp;

    public event EventHandler? CapabilitiesChanged;

    public event EventHandler? StateChanged;

    public DeviceCapabilities<FocuserCapabilities> Capabilities
    {
        get { lock (_control) { return _capabilities; } }
    }

    public FocuserTelemetry? Telemetry
    {
        get
        {
            lock (_control)
            {
                return _capabilities.IsAvailable
                    ? new FocuserTelemetry
                    {
                        Position = Position,
                        IsMoving = MotionState == FocuserMotionState.Moving,
                        Temperature = SimulatedTemperature,
                        TempComp = _tempComp,
                    }
                    : null;
            }
        }
    }

    private void OnConnectionStateSet(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connected)
        {
            lock (_control)
            {
                _capabilities = DeviceCapabilities<FocuserCapabilities>.Of(new FocuserCapabilities
                {
                    Driver = new DriverMetadata("Astra simulated focuser", "A focuser that only pretends", "Astra.Runtime", "1.0", null),
                    Absolute = true,
                    MaxStep = MaxPosition,
                    MaxIncrement = MaxPosition - MinPosition,
                    StepSizeMicrons = 1.0,
                    HasTemperature = true,
                    TempCompAvailable = true,
                    CanHalt = true,
                    Notes = ["The temperature does not change; temperature compensation is remembered and has no effect."],
                });
            }
        }
        else if (state == DeviceConnectionState.Disconnected)
        {
            lock (_control)
            {
                _capabilities = DeviceCapabilities<FocuserCapabilities>.Unknown;
            }
        }
        else
        {
            return;
        }

        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsAvailable)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public async Task MoveByAsync(int steps, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsAvailable)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }

        var target = (long)Position + steps;
        if (target < MinPosition || target > MaxPosition)
        {
            throw new ArgumentOutOfRangeException(
                nameof(steps), steps, $"Moving by {steps} from {Position} would leave the range {MinPosition} to {MaxPosition}.");
        }

        await MoveToAsync((int)target, cancellationToken);
    }

    public Task SetTempCompAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.IsAvailable)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }

        lock (_control)
        {
            _tempComp = enabled;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

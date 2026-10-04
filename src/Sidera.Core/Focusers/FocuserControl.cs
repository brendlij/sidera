using Sidera.Core.Devices;

namespace Sidera.Core.Focusers;

/// <summary>
/// What a focuser supports. <see cref="Absolute"/> decides how it can be used: an absolute focuser moves to positions
/// (everything of Sidera, autofocus included, works with it); a relative focuser only moves by a number of steps, has no
/// position to report, and is refused wherever a position is needed.
/// </summary>
public sealed record FocuserCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    public bool Absolute { get; init; }

    /// <summary>The highest position of an absolute focuser (it moves between 0 and this); <c>null</c> for a relative one.</summary>
    public int? MaxStep { get; init; }

    /// <summary>The most steps one move may be.</summary>
    public int? MaxIncrement { get; init; }

    /// <summary>The size of a step in microns, when the driver says.</summary>
    public double? StepSizeMicrons { get; init; }

    /// <summary>The focuser reports a temperature.</summary>
    public bool HasTemperature { get; init; }

    /// <summary>The focuser can compensate for temperature itself.</summary>
    public bool TempCompAvailable { get; init; }

    /// <summary>
    /// Whether the driver implements Halt cannot be asked without trying it, so it is not known: <c>null</c> until a move was
    /// stopped. Sidera never claims a stop that the focuser did not confirm.
    /// </summary>
    public bool? CanHalt { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What a focuser is doing and measuring now. Members the focuser does not report are <c>null</c>.</summary>
public sealed record FocuserTelemetry
{
    /// <summary>The position of an absolute focuser; <c>null</c> for a relative one, which has none to report.</summary>
    public int? Position { get; init; }

    public bool IsMoving { get; init; }
    public double? Temperature { get; init; }
    public bool? TempComp { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A focuser that says what it supports and can do more than move to a position: move by a number of steps, switch its
/// temperature compensation, report its temperature.
/// </summary>
public interface IFocuserControl : IFocuser, ICapable<FocuserCapabilities>, IObservableDevice
{
    FocuserTelemetry? Telemetry { get; }

    /// <summary>
    /// Moves by a number of steps (negative is the other way): for a relative focuser the only kind of move there is, for
    /// an absolute one a move to the current position plus the steps, which must stay in range. Stopped by cancelling, like
    /// any move of the focuser, with the same honesty about whether it really stopped.
    /// </summary>
    Task MoveByAsync(int steps, CancellationToken cancellationToken = default);

    Task SetTempCompAsync(bool enabled, CancellationToken cancellationToken = default);
}

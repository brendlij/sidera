using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Rotators;

public enum RotatorMotionState
{
    /// <summary>Standing still at <see cref="IRotator.Position"/>.</summary>
    Idle,

    /// <summary>Moving to a target position.</summary>
    Moving,
}

/// <summary>A rotator started or stopped moving. <paramref name="Position"/> is where it stands at the time of the change.</summary>
public sealed record RotatorMotionStateChanged(DeviceId DeviceId, RotatorMotionState PreviousState, RotatorMotionState NewState, double Position) : ISideraEvent;

/// <summary>A rotator arrived at a new position (published once when a move has ended, not as telemetry while it moves).</summary>
public sealed record RotatorPositionChanged(DeviceId DeviceId, double PreviousPosition, double Position) : ISideraEvent;

/// <summary>
/// A motorized rotator turning the camera. Its position is what the driver calls the position: degrees from 0 (inclusive) to 360 (exclusive),
/// in the frame that moves are commanded in (a driver may have been synchronized to some zero of its own). It says nothing about how the sky looks in the
/// image: that is a different angle, related to this one by a calibration (see <see cref="RotatorSkyModel"/>), and the two are never taken for each other.
/// </summary>
public interface IRotator : IDevice
{
    RotatorMotionState MotionState { get; }

    /// <summary>The position in degrees, 0 to 360: the start position, or the last position actually reached.</summary>
    double Position { get; }

    /// <summary>
    /// Moves to the absolute <paramref name="positionDegrees"/> (any angle; it is taken modulo 360) and completes when it has arrived. Cancelling asks the
    /// hardware to halt and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rotator is not connected, or is already moving.</exception>
    Task MoveToAsync(double positionDegrees, CancellationToken cancellationToken = default);
}

/// <summary>What a rotator supports, as it reported it or as the adapter knows it. A thing that cannot be known without trying is <c>null</c>, not guessed.</summary>
public sealed record RotatorCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    /// <summary>The rotator moves to absolute positions.</summary>
    public bool AbsoluteMove { get; init; }

    /// <summary>The rotator moves by an angle.</summary>
    public bool RelativeMove { get; init; }

    /// <summary>
    /// Whether the driver implements Halt cannot be asked without trying it: <c>null</c> until a move was stopped, then what was seen. Sidera never claims a stop that the
    /// rotator did not confirm.
    /// </summary>
    public bool? CanHalt { get; init; }

    /// <summary>The position can be synchronized: the driver takes a new value for where it is.</summary>
    public bool CanSync { get; init; }

    /// <summary>The direction of the motor can be reversed.</summary>
    public bool CanReverse { get; init; }

    /// <summary>The driver reports the mechanical position besides the position (the position without any synchronization offset).</summary>
    public bool HasMechanicalPosition { get; init; }

    /// <summary>The angle of one step of the motor in degrees, when the driver says.</summary>
    public double? StepSizeDegrees { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What a rotator is doing and measuring now. Members the rotator does not report are <c>null</c>.</summary>
public sealed record RotatorTelemetry
{
    public double? Position { get; init; }

    /// <summary>The mechanical position: the position as the motor has it, without a synchronization offset; <c>null</c> when the driver has no such thing.</summary>
    public double? MechanicalPosition { get; init; }

    public bool IsMoving { get; init; }

    /// <summary>Whether the direction is reversed; <c>null</c> when the rotator cannot say.</summary>
    public bool? Reversed { get; init; }

    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>A rotator that says what it supports and can be moved by an angle, stopped on request, and set up.</summary>
public interface IRotatorControl : IRotator, ICapable<RotatorCapabilities>, IObservableDevice
{
    RotatorTelemetry? Telemetry { get; }

    /// <summary>The mechanical position, when the driver reports one.</summary>
    double? MechanicalPosition { get; }

    /// <summary>Moves by an angle (negative is the other way) and completes when it has arrived.</summary>
    Task MoveByAsync(double degrees, CancellationToken cancellationToken = default);

    /// <summary>Asks the rotator to stop where it is. Safe at any time: it does not wait for the operation that is running.</summary>
    Task HaltAsync(CancellationToken cancellationToken = default);

    /// <summary>Tells the rotator that it stands at <paramref name="positionDegrees"/> now. Changes what it calls its position, not where it points. Only when it can sync.</summary>
    Task SyncAsync(double positionDegrees, CancellationToken cancellationToken = default);

    /// <summary>Reverses the direction of the motor. Only when it can.</summary>
    Task SetReversedAsync(bool reversed, CancellationToken cancellationToken = default);
}

/// <summary>
/// The relation between the position of a rotator and the rotation of the sky in the image, as a calibration and not as a truth: <c>sky = ±position + offset</c>. The sky
/// rotation is the one of Sidera everywhere (<see cref="Astrometry.SkyMath.NormalizeRotationDegrees"/>: the angle from the top of the image to celestial north, counterclockwise, in
/// (-180, 180]), the one a plate solve measures. The offset absorbs how the camera sits on the rotator, the zero of the driver and the parity of the optics; whether more position
/// means more sky rotation (<see cref="Reversed"/> false) or less depends on the mounting and cannot be known from a single solve, so it is a setting that is checked by the
/// verification of every rotation, never assumed silently.
/// </summary>
/// <param name="OffsetDegrees">The sky rotation at position 0 (for a rotator that is not reversed).</param>
/// <param name="Reversed">More position means less sky rotation.</param>
/// <param name="CalibratedAt">When the offset was measured; <c>null</c> for an offset that was entered.</param>
public sealed record RotatorSkyModel(double OffsetDegrees, bool Reversed = false, DateTimeOffset? CalibratedAt = null)
{
    /// <summary>The sky rotation, in (-180, 180], at a position of the rotator.</summary>
    public double SkyRotationOf(double positionDegrees) =>
        Astrometry.SkyMath.NormalizeRotationDegrees((Reversed ? -positionDegrees : positionDegrees) + OffsetDegrees);

    /// <summary>The position, 0 (inclusive) to 360 (exclusive), at which the sky has the desired rotation.</summary>
    public double PositionFor(double desiredSkyRotationDegrees)
    {
        var wanted = desiredSkyRotationDegrees - OffsetDegrees;
        return Normalize360(Reversed ? -wanted : wanted);
    }

    /// <summary>The model that fits a measurement: at <paramref name="positionDegrees"/> the sky had <paramref name="solvedSkyRotationDegrees"/>; the direction stays as it is.</summary>
    public RotatorSkyModel Calibrated(double positionDegrees, double solvedSkyRotationDegrees, DateTimeOffset? at = null) =>
        this with
        {
            OffsetDegrees = Astrometry.SkyMath.NormalizeRotationDegrees(solvedSkyRotationDegrees - (Reversed ? -positionDegrees : positionDegrees)),
            CalibratedAt = at ?? CalibratedAt,
        };

    /// <summary>An angle in degrees as [0, 360).</summary>
    public static double Normalize360(double degrees)
    {
        var wrapped = degrees % 360.0;
        if (wrapped < 0)
        {
            wrapped += 360.0;
        }

        return wrapped >= 360.0 ? 0.0 : wrapped;
    }
}

using Sidera.Core.Devices;
using Sidera.Core.Events;

namespace Sidera.Core.Domes;

/// <summary>What the shutter of a dome (or the roof of a roll-off roof) is doing, as the driver reports it.</summary>
public enum DomeShutterState
{
    /// <summary>Not known: the dome has no shutter that Sidera can read, or it is not connected.</summary>
    Unknown,

    Open,
    Closed,
    Opening,
    Closing,

    /// <summary>The driver reports an error state. Nothing may be assumed about where the shutter stands.</summary>
    Error,
}

/// <summary>The shutter of a dome changed its state (published when a change is seen, not as telemetry).</summary>
public sealed record DomeShutterStateChanged(DeviceId DeviceId, DomeShutterState PreviousState, DomeShutterState NewState) : ISideraEvent;

/// <summary>Something about a dome that is not its shutter changed: it started or stopped moving, arrived at park or home, or its azimuth changed.</summary>
public sealed record DomeStateChanged(DeviceId DeviceId) : ISideraEvent;

/// <summary>
/// A dome or a roll-off roof. It is a shared piece of equipment (like the mount): it belongs to the observatory, not to one imaging setup. A roll-off roof is a dome that has a shutter and no azimuth;
/// <see cref="Azimuth"/> is <c>null</c> for it, and what it does not support is said by <see cref="DomeCapabilities"/>, not guessed.
/// </summary>
public interface IDome : IDevice
{
    DomeShutterState ShutterState { get; }

    /// <summary>The azimuth of the dome slit in degrees (0 north, 90 east); <c>null</c> when the dome has no azimuth or it was not read.</summary>
    double? Azimuth { get; }

    /// <summary>The dome moves: it turns, or its shutter moves. As ASCOM says it, with no distinction.</summary>
    bool IsMoving { get; }
}

/// <summary>What a dome supports, as it reported it. A thing that cannot be known without trying is <c>null</c>, not guessed.</summary>
public sealed record DomeCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    /// <summary>The shutter can be opened and closed by Sidera.</summary>
    public bool CanSetShutter { get; init; }

    /// <summary>The dome can be turned to an azimuth.</summary>
    public bool CanSetAzimuth { get; init; }

    /// <summary>The dome can be moved to an altitude (the shutter of a dome whose slit opens by degrees).</summary>
    public bool CanSetAltitude { get; init; }

    public bool CanPark { get; init; }

    public bool CanSetPark { get; init; }

    public bool CanFindHome { get; init; }

    public bool CanSyncAzimuth { get; init; }

    /// <summary>The driver itself follows the mount (slaving). Sidera does not switch it; this is only what the driver says.</summary>
    public bool CanSlave { get; init; }

    /// <summary>A shutter and no azimuth: what a roll-off roof is to a program.</summary>
    public bool IsShutterOnly => CanSetShutter && !CanSetAzimuth;

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What a dome is doing and measuring now. Members the dome does not report are <c>null</c>.</summary>
public sealed record DomeTelemetry
{
    public DomeShutterState ShutterState { get; init; } = DomeShutterState.Unknown;

    public double? Azimuth { get; init; }

    public double? Altitude { get; init; }

    public bool? AtHome { get; init; }

    public bool? AtPark { get; init; }

    public bool IsMoving { get; init; }

    /// <summary>The driver follows the mount; <c>null</c> when it cannot say.</summary>
    public bool? Slaved { get; init; }

    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A dome that says what it supports and can open and close its shutter, turn, park, find home and be stopped. Every one of these is an explicit call: connecting, reading or discovering a dome never moves it.
/// </summary>
public interface IDomeControl : IDome, ICapable<DomeCapabilities>, IObservableDevice
{
    DomeTelemetry? Telemetry { get; }

    /// <summary>
    /// Opens the shutter and completes when it reports open. Cancelling asks the dome to stop moving and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The dome is not connected, cannot set its shutter, or the shutter reported an error.</exception>
    Task OpenShutterAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the shutter and completes when it reports closed; see <see cref="OpenShutterAsync"/>.</summary>
    Task CloseShutterAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns the dome to an azimuth (0 to 360, exclusive) and completes when it has arrived. Only when it can set its azimuth.</summary>
    Task SlewToAzimuthAsync(double azimuthDegrees, CancellationToken cancellationToken = default);

    /// <summary>Parks the dome and completes when it is parked. Only when it can park.</summary>
    Task ParkAsync(CancellationToken cancellationToken = default);

    /// <summary>Finds the home position and completes when it is there. Only when it can find home.</summary>
    Task FindHomeAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks the dome to stop whatever it moves, shutter and azimuth together (ASCOM has no separate stop). Safe at any time: it does not wait for the operation that is running.</summary>
    Task HaltAsync(CancellationToken cancellationToken = default);
}

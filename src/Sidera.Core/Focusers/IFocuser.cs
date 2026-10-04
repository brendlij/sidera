using Sidera.Core.Devices;

namespace Sidera.Core.Focusers;

/// <summary>
/// A focuser with absolute positioning: its position is a whole number of focuser steps between
/// <see cref="MinPosition"/> and <see cref="MaxPosition"/>. Deliberately small: no focus quality, no backlash,
/// no temperature; those belong to autofocus, which builds on this.
/// </summary>
public interface IFocuser : IDevice
{
    /// <summary>
    /// <c>false</c> for a focuser that can only move by a number of steps (see <see cref="IFocuserControl"/>): it has no
    /// position, <see cref="Position"/> and the limits mean nothing, and everything that needs a position (a move to a
    /// target, autofocus) refuses it before anything moves. Absolute unless a device says otherwise.
    /// </summary>
    bool IsAbsolute => true;

    FocuserMotionState MotionState { get; }

    /// <summary>The current position in steps: the start position, or the last position that was actually reached.</summary>
    int Position { get; }

    /// <summary>The lowest position the focuser can move to.</summary>
    int MinPosition { get; }

    /// <summary>The highest position the focuser can move to.</summary>
    int MaxPosition { get; }

    /// <summary>
    /// Moves to the absolute <paramref name="target"/> and completes when it has arrived.
    /// Cancelling throws <see cref="OperationCanceledException"/>; the target was then not reached.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is outside <see cref="MinPosition"/>..<see cref="MaxPosition"/>.</exception>
    /// <exception cref="InvalidOperationException">The focuser is not connected, or is already moving.</exception>
    Task MoveToAsync(int target, CancellationToken cancellationToken = default);
}

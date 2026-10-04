namespace Sidera.Core.Focusers;

public enum FocuserMotionState
{
    /// <summary>Standing still at <see cref="IFocuser.Position"/>.</summary>
    Idle,

    /// <summary>Moving to a target position.</summary>
    Moving
}

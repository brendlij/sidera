namespace Sidera.Core.FilterWheels;

public enum FilterWheelMotionState
{
    /// <summary>Standing still at <see cref="IFilterWheel.CurrentSlot"/>.</summary>
    Idle,

    /// <summary>Turning to another slot.</summary>
    Moving
}

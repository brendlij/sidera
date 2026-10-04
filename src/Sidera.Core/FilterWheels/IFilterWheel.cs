using Sidera.Core.Devices;

namespace Sidera.Core.FilterWheels;

/// <summary>A filter wheel: a fixed list of named slots, one of them in the light path.</summary>
public interface IFilterWheel : IDevice
{
    FilterWheelMotionState MotionState { get; }

    /// <summary>The slots of the wheel, ordered by index. Indexes are 0..Count-1.</summary>
    IReadOnlyList<FilterSlot> Slots { get; }

    /// <summary>The slot in the light path: the start slot, or the last one that was actually reached.</summary>
    FilterSlot CurrentSlot { get; }

    /// <summary>
    /// Turns to the slot with the given index and completes when it has arrived. Cancelling throws
    /// <see cref="OperationCanceledException"/>; the slot was then not reached.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">There is no slot with this index.</exception>
    /// <exception cref="InvalidOperationException">The wheel is not connected, or is already moving.</exception>
    Task MoveToSlotAsync(int slotIndex, CancellationToken cancellationToken = default);
}

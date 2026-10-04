using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Turns a connected filter wheel to a slot, identified by its index; it never connects the wheel. Requires
/// exclusive use of the filter wheel device and nothing else: a camera of the same rig can keep exposing, and other
/// rigs are not touched.
/// </summary>
public sealed class ChangeFilterAction : IResourceAwareSequenceStep
{
    private readonly DeviceRegistry _registry;

    public ChangeFilterAction(DeviceRegistry registry, DeviceId filterWheelId, int slotIndex)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);

        _registry = registry;
        FilterWheelId = filterWheelId;
        SlotIndex = slotIndex;
    }

    public DeviceId FilterWheelId { get; }

    /// <summary>The zero-based index of the target slot.</summary>
    public int SlotIndex { get; }

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Change filter to slot {SlotIndex}");

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(FilterWheelId)];

    /// <summary>Returns a result whose payload is the <see cref="FilterSlot"/> that is current afterwards.</summary>
    /// <exception cref="InvalidOperationException">The wheel is unknown, not a filter wheel, or not connected.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The wheel has no slot with this index.</exception>
    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var wheel = DeviceLookup.Resolve<IFilterWheel>(_registry, FilterWheelId, "filter wheel");

        if (wheel.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Filter wheel '{FilterWheelId}' is not connected.");
        }

        await wheel.MoveToSlotAsync(SlotIndex, cancellationToken);
        return new SequenceStepResult(wheel.CurrentSlot);
    }
}

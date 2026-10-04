using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The slots of the equipment page: one for each kind of device, shown as tabs. A slot is where a driver is chosen and the device is
/// connected and used; the page does not add or remove anything by hand.
/// </summary>
public sealed partial class EquipmentViewModel
{
    /// <summary>Camera, mount, focuser, filter wheel and guider.</summary>
    public IReadOnlyList<DeviceSlotViewModel> Slots { get; private set; } = [];

    /// <summary>The slot whose page is shown.</summary>
    [ObservableProperty]
    public partial DeviceSlotViewModel? SelectedSlot { get; private set; }

    private void BuildSlots()
    {
        Slots =
        [
            new DeviceSlotViewModel(this, _management, EquipmentPage.Camera, DeviceType.Camera, "Camera", "No camera"),
            new DeviceSlotViewModel(this, _management, EquipmentPage.Mount, DeviceType.Mount, "Mount", "No mount"),
            new DeviceSlotViewModel(this, _management, EquipmentPage.Focuser, DeviceType.Focuser, "Focuser", "No focuser"),
            new DeviceSlotViewModel(this, _management, EquipmentPage.FilterWheel, DeviceType.FilterWheel, "Filter Wheel", "No filter wheel"),
            new DeviceSlotViewModel(this, _management, EquipmentPage.Guider, DeviceType.Guider, "Guider", "No guider"),
        ];
        foreach (var slot in Slots)
        {
            slot.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeviceSlotViewModel.IsSelected) && slot.IsSelected)
                {
                    SelectSlot(slot);
                }
            };
        }

        DevicesChanged += (_, _) =>
        {
            foreach (var slot in Slots)
            {
                slot.Refresh();
            }
        };
    }

    private void SelectSlot(DeviceSlotViewModel slot)
    {
        SelectedSlot = slot;
        foreach (var other in Slots.Where(s => s != slot && s.IsSelected))
        {
            other.IsSelected = false;
        }
    }

    /// <summary>The device a slot controls: the first of its kind.</summary>
    internal DeviceViewModelBase? DeviceOfKind(DeviceType type) => type switch
    {
        DeviceType.Camera => _cameras.FirstOrDefault(),
        DeviceType.Mount => _mounts.FirstOrDefault(),
        DeviceType.Focuser => _focusers.FirstOrDefault(),
        DeviceType.FilterWheel => _filterWheels.FirstOrDefault(),
        DeviceType.Guider => _guiders.FirstOrDefault(),
        _ => null,
    };

    internal bool IdInUse(string id) =>
        (_management?.Service.Configuration.Find(id) is not null) || _host.DeviceRegistry.TryGet(new DeviceId(id), out _);
}

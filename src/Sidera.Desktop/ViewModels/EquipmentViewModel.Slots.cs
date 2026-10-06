using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

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
            new DeviceSlotViewModel(this, _management, EquipmentPage.Rotator, DeviceType.Rotator, "Rotator", "No rotator"),
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

    /// <summary>The device a slot controls: the one the workspace shows when it is of the kind of the slot (the rig's, or the one chosen among the standalone devices), else the first of its kind.</summary>
    internal DeviceViewModelBase? DeviceOfKind(DeviceType type) =>
        _addingKind is { } adding && KindOfPage(adding) == type ? null : SelectedDevice is { } shown && KindOf(shown) == type ? shown : FirstOfKind(type);

    private static DeviceType KindOfPage(EquipmentPage page) => page switch
    {
        EquipmentPage.Camera => DeviceType.Camera,
        EquipmentPage.Mount => DeviceType.Mount,
        EquipmentPage.Focuser => DeviceType.Focuser,
        EquipmentPage.FilterWheel => DeviceType.FilterWheel,
        EquipmentPage.Rotator => DeviceType.Rotator,
        _ => DeviceType.Guider,
    };

    private static DeviceType KindOf(DeviceViewModelBase device) => device switch
    {
        CameraViewModel => DeviceType.Camera,
        MountViewModel => DeviceType.Mount,
        FocuserViewModel => DeviceType.Focuser,
        FilterWheelViewModel => DeviceType.FilterWheel,
        RotatorViewModel => DeviceType.Rotator,
        _ => DeviceType.Guider,
    };

    private DeviceViewModelBase? FirstOfKind(DeviceType type) => type switch
    {
        DeviceType.Camera => _cameras.FirstOrDefault(),
        DeviceType.Mount => _mounts.FirstOrDefault(),
        DeviceType.Focuser => _focusers.FirstOrDefault(),
        DeviceType.FilterWheel => _filterWheels.FirstOrDefault(),
        DeviceType.Guider => _guiders.FirstOrDefault(),
        DeviceType.Rotator => _rotators.FirstOrDefault(),
        _ => null,
    };

    internal bool IdInUse(string id) =>
        (_management?.Service.Configuration.Find(id) is not null) || _host.DeviceRegistry.TryGet(new DeviceId(id), out _);
}

using System;
using System.Linq;
using Sidera.Core.Devices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The equipment the whole session shares, as the Sequencer page shows it: the mount and the guider that the draft
/// selects, with what Sidera knows about them right now (whether they are connected, whether the mount is slewing,
/// what the guider is doing). Nothing is shown that the device does not report.
/// </summary>
public sealed partial class SharedEquipmentViewModel : ViewModelBase
{
    private readonly EquipmentViewModel _equipment;

    public SharedEquipmentViewModel(SequenceDraftViewModel draft, EquipmentViewModel equipment)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(equipment);
        Draft = draft;
        _equipment = equipment;

        foreach (var device in equipment.Mounts.Cast<DeviceViewModelBase>().Concat(equipment.Guiders))
        {
            device.Refreshed += (_, _) => Refresh();
        }

        draft.SharedMount.PropertyChanged += (_, e) => OnPicked(e.PropertyName);
        draft.SharedGuider.PropertyChanged += (_, e) => OnPicked(e.PropertyName);
        Refresh();
    }

    public SequenceDraftViewModel Draft { get; }

    public DevicePickerViewModel Mount => Draft.SharedMount;
    public DevicePickerViewModel Guider => Draft.SharedGuider;

    [ObservableProperty]
    public partial string MountText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GuiderText { get; private set; } = string.Empty;

    private void OnPicked(string? property)
    {
        if (property == nameof(DevicePickerViewModel.Selected))
        {
            Refresh();
        }
    }

    /// <summary>Reads the state of the selected mount and guider again.</summary>
    public void Refresh()
    {
        var mount = _equipment.Mounts.FirstOrDefault(m => m.DeviceIdText == Draft.SharedMount.SelectedId?.Value);
        MountText = mount is null
            ? Draft.SharedMount.SelectedId is null ? "No mount selected" : "Not available"
            : mount.IsConnected ? $"Connected · {mount.MotionState}" : mount.ConnectionState.ToString();

        var guider = _equipment.Guiders.FirstOrDefault(g => g.DeviceIdText == Draft.SharedGuider.SelectedId?.Value);
        GuiderText = guider is null
            ? Draft.SharedGuider.SelectedId is null ? "No guider selected" : "Not available"
            : guider.IsConnected ? $"Connected · {guider.GuidingState}" : guider.ConnectionState.ToString();
    }
}

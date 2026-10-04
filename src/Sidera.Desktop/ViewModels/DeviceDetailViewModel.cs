using System;
using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Mounts;
using Sidera.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The detail of one device: the device and its connection and, in the classes that derive from it, what belongs to one kind
/// of device only (the capability-driven panel of a camera, a mount or a focuser). The workspace page of the device shows it
/// in sections; there are no tabs, and what a device does not support is not there.
/// </summary>
public abstract partial class DeviceDetailViewModel : ViewModelBase, IDisposable
{
    private bool _isShown;

    protected DeviceDetailViewModel(DeviceViewModelBase device, DeviceConfigurationViewModel? configuration)
    {
        Device = device;
        Configuration = configuration;
    }

    /// <summary>
    /// The configuration of the device (what drives it; edit, set up, remove); <c>null</c> where the equipment cannot be
    /// changed, which is where the page has no <see cref="EquipmentManagement"/>.
    /// </summary>
    public DeviceConfigurationViewModel? Configuration { get; }

    /// <summary>
    /// The panel with the capability-driven settings and controls of the device (what the device says it supports); <c>null</c>
    /// for a device that has none or whose class does not offer the capability interface.
    /// </summary>
    public DevicePanelViewModel? Panel { get; protected set; }

    public bool HasPanel => Panel is not null;

    /// <summary>A stop that is always there for a device that moves; <c>null</c> for one that does not.</summary>
    public virtual System.Windows.Input.ICommand? StopCommand => null;

    public bool HasStop => StopCommand is not null;

    /// <summary>The detail is on screen; the panel reads the state of the device regularly meanwhile.</summary>
    public bool IsShown
    {
        get => _isShown;
        set
        {
            _isShown = value;
            if (Panel is not null)
            {
                Panel.IsShown = value;
            }
        }
    }

    public void Dispose() => Panel?.Dispose();

    /// <summary>The device the detail is about: its identity, connection and state are those of the card view model.</summary>
    public DeviceViewModelBase Device { get; }
}

/// <summary>
/// The camera. What it reports about its sensor it does not report yet (the device interface has no resolution or pixel
/// size); when the camera is part of a rig, the optics of the rig say them, and they are shown as the rig's.
/// </summary>
public sealed class CameraDetailViewModel : DeviceDetailViewModel
{
    public CameraDetailViewModel(
        CameraViewModel camera, RigViewModel? rig, DeviceConfigurationViewModel? configuration = null, IDevicePreferenceStore? preferences = null,
        TimeSpan? pollInterval = null)
        : base(camera, configuration)
    {
        Camera = camera;
        Rig = rig;
        if (camera.DeviceModel is ICameraControl control)
        {
            Settings = new CameraSettingsViewModel(camera, control, preferences, pollInterval);
            Panel = Settings;
        }
    }

    /// <summary>The settings of the camera that its capabilities allow; <c>null</c> when the camera cannot say what it supports.</summary>
    public CameraSettingsViewModel? Settings { get; }

    public CameraViewModel Camera { get; }

    /// <summary>The rig the camera is part of, if any; the camera works the same without one.</summary>
    public RigViewModel? Rig { get; }

    public bool HasRig => Rig is not null;

    public string RigText => Rig?.Name ?? "Not part of a rig";

    public string ResolutionText => Rig is { } r ? $"{r.ResolutionText} (from {r.Name})" : "Not reported";

    public string PixelSizeText => Rig is { } r ? $"{r.PixelSizeText} (from {r.Name})" : "Not reported";

    public string SensorText => Rig is { } r ? $"{r.SensorText} (from {r.Name})" : "Not reported";
}

/// <summary>The focuser: position and travel, a move, temperature and its compensation where it has them.</summary>
public sealed class FocuserDetailViewModel : DeviceDetailViewModel
{
    public FocuserDetailViewModel(
        FocuserViewModel focuser, RigViewModel? rig, DeviceConfigurationViewModel? configuration = null, IDevicePreferenceStore? preferences = null,
        TimeSpan? pollInterval = null)
        : base(focuser, configuration)
    {
        Focuser = focuser;
        Rig = rig;
        if (focuser.DeviceModel is IFocuserControl control)
        {
            Control = new FocuserControlViewModel(focuser, control, preferences, pollInterval);
            Panel = Control;
        }
    }

    public FocuserControlViewModel? Control { get; }
    public FocuserViewModel Focuser { get; }
    public RigViewModel? Rig { get; }
    public string RigText => Rig?.Name ?? "Not part of a rig";

}

/// <summary>The filter wheel: the filter in the light path and a change; slot names, focus offsets and settle delays will be its settings.</summary>
public sealed class FilterWheelDetailViewModel(FilterWheelViewModel wheel, RigViewModel? rig, DeviceConfigurationViewModel? configuration = null)
    : DeviceDetailViewModel(wheel, configuration)
{
    public FilterWheelViewModel Wheel { get; } = wheel;
    public RigViewModel? Rig { get; } = rig;
    public string RigText => Rig?.Name ?? "Not part of a rig";

}

/// <summary>The mount: where it points and what it is doing, a slew; limits, site and tracking will be its settings.</summary>
public sealed class MountDetailViewModel : DeviceDetailViewModel
{
    public MountDetailViewModel(
        MountViewModel mount, DeviceConfigurationViewModel? configuration = null, IDevicePreferenceStore? preferences = null,
        TimeSpan? pollInterval = null)
        : base(mount, configuration)
    {
        Mount = mount;
        if (mount.DeviceModel is IMountControl control)
        {
            Control = new MountControlViewModel(mount, control, preferences, pollInterval);
            Panel = Control;
        }
    }

    public MountControlViewModel? Control { get; }
    public MountViewModel Mount { get; }

    public override System.Windows.Input.ICommand? StopCommand => Control?.StopCommand;

}

/// <summary>The guider: whether it guides, start and stop; dither defaults, settling and the backend will be its settings.</summary>
public sealed class GuiderDetailViewModel(GuiderViewModel guider, DeviceConfigurationViewModel? configuration = null)
    : DeviceDetailViewModel(guider, configuration)
{
    public GuiderViewModel Guider { get; } = guider;

}

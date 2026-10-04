using System;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Core.Mounts;
using Astra.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.ViewModels;

/// <summary>The parts of the detail of a device. Every kind of device has the same four; what is in them is its own.</summary>
public enum DeviceDetailSection
{
    /// <summary>What the device is and what it is doing.</summary>
    Overview,

    /// <summary>What can be done with it by hand today.</summary>
    Controls,

    /// <summary>What can be set on it. Empty until the device offers settings that Astra implements.</summary>
    Settings,

    /// <summary>What drives it: the backend, the identity, the capabilities it reports.</summary>
    DriverInfo,
}

/// <summary>
/// The detail of one device: the common shell (the device, its connection, the four sections) and, in the classes that
/// derive from it, what belongs to one kind of device only. There is one such class per kind of device because each
/// kind will have settings of its own: a camera gain, a focuser backlash, the focus offsets of a filter wheel. None of
/// that is built here. The place for it is the derived class (the state of the settings) and the Settings section of
/// its view (the controls), next to the Controls that exist today.
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverviewSection))]
    [NotifyPropertyChangedFor(nameof(IsControlsSection))]
    [NotifyPropertyChangedFor(nameof(IsSettingsSection))]
    [NotifyPropertyChangedFor(nameof(IsDriverInfoSection))]
    public partial DeviceDetailSection Section { get; set; }

    public bool IsOverviewSection
    {
        get => Section == DeviceDetailSection.Overview;
        set => Pick(DeviceDetailSection.Overview, value);
    }

    public bool IsControlsSection
    {
        get => Section == DeviceDetailSection.Controls;
        set => Pick(DeviceDetailSection.Controls, value);
    }

    public bool IsSettingsSection
    {
        get => Section == DeviceDetailSection.Settings;
        set => Pick(DeviceDetailSection.Settings, value);
    }

    public bool IsDriverInfoSection
    {
        get => Section == DeviceDetailSection.DriverInfo;
        set => Pick(DeviceDetailSection.DriverInfo, value);
    }

    private void Pick(DeviceDetailSection section, bool selected)
    {
        if (selected)
        {
            Section = section;
        }
    }

    /// <summary>
    /// What the Settings section says while the device has no settings that Astra implements: which ones will be here.
    /// A kind of device that gets settings shows them in its view instead.
    /// </summary>
    public abstract string NoSettingsText { get; }
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

    public override string NoSettingsText =>
        "Gain, offset, cooling, binning, region of interest and readout mode will be set here once Astra supports them.";

    public string ResolutionText => Rig is { } r ? $"{r.ResolutionText} (from {r.Name})" : "Not reported";

    public string PixelSizeText => Rig is { } r ? $"{r.PixelSizeText} (from {r.Name})" : "Not reported";

    public string SensorText => Rig is { } r ? $"{r.SensorText} (from {r.Name})" : "Not reported";
}

/// <summary>The focuser: position and travel, a move; backlash, speed and limits will be its settings.</summary>
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

    public override string NoSettingsText => "Backlash, speed, travel limits and temperature compensation will be set here once Astra supports them.";
}

/// <summary>The filter wheel: the filter in the light path and a change; slot names, focus offsets and settle delays will be its settings.</summary>
public sealed class FilterWheelDetailViewModel(FilterWheelViewModel wheel, RigViewModel? rig, DeviceConfigurationViewModel? configuration = null)
    : DeviceDetailViewModel(wheel, configuration)
{
    public FilterWheelViewModel Wheel { get; } = wheel;
    public RigViewModel? Rig { get; } = rig;
    public string RigText => Rig?.Name ?? "Not part of a rig";

    public override string NoSettingsText => "Slot names, focus offsets per filter and settle delays will be set here once Astra supports them.";
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

    public override string NoSettingsText => "Slew limits, the observing site, pier side and the tracking mode will be set here once Astra supports them.";
}

/// <summary>The guider: whether it guides, start and stop; dither defaults, settling and the backend will be its settings.</summary>
public sealed class GuiderDetailViewModel(GuiderViewModel guider, DeviceConfigurationViewModel? configuration = null)
    : DeviceDetailViewModel(guider, configuration)
{
    public GuiderViewModel Guider { get; } = guider;

    public override string NoSettingsText => "Dither defaults, settling thresholds and the guiding backend will be set here once Astra supports them.";
}

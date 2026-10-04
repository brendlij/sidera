using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Focusers;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// What the equipment page shows. The two are different things and are never on one page: the devices are the hardware,
/// the rigs are optional groupings of those devices into optical trains.
/// </summary>
public enum EquipmentMode
{
    /// <summary>The registered devices, grouped by kind. The default.</summary>
    Devices,

    /// <summary>The configured rigs. Offered only when there are any.</summary>
    Rigs,
}

/// <summary>A titled group of devices in the browser of the equipment page: all the cameras, all the focusers.</summary>
public sealed record EquipmentSection(string Title, IReadOnlyList<DeviceViewModelBase> Items)
{
    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// The equipment page: a workspace with a browser on the left and the detail of what is selected on the right, in two
/// modes. <b>Devices</b> (the default) browses every device registered with the runtime, grouped by kind (cameras,
/// focusers, filter wheels, mounts, guiders) and shows one device in detail: its state, its controls, its settings and
/// its driver. A mount and a guider are devices like the others here; which of them a session shares is for the session
/// to say. <b>Rigs</b> browses the configured rigs, each a grouping of devices with its optics, and is offered only when
/// there are any: an installation without a rig is a normal one, and no device needs a rig to be controlled. Other device
/// kinds are not shown because Astra has nothing to do with them yet.
/// <para>
/// Each mode remembers its own selection, so going to the rigs and back leaves the device where it was. The first device
/// (and the first rig) is selected from the start, so the workspace is never empty while there is something to show.
/// </para>
/// </summary>
public sealed partial class EquipmentViewModel : ViewModelBase, IDisposable
{
    private readonly Dictionary<DeviceViewModelBase, DeviceDetailViewModel> _details = [];

    public EquipmentViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        TimeSpan manualExposure
    )
    {
        var devices = host.DeviceRegistry.GetAll().OrderBy(d => d.Id.Value, StringComparer.Ordinal).ToList();

        Cameras = devices.OfType<ICamera>()
            .Select(c => new CameraViewModel(c, host, postToUi, activity, imaging, manualExposure)).ToList();
        Focusers = devices.OfType<IFocuser>().Select(f => new FocuserViewModel(f, host, postToUi, activity)).ToList();
        FilterWheels = devices.OfType<IFilterWheel>().Select(w => new FilterWheelViewModel(w, host, postToUi, activity)).ToList();
        Mounts = devices.OfType<IMount>().Select(m => new MountViewModel(m, host, postToUi, activity)).ToList();
        Guiders = devices.OfType<IGuider>().Select(g => new GuiderViewModel(g, host, postToUi, activity)).ToList();
        Rigs = host.RigRegistry.GetAll().OrderBy(r => r.Id.Value, StringComparer.Ordinal)
            .Select(r => new RigViewModel(r, host, Cameras, Focusers, FilterWheels)).ToList();

        foreach (var device in Devices)
        {
            var captured = device;
            device.OpenCommand = new RelayCommand(() => OpenDevice(captured));
            _details[device] = CreateDetail(device);
        }

        foreach (var rig in Rigs)
        {
            var captured = rig;
            rig.OpenCommand = new RelayCommand(() => OpenRig(captured));
        }

        Sections = BuildSections();
        Show(Devices.FirstOrDefault());
        Show(Rigs.FirstOrDefault());
    }

    public IReadOnlyList<RigViewModel> Rigs { get; }
    public IReadOnlyList<CameraViewModel> Cameras { get; }
    public IReadOnlyList<FocuserViewModel> Focusers { get; }
    public IReadOnlyList<FilterWheelViewModel> FilterWheels { get; }
    public IReadOnlyList<MountViewModel> Mounts { get; }
    public IReadOnlyList<GuiderViewModel> Guiders { get; }

    /// <summary>Rigs are optional: nothing about them is shown when none is configured.</summary>
    public bool HasRigs => Rigs.Count > 0;

    /// <summary>There is at least one device.</summary>
    public bool HasDevices => Devices.Any();

    /// <summary>Every device, in display order.</summary>
    public IEnumerable<DeviceViewModelBase> Devices => Cameras.Cast<DeviceViewModelBase>()
        .Concat(Focusers).Concat(FilterWheels).Concat(Mounts).Concat(Guiders);

    // The mode

    /// <summary>What the page shows now. Rigs is only offered with rigs; without any the page stays on the devices.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDevicesMode))]
    [NotifyPropertyChangedFor(nameof(IsRigsMode))]
    public partial EquipmentMode Mode { get; set; }

    public bool IsDevicesMode
    {
        get => Mode == EquipmentMode.Devices;
        set => Select(EquipmentMode.Devices, value);
    }

    public bool IsRigsMode
    {
        get => Mode == EquipmentMode.Rigs;
        set => Select(EquipmentMode.Rigs, value);
    }

    private void Select(EquipmentMode mode, bool selected)
    {
        if (selected)
        {
            Mode = mode == EquipmentMode.Rigs && !HasRigs ? EquipmentMode.Devices : mode;
        }
    }

    // The browser

    /// <summary>
    /// The groups of devices, by kind and in this order: cameras, focusers, filter wheels, mounts, guiders. A kind the
    /// installation has no device of is left out. Rigs are not in here.
    /// </summary>
    public IReadOnlyList<EquipmentSection> Sections { get; private set; } = [];

    public bool HasSections => Sections.Count > 0;

    private IReadOnlyList<EquipmentSection> BuildSections() => new[]
    {
        new EquipmentSection("Cameras", Cameras.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Focusers", Focusers.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Filter Wheels", FilterWheels.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Mounts", Mounts.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Guiders", Guiders.Cast<DeviceViewModelBase>().ToList()),
    }.Where(section => section.HasItems).ToList();

    // The detail

    /// <summary>The device whose detail the Devices mode shows; <c>null</c> only while there is no device.</summary>
    [ObservableProperty]
    public partial DeviceViewModelBase? SelectedDevice { get; private set; }

    /// <summary>The detail view model of <see cref="SelectedDevice"/>: the common shell and what is of its kind.</summary>
    [ObservableProperty]
    public partial DeviceDetailViewModel? SelectedDetail { get; private set; }

    /// <summary>The rig whose detail the Rigs mode shows; <c>null</c> only while there is no rig.</summary>
    [ObservableProperty]
    public partial RigViewModel? SelectedRig { get; private set; }

    /// <summary>The detail of a device, by the device.</summary>
    public DeviceDetailViewModel DetailOf(DeviceViewModelBase device) => _details[device];

    /// <summary>Opens a device in the Devices mode, wherever the request came from (its row, or a rig that names it).</summary>
    private void OpenDevice(DeviceViewModelBase device)
    {
        Mode = EquipmentMode.Devices;
        Show(device);
    }

    private void OpenRig(RigViewModel rig)
    {
        Mode = EquipmentMode.Rigs;
        Show(rig);
    }

    private void Show(DeviceViewModelBase? device)
    {
        if (SelectedDevice is { } before)
        {
            before.IsSelected = false;
        }

        SelectedDevice = device;
        SelectedDetail = device is null ? null : _details[device];
        if (device is not null)
        {
            device.IsSelected = true;
        }
    }

    private void Show(RigViewModel? rig)
    {
        if (SelectedRig is { } before)
        {
            before.IsSelected = false;
        }

        SelectedRig = rig;
        if (rig is not null)
        {
            rig.IsSelected = true;
        }
    }

    // The rig a device is part of, if any: only for what the detail tells about it, never for what the device can do.
    private DeviceDetailViewModel CreateDetail(DeviceViewModelBase device) => device switch
    {
        CameraViewModel camera => new CameraDetailViewModel(camera, Rigs.FirstOrDefault(r => r.Camera == camera)),
        FocuserViewModel focuser => new FocuserDetailViewModel(focuser, Rigs.FirstOrDefault(r => r.Focuser == focuser)),
        FilterWheelViewModel wheel => new FilterWheelDetailViewModel(wheel, Rigs.FirstOrDefault(r => r.FilterWheel == wheel)),
        MountViewModel mount => new MountDetailViewModel(mount),
        GuiderViewModel guider => new GuiderDetailViewModel(guider),
        _ => throw new NotSupportedException($"No detail for {device.GetType().Name}."),
    };

    public void Dispose()
    {
        foreach (var device in Devices)
        {
            device.Dispose();
        }
    }
}

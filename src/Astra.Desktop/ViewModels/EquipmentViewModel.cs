using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Astra.Ascom;
using Astra.Ascom.Discovery;
using Astra.Core.Devices;
using Astra.Core.FilterWheels;
using Astra.Core.Focusers;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Desktop.Hardware;
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
/// What lets the equipment page change the equipment: the service that keeps the runtime and the file in step, the
/// discovery of ASCOM drivers and the driver setup dialogs. Without it the page only shows what the host has (the tests of
/// the pages, a host that is composed in code).
/// </summary>
public sealed record EquipmentManagement(EquipmentService Service, IAscomDiscovery Discovery, IAscomSetupService Setup);

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
/// <para>
/// With an <see cref="EquipmentManagement"/> the page can add, edit and remove devices while Astra runs. The lists
/// (<see cref="Cameras"/> and the others) are the same list objects for the whole life of the page and change in place, so
/// that the view models that were given one keep seeing the equipment as it is.
/// </para>
/// </summary>
public sealed partial class EquipmentViewModel : ViewModelBase, IDisposable, IDeviceManager
{
    private readonly Dictionary<DeviceViewModelBase, DeviceDetailViewModel> _details = [];
    private readonly AstraRuntimeHost _host;
    private readonly Action<Action> _postToUi;
    private readonly SessionActivity _activity;
    private readonly ImagingViewModel _imaging;
    private readonly TimeSpan _manualExposure;
    private readonly EquipmentManagement? _management;
    private readonly List<CameraViewModel> _cameras;
    private readonly List<FocuserViewModel> _focusers;
    private readonly List<FilterWheelViewModel> _filterWheels;
    private readonly List<MountViewModel> _mounts;
    private readonly List<GuiderViewModel> _guiders;
    private readonly List<RigViewModel> _rigs;

    public EquipmentViewModel(
        AstraRuntimeHost host,
        Action<Action> postToUi,
        SessionActivity activity,
        ImagingViewModel imaging,
        TimeSpan manualExposure,
        EquipmentManagement? management = null
    )
    {
        _host = host;
        _postToUi = postToUi;
        _activity = activity;
        _imaging = imaging;
        _manualExposure = manualExposure;
        _management = management;

        var devices = host.DeviceRegistry.GetAll().OrderBy(d => d.Id.Value, StringComparer.Ordinal).ToList();
        _cameras = [.. devices.OfType<ICamera>().Select(CreateCamera)];
        _focusers = [.. devices.OfType<IFocuser>().Select(f => new FocuserViewModel(f, host, postToUi, activity))];
        _filterWheels = [.. devices.OfType<IFilterWheel>().Select(w => new FilterWheelViewModel(w, host, postToUi, activity))];
        _mounts = [.. devices.OfType<IMount>().Select(m => new MountViewModel(m, host, postToUi, activity))];
        _guiders = [.. devices.OfType<IGuider>().Select(g => new GuiderViewModel(g, host, postToUi, activity))];
        _rigs = [];
        BuildRigs();

        foreach (var device in Devices)
        {
            Attach(device);
        }

        Sections = BuildSections();
        Show(Devices.FirstOrDefault());
        Show(_rigs.FirstOrDefault());

        if (management is not null)
        {
            management.Service.Changed += OnEquipmentChanged;
            ProblemsText = string.Join(Environment.NewLine, management.Service.Problems);
        }
    }

    public IReadOnlyList<RigViewModel> Rigs => _rigs;
    public IReadOnlyList<CameraViewModel> Cameras => _cameras;
    public IReadOnlyList<FocuserViewModel> Focusers => _focusers;
    public IReadOnlyList<FilterWheelViewModel> FilterWheels => _filterWheels;
    public IReadOnlyList<MountViewModel> Mounts => _mounts;
    public IReadOnlyList<GuiderViewModel> Guiders => _guiders;

    /// <summary>Rigs are optional: nothing about them is shown when none is configured.</summary>
    public bool HasRigs => _rigs.Count > 0;

    /// <summary>There is at least one device.</summary>
    public bool HasDevices => Devices.Any();

    /// <summary>Every device, in display order.</summary>
    public IEnumerable<DeviceViewModelBase> Devices => _cameras.Cast<DeviceViewModelBase>()
        .Concat(_focusers).Concat(_filterWheels).Concat(_mounts).Concat(_guiders);

    /// <summary>A device was added, replaced or removed, or rigs were added: pages that built something from the lists build it again.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>A device view model came into the page (a device was added or replaced): hook what has to follow its state.</summary>
    public event EventHandler<DeviceViewModelBase>? DeviceViewModelAdded;

    /// <summary>A device view model left the page; it is disposed after the event.</summary>
    public event EventHandler<DeviceViewModelBase>? DeviceViewModelRemoved;

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSections))]
    public partial IReadOnlyList<EquipmentSection> Sections { get; private set; } = [];

    public bool HasSections => Sections.Count > 0;

    private IReadOnlyList<EquipmentSection> BuildSections() => new[]
    {
        new EquipmentSection("Cameras", _cameras.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Focusers", _focusers.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Filter Wheels", _filterWheels.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Mounts", _mounts.Cast<DeviceViewModelBase>().ToList()),
        new EquipmentSection("Guiders", _guiders.Cast<DeviceViewModelBase>().ToList()),
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
            if (_details.TryGetValue(before, out var hidden))
            {
                hidden.IsShown = false;
            }
        }

        SelectedDevice = device;
        SelectedDetail = device is null ? null : _details[device];
        if (SelectedDetail is { } shown)
        {
            shown.IsShown = true;
        }

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
    private DeviceDetailViewModel CreateDetail(DeviceViewModelBase device)
    {
        var configuration = _management is null ? null : new DeviceConfigurationViewModel(device, this);
        var preferences = _management?.Service;
        var poll = DevicePanelViewModel.DefaultPollInterval;
        return device switch
        {
            CameraViewModel camera => new CameraDetailViewModel(camera, _rigs.FirstOrDefault(r => r.Camera == camera), configuration, preferences, poll),
            FocuserViewModel focuser => new FocuserDetailViewModel(focuser, _rigs.FirstOrDefault(r => r.Focuser == focuser), configuration, preferences, poll),
            FilterWheelViewModel wheel => new FilterWheelDetailViewModel(wheel, _rigs.FirstOrDefault(r => r.FilterWheel == wheel), configuration),
            MountViewModel mount => new MountDetailViewModel(mount, configuration, preferences, poll),
            GuiderViewModel guider => new GuiderDetailViewModel(guider, configuration),
            _ => throw new NotSupportedException($"No detail for {device.GetType().Name}."),
        };
    }

    private CameraViewModel CreateCamera(ICamera camera) =>
        new(camera, _host, _postToUi, _activity, _imaging, _manualExposure);

    private DeviceViewModelBase? CreateViewModel(IDevice device) => device switch
    {
        ICamera camera => CreateCamera(camera),
        IFocuser focuser => new FocuserViewModel(focuser, _host, _postToUi, _activity),
        IFilterWheel wheel => new FilterWheelViewModel(wheel, _host, _postToUi, _activity),
        IMount mount => new MountViewModel(mount, _host, _postToUi, _activity),
        IGuider guider => new GuiderViewModel(guider, _host, _postToUi, _activity),
        _ => null,
    };

    private void Attach(DeviceViewModelBase device)
    {
        device.OpenCommand = new RelayCommand(() => OpenDevice(device));
        _details[device] = CreateDetail(device);
    }

    private void BuildRigs()
    {
        _rigs.Clear();
        foreach (var rig in _host.RigRegistry.GetAll().OrderBy(r => r.Id.Value, StringComparer.Ordinal))
        {
            var vm = new RigViewModel(rig, _host, _cameras, _focusers, _filterWheels);
            var captured = vm;
            vm.OpenCommand = new RelayCommand(() => OpenRig(captured));
            _rigs.Add(vm);
        }
    }

    // Changing the equipment

    /// <summary>The page can add, edit and remove devices.</summary>
    public bool CanManage => _management is not null;

    /// <summary>What the pages above the equipment know about devices being in use that the page does not: the sequence.</summary>
    public Func<string, string?>? RemovalGuard { get; set; }

    /// <summary>The form for adding or editing a device, or <c>null</c> while there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorOpen))]
    public partial DeviceEditorViewModel? Editor { get; private set; }

    public bool IsEditorOpen => Editor is not null;

    /// <summary>What went wrong while loading the equipment (an unreadable file, a device that could not be created); empty when nothing did.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string ProblemsText { get; private set; } = string.Empty;

    public bool HasProblems => ProblemsText.Length > 0;

    /// <summary>The last thing that was asked of the equipment and did not work (the demo, for example).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string NoticeText { get; private set; } = string.Empty;

    public bool HasNotice => NoticeText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void AddDevice()
    {
        NoticeText = string.Empty;
        Mode = EquipmentMode.Devices;
        Editor = NewEditor(null);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void AddDemoEquipment()
    {
        var result = _management!.Service.AddDemoEquipment();
        NoticeText = result.Problem ?? string.Empty;
    }

    private DeviceEditorViewModel NewEditor(DeviceConfiguration? existing) => new(
        existing,
        configuration => existing is null ? _management!.Service.Add(configuration) : _management!.Service.Update(configuration),
        () => Editor = null,
        id => _management!.Service.Configuration.Find(id) is not null || _host.DeviceRegistry.TryGet(new DeviceId(id), out _),
        _management!.Discovery,
        _management.Setup);

    public string? WhyCannotEdit(DeviceViewModelBase device) => _management?.Service.WhyCannotEdit(device.DeviceIdText);

    public string? WhyCannotRemove(DeviceViewModelBase device)
    {
        if (_management is null)
        {
            return "The equipment cannot be changed here.";
        }

        return _management.Service.WhyCannotRemove(device.DeviceIdText) ?? RemovalGuard?.Invoke(device.DeviceIdText);
    }

    public void BeginEdit(DeviceViewModelBase device)
    {
        if (_management?.Service.ConfigurationOf(device.DeviceIdText) is { } configuration)
        {
            NoticeText = string.Empty;
            Editor = NewEditor(configuration);
        }
    }

    public string? Remove(DeviceViewModelBase device)
    {
        if (WhyCannotRemove(device) is { } problem)
        {
            return problem;
        }

        return _management!.Service.Remove(device.DeviceIdText).Problem;
    }

    public async Task<string?> SetupDriverAsync(DeviceViewModelBase device)
    {
        if (_management is null
            || _management.Service.ConfigurationOf(device.DeviceIdText) is not { Backend: DeviceBackend.Ascom, ProgId: { } progId } configuration
            || AscomDeviceKinds.From(configuration.Type) is not { } kind)
        {
            return "This device has no driver to set up.";
        }

        var result = await _management.Setup.ShowAsync(kind, progId);
        return result.Completed ? null : result.Problem;
    }

    // The service made a change and saved it: the page follows.
    private void OnEquipmentChanged(object? sender, EquipmentChange change)
    {
        switch (change.Kind)
        {
            case EquipmentChangeKind.DeviceAdded when change.Device is { } added:
                if (CreateViewModel(added) is { } vm)
                {
                    InsertSorted(vm);
                    Attach(vm);
                    RefreshSections();
                    DeviceViewModelAdded?.Invoke(this, vm);
                    OpenDevice(vm);
                }

                break;

            case EquipmentChangeKind.DeviceReplaced when change.Device is { } replaced:
                ReplaceViewModel(replaced);
                break;

            case EquipmentChangeKind.DeviceRemoved when change.DeviceId is { } removedId:
                RemoveViewModel(removedId);
                break;

            case EquipmentChangeKind.RigsAdded:
                RebuildRigsKeepingSelection();
                RefreshDetails();
                break;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasRigs));
        OnPropertyChanged(nameof(HasDevices));
    }

    private void ReplaceViewModel(IDevice replaced)
    {
        var old = Devices.FirstOrDefault(d => d.DeviceIdText == replaced.Id.Value);
        var wasSelected = old is not null && ReferenceEquals(SelectedDevice, old);
        var section = old is not null && _details.TryGetValue(old, out var oldDetail) ? oldDetail.Section : DeviceDetailSection.Overview;
        if (old is not null)
        {
            Detach(old);
        }

        if (CreateViewModel(replaced) is not { } vm)
        {
            return;
        }

        InsertSorted(vm);
        Attach(vm);
        _details[vm].Section = section; // the tab the user was on stays open
        RebuildRigsKeepingSelection();
        RefreshSections();
        DeviceViewModelAdded?.Invoke(this, vm);
        if (wasSelected)
        {
            Show(vm);
        }
    }

    private void RemoveViewModel(string id)
    {
        var old = Devices.FirstOrDefault(d => d.DeviceIdText == id);
        if (old is null)
        {
            return;
        }

        var wasSelected = ReferenceEquals(SelectedDevice, old);
        var index = Devices.ToList().IndexOf(old);
        Detach(old);
        RefreshSections();
        if (wasSelected)
        {
            var remaining = Devices.ToList();
            Show(remaining.Count == 0 ? null : remaining[Math.Min(index, remaining.Count - 1)]);
        }
    }

    // A device view model leaves the page: its row, its detail, and what it listens to.
    private void Detach(DeviceViewModelBase device)
    {
        switch (device)
        {
            case CameraViewModel camera:
                _cameras.Remove(camera);
                break;
            case FocuserViewModel focuser:
                _focusers.Remove(focuser);
                break;
            case FilterWheelViewModel wheel:
                _filterWheels.Remove(wheel);
                break;
            case MountViewModel mount:
                _mounts.Remove(mount);
                break;
            case GuiderViewModel guider:
                _guiders.Remove(guider);
                break;
        }

        if (_details.Remove(device, out var removedDetail))
        {
            removedDetail.Dispose();
        }

        device.IsSelected = false;
        DeviceViewModelRemoved?.Invoke(this, device);
        device.Dispose();
    }

    private void InsertSorted(DeviceViewModelBase device)
    {
        static void Insert<T>(List<T> list, T item) where T : DeviceViewModelBase
        {
            var index = list.FindIndex(existing => string.CompareOrdinal(existing.DeviceIdText, item.DeviceIdText) > 0);
            list.Insert(index < 0 ? list.Count : index, item);
        }

        switch (device)
        {
            case CameraViewModel camera:
                Insert(_cameras, camera);
                break;
            case FocuserViewModel focuser:
                Insert(_focusers, focuser);
                break;
            case FilterWheelViewModel wheel:
                Insert(_filterWheels, wheel);
                break;
            case MountViewModel mount:
                Insert(_mounts, mount);
                break;
            case GuiderViewModel guider:
                Insert(_guiders, guider);
                break;
        }
    }

    private void RefreshSections()
    {
        Sections = BuildSections();
        OnPropertyChanged(nameof(HasDevices));
    }

    // The rigs read their devices when they are made: they are made again when a device was replaced or rigs were added,
    // and the rig that was selected stays selected.
    private void RebuildRigsKeepingSelection()
    {
        var selected = SelectedRig?.RigIdText;
        BuildRigs();
        Show(_rigs.FirstOrDefault(r => r.RigIdText == selected) ?? _rigs.FirstOrDefault());
    }

    // The details name the rig of their device; they are made again once the rigs are.
    private void RefreshDetails()
    {
        foreach (var device in Devices.ToList())
        {
            var section = DeviceDetailSection.Overview;
            if (_details.TryGetValue(device, out var previous))
            {
                section = previous.Section;
                previous.Dispose();
            }

            _details[device] = CreateDetail(device);
            _details[device].Section = section;
        }

        SelectedDetail = SelectedDevice is null ? null : _details[SelectedDevice];
        if (SelectedDetail is { } shown)
        {
            shown.IsShown = true;
        }
    }

    public void Dispose()
    {
        if (_management is not null)
        {
            _management.Service.Changed -= OnEquipmentChanged;
        }

        foreach (var detail in _details.Values)
        {
            detail.Dispose();
        }

        foreach (var device in Devices)
        {
            device.Dispose();
        }
    }
}

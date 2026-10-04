using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Desktop.Hardware;
using Sidera.Runtime;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// What lets the equipment page change the equipment: the service that keeps the runtime and the file in step, the
/// discovery of ASCOM drivers and the driver setup dialogs. Without it the page only shows what the host has (the tests of
/// the pages, a host that is composed in code).
/// </summary>
public sealed record EquipmentManagement(EquipmentService Service, IAscomDiscovery Discovery, IAscomSetupService Setup, Sidera.Desktop.Settings.SiteService? Site = null);

/// <summary>
/// The equipment page: a workspace with a browser on the left and the detail of what is selected on the right, in two
/// modes. <b>Devices</b> (the default) browses every device registered with the runtime, grouped by kind (cameras,
/// focusers, filter wheels, mounts, guiders) and shows one device in detail: its state, its controls, its settings and
/// its driver. A mount and a guider are devices like the others here; which of them a session shares is for the session
/// to say. <b>Rigs</b> browses the configured rigs, each a grouping of devices with its optics, and is offered only when
/// there are any: an installation without a rig is a normal one, and no device needs a rig to be controlled. Other device
/// kinds are not shown because Sidera has nothing to do with them yet.
/// <para>
/// Each mode remembers its own selection, so going to the rigs and back leaves the device where it was. The first device
/// (and the first rig) is selected from the start, so the workspace is never empty while there is something to show.
/// </para>
/// <para>
/// With an <see cref="EquipmentManagement"/> the page can add, edit and remove devices while Sidera runs. The lists
/// (<see cref="Cameras"/> and the others) are the same list objects for the whole life of the page and change in place, so
/// that the view models that were given one keep seeing the equipment as it is.
/// </para>
/// </summary>
public sealed partial class EquipmentViewModel : ViewModelBase, IDisposable, IDeviceManager
{
    private readonly Dictionary<DeviceViewModelBase, DeviceDetailViewModel> _details = [];
    private readonly SideraRuntimeHost _host;
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
    private bool _addingMany;

    public EquipmentViewModel(
        SideraRuntimeHost host,
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

        BuildNavigation();
        BuildSlots();

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

    // The rig a device is part of, if any: only for what the detail tells about it, never for what the device can do.
    private DeviceDetailViewModel CreateDetail(DeviceViewModelBase device)
    {
        var configuration = _management is null ? null : new DeviceConfigurationViewModel(device, this);
        var preferences = _management?.Service;
        var poll = DevicePanelViewModel.DefaultPollInterval;
        return device switch
        {
            CameraViewModel camera => new CameraDetailViewModel(camera, _rigs.FirstOrDefault(r => r.Camera == camera), configuration, preferences, poll, _management?.Service),
            FocuserViewModel focuser => new FocuserDetailViewModel(focuser, _rigs.FirstOrDefault(r => r.Focuser == focuser), configuration, preferences, poll),
            FilterWheelViewModel wheel => new FilterWheelDetailViewModel(wheel, _rigs.FirstOrDefault(r => r.FilterWheel == wheel), configuration),
            MountViewModel mount => new MountDetailViewModel(mount, configuration, preferences, poll, _management?.Site),
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
            foreach (var member in vm.Members)
            {
                var page = member.Role switch
                {
                    "Camera" => EquipmentPage.Camera,
                    "Focuser" => EquipmentPage.Focuser,
                    _ => EquipmentPage.FilterWheel,
                };
                member.OpenCommand = new RelayCommand(() => OpenRigPage(captured, page));
            }
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
        Editor = NewEditor(null);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void AddDemoEquipment()
    {
        // Many devices at once: the page stays where it is (the overview shows what came), instead of opening the last of them.
        _addingMany = true;
        try
        {
            var result = _management!.Service.AddDemoEquipment();
            NoticeText = result.Problem ?? string.Empty;
        }
        finally
        {
            _addingMany = false;
        }
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
                    RefreshNavigation();
                    DeviceViewModelAdded?.Invoke(this, vm);
                    if (!_addingMany)
                    {
                        OpenDevice(vm);
                    }
                }

                break;

            case EquipmentChangeKind.DeviceReplaced when change.Device is { } replaced:
                ReplaceViewModel(replaced);
                break;

            case EquipmentChangeKind.DeviceRemoved when change.DeviceId is { } removedId:
                RemoveViewModel(removedId);
                break;

            case EquipmentChangeKind.RigsAdded:
            case EquipmentChangeKind.RigsChanged:
                BuildRigs();
                RefreshDetails();
                RefreshNavigation();
                break;
        }

        DevicesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(HasRigs));
        OnPropertyChanged(nameof(HasDevices));
    }

    private void ReplaceViewModel(IDevice replaced)
    {
        var old = Devices.FirstOrDefault(d => d.DeviceIdText == replaced.Id.Value);
        if (old is not null)
        {
            Detach(old);
        }

        if (CreateViewModel(replaced) is not { } vm)
        {
            RefreshNavigation();
            return;
        }

        InsertSorted(vm);
        Attach(vm);
        BuildRigs();
        RefreshDetails();
        DeviceViewModelAdded?.Invoke(this, vm);
        RefreshNavigation(); // the selections are by device id: the new device takes the place of the old one
    }

    private void RemoveViewModel(string id)
    {
        var old = Devices.FirstOrDefault(d => d.DeviceIdText == id);
        if (old is null)
        {
            return;
        }

        Detach(old);
        BuildRigs();
        RefreshDetails();
        RefreshNavigation();
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

    // The details name the rig of their device; they are made again once the rigs are.
    private void RefreshDetails()
    {
        foreach (var device in Devices.ToList())
        {
            if (_details.TryGetValue(device, out var previous))
            {
                previous.Dispose();
            }

            _details[device] = CreateDetail(device);
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

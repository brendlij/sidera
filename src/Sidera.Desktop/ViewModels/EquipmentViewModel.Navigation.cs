using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The pages of the equipment workspace. A device kind has a page of its own; a rig has an overview besides the pages of the
/// devices it names. Nothing here knows how a page looks.
/// </summary>
public enum EquipmentPage
{
    Overview,
    Camera,
    Mount,
    Focuser,
    FilterWheel,
    Guider,
}

/// <summary>One context of the workspace: the devices operated directly (standalone), or one rig.</summary>
public sealed partial class EquipmentContextViewModel(string title, string key, RigViewModel? rig, ICommand select) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>"standalone" or the id of the rig.</summary>
    public string Key { get; } = key;

    /// <summary>The rig of this context; <c>null</c> for the standalone devices.</summary>
    public RigViewModel? Rig { get; } = rig;

    public bool IsStandalone => Rig is null;

    public ICommand SelectCommand { get; } = select;

    private bool _syncing;

    /// <summary>Selected; setting it to <c>true</c> (a tab that was chosen) selects the context.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !_syncing)
        {
            SelectCommand.Execute(null);
        }
    }

    /// <summary>Shows whether this context is the selected one, without selecting it.</summary>
    internal void Show(bool selected)
    {
        _syncing = true;
        try
        {
            IsSelected = selected;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>One page of the selected context, as a tab: the overview of a rig, or the page of a device kind.</summary>
public sealed partial class EquipmentPageViewModel(EquipmentPage page, string title, ICommand select) : ObservableObject
{
    public EquipmentPage Page { get; } = page;
    public string Title { get; } = title;
    public ICommand SelectCommand { get; } = select;

    private bool _syncing;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !_syncing)
        {
            SelectCommand.Execute(null);
        }
    }

    internal void Show(bool selected)
    {
        _syncing = true;
        try
        {
            IsSelected = selected;
        }
        finally
        {
            _syncing = false;
        }
    }
}

/// <summary>A step of the way to where the workspace is: a link to a place above, or the place itself (no command).</summary>
public sealed record BreadcrumbItem(string Title, ICommand? Command)
{
    public bool IsLink => Command is not null;
    public bool IsCurrent => Command is null;
}

/// <summary>One line of the equipment overview: a device of a group, with its state, which opens its page.</summary>
public sealed record EquipmentLandingRow(string Role, DeviceViewModelBase Device, ICommand OpenCommand)
{
    public string Name => Device.Name;
}

/// <summary>
/// One group of the equipment overview: a rig with its devices, or the devices that belong to no rig. What it says about the
/// state follows the devices.
/// </summary>
public sealed partial class EquipmentLandingGroupViewModel : ObservableObject
{
    public EquipmentLandingGroupViewModel(string title, RigViewModel? rig, IReadOnlyList<EquipmentLandingRow> rows, ICommand? open)
    {
        Title = title;
        Rig = rig;
        Rows = rows;
        OpenCommand = open;
        foreach (var row in rows)
        {
            row.Device.Refreshed += (_, _) => Refresh();
        }

        Refresh();
    }

    public string Title { get; }
    public RigViewModel? Rig { get; }
    public IReadOnlyList<EquipmentLandingRow> Rows { get; }
    public ICommand? OpenCommand { get; }

    public bool IsRig => Rig is not null;

    /// <summary>"3 devices · 2 connected".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    private void Refresh()
    {
        var connected = Rows.Count(r => r.Device.IsConnected);
        SummaryText = $"{Rows.Count} {(Rows.Count == 1 ? "device" : "devices")} · {connected} connected";
    }
}

/// <summary>
/// The navigation of the equipment workspace: <b>contexts</b> (the devices operated directly, and each rig, which is only there
/// when there is one), the <b>pages</b> of the selected context (the device kinds, or the overview and the devices of a rig),
/// and the <b>device</b> of the page. A device kind gets a page of its own with a choice of the device when there are several; the
/// device of a rig page is the one the rig names, and a rig page of a device the rig does not have is not offered. Entering the
/// workspace shows an overview of everything. The state stays while the application runs: leaving for another page and coming
/// back returns to the same context, page and device.
/// </summary>
public sealed partial class EquipmentViewModel
{
    private const string StandaloneKey = "standalone";

    // What is remembered: the page of each context, and the device of each standalone page (by id: the device may be replaced).
    private readonly Dictionary<string, EquipmentPage> _pageOfContext = [];
    private readonly Dictionary<EquipmentPage, string> _standaloneDevice = [];
    private string? _contextKey;
    private bool _applying;

    /// <summary>Standalone and then each rig, with the rigs by their names.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EquipmentContextViewModel> Contexts { get; private set; } = [];

    /// <summary>The selected context; <c>null</c> while the overview of the equipment is shown.</summary>
    [ObservableProperty]
    public partial EquipmentContextViewModel? SelectedContext { get; private set; }

    /// <summary>The rig of the selected context; <c>null</c> for the overview and for the standalone devices.</summary>
    [ObservableProperty]
    public partial RigViewModel? SelectedRig { get; private set; }

    /// <summary>The pages of the selected context.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EquipmentPageViewModel> Pages { get; private set; } = [];

    /// <summary>The selected page; <c>null</c> while the overview of the equipment shown.</summary>
    [ObservableProperty]
    public partial EquipmentPage? SelectedPage { get; private set; }

    /// <summary>The overview of all the equipment is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContextShown))]
    public partial bool IsLanding { get; private set; } = true;

    public bool IsContextShown => !IsLanding;

    /// <summary>The overview of a rig is shown.</summary>
    [ObservableProperty]
    public partial bool IsRigOverview { get; private set; }

    /// <summary>The page of a device kind is shown (standalone or of a rig).</summary>
    [ObservableProperty]
    public partial bool IsDeviceWorkspace { get; private set; }

    /// <summary>The selected context has no page to show: no device of any kind.</summary>
    [ObservableProperty]
    public partial bool IsContextEmpty { get; private set; }

    /// <summary>The device the page controls; <c>null</c> when the page has none.</summary>
    [ObservableProperty]
    public partial DeviceViewModelBase? SelectedDevice { get; private set; }

    /// <summary>The detail view model of <see cref="SelectedDevice"/>: what the page of its kind shows.</summary>
    [ObservableProperty]
    public partial DeviceDetailViewModel? SelectedDetail { get; private set; }

    /// <summary>The devices of the kind of the page to choose from; only for standalone pages.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DeviceViewModelBase> DeviceChoices { get; private set; } = [];

    /// <summary>The page offers a choice of the device: standalone and more than one device of the kind.</summary>
    [ObservableProperty]
    public partial bool HasDeviceChooser { get; private set; }

    /// <summary>The device the chooser shows; choosing another one makes it the device of the page.</summary>
    public DeviceViewModelBase? ChosenDevice
    {
        get => SelectedDevice;
        set
        {
            if (value is not null && !ReferenceEquals(value, SelectedDevice) && !_applying && SelectedPage is { } page && _contextKey == StandaloneKey)
            {
                _standaloneDevice[page] = value.DeviceIdText;
                Apply();
            }
        }
    }

    /// <summary>What a page says when the rig does not have the device of its kind; empty when it has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingDevice))]
    public partial string MissingDeviceText { get; private set; } = string.Empty;

    public bool HasMissingDevice => MissingDeviceText.Length > 0;

    /// <summary>The way to where the workspace is: "Equipment › Main Rig › Camera".</summary>
    [ObservableProperty]
    public partial IReadOnlyList<BreadcrumbItem> Breadcrumb { get; private set; } = [];

    /// <summary>There is a place above the one that is shown.</summary>
    [ObservableProperty]
    public partial bool CanGoBack { get; private set; }

    /// <summary>The name of the place Back leads to, for the button.</summary>
    [ObservableProperty]
    public partial string BackText { get; private set; } = string.Empty;

    /// <summary>The groups of the overview: each rig with its devices, then the devices that belong to no rig.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EquipmentLandingGroupViewModel> LandingGroups { get; private set; } = [];

    public bool HasLandingGroups => LandingGroups.Count > 0;

    /// <summary>The detail of a device, by the device.</summary>
    public DeviceDetailViewModel DetailOf(DeviceViewModelBase device) => _details[device];

    // ---- Commands

    /// <summary>Goes one place up: from a device page of a rig to the rig, from anything else to the overview.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        if (!CanGoBack)
        {
            return;
        }

        if (IsDeviceWorkspace && SelectedRig is not null)
        {
            _pageOfContext[_contextKey!] = EquipmentPage.Overview;
        }
        else
        {
            _contextKey = null;
        }

        Apply();
    }

    [RelayCommand]
    private void ShowOverview()
    {
        _contextKey = null;
        Apply();
    }

    // ---- Opening

    /// <summary>Opens a device on its page among the standalone devices, wherever the request came from.</summary>
    private void OpenDevice(DeviceViewModelBase device)
    {
        var page = PageOf(device);
        _contextKey = StandaloneKey;
        _pageOfContext[StandaloneKey] = page;
        _standaloneDevice[page] = device.DeviceIdText;
        Apply();
    }

    private void OpenRig(RigViewModel rig)
    {
        _contextKey = rig.RigIdText;
        Apply();
    }

    private void OpenRigPage(RigViewModel rig, EquipmentPage page)
    {
        _contextKey = rig.RigIdText;
        _pageOfContext[rig.RigIdText] = page;
        Apply();
    }

    private void SelectContext(string key)
    {
        _contextKey = key;
        Apply();
    }

    private void SelectPage(EquipmentPage page)
    {
        if (_contextKey is { } key)
        {
            _pageOfContext[key] = page;
            Apply();
        }
    }

    private static EquipmentPage PageOf(DeviceViewModelBase device) => device switch
    {
        CameraViewModel => EquipmentPage.Camera,
        MountViewModel => EquipmentPage.Mount,
        FocuserViewModel => EquipmentPage.Focuser,
        FilterWheelViewModel => EquipmentPage.FilterWheel,
        _ => EquipmentPage.Guider,
    };

    private static string TitleOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Overview => "Overview",
        EquipmentPage.Camera => "Camera",
        EquipmentPage.Mount => "Mount",
        EquipmentPage.Focuser => "Focuser",
        EquipmentPage.FilterWheel => "Filter Wheel",
        _ => "Guider",
    };

    private IEnumerable<DeviceViewModelBase> DevicesOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Camera => _cameras,
        EquipmentPage.Mount => _mounts,
        EquipmentPage.Focuser => _focusers,
        EquipmentPage.FilterWheel => _filterWheels,
        EquipmentPage.Guider => _guiders,
        _ => [],
    };

    // ---- Building and applying

    private void BuildNavigation()
    {
        Contexts =
        [
            new EquipmentContextViewModel("Standalone", StandaloneKey, null, new RelayCommand(() => SelectContext(StandaloneKey))),
            .. _rigs.Select(rig => new EquipmentContextViewModel(rig.Name, rig.RigIdText, rig, new RelayCommand(() => SelectContext(rig.RigIdText)))),
        ];
        BuildLanding();
        Apply();
    }

    /// <summary>The lists changed (a device or a rig came or went): the contexts, the pages and the selections are worked out again.</summary>
    private void RefreshNavigation() => BuildNavigation();

    private void BuildLanding()
    {
        var groups = new List<EquipmentLandingGroupViewModel>();
        var inRigs = new HashSet<DeviceViewModelBase>();
        foreach (var rig in _rigs)
        {
            var rows = new List<EquipmentLandingRow>();
            void Add(string role, DeviceViewModelBase? device, EquipmentPage page)
            {
                if (device is null)
                {
                    return;
                }

                inRigs.Add(device);
                rows.Add(new EquipmentLandingRow(role, device, new RelayCommand(() => OpenRigPage(rig, page))));
            }

            Add("Camera", rig.Camera, EquipmentPage.Camera);
            Add("Focuser", rig.Focuser, EquipmentPage.Focuser);
            Add("Filter Wheel", rig.FilterWheel, EquipmentPage.FilterWheel);
            groups.Add(new EquipmentLandingGroupViewModel(rig.Name, rig, rows, new RelayCommand(() => OpenRig(rig))));
        }

        var standalone = Devices.Where(d => !inRigs.Contains(d))
            .Select(d => new EquipmentLandingRow(d.KindTitle, d, new RelayCommand(() => OpenDevice(d)))).ToList();
        if (standalone.Count > 0)
        {
            groups.Add(new EquipmentLandingGroupViewModel(_rigs.Count > 0 ? "Standalone devices" : "Devices", null, standalone, null));
        }

        LandingGroups = groups;
        OnPropertyChanged(nameof(HasLandingGroups));
    }

    // Works out everything that follows from the context, the page and the remembered devices.
    private void Apply()
    {
        _applying = true;
        try
        {
            // A context that is gone (its rig was removed) is the overview again.
            var context = _contextKey is null ? null : Contexts.FirstOrDefault(c => c.Key == _contextKey);
            _contextKey = context?.Key;
            foreach (var c in Contexts)
            {
                c.Show(ReferenceEquals(c, context));
            }

            SelectedContext = context;
            SelectedRig = context?.Rig;
            IsLanding = context is null;

            var pages = context is null ? [] : PagesOf(context);
            var page = (EquipmentPage?)null;
            if (context is not null && pages.Count > 0)
            {
                page = _pageOfContext.TryGetValue(context.Key, out var remembered) && pages.Any(p => p.Page == remembered)
                    ? remembered
                    : pages[0].Page;
                _pageOfContext[context.Key] = page.Value;
            }

            foreach (var tab in pages)
            {
                tab.Show(tab.Page == page);
            }

            Pages = pages;
            SelectedPage = page;
            IsContextEmpty = context is not null && pages.Count == 0;
            IsRigOverview = context?.Rig is not null && page == EquipmentPage.Overview;
            IsDeviceWorkspace = context is not null && page is { } shown && shown != EquipmentPage.Overview;

            // The device of the page: the one the rig names, or the one chosen among the standalone devices.
            DeviceViewModelBase? device = null;
            var choices = (IReadOnlyList<DeviceViewModelBase>)[];
            var missing = string.Empty;
            if (IsDeviceWorkspace)
            {
                var kind = page!.Value;
                if (context!.Rig is { } rig)
                {
                    device = kind switch
                    {
                        EquipmentPage.Camera => rig.Camera,
                        EquipmentPage.Focuser => rig.Focuser,
                        EquipmentPage.FilterWheel => rig.FilterWheel,
                        _ => null,
                    };
                    if (device is null)
                    {
                        missing = $"No {TitleOf(kind).ToLowerInvariant()} is configured for this rig.";
                    }
                }
                else
                {
                    choices = [.. DevicesOf(kind)];
                    device = _standaloneDevice.TryGetValue(kind, out var id) ? choices.FirstOrDefault(d => d.DeviceIdText == id) : null;
                    device ??= choices.FirstOrDefault();
                    if (device is not null)
                    {
                        _standaloneDevice[kind] = device.DeviceIdText;
                    }
                }
            }

            DeviceChoices = choices;
            HasDeviceChooser = choices.Count > 1;
            MissingDeviceText = missing;
            ShowDevice(device);
            OnPropertyChanged(nameof(ChosenDevice));
            BuildBreadcrumb(context, page);
        }
        finally
        {
            _applying = false;
        }
    }

    private IReadOnlyList<EquipmentPageViewModel> PagesOf(EquipmentContextViewModel context)
    {
        var pages = new List<EquipmentPageViewModel>();
        void Add(EquipmentPage page) => pages.Add(new EquipmentPageViewModel(page, TitleOf(page), new RelayCommand(() => SelectPage(page))));

        if (context.Rig is { } rig)
        {
            Add(EquipmentPage.Overview);
            Add(EquipmentPage.Camera);
            if (rig.HasFocuser)
            {
                Add(EquipmentPage.Focuser);
            }

            if (rig.HasFilterWheel)
            {
                Add(EquipmentPage.FilterWheel);
            }
        }
        else
        {
            foreach (var page in new[] { EquipmentPage.Camera, EquipmentPage.Mount, EquipmentPage.Focuser, EquipmentPage.FilterWheel, EquipmentPage.Guider })
            {
                if (DevicesOf(page).Any())
                {
                    Add(page);
                }
            }
        }

        return pages;
    }

    private void ShowDevice(DeviceViewModelBase? device)
    {
        if (ReferenceEquals(SelectedDevice, device))
        {
            return;
        }

        if (SelectedDevice is { } before)
        {
            before.IsSelected = false;
            if (_details.TryGetValue(before, out var hidden))
            {
                hidden.IsShown = false;
            }
        }

        SelectedDevice = device;
        SelectedDetail = device is not null && _details.TryGetValue(device, out var detail) ? detail : null;
        if (SelectedDetail is { } shown)
        {
            shown.IsShown = true;
        }

        if (device is not null)
        {
            device.IsSelected = true;
        }
    }

    private void BuildBreadcrumb(EquipmentContextViewModel? context, EquipmentPage? page)
    {
        var overview = new RelayCommand(() =>
        {
            _contextKey = null;
            Apply();
        });

        var items = new List<BreadcrumbItem>();
        string? back = null;
        if (context is null)
        {
            items.Add(new BreadcrumbItem("Equipment", null));
        }
        else if (context.Rig is { } rig)
        {
            items.Add(new BreadcrumbItem("Equipment", overview));
            if (page == EquipmentPage.Overview || page is null)
            {
                items.Add(new BreadcrumbItem(rig.Name, null));
                back = "Equipment";
            }
            else
            {
                items.Add(new BreadcrumbItem(rig.Name, new RelayCommand(() => OpenRigPage(rig, EquipmentPage.Overview))));
                items.Add(new BreadcrumbItem(TitleOf(page.Value), null));
                back = rig.Name;
            }
        }
        else
        {
            items.Add(new BreadcrumbItem("Equipment", overview));
            items.Add(new BreadcrumbItem(page is { } p ? TitleOf(p) : "Standalone", null));
            back = "Equipment";
        }

        Breadcrumb = items;
        CanGoBack = back is not null;
        BackText = back ?? string.Empty;
        BackCommand.NotifyCanExecuteChanged();
    }
}

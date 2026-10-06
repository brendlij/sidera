using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
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
    Rotator,
}

/// <summary>
/// The top level of the equipment page: the overview, one section for each kind of device (each holds zero or more devices), and the imaging setups when there are any. A section is a place the
/// user goes to; which context or page of the workspace it is, is for <see cref="EquipmentViewModel"/>.
/// </summary>
public enum EquipmentSection
{
    Overview,
    Cameras,
    Focusers,
    FilterWheels,
    Rotators,
    Mounts,
    Guiders,
    ImagingSetups,
}

/// <summary>One tab of the equipment page.</summary>
public sealed partial class EquipmentSectionViewModel(EquipmentSection section, string title, ICommand select) : ObservableObject
{
    public EquipmentSection Section { get; } = section;
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

/// <summary>One context of the workspace: the devices by their kind, or one imaging setup.</summary>
public sealed partial class EquipmentContextViewModel(string title, string key, RigViewModel? rig, ICommand select) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>"devices" or the id of the imaging setup.</summary>
    public string Key { get; } = key;

    /// <summary>The imaging setup of this context; <c>null</c> for the devices.</summary>
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

    /// <summary>What a rig could still be given from the devices that are in no rig ("Mount, Focuser"); empty for other groups and when there is nothing to add.</summary>
    public string HintText { get; internal set; } = string.Empty;

    public bool HasHint => HintText.Length > 0;

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
    private const string StandaloneKey = "devices";

    // What is remembered: the page of each context, and the device of each standalone page (by id: the device may be replaced).
    private readonly Dictionary<string, EquipmentPage> _pageOfContext = [];
    private readonly Dictionary<EquipmentPage, string> _standaloneDevice = [];
    private string? _contextKey;
    private bool _applying;

    /// <summary>The devices, and then each imaging setup, with the setups by their names.</summary>
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

    /// <summary>Managing the rig whose overview is shown (rename, remove, give it devices); <c>null</c> elsewhere and when the equipment cannot be changed here.</summary>
    [ObservableProperty]
    public partial RigSetupViewModel? RigSetup { get; private set; }

    /// <summary>Adding a rig; <c>null</c> when the equipment cannot be changed here.</summary>
    public AddRigViewModel? AddRig { get; private set; }

    internal void Notify(string text) => NoticeText = text;

    private void OpenRigNamed(string name)
    {
        if (_rigs.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)) is { } rig)
        {
            OpenRig(rig);
        }
    }

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
        _addingKind = null;
        _contextKey = StandaloneKey;
        _pageOfContext[StandaloneKey] = page;
        _standaloneDevice[page] = device.DeviceIdText;
        Apply();
    }

    private void OpenRig(RigViewModel rig)
    {
        _addingKind = null;
        _contextKey = rig.RigIdText;
        Apply();
    }

    // The page of a kind of device, with its remembered device.
    private void OpenKind(EquipmentPage page)
    {
        _addingKind = null;
        _contextKey = StandaloneKey;
        _pageOfContext[StandaloneKey] = page;
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
            _addingKind = null;
            _pageOfContext[key] = page;
            Apply();
        }
    }

    // ---- sections: the overview, one for each kind of device, the imaging setups

    /// <summary>The tabs of the page: the overview, the kinds of device (always all of them: a kind without a device is where the first one is added) and, when there are any, the imaging setups.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EquipmentSectionViewModel> Sections { get; private set; } = [];

    /// <summary>The section that is shown.</summary>
    [ObservableProperty]
    public partial EquipmentSection SelectedSection { get; private set; }

    /// <summary>The imaging setups are shown: their list, and the overview of the selected one.</summary>
    public bool IsImagingSetupsSection => SelectedSection == EquipmentSection.ImagingSetups;

    /// <summary>There are imaging setups to show a section for. None when there is one camera and no setup was made: nothing about setups is on the page then.</summary>
    public bool HasImagingSetups => _rigs.Count > 0;

    /// <summary>The imaging setups as tabs, in the section of the imaging setups.</summary>
    public IReadOnlyList<EquipmentContextViewModel> SetupContexts => [.. Contexts.Where(c => !c.IsStandalone)];

    private static string SectionTitleOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Camera => "Cameras",
        EquipmentPage.Focuser => "Focusers",
        EquipmentPage.FilterWheel => "Filter Wheels",
        EquipmentPage.Rotator => "Rotators",
        EquipmentPage.Mount => "Mounts",
        EquipmentPage.Guider => "Guiders",
        _ => "Overview",
    };

    private static EquipmentSection SectionOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Camera => EquipmentSection.Cameras,
        EquipmentPage.Focuser => EquipmentSection.Focusers,
        EquipmentPage.FilterWheel => EquipmentSection.FilterWheels,
        EquipmentPage.Rotator => EquipmentSection.Rotators,
        EquipmentPage.Mount => EquipmentSection.Mounts,
        EquipmentPage.Guider => EquipmentSection.Guiders,
        _ => EquipmentSection.Overview,
    };

    private void BuildSections()
    {
        var sections = new List<EquipmentSectionViewModel> { new(EquipmentSection.Overview, "Overview", new RelayCommand(() => SelectSection(EquipmentSection.Overview))) };
        foreach (var page in KindPages)
        {
            var section = SectionOf(page);
            sections.Add(new EquipmentSectionViewModel(section, SectionTitleOf(page), new RelayCommand(() => SelectSection(section))));
        }

        if (HasImagingSetups)
        {
            sections.Add(new EquipmentSectionViewModel(EquipmentSection.ImagingSetups, "Imaging Setups", new RelayCommand(() => SelectSection(EquipmentSection.ImagingSetups))));
        }

        Sections = sections;
        OnPropertyChanged(nameof(HasImagingSetups));
        OnPropertyChanged(nameof(SetupContexts));
    }

    private void SelectSection(EquipmentSection section)
    {
        _addingKind = null;
        switch (section)
        {
            case EquipmentSection.Overview:
                _contextKey = null;
                break;
            case EquipmentSection.ImagingSetups:
                _contextKey = _contextKey is { } current && current != StandaloneKey && Contexts.Any(c => c.Key == current) ? current : _rigs.FirstOrDefault()?.RigIdText ?? _contextKey;
                break;
            default:
                _contextKey = StandaloneKey;
                _pageOfContext[StandaloneKey] = KindPages.First(p => SectionOf(p) == section);
                break;
        }

        Apply();
    }

    private void ApplySections(EquipmentContextViewModel? context, EquipmentPage? page)
    {
        SelectedSection = context is null ? EquipmentSection.Overview : context.Rig is not null ? EquipmentSection.ImagingSetups : page is { } shown ? SectionOf(shown) : EquipmentSection.Overview;
        OnPropertyChanged(nameof(IsImagingSetupsSection));
        foreach (var tab in Sections)
        {
            tab.Show(tab.Section == SelectedSection);
        }
    }

    // ---- adding a device of a kind, and naming it

    private EquipmentPage? _addingKind;

    /// <summary>A new device of the kind of the page is being added: the page shows an empty slot for its driver instead of the device that was there.</summary>
    public bool IsAddingDevice => _addingKind is not null;

    /// <summary>The kind of device the page is for, singular and lower case: "camera".</summary>
    public string KindNoun => SelectedPage is { } page && page != EquipmentPage.Overview ? TitleOf(page).ToLowerInvariant() : "device";

    /// <summary>"Camera": the label of the choice of the device.</summary>
    public string KindLabel => SelectedPage is { } page && page != EquipmentPage.Overview ? TitleOf(page) : "Device";

    /// <summary>"+ Add Camera".</summary>
    public string AddKindText => $"+ Add {(SelectedPage is { } page && page != EquipmentPage.Overview ? TitleOf(page) : "device")}";

    private bool CanAddToKind => _management is not null && IsDeviceWorkspace && SelectedRig is null && !IsAddingDevice;

    /// <summary>Adds another device of this kind: its driver is chosen in the empty slot that opens, and it becomes a device of the equipment with a name and an id of its own.</summary>
    [RelayCommand(CanExecute = nameof(CanAddToKind))]
    private void AddToKind()
    {
        if (SelectedPage is { } page && page != EquipmentPage.Overview)
        {
            NoticeText = string.Empty;
            _addingKind = page;
            Apply();
        }
    }

    /// <summary>Leaves the empty slot of a device that was to be added; the device that was shown before is shown again.</summary>
    [RelayCommand]
    private void CancelAdd()
    {
        _addingKind = null;
        Apply();
    }

    /// <summary>The name of the device of the page, as it is edited.</summary>
    [ObservableProperty]
    public partial string NameText { get; set; } = string.Empty;

    /// <summary>Why the device cannot be renamed now (it is connected), or why the name was refused; empty when nothing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameProblem))]
    public partial string RenameProblemText { get; private set; } = string.Empty;

    public bool HasRenameProblem => RenameProblemText.Length > 0;

    private bool CanRename => _management is not null && SelectedDevice is not null && !IsAddingDevice;

    /// <summary>Gives the device another name. The id (what sequences and setups refer to) stays; a device that is connected is not renamed.</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        RenameProblemText = string.Empty;
        if (SelectedDevice is not { } device || _management is null)
        {
            return;
        }

        var name = NameText.Trim();
        if (name == device.Name)
        {
            return;
        }

        if (_management.Service.ConfigurationOf(device.DeviceIdText) is not { } configuration)
        {
            RenameProblemText = "The device is not part of the equipment.";
            return;
        }

        if (name.Length == 0)
        {
            RenameProblemText = "The device needs a name.";
            return;
        }

        if (Devices.Any(d => d.KindTitle == device.KindTitle && d.DeviceIdText != device.DeviceIdText && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            RenameProblemText = $"There is already a {KindNoun} named '{name}'.";
            return;
        }

        var result = _management.Service.Update(configuration with { Name = name });
        if (!result.Succeeded)
        {
            RenameProblemText = result.Problem ?? "The device could not be renamed.";
            NameText = device.Name;
        }
    }

    /// <summary>A name for a new device of a kind that no other device of the kind has: "Simulator", then "Simulator 2".</summary>
    internal string UniqueName(DeviceType type, string name, string? exceptId = null)
    {
        var taken = Devices.Where(d => KindOf(d) == type && d.DeviceIdText != exceptId).Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            candidate = $"{name} {n}";
        }

        return candidate;
    }

    private static EquipmentPage PageOf(DeviceViewModelBase device) => device switch
    {
        CameraViewModel => EquipmentPage.Camera,
        MountViewModel => EquipmentPage.Mount,
        FocuserViewModel => EquipmentPage.Focuser,
        FilterWheelViewModel => EquipmentPage.FilterWheel,
        RotatorViewModel => EquipmentPage.Rotator,
        _ => EquipmentPage.Guider,
    };

    // The kinds in the order the equipment is described in: camera, focuser, filter wheel, rotator, mount, guider.
    private static readonly EquipmentPage[] KindPages =
        [EquipmentPage.Camera, EquipmentPage.Focuser, EquipmentPage.FilterWheel, EquipmentPage.Rotator, EquipmentPage.Mount, EquipmentPage.Guider];

    private static string TitleOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Overview => "Overview",
        EquipmentPage.Camera => "Camera",
        EquipmentPage.Mount => "Mount",
        EquipmentPage.Focuser => "Focuser",
        EquipmentPage.FilterWheel => "Filter Wheel",
        EquipmentPage.Rotator => "Rotator",
        _ => "Guider",
    };

    private IEnumerable<DeviceViewModelBase> DevicesOf(EquipmentPage page) => page switch
    {
        EquipmentPage.Camera => _cameras,
        EquipmentPage.Mount => _mounts,
        EquipmentPage.Focuser => _focusers,
        EquipmentPage.FilterWheel => _filterWheels,
        EquipmentPage.Rotator => _rotators,
        EquipmentPage.Guider => _guiders,
        _ => [],
    };

    // ---- Building and applying

    private void BuildNavigation()
    {
        if (AddRig is null && _management is not null)
        {
            AddRig = new AddRigViewModel(_management.Service, Notify, OpenRigNamed);
            OnPropertyChanged(nameof(AddRig));
        }

        Contexts =
        [
            new EquipmentContextViewModel("Devices", StandaloneKey, null, new RelayCommand(() => SelectContext(StandaloneKey))),
            .. _rigs.Select(rig => new EquipmentContextViewModel(rig.Name, rig.RigIdText, rig, new RelayCommand(() => SelectContext(rig.RigIdText)))),
        ];
        BuildLanding();
        BuildSections();
        Apply();
    }

    /// <summary>The lists changed (a device or a rig came or went): the contexts, the pages and the selections are worked out again.</summary>
    private void RefreshNavigation() => BuildNavigation();

    // The overview: every device once, under its kind (each row opens the page of the device), and then the imaging setups that were made, each with the devices it is made of.
    private void BuildLanding()
    {
        var groups = new List<EquipmentLandingGroupViewModel>();
        foreach (var page in KindPages)
        {
            var devices = DevicesOf(page).ToList();
            if (devices.Count == 0)
            {
                continue;
            }

            var rows = devices.Select(d => new EquipmentLandingRow(d.KindTitle, d, new RelayCommand(() => OpenDevice(d)))).ToList();
            groups.Add(new EquipmentLandingGroupViewModel(SectionTitleOf(page), null, rows, new RelayCommand(() => OpenKind(page))));
        }

        foreach (var rig in _rigs)
        {
            var rows = new List<EquipmentLandingRow>();
            void Add(string role, DeviceViewModelBase? device)
            {
                if (device is not null)
                {
                    rows.Add(new EquipmentLandingRow(role, device, new RelayCommand(() => OpenDevice(device))));
                }
            }

            Add("Camera", rig.Camera);
            Add("Mount", rig.Mount);
            Add("Focuser", rig.Focuser);
            Add("Filter Wheel", rig.FilterWheel);
            Add("Guider", rig.Guider);
            Add("Rotator", rig.Rotator);
            groups.Add(new EquipmentLandingGroupViewModel(rig.Name, rig, rows, new RelayCommand(() => OpenRig(rig))));
        }

        LandingGroups = groups;
        OnPropertyChanged(nameof(HasLandingGroups));
        OnPropertyChanged(nameof(NeedsSetupHint));
    }

    /// <summary>
    /// With more than one camera and no imaging setup, the equipment says what a setup is for: Sidera does not guess which camera an imaging block means. With one camera nothing is said: the
    /// camera is the setup.
    /// </summary>
    public bool NeedsSetupHint => _rigs.Count == 0 && _cameras.Count >= 2 && _management is not null;

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
                        EquipmentPage.Mount => rig.Mount,
                        EquipmentPage.Focuser => rig.Focuser,
                        EquipmentPage.FilterWheel => rig.FilterWheel,
                        EquipmentPage.Guider => rig.Guider,
                        EquipmentPage.Rotator => rig.Rotator,
                        _ => null,
                    };
                    if (device is null)
                    {
                        missing = $"No {TitleOf(kind).ToLowerInvariant()} is configured for this rig.";
                    }
                }
                else if (_addingKind == kind)
                {
                    // A new device is being added: the slot is empty until its driver is chosen.
                    choices = [.. DevicesOf(kind)];
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
            HasDeviceChooser = choices.Count > 1 && !IsAddingDevice;
            MissingDeviceText = missing;
            ShowDevice(device);
            OnPropertyChanged(nameof(ChosenDevice));
            NameText = device?.Name ?? string.Empty;
            RenameProblemText = device is not null && WhyCannotEdit(device) is { } cannotRename ? cannotRename : string.Empty;
            OnPropertyChanged(nameof(IsAddingDevice));
            OnPropertyChanged(nameof(KindNoun));
            OnPropertyChanged(nameof(KindLabel));
            OnPropertyChanged(nameof(AddKindText));
            AddToKindCommand.NotifyCanExecuteChanged();
            RenameCommand.NotifyCanExecuteChanged();
            ApplySections(context, page);
            BuildBreadcrumb(context, page);
            RigSetup = IsRigOverview && _management is not null && context?.Rig is { } setupRig ? new RigSetupViewModel(setupRig, _management.Service, Notify) : null;
            AddRig?.Refresh();

            // The driver row, the connection and the page of the device are those of the slot of its kind.
            var slot = IsDeviceWorkspace && page is { } shownPage ? Slots.FirstOrDefault(s => s.Page == shownPage) : null;
            SelectedSlot = slot;
            slot?.Refresh();
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
            // Only what the rig has: a page of a device that the rig does not have would be empty.
            Add(EquipmentPage.Overview);
            Add(EquipmentPage.Camera);
            if (rig.HasMount)
            {
                Add(EquipmentPage.Mount);
            }

            if (rig.HasFocuser)
            {
                Add(EquipmentPage.Focuser);
            }

            if (rig.HasFilterWheel)
            {
                Add(EquipmentPage.FilterWheel);
            }

            if (rig.HasGuider)
            {
                Add(EquipmentPage.Guider);
            }

            if (rig.HasRotator)
            {
                Add(EquipmentPage.Rotator);
            }
        }
        else
        {
            // Every kind has its page, with devices or without: zero devices is where the first one is added.
            foreach (var page in KindPages)
            {
                Add(page);
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
            items.Add(new BreadcrumbItem(page is { } p ? SectionTitleOf(p) : "Devices", null));
            back = "Equipment";
        }

        Breadcrumb = items;
        CanGoBack = back is not null;
        BackText = back ?? string.Empty;
        BackCommand.NotifyCanExecuteChanged();
    }
}

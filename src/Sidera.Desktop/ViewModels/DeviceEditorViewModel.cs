using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>One choice of the kind of device.</summary>
public sealed record DeviceTypeChoice(DeviceType Type, string Title);

/// <summary>One choice of the backend.</summary>
public sealed record BackendChoice(DeviceBackend Backend, string Title);

/// <summary>
/// The form for adding a device, or changing one that is not connected: its kind, its backend, for ASCOM the driver
/// (from the drivers found on the computer, or a ProgId typed in), its name and its id. The id is suggested from the kind and
/// the name until it is typed; it is shown but fixed when a device is edited, and so are its kind and backend. Nothing is
/// connected and no driver is created: the form only reads the drivers that are installed, and offers the driver's own setup
/// dialog as a separate button.
/// </summary>
public sealed partial class DeviceEditorViewModel : ViewModelBase
{
    private static readonly DeviceTypeChoice[] AllTypes =
    [
        new(DeviceType.Camera, "Camera"),
        new(DeviceType.Focuser, "Focuser"),
        new(DeviceType.Mount, "Mount"),
        new(DeviceType.FilterWheel, "Filter Wheel"),
        new(DeviceType.Guider, "Guider"),
    ];

    private readonly IAscomDiscovery? _discovery;
    private readonly IAscomSetupService? _setup;
    private readonly Func<DeviceConfiguration, EquipmentResult> _commit;
    private readonly Action _close;
    private readonly Func<string, bool> _idTaken;
    private bool _nameEdited;
    private bool _idEdited;
    private bool _updating;
    private int _discoveryVersion;

    /// <param name="existing">The device to change, or <c>null</c> to add a new one.</param>
    /// <param name="commit">Applies the configuration (adds or replaces the device) and says whether it worked.</param>
    /// <param name="close">Closes the form.</param>
    /// <param name="idTaken">Whether a device id is in use already.</param>
    public DeviceEditorViewModel(
        DeviceConfiguration? existing,
        Func<DeviceConfiguration, EquipmentResult> commit,
        Action close,
        Func<string, bool> idTaken,
        IAscomDiscovery? discovery,
        IAscomSetupService? setup)
    {
        _commit = commit;
        _close = close;
        _idTaken = idTaken;
        _discovery = discovery;
        _setup = setup;
        Existing = existing;

        BackendChoices = discovery is null
            ? [new BackendChoice(DeviceBackend.Simulator, "Simulator")]
            : [new BackendChoice(DeviceBackend.Simulator, "Simulator"), new BackendChoice(DeviceBackend.Ascom, "ASCOM")];

        _updating = true;
        SelectedBackend = BackendChoices.First(b => b.Backend == (existing?.Backend ?? DeviceBackend.Simulator));
        RebuildTypes();
        SelectedType = TypeChoices.FirstOrDefault(t => t.Type == existing?.Type) ?? TypeChoices[0];
        NameInput = existing?.Name ?? string.Empty;
        IdInput = existing?.Id ?? EquipmentIds.Suggest(SelectedType.Type, null);
        _updating = false;
        _nameEdited = existing is not null;
        _idEdited = existing is not null;

        if (existing is { Backend: DeviceBackend.Ascom } ascom)
        {
            CustomProgIdInput = string.Empty;
            _ = LoadDriversAsync(ascom.ProgId);
        }

        Validate();
    }

    public DeviceConfiguration? Existing { get; }

    public bool IsEdit => Existing is not null;

    public string Title => IsEdit ? "Edit device" : "Add device";

    public string SaveText => IsEdit ? "Save" : "Add device";

    public IReadOnlyList<BackendChoice> BackendChoices { get; }

    /// <summary>The kinds of device the chosen backend has: ASCOM drives cameras, focusers and mounts.</summary>
    public ObservableCollection<DeviceTypeChoice> TypeChoices { get; } = [];

    /// <summary>The drivers found on this computer for the chosen kind.</summary>
    public ObservableCollection<AscomDriverInfo> Drivers { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAscom))]
    [NotifyPropertyChangedFor(nameof(CanChangeKind))]
    public partial BackendChoice SelectedBackend { get; set; } = null!;

    [ObservableProperty]
    public partial DeviceTypeChoice SelectedType { get; set; } = null!;

    [ObservableProperty]
    public partial AscomDriverInfo? SelectedDriver { get; set; }

    /// <summary>A ProgId typed in, for a driver that discovery does not list. It wins over the list when it is not empty.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetupDriverCommand))]
    public partial string CustomProgIdInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NameInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IdInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDiscovering { get; private set; }

    /// <summary>What discovery had to say: the platform is missing, there is no driver of the kind. Empty when all is well.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiscoveryMessage))]
    public partial string DiscoveryText { get; private set; } = string.Empty;

    public bool HasDiscoveryMessage => DiscoveryText.Length > 0;

    /// <summary>What is wrong with the form now, as a sentence; empty when it can be saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string ProblemText { get; private set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>What went wrong when the device was saved or the setup dialog opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailure))]
    public partial string FailureText { get; private set; } = string.Empty;

    public bool HasFailure => FailureText.Length > 0;

    /// <summary>The setup dialog of the driver is open: the form waits.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetupDriverCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsAscom => SelectedBackend.Backend == DeviceBackend.Ascom;

    /// <summary>The kind and the backend are chosen when a device is added, and fixed afterwards.</summary>
    public bool CanChangeKind => !IsEdit;

    /// <summary>The id is typed in only for a new device.</summary>
    public bool CanChangeId => !IsEdit;

    /// <summary>The ProgId of the chosen driver: the typed one, else the one picked from the list, else <c>null</c>.</summary>
    public string? ProgId => !string.IsNullOrWhiteSpace(CustomProgIdInput)
        ? CustomProgIdInput.Trim()
        : SelectedDriver?.ProgId;

    private AscomDeviceKind? AscomKind => SelectedType is null ? null : AscomDeviceKinds.From(SelectedType.Type);

    partial void OnSelectedBackendChanged(BackendChoice value)
    {
        if (_updating)
        {
            return;
        }

        RebuildTypes();
        if (!TypeChoices.Contains(SelectedType))
        {
            SelectedType = TypeChoices[0];
        }
        else
        {
            AfterKindChanged();
        }
    }

    partial void OnSelectedTypeChanged(DeviceTypeChoice value)
    {
        if (!_updating && value is not null)
        {
            AfterKindChanged();
        }
    }

    partial void OnNameInputChanged(string value)
    {
        if (_updating)
        {
            return;
        }

        _nameEdited = true;
        SuggestId();
        Validate();
    }

    partial void OnIdInputChanged(string value)
    {
        if (!_updating)
        {
            _idEdited = true;
            Validate();
        }
    }

    // The list is alphabetical, which puts the simulators of the ASCOM platform (OmniSim, "Simulator") among or before the real drivers.
    // A driver is not preselected for being first: a real one is, when there is one.
    private AscomDriverInfo? DefaultDriver() =>
        Drivers.FirstOrDefault(d => !IsSimulatorDriver(d)) ?? Drivers.FirstOrDefault();

    private static bool IsSimulatorDriver(AscomDriverInfo driver) =>
        driver.ProgId.Contains("OmniSim", StringComparison.OrdinalIgnoreCase)
        || driver.ProgId.Contains("Simulator", StringComparison.OrdinalIgnoreCase)
        || driver.Name.Contains("simulator", StringComparison.OrdinalIgnoreCase);

    partial void OnSelectedDriverChanged(AscomDriverInfo? value)
    {
        if (_updating)
        {
            return;
        }

        // A driver picked from the list is the choice: a ProgId typed earlier would win over it without anyone seeing it.
        if (value is not null && CustomProgIdInput.Length > 0)
        {
            CustomProgIdInput = string.Empty;
        }

        // A driver picked from the list names the device, unless the user has already named it.
        if (value is not null && !_nameEdited)
        {
            _updating = true;
            NameInput = value.Name;
            _updating = false;
            SuggestId();
        }

        OnPropertyChanged(nameof(ProgId));
        SetupDriverCommand.NotifyCanExecuteChanged();
        Validate();
    }

    partial void OnCustomProgIdInputChanged(string value)
    {
        OnPropertyChanged(nameof(ProgId));
        Validate();
    }

    private void RebuildTypes()
    {
        TypeChoices.Clear();
        foreach (var choice in AllTypes.Where(t => !IsAscom || AscomDeviceFactory.Supports(t.Type)))
        {
            TypeChoices.Add(choice);
        }
    }

    private void AfterKindChanged()
    {
        SuggestId();
        if (IsAscom)
        {
            _ = LoadDriversAsync(null);
        }
        else
        {
            Drivers.Clear();
            SelectedDriver = null;
            DiscoveryText = string.Empty;
        }

        Validate();
    }

    private void SuggestId()
    {
        if (IsEdit || _idEdited)
        {
            return;
        }

        // A drop-down forgets its selection while its list is replaced; there is nothing to suggest from until it has one again.
        if (SelectedType is null)
        {
            return;
        }

        var baseId = EquipmentIds.Suggest(SelectedType.Type, NameInput);
        var id = baseId;
        for (var n = 2; _idTaken(id); n++)
        {
            id = $"{baseId}-{n}";
        }

        _updating = true;
        IdInput = id;
        _updating = false;
    }

    /// <summary>Reads the drivers of the chosen kind again, and keeps the choice (<paramref name="keep"/>) when it is still there.</summary>
    [RelayCommand]
    private Task RefreshDrivers() => LoadDriversAsync(ProgId);

    private async Task LoadDriversAsync(string? keep)
    {
        if (_discovery is null || AscomKind is not { } kind)
        {
            return;
        }

        var version = ++_discoveryVersion;
        IsDiscovering = true;
        try
        {
            var result = await _discovery.DiscoverAsync(kind);
            if (version != _discoveryVersion)
            {
                return; // another kind was chosen meanwhile
            }

            _updating = true;
            try
            {
                Drivers.Clear();
                foreach (var driver in result.Drivers)
                {
                    Drivers.Add(driver);
                }

                var match = keep is null ? null : Drivers.FirstOrDefault(d => string.Equals(d.ProgId, keep, StringComparison.OrdinalIgnoreCase));
                SelectedDriver = match ?? (keep is null ? DefaultDriver() : null);
                CustomProgIdInput = keep is not null && match is null ? keep : string.Empty;
            }
            finally
            {
                _updating = false;
            }

            DiscoveryText = result.Problem ?? (Drivers.Count == 0 ? "No ASCOM driver of this kind is installed. A ProgId can be typed in." : string.Empty);
            if (SelectedDriver is { } selected && !_nameEdited)
            {
                _updating = true;
                NameInput = selected.Name;
                _updating = false;
                SuggestId();
            }

            OnPropertyChanged(nameof(ProgId));
            SetupDriverCommand.NotifyCanExecuteChanged();
            Validate();
        }
        finally
        {
            if (version == _discoveryVersion)
            {
                IsDiscovering = false;
            }
        }
    }

    private void Validate()
    {
        ProblemText = FindProblem() ?? string.Empty;
        FailureText = string.Empty;
    }

    private string? FindProblem()
    {
        if (string.IsNullOrWhiteSpace(NameInput))
        {
            return "Give the device a name.";
        }

        if (EquipmentIds.Problem(IdInput) is { } idProblem)
        {
            return idProblem;
        }

        if (!IsEdit && _idTaken(IdInput.Trim()))
        {
            return $"A device with the id '{IdInput.Trim()}' already exists.";
        }

        if (IsAscom && string.IsNullOrWhiteSpace(ProgId))
        {
            return "Choose an ASCOM driver, or type its ProgId.";
        }

        return null;
    }

    private bool CanSave() => !IsBusy && !HasProblem;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var settings = new Dictionary<string, string>();
        if (IsAscom)
        {
            settings[DeviceConfiguration.ProgIdKey] = ProgId!;
            var name = SelectedDriver is { } driver && string.Equals(driver.ProgId, ProgId, StringComparison.OrdinalIgnoreCase)
                ? driver.Name
                : Existing?.DriverName is { } known && string.Equals(Existing.ProgId, ProgId, StringComparison.OrdinalIgnoreCase) ? known : null;
            if (!string.IsNullOrWhiteSpace(name))
            {
                settings[DeviceConfiguration.DriverNameKey] = name;
            }
        }
        else if (Existing is { Backend: DeviceBackend.Simulator })
        {
            // A simulated device keeps the settings it has (positions, filters).
            foreach (var (key, value) in Existing.Settings)
            {
                settings[key] = value;
            }
        }

        var configuration = new DeviceConfiguration(
            Existing?.Id ?? IdInput.Trim(), NameInput.Trim(), Existing?.Type ?? SelectedType.Type,
            Existing?.Backend ?? SelectedBackend.Backend, settings);

        var result = _commit(configuration);
        if (result.Succeeded)
        {
            _close();
        }
        else
        {
            FailureText = result.Problem ?? "The device could not be saved.";
        }
    }

    private bool CanSetup() => IsAscom && !IsBusy && !string.IsNullOrWhiteSpace(ProgId) && _setup is not null && AscomKind is not null;

    /// <summary>Opens the setup dialog of the chosen driver, so that it can be configured before the device is added.</summary>
    [RelayCommand(CanExecute = nameof(CanSetup))]
    private async Task SetupDriverAsync()
    {
        IsBusy = true;
        FailureText = string.Empty;
        try
        {
            var result = await _setup!.ShowAsync(AscomKind!.Value, ProgId!);
            if (!result.Completed && result.Problem is { } problem)
            {
                FailureText = problem;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _close();
}

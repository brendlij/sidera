using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

/// <summary>One entry of the driver list of a device slot: no device, a simulator, or an installed ASCOM driver.</summary>
public sealed record DriverChoice(string Group, string Text, DeviceBackend? Backend, string? ProgId, string? DriverName)
{
    /// <summary>"No camera": the slot has no device.</summary>
    public bool IsNone => Backend is null;

    public bool IsAscom => Backend == DeviceBackend.Ascom;

    public bool IsPhd2 => Backend == DeviceBackend.Phd2;

    public static DriverChoice None(string text) => new(string.Empty, text, null, null, null);

    public static DriverChoice Simulator => new("Sidera", "Simulator", DeviceBackend.Simulator, null, null);

    public static DriverChoice Phd2 => new("PHD2", "PHD2", DeviceBackend.Phd2, null, null);

    public static DriverChoice Ascom(string progId, string name) => new("ASCOM", name, DeviceBackend.Ascom, progId, name);

    public bool Matches(DeviceConfiguration configuration) =>
        Backend == configuration.Backend
        && (Backend != DeviceBackend.Ascom || string.Equals(ProgId, configuration.ProgId, StringComparison.OrdinalIgnoreCase));

    public DeviceConfiguration ToConfiguration(string id, DeviceType type, IReadOnlyDictionary<string, string>? settings = null) => Backend switch
    {
        DeviceBackend.Ascom => DeviceConfiguration.Ascom(id, Text, type, ProgId!, DriverName),
        DeviceBackend.Phd2 => new DeviceConfiguration(id, Text, type, DeviceBackend.Phd2, settings ?? Sidera.Phd2.Phd2Endpoint.Default.ToSettings()),
        _ => DeviceConfiguration.Simulator(id, Text, type),
    };
}

/// <summary>
/// The place of one kind of device in the equipment, as one choice: which driver it is (none, a simulator, or an installed ASCOM
/// driver), set up, rescanned and connected from one row. Choosing a driver is the whole of configuring: the device of the slot is
/// made, replaced or removed to match, and keeps the same id, so that what refers to it (sequences, rigs) keeps working. A device
/// that is connected is not swapped; the slot says so and keeps the choice it has.
/// </summary>
public sealed partial class DeviceSlotViewModel : ViewModelBase
{
    private readonly EquipmentViewModel _owner;
    private readonly EquipmentManagement? _management;
    private readonly DriverChoice _none;
    private bool _syncing;
    private int _discoveryVersion;

    public DeviceSlotViewModel(EquipmentViewModel owner, EquipmentManagement? management, EquipmentPage page, DeviceType type, string title, string noneText)
    {
        _owner = owner;
        _management = management;
        Page = page;
        Type = type;
        Title = title;
        _none = DriverChoice.None(noneText);
        AscomKind = AscomDeviceKinds.From(type);
        Refresh();
        if (management is not null && AscomKind is not null)
        {
            _ = RescanAsync();
        }
    }

    public EquipmentPage Page { get; }
    public DeviceType Type { get; }
    public string Title { get; }
    public AscomDeviceKind? AscomKind { get; }

    /// <summary>The choices: none first, the simulator, then the ASCOM drivers that are installed.</summary>
    public ObservableCollection<DriverChoice> Choices { get; } = [];

    [ObservableProperty]
    public partial DriverChoice? SelectedChoice { get; set; }

    /// <summary>The device of this slot, or <c>null</c> when the choice is none.</summary>
    [ObservableProperty]
    public partial DeviceViewModelBase? Device { get; private set; }

    /// <summary>The page of that device: its state and its controls.</summary>
    [ObservableProperty]
    public partial DeviceDetailViewModel? Detail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string ProblemText { get; private set; } = string.Empty;

    public bool HasProblem => ProblemText.Length > 0;

    /// <summary>What discovery said, when it found nothing or failed.</summary>
    [ObservableProperty]
    public partial string DiscoveryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsScanning { get; private set; }

    /// <summary>This slot is the page that is shown.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The driver can be changed: not while the device is connected, connecting or disconnecting.</summary>
    public bool CanChoose => _management is not null && !IsDeviceInUse;

    private bool IsDeviceInUse => Device is { ConnectionState: not (DeviceConnectionState.Disconnected or DeviceConnectionState.Faulted) };

    public bool IsEmpty => Device is null;

    public string EmptyText => _owner.IsAddingDevice ? $"Choose the driver of the new {Title.ToLowerInvariant()} above." : $"No {Title.ToLowerInvariant()} chosen. Pick a driver above to use one.";

    partial void OnSelectedChoiceChanged(DriverChoice? value)
    {
        OnPropertyChanged(nameof(IsPhd2Selected));
        SavePhd2Command.NotifyCanExecuteChanged();
        if (_syncing || value is null)
        {
            return;
        }

        ApplyChoice(value);
    }

    /// <summary>
    /// The controls of the device are shown only while it is connected: a device that is not connected shows its name and state and nothing else (no stale values, no controls that cannot do anything).
    /// The optics of a camera are the exception: they are settings, not controls, and are needed before the first connection.
    /// </summary>
    public bool ShowWorkspace => Detail is not null && (Detail is CameraDetailViewModel || Device is { IsConnected: true });

    partial void OnDetailChanged(DeviceDetailViewModel? value) => OnPropertyChanged(nameof(ShowWorkspace));

    partial void OnDeviceChanged(DeviceViewModelBase? oldValue, DeviceViewModelBase? newValue)
    {
        OnPropertyChanged(nameof(ShowWorkspace));
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnDevicePropertyChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnDevicePropertyChanged;
        }

        OnDeviceUseChanged();
        OnPropertyChanged(nameof(IsEmpty));
        SetupCommand.NotifyCanExecuteChanged();
    }

    private void OnDevicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceViewModelBase.ConnectionState))
        {
            OnDeviceUseChanged();
        }
    }

    private void OnDeviceUseChanged()
    {
        OnPropertyChanged(nameof(ShowWorkspace));
        OnPropertyChanged(nameof(CanChoose));
        SetupCommand.NotifyCanExecuteChanged();
        RescanCommand.NotifyCanExecuteChanged();
        SavePhd2Command.NotifyCanExecuteChanged();
    }

    /// <summary>Reads the equipment again: the device of the slot and the choice that matches it.</summary>
    internal void Refresh()
    {
        Device = _owner.DeviceOfKind(Type);
        Detail = Device is null ? null : _owner.DetailOf(Device);
        if (Choices.Count == 0)
        {
            Choices.Add(_none);
            Choices.Add(DriverChoice.Simulator);
            if (Type == DeviceType.Guider)
            {
                Choices.Add(DriverChoice.Phd2);
            }
        }

        var configuration = Device is null ? null : _management?.Service.ConfigurationOf(Device.DeviceIdText);
        if (configuration is { Backend: DeviceBackend.Phd2 } && !_phd2Edited)
        {
            var endpoint = EndpointOf(configuration);
            _phd2Filling = true;
            Phd2Host = endpoint.Host;
            Phd2PortText = endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _phd2Filling = false;
        }
        DriverChoice? match = null;
        if (configuration is not null)
        {
            match = Choices.FirstOrDefault(c => c.Matches(configuration));
            if (match is null && configuration.Backend == DeviceBackend.Ascom && configuration.ProgId is { } progId)
            {
                // A driver that is configured and no longer found is still the choice, so that the slot does not pretend otherwise.
                match = DriverChoice.Ascom(progId, configuration.DriverName ?? configuration.Name);
                Choices.Add(match);
            }
        }

        _syncing = true;
        try
        {
            SelectedChoice = match ?? (Device is null ? null : Choices.FirstOrDefault(c => c.Backend == DeviceBackendOf(Device)) ?? _none);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static DeviceBackend? DeviceBackendOf(DeviceViewModelBase device) =>
        device.BackendText == "Simulator" ? DeviceBackend.Simulator : DeviceBackend.Ascom;

    /// <summary>Lists the ASCOM drivers that are installed again.</summary>
    [RelayCommand(CanExecute = nameof(CanRescan))]
    private async Task RescanAsync()
    {
        if (_management is null || AscomKind is not { } kind)
        {
            return;
        }

        var version = ++_discoveryVersion;
        IsScanning = true;
        try
        {
            var result = await _management.Discovery.DiscoverAsync(kind);
            if (version != _discoveryVersion)
            {
                return;
            }

            _syncing = true;
            try
            {
                for (var i = Choices.Count - 1; i >= 0; i--)
                {
                    if (Choices[i].IsAscom)
                    {
                        Choices.RemoveAt(i);
                    }
                }

                foreach (var driver in result.Drivers.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    Choices.Add(DriverChoice.Ascom(driver.ProgId, driver.Name));
                }

                DiscoveryText = result.Problem ?? (result.Drivers.Count == 0 ? "No ASCOM driver of this kind is installed." : string.Empty);
            }
            finally
            {
                _syncing = false;
            }

            Refresh();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanRescan() => _management is not null && !IsDeviceInUse && AscomKind is not null && !IsScanning;

    partial void OnIsScanningChanged(bool value) => RescanCommand.NotifyCanExecuteChanged();

    /// <summary>Opens the setup dialog of the chosen ASCOM driver.</summary>
    [RelayCommand(CanExecute = nameof(CanSetup))]
    private async Task SetupAsync()
    {
        if (_management is null || AscomKind is not { } kind || SetupProgId() is not { } progId)
        {
            return;
        }

        ProblemText = string.Empty;
        var result = await _management.Setup.ShowAsync(kind, progId);
        if (!result.Completed && result.Problem is { } problem)
        {
            ProblemText = problem;
        }
    }

    // The driver whose setup is opened: the one that is chosen in the list, else the one of the configured ASCOM device (which may be missing from the list of installed drivers, and
    // is then the very case where its setup is needed). Not the connection of the device: setup is for a device that is not connected.
    private string? SetupProgId() =>
        SelectedChoice is { IsAscom: true, ProgId: { } chosen } ? chosen
        : Device is { } device && _management?.Service.ConfigurationOf(device.DeviceIdText) is { Backend: DeviceBackend.Ascom, ProgId: { } configured } ? configured
        : null;

    public bool CanSetup() => _management is not null && !IsDeviceInUse && AscomKind is not null && SetupProgId() is not null;

    // Makes the device of the slot match the choice: removes it, or replaces it with the one of the driver, with the same id.
    private void ApplyChoice(DriverChoice choice)
    {
        ProblemText = string.Empty;
        SetupCommand.NotifyCanExecuteChanged();
        if (_management is null)
        {
            return;
        }

        var service = _management.Service;
        var current = Device;
        var configuration = current is null ? null : service.ConfigurationOf(current.DeviceIdText);
        if (configuration is not null && choice.Matches(configuration))
        {
            return;
        }

        if (current is null && choice.IsNone)
        {
            return;
        }

        IReadOnlyDictionary<string, string>? settings = null;
        if (choice.IsPhd2)
        {
            // Checked before anything is removed: a wrong host or port does not cost the device that is there.
            if (!TryEndpoint(out var endpoint, out var endpointProblem))
            {
                Fail(endpointProblem);
                return;
            }

            settings = endpoint.ToSettings();
        }

        if (current is not null)
        {
            if (_owner.Remove(current) is { } cannot)
            {
                Fail(cannot);
                return;
            }
        }

        if (choice.IsNone)
        {
            return; // the service reported the removal; the page follows it
        }

        var id = configuration?.Id ?? FreshId();
        var wanted = choice.ToConfiguration(id, Type, settings);
        var added = service.Add(configuration is null ? wanted with { Name = _owner.UniqueName(Type, wanted.Name) } : wanted);
        if (!added.Succeeded)
        {
            if (configuration is not null)
            {
                service.Add(configuration); // what was there stays
            }

            Fail(added.Problem ?? "The device could not be set up.");
        }
    }

    /// <summary>The host of PHD2 as typed; for a PHD2 slot.</summary>
    [ObservableProperty]
    public partial string Phd2Host { get; set; } = Sidera.Phd2.Phd2Endpoint.DefaultHost;

    /// <summary>The port of PHD2 as typed: 4400, or 4401 and so on for a second instance of PHD2.</summary>
    [ObservableProperty]
    public partial string Phd2PortText { get; set; } = Sidera.Phd2.Phd2Endpoint.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool IsPhd2Selected => SelectedChoice is { IsPhd2: true };

    private bool _phd2Edited;
    private bool _phd2Filling;

    partial void OnPhd2HostChanged(string value) => Phd2Changed();

    partial void OnPhd2PortTextChanged(string value) => Phd2Changed();

    private void Phd2Changed()
    {
        _phd2Edited |= !_phd2Filling;
        SavePhd2Command.NotifyCanExecuteChanged();
    }

    private static Sidera.Phd2.Phd2Endpoint EndpointOf(DeviceConfiguration configuration)
    {
        try
        {
            return Sidera.Phd2.Phd2Endpoint.FromSettings(configuration.Settings);
        }
        catch (FormatException)
        {
            return Sidera.Phd2.Phd2Endpoint.Default;
        }
    }

    private bool TryEndpoint(out Sidera.Phd2.Phd2Endpoint endpoint, out string problem)
    {
        endpoint = Sidera.Phd2.Phd2Endpoint.Default;
        if (!int.TryParse(Phd2PortText?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            problem = "The port of PHD2 must be a number from 1 to 65535.";
            return false;
        }

        var host = Phd2Host?.Trim() ?? string.Empty;
        if (Sidera.Phd2.Phd2Endpoint.Problem(host, port) is { } invalid)
        {
            problem = invalid;
            return false;
        }

        endpoint = new Sidera.Phd2.Phd2Endpoint(host, port);
        problem = string.Empty;
        return true;
    }

    /// <summary>Keeps the host and the port of the PHD2 guider; the guider has to be disconnected.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePhd2))]
    private void SavePhd2()
    {
        ProblemText = string.Empty;
        if (_management is null || Device is null || _management.Service.ConfigurationOf(Device.DeviceIdText) is not { Backend: DeviceBackend.Phd2 } configuration)
        {
            return;
        }

        if (!TryEndpoint(out var endpoint, out var problem))
        {
            ProblemText = problem;
            return;
        }

        var result = _management.Service.Update(configuration with { Settings = endpoint.ToSettings() });
        if (!result.Succeeded)
        {
            ProblemText = result.Problem ?? "The settings could not be saved.";
            return;
        }

        _phd2Edited = false;
    }

    private bool CanSavePhd2() => _management is not null && !IsDeviceInUse && Device is not null && SelectedChoice is { IsPhd2: true };

    private void Fail(string problem)
    {
        ProblemText = problem;
        Refresh(); // the choice goes back to what the equipment has
        // The combo box is still in the middle of its own selection change: it takes the revert once that is over.
        Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    private string FreshId()
    {
        var baseId = Type switch
        {
            DeviceType.FilterWheel => "filterwheel.main",
            _ => Type.ToString().ToLowerInvariant() + ".main",
        };
        var id = baseId;
        for (var n = 2; _owner.IdInUse(id); n++)
        {
            id = $"{baseId}-{n}";
        }

        return id;
    }
}

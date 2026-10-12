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

    public bool IsAlpaca => Backend == DeviceBackend.Alpaca;

    public static DriverChoice None(string text) => new(string.Empty, text, null, null, null);

    public static DriverChoice Simulator => new("Sidera", "Simulator", DeviceBackend.Simulator, null, null);

    public static DriverChoice Phd2 => new("PHD2", "PHD2", DeviceBackend.Phd2, null, null);

    public static DriverChoice Alpaca => new("Alpaca", "ASCOM Alpaca (network)", DeviceBackend.Alpaca, null, null);

    public static DriverChoice Ascom(string progId, string name) => new("ASCOM", name, DeviceBackend.Ascom, progId, name);

    public bool Matches(DeviceConfiguration configuration) =>
        Backend == configuration.Backend
        && (Backend != DeviceBackend.Ascom || string.Equals(ProgId, configuration.ProgId, StringComparison.OrdinalIgnoreCase));

    public DeviceConfiguration ToConfiguration(string id, DeviceType type, IReadOnlyDictionary<string, string>? settings = null) => Backend switch
    {
        DeviceBackend.Ascom => DeviceConfiguration.Ascom(id, Text, type, ProgId!, DriverName),
        DeviceBackend.Phd2 => new DeviceConfiguration(id, Text, type, DeviceBackend.Phd2, settings ?? Sidera.Phd2.Phd2Endpoint.Default.ToSettings()),
        DeviceBackend.Alpaca => new DeviceConfiguration(id, Text, type, DeviceBackend.Alpaca, settings ?? throw new ArgumentException("An Alpaca device needs the address of its server.", nameof(settings))),
        _ => DeviceConfiguration.Simulator(id, Text, type),
    };
}

/// <summary>A dome that an Alpaca server says it has: what the list of the search shows, and what choosing it fills in.</summary>
public sealed record AlpacaFoundDome(string Name, string Host, int Port, int Number, string? ServerName)
{
    public string Text => ServerName is { Length: > 0 } ? $"{Name}  ·  {Host}:{Port}/{Number}  ·  {ServerName}" : $"{Name}  ·  {Host}:{Port}/{Number}";
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
        OnPropertyChanged(nameof(IsAlpacaSelected));
        SavePhd2Command.NotifyCanExecuteChanged();
        UseAlpacaCommand.NotifyCanExecuteChanged();
        SearchHostCommand.NotifyCanExecuteChanged();
        SearchNetworkCommand.NotifyCanExecuteChanged();
        if (_syncing || value is null)
        {
            return;
        }

        if (value.IsAlpaca)
        {
            // There is no address to guess: choosing Alpaca shows where to enter or search for it, and the device is made by "Use this dome".
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
        UseAlpacaCommand.NotifyCanExecuteChanged();
        SearchHostCommand.NotifyCanExecuteChanged();
        SearchNetworkCommand.NotifyCanExecuteChanged();
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

            if (Type == DeviceType.Dome)
            {
                Choices.Add(DriverChoice.Alpaca);
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
        if (configuration is { Backend: DeviceBackend.Alpaca } && !_alpacaEdited && AlpacaEndpointOf(configuration) is { } alpacaEndpoint)
        {
            _alpacaFilling = true;
            AlpacaHost = alpacaEndpoint.Host;
            AlpacaPortText = alpacaEndpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AlpacaNumberText = alpacaEndpoint.DeviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            AlpacaName = configuration.Name;
            _alpacaFilling = false;
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

    // ---- ASCOM Alpaca: a dome on the network, at an address that is entered or found

    /// <summary>The host of the Alpaca server as typed: a name or an address.</summary>
    [ObservableProperty]
    public partial string AlpacaHost { get; set; } = string.Empty;

    /// <summary>The port of the Alpaca server as typed (11111 is the usual one of ASCOM Remote; simulators often use 32323).</summary>
    [ObservableProperty]
    public partial string AlpacaPortText { get; set; } = "11111";

    /// <summary>The number of the device on that server: 0 for the first dome.</summary>
    [ObservableProperty]
    public partial string AlpacaNumberText { get; set; } = "0";

    /// <summary>The name the dome is given in Sidera; what the search found, or what was typed.</summary>
    [ObservableProperty]
    public partial string AlpacaName { get; set; } = string.Empty;

    /// <summary>What the search said: that it is asking, how many domes it found, or why it found none.</summary>
    [ObservableProperty]
    public partial string AlpacaSearchText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAlpacaSearching { get; private set; }

    /// <summary>The domes that answered the last search; choosing one fills in the address.</summary>
    public ObservableCollection<AlpacaFoundDome> AlpacaFound { get; } = [];

    [ObservableProperty]
    public partial AlpacaFoundDome? SelectedAlpacaFound { get; set; }

    public bool IsAlpacaSelected => SelectedChoice is { IsAlpaca: true };

    public bool HasAlpacaFound => AlpacaFound.Count > 0;

    private bool _alpacaEdited;
    private bool _alpacaFilling;
    private int _alpacaVersion;

    partial void OnAlpacaHostChanged(string value) => AlpacaChanged();

    partial void OnAlpacaPortTextChanged(string value) => AlpacaChanged();

    partial void OnAlpacaNumberTextChanged(string value) => AlpacaChanged();

    partial void OnIsAlpacaSearchingChanged(bool value)
    {
        SearchHostCommand.NotifyCanExecuteChanged();
        SearchNetworkCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAlpacaFoundChanged(AlpacaFoundDome? value)
    {
        if (value is null)
        {
            return;
        }

        _alpacaFilling = true;
        AlpacaHost = value.Host;
        AlpacaPortText = value.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AlpacaNumberText = value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AlpacaName = value.Name;
        _alpacaFilling = false;
        _alpacaEdited = true;
        UseAlpacaCommand.NotifyCanExecuteChanged();
    }

    private void AlpacaChanged()
    {
        _alpacaEdited |= !_alpacaFilling;
        UseAlpacaCommand.NotifyCanExecuteChanged();
    }

    private static Sidera.Alpaca.AlpacaEndpoint? AlpacaEndpointOf(DeviceConfiguration configuration)
    {
        try
        {
            return Sidera.Alpaca.AlpacaEndpoint.FromSettings(configuration.Settings);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private bool TryAlpacaEndpoint(out Sidera.Alpaca.AlpacaEndpoint endpoint, out string problem)
    {
        endpoint = null!;
        var host = AlpacaHost?.Trim() ?? string.Empty;
        if (!int.TryParse(AlpacaPortText?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            problem = "The port must be a number from 1 to 65535.";
            return false;
        }

        if (host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('['))
        {
            host = "[" + host + "]"; // an IPv6 address
        }

        var number = string.IsNullOrWhiteSpace(AlpacaNumberText) ? "0" : AlpacaNumberText.Trim();
        if (!Sidera.Alpaca.AlpacaEndpoint.TryParse($"{host}:{AlpacaPortText!.Trim()}/{number}", out var parsed, out var invalid))
        {
            problem = invalid ?? "The address of the Alpaca server is not valid.";
            return false;
        }

        endpoint = parsed!;
        problem = string.Empty;
        return true;
    }

    /// <summary>Makes the dome of this slot the one at the address: a new dome, or the address of the Alpaca dome that is there (which must be disconnected). Connects nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanUseAlpaca))]
    private void UseAlpaca()
    {
        ProblemText = string.Empty;
        if (_management is null)
        {
            return;
        }

        if (!TryAlpacaEndpoint(out var endpoint, out var problem))
        {
            ProblemText = problem;
            return;
        }

        var service = _management.Service;
        var current = Device;
        var configuration = current is null ? null : service.ConfigurationOf(current.DeviceIdText);
        var name = string.IsNullOrWhiteSpace(AlpacaName) ? "Alpaca dome" : AlpacaName.Trim();
        if (configuration is { Backend: DeviceBackend.Alpaca })
        {
            var updated = service.Update(configuration with { Settings = endpoint.ToSettings(), Name = name });
            if (!updated.Succeeded)
            {
                ProblemText = updated.Problem ?? "The address could not be saved.";
                return;
            }

            _alpacaEdited = false;
            return;
        }

        // A dome of another driver is replaced, with the same id; if the new one cannot be made, the old one stays.
        if (current is not null && _owner.Remove(current) is { } cannot)
        {
            Fail(cannot);
            return;
        }

        var id = configuration?.Id ?? FreshId();
        var wanted = new DeviceConfiguration(id, name, Type, DeviceBackend.Alpaca, endpoint.ToSettings());
        var added = service.Add(configuration is null ? wanted with { Name = _owner.UniqueName(Type, name) } : wanted);
        if (!added.Succeeded)
        {
            if (configuration is not null)
            {
                service.Add(configuration);
            }

            Fail(added.Problem ?? "The dome could not be set up.");
            return;
        }

        _alpacaEdited = false;
    }

    private bool CanUseAlpaca() => _management is not null && !IsDeviceInUse && SelectedChoice is { IsAlpaca: true };

    /// <summary>Asks the host that is typed whether an Alpaca server is there (one message to that host only) and lists its domes.</summary>
    [RelayCommand(CanExecute = nameof(CanSearchAlpaca))]
    private Task SearchHostAsync() => SearchAlpacaAsync(broadcast: false);

    /// <summary>
    /// Asks every device of the local network (one broadcast message) and lists the domes of the Alpaca servers that answer. Nothing is connected or moved; it is a question, and a server that
    /// is not asked directly is only asked this way.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSearchAlpaca))]
    private Task SearchNetworkAsync() => SearchAlpacaAsync(broadcast: true);

    private bool CanSearchAlpaca() => _management?.Alpaca is not null && !IsAlpacaSearching && SelectedChoice is { IsAlpaca: true };

    private async Task SearchAlpacaAsync(bool broadcast)
    {
        if (_management?.Alpaca is not { } discovery)
        {
            return;
        }

        var host = AlpacaHost?.Trim().Trim('[', ']') ?? string.Empty;
        if (!broadcast && host.Length == 0)
        {
            AlpacaSearchText = "Enter the name or address of the computer or device to ask.";
            return;
        }

        var version = ++_alpacaVersion;
        IsAlpacaSearching = true;
        AlpacaSearchText = broadcast ? "Asking the local network…" : $"Asking {host}…";
        AlpacaFound.Clear();
        OnPropertyChanged(nameof(HasAlpacaFound));
        try
        {
            var request = new Sidera.Alpaca.AlpacaDiscoveryRequest { Hosts = broadcast ? [] : [host], Broadcast = broadcast, Timeout = TimeSpan.FromSeconds(broadcast ? 3 : 2) };
            var result = await discovery.DiscoverAsync(request);
            if (!broadcast && result.Servers.Count == 0 && int.TryParse(AlpacaPortText?.Trim(), out var port) && port is >= 1 and <= 65535)
            {
                // A server may not answer discovery (ASCOM Remote behind a router, for one) and still be there: ask it directly at the port that is typed.
                result = await discovery.ProbeAsync(host, port);
            }

            if (version != _alpacaVersion)
            {
                return;
            }

            foreach (var server in result.Servers)
            {
                foreach (var dome in server.Domes)
                {
                    AlpacaFound.Add(new AlpacaFoundDome(dome.Name, server.Host, server.Port, dome.DeviceNumber, server.ServerName));
                }
            }

            OnPropertyChanged(nameof(HasAlpacaFound));
            AlpacaSearchText = AlpacaFound.Count > 0
                ? AlpacaFound.Count == 1 ? "Found 1 dome. Choose it to fill in the address." : $"Found {AlpacaFound.Count} domes. Choose one to fill in the address."
                : result.Servers.Count > 0 ? "An Alpaca server answered, but it has no dome."
                : result.Problem ?? (broadcast ? "No Alpaca server answered on the local network." : $"No Alpaca server answered at {host}.");
        }
        finally
        {
            if (version == _alpacaVersion)
            {
                IsAlpacaSearching = false;
            }
        }
    }

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

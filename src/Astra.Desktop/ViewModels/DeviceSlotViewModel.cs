using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Astra.Ascom.Discovery;
using Astra.Core.Devices;
using Astra.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>One entry of the driver list of a device slot: no device, a simulator, or an installed ASCOM driver.</summary>
public sealed record DriverChoice(string Group, string Text, DeviceBackend? Backend, string? ProgId, string? DriverName)
{
    /// <summary>"No camera": the slot has no device.</summary>
    public bool IsNone => Backend is null;

    public bool IsAscom => Backend == DeviceBackend.Ascom;

    public static DriverChoice None(string text) => new(string.Empty, text, null, null, null);

    public static DriverChoice Simulator => new("Astra", "Simulator", DeviceBackend.Simulator, null, null);

    public static DriverChoice Ascom(string progId, string name) => new("ASCOM", name, DeviceBackend.Ascom, progId, name);

    public bool Matches(DeviceConfiguration configuration) =>
        Backend == configuration.Backend
        && (Backend != DeviceBackend.Ascom || string.Equals(ProgId, configuration.ProgId, StringComparison.OrdinalIgnoreCase));

    public DeviceConfiguration ToConfiguration(string id, DeviceType type) => Backend switch
    {
        DeviceBackend.Ascom => DeviceConfiguration.Ascom(id, Text, type, ProgId!, DriverName),
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

    public bool CanChoose => _management is not null;

    public bool IsEmpty => Device is null;

    public string EmptyText => $"No {Title.ToLowerInvariant()} chosen. Pick a driver above to use one.";

    partial void OnSelectedChoiceChanged(DriverChoice? value)
    {
        if (_syncing || value is null)
        {
            return;
        }

        ApplyChoice(value);
    }

    partial void OnDeviceChanged(DeviceViewModelBase? value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        SetupCommand.NotifyCanExecuteChanged();
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
        }

        var configuration = Device is null ? null : _management?.Service.ConfigurationOf(Device.DeviceIdText);
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

    private bool CanRescan() => _management is not null && AscomKind is not null && !IsScanning;

    partial void OnIsScanningChanged(bool value) => RescanCommand.NotifyCanExecuteChanged();

    /// <summary>Opens the setup dialog of the chosen ASCOM driver.</summary>
    [RelayCommand(CanExecute = nameof(CanSetup))]
    private async Task SetupAsync()
    {
        if (_management is null || AscomKind is not { } kind || SelectedChoice is not { IsAscom: true, ProgId: { } progId })
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

    public bool CanSetup() => _management is not null && SelectedChoice is { IsAscom: true };

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
        var added = service.Add(choice.ToConfiguration(id, Type));
        if (!added.Succeeded)
        {
            if (configuration is not null)
            {
                service.Add(configuration); // what was there stays
            }

            Fail(added.Problem ?? "The device could not be set up.");
        }
    }

    private void Fail(string problem)
    {
        ProblemText = problem;
        Refresh(); // the choice goes back to what the equipment has
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

using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>What the equipment page can do with the configuration of a device. Implemented by the page.</summary>
public interface IDeviceManager
{
    /// <summary>Why the device cannot be edited now, or <c>null</c> when it can.</summary>
    string? WhyCannotEdit(DeviceViewModelBase device);

    /// <summary>Why the device cannot be removed now, or <c>null</c> when it can.</summary>
    string? WhyCannotRemove(DeviceViewModelBase device);

    void BeginEdit(DeviceViewModelBase device);

    /// <summary>Removes the device. A problem is reported in the sentence that is returned; <c>null</c> means it is gone.</summary>
    string? Remove(DeviceViewModelBase device);

    /// <summary>Opens the setup dialog of the driver of an ASCOM device; a problem is returned as a sentence.</summary>
    Task<string?> SetupDriverAsync(DeviceViewModelBase device);
}

/// <summary>
/// The configuration of one device as the Settings tab shows it: what drives it, and the three things that can be done
/// with it: edit it, set its driver up, remove it. A device that is connected cannot be changed, and a device that a rig or
/// the sequence uses cannot be removed; the panel says so instead of hiding the buttons.
/// </summary>
public sealed partial class DeviceConfigurationViewModel : ViewModelBase
{
    private readonly DeviceViewModelBase _device;
    private readonly IDeviceManager _manager;

    public DeviceConfigurationViewModel(DeviceViewModelBase device, IDeviceManager manager)
    {
        _device = device;
        _manager = manager;
        device.Refreshed += (_, _) => Refresh();
        Refresh();
    }

    public string BackendText => _device.BackendText;

    /// <summary>The device is an ASCOM device: it has a driver that can be chosen and set up.</summary>
    public bool IsAscom => string.Equals(_device.BackendText, "ASCOM", StringComparison.Ordinal);

    /// <summary>The ProgId of the driver, or "Built in" for a simulator.</summary>
    public string DriverText => _device.DriverIdText ?? "Built in";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(SetupDriverCommand))]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial bool CanRemove { get; private set; }

    /// <summary>Why the configuration cannot be changed now ("Disconnect the device to change it."), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditBlock))]
    public partial string EditBlockText { get; private set; } = string.Empty;

    public bool HasEditBlock => EditBlockText.Length > 0;

    /// <summary>Why the device cannot be removed although it can be edited ("It is part of the rig ..."), or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemoveBlock))]
    public partial string RemoveBlockText { get; private set; } = string.Empty;

    public bool HasRemoveBlock => RemoveBlockText.Length > 0 && CanEdit;

    /// <summary>The outcome of the last setup or removal, when it was a problem.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string MessageText { get; private set; } = string.Empty;

    public bool HasMessage => MessageText.Length > 0;

    /// <summary>The driver's setup dialog is open.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetupDriverCommand))]
    public partial bool IsSettingUp { get; private set; }

    /// <summary>Remove was asked for once and waits for the second click.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingRemove { get; private set; }

    private void Refresh()
    {
        var edit = _manager.WhyCannotEdit(_device);
        var remove = _manager.WhyCannotRemove(_device);
        CanEdit = edit is null;
        CanRemove = remove is null;
        EditBlockText = edit ?? string.Empty;
        RemoveBlockText = edit is null ? remove ?? string.Empty : string.Empty;
        if (!CanRemove)
        {
            IsConfirmingRemove = false;
        }

        OnPropertyChanged(nameof(HasRemoveBlock));
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void Edit()
    {
        MessageText = string.Empty;
        _manager.BeginEdit(_device);
    }

    [RelayCommand(CanExecute = nameof(CanEditAndIdle))]
    private async Task SetupDriverAsync()
    {
        MessageText = string.Empty;
        IsSettingUp = true;
        try
        {
            MessageText = await _manager.SetupDriverAsync(_device) ?? string.Empty;
        }
        finally
        {
            IsSettingUp = false;
        }
    }

    private bool CanEditAndIdle() => CanEdit && IsAscom && !IsSettingUp;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        MessageText = string.Empty;
        IsConfirmingRemove = true;
    }

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void ConfirmRemove()
    {
        IsConfirmingRemove = false;
        if (_manager.Remove(_device) is { } problem)
        {
            MessageText = problem;
        }
    }
}

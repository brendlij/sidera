using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Astra.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The extra controls and readouts of a focuser: moving by a number of steps (the only move of a relative focuser), the
/// temperature, temperature compensation and the step size, with the limits of the driver. A relative focuser is shown as
/// one, with no position and no target. Temperature compensation is kept as a preference and applied after the next connect.
/// </summary>
public sealed partial class FocuserControlViewModel : DevicePanelViewModel
{
    private readonly IFocuserControl _focuser;

    public FocuserControlViewModel(
        FocuserViewModel device, IFocuserControl focuser, IDevicePreferenceStore? preferences = null, TimeSpan? pollInterval = null)
        : base(device, focuser, preferences, pollInterval)
    {
        _focuser = focuser;
        focuser.CapabilitiesChanged += OnDeviceChanged;
        focuser.StateChanged += OnDeviceChanged;
        Update();
    }

    public override IReadOnlyList<InfoLine> DriverInfo => InfoLines;

    partial void OnInfoLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(DriverInfo));

    protected override bool HasCapabilities => _focuser.Capabilities.IsAvailable;

    private void OnDeviceChanged(object? sender, EventArgs e) => DeviceChanged();

    /// <summary>The focuser moves to positions; false for a relative focuser.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRelative))]
    public partial bool IsAbsolute { get; private set; } = true;

    public bool IsRelative => IsAvailable && !IsAbsolute;

    [ObservableProperty]
    public partial bool ShowTemperature { get; private set; }

    [ObservableProperty]
    public partial bool ShowTempComp { get; private set; }

    [ObservableProperty]
    public partial string TemperatureText { get; private set; } = "Not reported";

    [ObservableProperty]
    public partial bool TempComp { get; set; }

    [ObservableProperty]
    public partial string StepsText { get; set; } = "100";

    [ObservableProperty]
    public partial string StepsHint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> InfoLines { get; private set; } = [];

    protected override void Rebuild()
    {
        var c = _focuser.Capabilities.Value;
        if (c is null)
        {
            ShowTemperature = ShowTempComp = false;
            IsAbsolute = true;
            InfoLines = [];
            TemperatureText = "Not reported";
            OnPropertyChanged(nameof(IsRelative));
            return;
        }

        IsAbsolute = c.Absolute;
        OnPropertyChanged(nameof(IsRelative));
        ShowTemperature = c.HasTemperature;
        ShowTempComp = c.TempCompAvailable;
        var t = _focuser.Telemetry;
        TemperatureText = t?.Temperature is { } temperature ? $"{temperature.ToString("0.0#", CultureInfo.InvariantCulture)} °C" : "Not reported";
        if (!_editing)
        {
            TempComp = t?.TempComp ?? false;
        }

        StepsHint = c.MaxIncrement is { } max ? $"At most {max} steps per move" : string.Empty;
        var lines = new List<InfoLine> { new("Focuser type", c.Absolute ? "Absolute" : "Relative (no position)") };
        if (c.MaxStep is { } maxStep)
        {
            lines.Add(new("Maximum position", maxStep.ToString(CultureInfo.InvariantCulture)));
        }

        if (c.MaxIncrement is { } increment)
        {
            lines.Add(new("Maximum move", $"{increment} steps"));
        }

        if (c.StepSizeMicrons is { } step)
        {
            lines.Add(new("Step size", $"{step.ToString("0.###", CultureInfo.InvariantCulture)} µm"));
        }

        lines.Add(new("Halt", c.CanHalt switch { true => "Confirmed", false => "Not supported", _ => "Not tried yet" }));
        var driver = c.Driver;
        if (driver.Name is { } name)
        {
            lines.Add(new("Driver", name));
        }

        if (driver.DriverVersion is { } version)
        {
            lines.Add(new("Driver version", version));
        }

        if (driver.InterfaceVersion is { } iface)
        {
            lines.Add(new("Interface version", iface.ToString(CultureInfo.InvariantCulture)));
        }

        if (driver.DriverInfo is { } info)
        {
            lines.Add(new("Driver info", info));
        }

        foreach (var note in c.Notes)
        {
            lines.Add(new("Note", note));
        }

        InfoLines = lines;
    }

    private bool _editing;

    protected override void OnCommandsChanged()
    {
        MoveByCommand.NotifyCanExecuteChanged();
        ApplyTempCompCommand.NotifyCanExecuteChanged();
    }

    partial void OnTempCompChanged(bool value) => _editing = true;

    /// <summary>Moves by the steps typed, in the direction given: "+" outwards, "-" inwards.</summary>
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task MoveByAsync(string? direction) => OperateAsync(() =>
    {
        if (!TryWhole(StepsText, out var steps) || steps <= 0)
        {
            throw new FormatException("The number of steps must be a positive whole number.");
        }

        return _focuser.MoveByAsync(direction == "-" ? -steps : steps);
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyTempCompAsync() => OperateAsync(async () =>
    {
        _editing = false;
        await _focuser.SetTempCompAsync(TempComp);
        SavePreferences(existing => DevicePreferences.WithTempComp(TempComp, existing));
    });

    protected override async Task ApplyPreferencesAsync()
    {
        if (Preferences is null || _focuser.Capabilities.Value is not { } c)
        {
            return;
        }

        if (DevicePreferences.TempCompOf(Preferences.GetPreferences(Device.DeviceIdText)) is { } wanted
            && _focuser.Telemetry?.TempComp != wanted)
        {
            if (c.TempCompAvailable)
            {
                await _focuser.SetTempCompAsync(wanted);
            }
            else
            {
                NoticeText = "Saved preferences kept but not applied, the focuser does not offer them now: temperature compensation.";
            }
        }
    }
}

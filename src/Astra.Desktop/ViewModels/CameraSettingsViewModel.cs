using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The settings of a camera as the Equipment page shows them: only what the connected camera says it supports (gain and
/// offset as a range or as named choices, binning, subframe, readout mode, fast readout, cooling), its sensor and driver
/// information, and the live temperatures. The same panel serves every backend; it knows capabilities, never drivers.
/// Applying sends only what differs from the settings the camera reported, and the choice is kept as a preference of the
/// device (never the cooler) that is applied again after the next connect.
/// </summary>
public sealed partial class CameraSettingsViewModel : DevicePanelViewModel
{
    private readonly ICameraControl _camera;
    private CameraCapabilities? _capabilities;

    public CameraSettingsViewModel(
        CameraViewModel device, ICameraControl camera, IDevicePreferenceStore? preferences = null, TimeSpan? pollInterval = null)
        : base(device, camera, preferences, pollInterval)
    {
        _camera = camera;
        Gain = new IntegerControlInput("Gain");
        Offset = new IntegerControlInput("Offset");
        Gain.PropertyChanged += (_, e) => MarkEditOf(e.PropertyName);
        Offset.PropertyChanged += (_, e) => MarkEditOf(e.PropertyName);
        camera.CapabilitiesChanged += OnDeviceChanged;
        camera.StateChanged += OnDeviceChanged;
        Update();
    }

    public IntegerControlInput Gain { get; }
    public IntegerControlInput Offset { get; }

    public override IReadOnlyList<InfoLine> DriverInfo => InfoLines;

    partial void OnInfoLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(DriverInfo));

    protected override bool HasCapabilities => _camera.Capabilities.IsAvailable;

    private void OnDeviceChanged(object? sender, EventArgs e) => DeviceChanged();

    // ---- What is offered

    [ObservableProperty]
    public partial bool ShowBinning { get; private set; }

    [ObservableProperty]
    public partial bool ShowAsymmetricBinning { get; private set; }

    [ObservableProperty]
    public partial bool ShowSubframe { get; private set; }

    [ObservableProperty]
    public partial bool ShowReadout { get; private set; }

    [ObservableProperty]
    public partial bool ShowFastReadout { get; private set; }

    [ObservableProperty]
    public partial bool ShowCooling { get; private set; }

    [ObservableProperty]
    public partial bool ShowTargetTemperature { get; private set; }

    [ObservableProperty]
    public partial bool ShowCooler { get; private set; }

    [ObservableProperty]
    public partial bool ShowCcdTemperature { get; private set; }

    [ObservableProperty]
    public partial bool ShowCoolerPower { get; private set; }

    [ObservableProperty]
    public partial bool ShowHeatSink { get; private set; }

    /// <summary>The camera offers at least one setting that can be changed here.</summary>
    [ObservableProperty]
    public partial bool HasSettings { get; private set; }

    public bool HasNoSettings => IsAvailable && !HasSettings;

    // ---- What is entered

    [ObservableProperty]
    public partial IReadOnlyList<int> BinChoices { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedBinX { get; set; } = 1;

    [ObservableProperty]
    public partial int SelectedBinY { get; set; } = 1;

    [ObservableProperty]
    public partial IReadOnlyList<string> ReadoutChoices { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectedReadout { get; set; } = -1;

    [ObservableProperty]
    public partial bool FastReadout { get; set; }

    [ObservableProperty]
    public partial string StartXText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StartYText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WidthText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeightText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TargetTemperatureText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CoolerOn { get; set; }

    // ---- What the camera reports now

    [ObservableProperty]
    public partial string CcdTemperatureText { get; private set; } = "Not reported";

    [ObservableProperty]
    public partial string CoolerPowerText { get; private set; } = "Not reported";

    [ObservableProperty]
    public partial string HeatSinkText { get; private set; } = "Not reported";

    /// <summary>The camera, its sensor and its driver in words, for the driver information.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> InfoLines { get; private set; } = [];

    public string SubframeHint => _capabilities is null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture,
            $"Inside {_capabilities.SensorWidth / Math.Max(1, SelectedBinX)} x {_capabilities.SensorHeight / Math.Max(1, SelectedBinY)} pixels at this binning");

    partial void OnSelectedBinXChanged(int value)
    {
        if (_capabilities is { CanAsymmetricBin: false } && SelectedBinY != value)
        {
            SelectedBinY = value;
        }

        OnPropertyChanged(nameof(SubframeHint));
        MarkEdit();
    }

    partial void OnSelectedReadoutChanged(int value) => MarkEdit();

    partial void OnFastReadoutChanged(bool value) => MarkEdit();

    partial void OnStartXTextChanged(string value) => MarkEdit();

    partial void OnStartYTextChanged(string value) => MarkEdit();

    partial void OnWidthTextChanged(string value) => MarkEdit();

    partial void OnHeightTextChanged(string value) => MarkEdit();

    partial void OnTargetTemperatureTextChanged(string value) => MarkEdit();

    partial void OnCoolerOnChanged(bool value) => MarkEdit();

    private bool _loading;

    // Only what the user can type or pick is an edit; the input announcing its capabilities is not.
    private void MarkEditOf(string? property)
    {
        if (property is nameof(IntegerControlInput.Text) or nameof(IntegerControlInput.SelectedIndex))
        {
            MarkEdit();
        }
    }

    private void MarkEdit()
    {
        if (!_loading)
        {
            _editing = true;
        }
    }

    partial void OnSelectedBinYChanged(int value)
    {
        if (_capabilities is { CanAsymmetricBin: false } && SelectedBinX != value)
        {
            SelectedBinX = value;
        }

        OnPropertyChanged(nameof(SubframeHint));
        MarkEdit();
    }

    protected override void Rebuild()
    {
        var c = _camera.Capabilities.Value;
        _capabilities = c;
        var s = _camera.Settings;
        if (c is null)
        {
            ShowBinning = ShowAsymmetricBinning = ShowSubframe = ShowReadout = ShowFastReadout = false;
            ShowCooling = ShowTargetTemperature = ShowCooler = ShowCcdTemperature = ShowCoolerPower = ShowHeatSink = false;
            HasSettings = false;
            Gain.Load(null, null);
            Offset.Load(null, null);
            InfoLines = [];
            CcdTemperatureText = CoolerPowerText = HeatSinkText = "Not reported";
            OnPropertyChanged(nameof(HasNoSettings));
            return;
        }

        ShowBinning = c.SupportsBinning;
        ShowAsymmetricBinning = c.SupportsBinning && c.CanAsymmetricBin;
        ShowSubframe = c.SupportsSubframe;
        ShowReadout = c.ReadoutModes.Count > 0;
        ShowFastReadout = c.CanFastReadout;
        ShowCooling = c.SupportsCooling;
        ShowTargetTemperature = c.CanSetCcdTemperature;
        ShowCooler = c.HasCooler;
        ShowCcdTemperature = c.HasCcdTemperature;
        ShowCoolerPower = c.CanGetCoolerPower;
        ShowHeatSink = c.HasHeatSinkTemperature;
        HasSettings = c.Gain is not null || c.Offset is not null || ShowBinning || ShowSubframe || ShowReadout || ShowFastReadout || ShowCooling;
        OnPropertyChanged(nameof(HasNoSettings));

        // The inputs are only reloaded while nothing is being edited: a refresh must not overwrite what the user is typing.
        if (!_editing)
        {
            _loading = true;
            Gain.Load(c.Gain, s?.Gain);
            Offset.Load(c.Offset, s?.Offset);
            BinChoices = Enumerable.Range(1, Math.Max(c.MaxBinX, c.MaxBinY)).ToList();
            SelectedBinX = s?.BinX ?? 1;
            SelectedBinY = s?.BinY ?? 1;

            // A drop-down forgets its selection when its list is replaced: say again which is selected.
            OnPropertyChanged(nameof(SelectedBinX));
            OnPropertyChanged(nameof(SelectedBinY));
            ReadoutChoices = c.ReadoutModes;
            SelectedReadout = s?.ReadoutMode ?? -1;
            FastReadout = s?.FastReadout ?? false;
            StartXText = Text(s?.StartX);
            StartYText = Text(s?.StartY);
            WidthText = Text(s?.NumX);
            HeightText = Text(s?.NumY);
            TargetTemperatureText = s?.TargetTemperature is { } t ? Format(t) : string.Empty;
            CoolerOn = s?.CoolerOn ?? false;
            OnPropertyChanged(nameof(SubframeHint));
            _loading = false;
        }

        var telemetry = _camera.Telemetry;
        CcdTemperatureText = telemetry?.CcdTemperature is { } temperature ? $"{Format(temperature)} °C" : "Not reported";
        CoolerPowerText = telemetry?.CoolerPower is { } power ? $"{Format(power)} %" : "Not reported";
        HeatSinkText = telemetry?.HeatSinkTemperature is { } sink ? $"{Format(sink)} °C" : "Not reported";
        InfoLines = BuildInfo(c);
    }

    private bool _editing;

    /// <summary>The user began editing: refreshes leave the inputs alone until applied or discarded.</summary>
    public void BeginEdit() => _editing = true;

    private static string Text(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static IReadOnlyList<InfoLine> BuildInfo(CameraCapabilities c)
    {
        var lines = new List<InfoLine>
        {
            new("Sensor", string.Create(CultureInfo.InvariantCulture, $"{c.SensorWidth} x {c.SensorHeight} pixels{(string.IsNullOrWhiteSpace(c.SensorName) ? string.Empty : $" · {c.SensorName}")}")),
            new("Sensor type", c.SensorType.ToString()),
        };
        if (c.PixelSizeXMicrons is { } px)
        {
            lines.Add(new("Pixel size", string.Create(CultureInfo.InvariantCulture, $"{px:0.##} x {c.PixelSizeYMicrons ?? px:0.##} µm")));
        }

        lines.Add(new("Maximum ADU", c.MaxAdu.ToString(CultureInfo.InvariantCulture)));
        if (c.BayerOffset is { } bayer)
        {
            lines.Add(new("Bayer offset", string.Create(CultureInfo.InvariantCulture, $"{bayer.X}, {bayer.Y}")));
        }

        if (c.MinExposureSeconds is { } min && c.MaxExposureSeconds is { } max)
        {
            lines.Add(new("Exposure limits", string.Create(CultureInfo.InvariantCulture, $"{min:0.######} to {max:0.###} s")));
        }

        lines.Add(new("Abort exposure", c.CanAbortExposure ? "Supported" : "Not supported"));
        lines.Add(new("Shutter", c.HasShutter ? "Yes" : "No"));
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

        return lines;
    }

    // ---- Commands

    protected override void OnCommandsChanged()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        FullFrameCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyAsync() => OperateAsync(async () =>
    {
        _editing = false;
        var change = ReadChange();
        if (change.IsEmpty)
        {
            return;
        }

        await _camera.ApplyAsync(change);
        SavePreferences(existing => DevicePreferences.From(change, existing));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void FullFrame()
    {
        if (_capabilities is null)
        {
            return;
        }

        StartXText = "0";
        StartYText = "0";
        WidthText = (_capabilities.SensorWidth / Math.Max(1, SelectedBinX)).ToString(CultureInfo.InvariantCulture);
        HeightText = (_capabilities.SensorHeight / Math.Max(1, SelectedBinY)).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Throws away what was typed and shows what the camera has.</summary>
    [RelayCommand]
    private void Discard()
    {
        _editing = false;
        Update();
    }

    // What differs from the settings the camera reported; nothing that is not offered is ever part of it.
    private CameraSettings ReadChange()
    {
        var c = _capabilities ?? throw new InvalidOperationException("The camera is not connected.");
        var current = _camera.Settings ?? new CameraSettings();
        int? D(int? entered, int? now) => entered is { } e && e != now ? e : null;

        var gain = D(Gain.Read(), current.Gain);
        var offset = D(Offset.Read(), current.Offset);
        var binX = c.SupportsBinning ? D(SelectedBinX, current.BinX) : null;
        var binY = c.SupportsBinning ? D(SelectedBinY, current.BinY) : null;
        var readout = c.ReadoutModes.Count > 0 && SelectedReadout >= 0 ? D(SelectedReadout, current.ReadoutMode) : null;
        bool? fast = c.CanFastReadout && current.FastReadout != FastReadout ? FastReadout : null;

        int? startX = null, startY = null, numX = null, numY = null;
        if (c.SupportsSubframe)
        {
            startX = D(WholeOrNull(StartXText, "Start X"), current.StartX);
            startY = D(WholeOrNull(StartYText, "Start Y"), current.StartY);
            numX = D(WholeOrNull(WidthText, "Width"), current.NumX);
            numY = D(WholeOrNull(HeightText, "Height"), current.NumY);
        }

        double? target = null;
        if (c.CanSetCcdTemperature && !string.IsNullOrWhiteSpace(TargetTemperatureText))
        {
            if (!TryNumber(TargetTemperatureText, out var t))
            {
                throw new FormatException("The target temperature must be a number.");
            }

            target = current.TargetTemperature is { } now && Math.Abs(now - t) < 1e-9 ? null : t;
        }

        bool? cooler = c.HasCooler && current.CoolerOn != CoolerOn ? CoolerOn : null;
        return new CameraSettings
        {
            Gain = gain, Offset = offset, BinX = binX, BinY = binY, StartX = startX, StartY = startY, NumX = numX, NumY = numY,
            ReadoutMode = readout, FastReadout = fast, TargetTemperature = target, CoolerOn = cooler,
        };
    }

    private static int? WholeOrNull(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryWhole(text, out var v) ? v : throw new FormatException($"{label} must be a whole number.");
    }

    // The stored preferences, applied to what this camera supports; what it does not support is left alone, and the
    // cooler is never switched on by a preference.
    protected override async Task ApplyPreferencesAsync()
    {
        if (Preferences is null || _camera.Capabilities.Value is not { } capabilities)
        {
            return;
        }

        var stored = DevicePreferences.ToCameraChange(Preferences.GetPreferences(Device.DeviceIdText));
        if (stored.IsEmpty)
        {
            return;
        }

        var current = _camera.Settings ?? new CameraSettings();
        var skipped = new List<string>();

        int? Keep(int? value, int? now, bool offered, string what)
        {
            if (value is null || value == now)
            {
                return null;
            }

            if (!offered)
            {
                skipped.Add(what);
                return null;
            }

            return value;
        }

        var change = new CameraSettings
        {
            Gain = Keep(stored.Gain, current.Gain, capabilities.Gain?.Accepts(stored.Gain ?? -1) == true, "gain"),
            Offset = Keep(stored.Offset, current.Offset, capabilities.Offset?.Accepts(stored.Offset ?? -1) == true, "offset"),
            BinX = Keep(stored.BinX, current.BinX, stored.BinX is { } bx && bx >= 1 && bx <= capabilities.MaxBinX, "binning"),
            BinY = Keep(stored.BinY, current.BinY, stored.BinY is { } by && by >= 1 && by <= capabilities.MaxBinY, "binning"),
            ReadoutMode = Keep(stored.ReadoutMode, current.ReadoutMode, stored.ReadoutMode is { } m && m >= 0 && m < capabilities.ReadoutModes.Count, "readout mode"),
            FastReadout = stored.FastReadout is { } f && capabilities.CanFastReadout && f != current.FastReadout ? f : null,
            TargetTemperature = stored.TargetTemperature is { } t && capabilities.CanSetCcdTemperature ? t : null,
        };

        // Binning must be valid as a pair; when the pair is not, it is left alone and said.
        if ((change.BinX is not null || change.BinY is not null)
            && CameraSettingsRules.Problems(capabilities, current, change) is { Count: > 0 } problems)
        {
            skipped.Add("binning (" + string.Join(" ", problems) + ")");
            change = change with { BinX = null, BinY = null };
        }

        if (!change.IsEmpty)
        {
            try
            {
                await _camera.ApplyAsync(change);
            }
            catch (ArgumentException ex)
            {
                NoticeText = $"The saved preferences do not fit this camera and were not applied: {ex.Message}";
                return;
            }
        }

        if (skipped.Count > 0)
        {
            NoticeText = $"Saved preferences kept but not applied, the camera does not offer them now: {string.Join(", ", skipped.Distinct())}.";
        }
    }
}

/// <summary>A label and a value, for the driver information of a device.</summary>
public sealed record InfoLine(string Label, string Value);

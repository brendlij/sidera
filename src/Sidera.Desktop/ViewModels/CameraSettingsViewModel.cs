using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Sidera.Desktop.ViewModels;

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

    /// <summary>The sections of the camera workspace that the camera has anything for.</summary>
    [ObservableProperty]
    public partial bool ShowCoolingSection { get; private set; }

    [ObservableProperty]
    public partial bool ShowFrameSection { get; private set; }

    [ObservableProperty]
    public partial bool ShowGainSection { get; private set; }

    /// <summary>What the camera says about its sensor: size, type, pixel size, maximum ADU.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> SensorLines { get; private set; } = [];

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
        MarkEdit(immediate: true);
    }

    partial void OnSelectedReadoutChanged(int value) => MarkEdit(immediate: true);

    partial void OnFastReadoutChanged(bool value) => MarkEdit(immediate: true);

    partial void OnStartXTextChanged(string value) => MarkEdit();

    partial void OnStartYTextChanged(string value) => MarkEdit();

    partial void OnWidthTextChanged(string value) => MarkEdit();

    partial void OnHeightTextChanged(string value) => MarkEdit();

    partial void OnTargetTemperatureTextChanged(string value) => MarkEdit();

    partial void OnCoolerOnChanged(bool value) => MarkEdit(immediate: true);

    private bool _loading;

    // Only what the user can type or pick is an edit; the input announcing its capabilities is not.
    private void MarkEditOf(string? property)
    {
        if (property is nameof(IntegerControlInput.Text) or nameof(IntegerControlInput.SelectedIndex))
        {
            MarkEdit(immediate: property == nameof(IntegerControlInput.SelectedIndex));
        }
    }

    // What is typed or picked is applied to the camera by itself: a choice at once, a typed value when the typing has paused. A value that is
    // not (yet) a number is left alone until it is one; a complete value the camera cannot take is reported, as it was with the button.
    private static readonly TimeSpan PickDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(900);
    private CancellationTokenSource? _autoApply;

    private void MarkEdit(bool immediate = false)
    {
        if (_loading)
        {
            return;
        }

        _editing = true;
        _autoApply?.Cancel();
        var cts = _autoApply = new CancellationTokenSource();
        _ = AutoApplyAsync(immediate ? PickDelay : TypingDelay, cts.Token);
    }

    private async Task AutoApplyAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(delay, cancellationToken);
                if (!_editing)
                {
                    return;
                }

                try
                {
                    if (ReadChange().IsEmpty)
                    {
                        _editing = false;
                        return;
                    }
                }
                catch (FormatException)
                {
                    return; // still being typed
                }

                if (ApplyCommand.CanExecute(null))
                {
                    await ApplyCommand.ExecuteAsync(null);
                    return;
                }

                delay = TimeSpan.FromMilliseconds(300); // busy with something else: try again shortly
            }
        }
        catch (OperationCanceledException)
        {
            // Another edit came, or the panel is gone.
        }
    }

    public override void Dispose()
    {
        _autoApply?.Cancel();
        base.Dispose();
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
            ShowCoolingSection = ShowFrameSection = ShowGainSection = false;
            SensorLines = [];
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
        ShowCoolingSection = ShowCooling || ShowCcdTemperature;
        ShowFrameSection = ShowBinning || ShowSubframe;
        ShowGainSection = c.Gain is not null || c.Offset is not null || ShowReadout || ShowFastReadout;

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
        SensorLines = [.. InfoLines.Where(l => l.Label is "Sensor" or "Sensor type" or "Pixel size" or "Maximum ADU")];
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
        if (_capabilities is { } capabilities)
        {
            var defaults = DefaultsOf(change, capabilities);
            SavePreferences(existing =>
            {
                var result = DevicePreferences.WithAcquisition(existing, defaults);
                return change.TargetTemperature is { } target ? DevicePreferences.WithTargetTemperature(result, target) : result;
            });
        }
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

    // The acquisition defaults, applied through the same resolution an exposure uses: checked against what the camera supports now,
    // and applied only when they all fit. A default that does not fit is kept, never changed or deleted, and said. The cooler is never
    // switched on by a preference.
    protected override async Task ApplyPreferencesAsync()
    {
        if (Preferences is null || _camera.Capabilities.Value is not { } capabilities)
        {
            return;
        }

        var stored = Preferences.GetPreferences(Device.DeviceIdText);
        var defaults = DevicePreferences.AcquisitionDefaults(stored);
        var current = _camera.Settings ?? new CameraSettings();
        var change = new CameraSettings();
        if (!defaults.IsDefault)
        {
            var plan = AcquisitionResolver.Resolve(AcquisitionIntent.Default, defaults, null, _camera.Capabilities, current);
            if (plan.Status == AcquisitionStatus.Invalid)
            {
                NoticeText = "The camera defaults do not fit this camera now and were not applied; they are kept as they are. "
                    + string.Join(" ", plan.Problems);
                return;
            }

            change = plan.Change;
        }

        if (DevicePreferences.TargetTemperatureOf(stored) is { } target && capabilities.CanSetCcdTemperature && current.TargetTemperature != target)
        {
            change = change with { TargetTemperature = target };
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
            }
        }
    }

    // What the user changed, as acquisition defaults: gain and offset as a number or, for a list, the name; the region as the whole
    // sensor or a rectangle; the readout mode by name. The settings the camera reports after the change say what the region came to.
    private AcquisitionIntent DefaultsOf(CameraSettings change, CameraCapabilities capabilities)
    {
        var applied = _camera.Settings ?? new CameraSettings();

        AcquisitionLevel? Level(IntegerControl? control, int? value) => value is not { } v
            ? null
            : control is { IsList: true } && v >= 0 && v < control.Choices.Count ? AcquisitionLevel.OfName(control.Choices[v]) : AcquisitionLevel.OfNumber(v);

        AcquisitionRegion? region = null;
        if (change.ChangesSubframe || change.BinX is not null || change.BinY is not null)
        {
            var width = capabilities.SensorWidth / Math.Max(1, applied.BinX ?? 1);
            var height = capabilities.SensorHeight / Math.Max(1, applied.BinY ?? 1);
            region = applied is { StartX: 0, StartY: 0 } && applied.NumX == width && applied.NumY == height
                ? AcquisitionRegion.Full
                : applied.NumX is { } nx && applied.NumY is { } ny && nx > 0 && ny > 0
                    ? AcquisitionRegion.Of(applied.StartX ?? 0, applied.StartY ?? 0, nx, ny)
                    : null;
        }

        return new AcquisitionIntent
        {
            Gain = Level(capabilities.Gain, change.Gain),
            Offset = Level(capabilities.Offset, change.Offset),
            BinX = change.BinX is not null || change.BinY is not null ? applied.BinX : null,
            BinY = change.BinX is not null || change.BinY is not null ? applied.BinY : null,
            Region = region,
            ReadoutMode = change.ReadoutMode is { } mode && mode >= 0 && mode < capabilities.ReadoutModes.Count ? capabilities.ReadoutModes[mode] : null,
            FastReadout = change.FastReadout,
        };
    }
}

/// <summary>A label and a value, for the driver information of a device.</summary>
public sealed record InfoLine(string Label, string Value);

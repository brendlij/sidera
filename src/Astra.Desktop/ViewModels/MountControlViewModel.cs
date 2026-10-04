using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Astra.Core.Devices;
using Astra.Core.Mounts;
using Astra.Desktop.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Astra.Desktop.ViewModels;

/// <summary>
/// The controls and the readouts of a mount as the Equipment page shows them: tracking and its rate, park, unpark, set park
/// and home, sync, alt-az slew, pulse guide, guide rates, refraction, moving an axis, the horizontal position, pier side,
/// sidereal time and site. Each control exists only when the mount says it supports it, the same for every backend. The
/// tracking rate, the guide rates and the refraction setting are kept as preferences and applied after the next connect.
/// </summary>
public sealed partial class MountControlViewModel : DevicePanelViewModel
{
    private readonly IMountControl _mount;
    private MountCapabilities? _capabilities;

    public MountControlViewModel(
        MountViewModel device, IMountControl mount, IDevicePreferenceStore? preferences = null, TimeSpan? pollInterval = null)
        : base(device, mount, preferences, pollInterval)
    {
        _mount = mount;
        mount.CapabilitiesChanged += OnDeviceChanged;
        mount.StateChanged += OnDeviceChanged;
        Update();
    }

    public override IReadOnlyList<InfoLine> DriverInfo => InfoLines;

    partial void OnInfoLinesChanged(IReadOnlyList<InfoLine> value) => OnPropertyChanged(nameof(DriverInfo));

    protected override bool HasCapabilities => _mount.Capabilities.IsAvailable;

    private void OnDeviceChanged(object? sender, EventArgs e) => DeviceChanged();

    [ObservableProperty]
    public partial bool ShowTracking { get; private set; }

    [ObservableProperty]
    public partial bool ShowTrackingRates { get; private set; }

    [ObservableProperty]
    public partial bool ShowPark { get; private set; }

    [ObservableProperty]
    public partial bool ShowUnpark { get; private set; }

    [ObservableProperty]
    public partial bool ShowSetPark { get; private set; }

    [ObservableProperty]
    public partial bool ShowHome { get; private set; }

    [ObservableProperty]
    public partial bool ShowSync { get; private set; }

    [ObservableProperty]
    public partial bool ShowAltAz { get; private set; }

    [ObservableProperty]
    public partial bool ShowPulseGuide { get; private set; }

    [ObservableProperty]
    public partial bool ShowGuideRates { get; private set; }

    [ObservableProperty]
    public partial bool ShowRefraction { get; private set; }

    [ObservableProperty]
    public partial bool ShowMoveAxis { get; private set; }

    [ObservableProperty]
    public partial bool ShowSecondaryAxis { get; private set; }

    [ObservableProperty]
    public partial bool HasControls { get; private set; }

    public bool HasNoControls => IsAvailable && !HasControls;

    [ObservableProperty]
    public partial bool IsTracking { get; private set; }

    [ObservableProperty]
    public partial bool IsParked { get; private set; }

    public string TrackingButtonText => IsTracking ? "Stop tracking" : "Start tracking";

    [ObservableProperty]
    public partial IReadOnlyList<TrackingRate> TrackingRateChoices { get; private set; } = [];

    [ObservableProperty]
    public partial TrackingRate SelectedTrackingRate { get; set; }

    [ObservableProperty]
    public partial string SyncRaText { get; set; } = "0";

    [ObservableProperty]
    public partial string SyncDecText { get; set; } = "0";

    [ObservableProperty]
    public partial string AltitudeText { get; set; } = "45";

    [ObservableProperty]
    public partial string AzimuthText { get; set; } = "180";

    [ObservableProperty]
    public partial string PulseMillisecondsText { get; set; } = "500";

    [ObservableProperty]
    public partial string GuideRateRaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GuideRateDecText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Refraction { get; set; }

    [ObservableProperty]
    public partial string AxisRateText { get; set; } = "0.5";

    [ObservableProperty]
    public partial string AxisRateHint { get; private set; } = string.Empty;

    /// <summary>What the mount reports now, line by line (only what it reports).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> TelemetryLines { get; private set; } = [];

    /// <summary>The mount, its site and its driver, for the driver information.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<InfoLine> InfoLines { get; private set; } = [];

    protected override void Rebuild()
    {
        var c = _mount.Capabilities.Value;
        _capabilities = c;
        if (c is null)
        {
            ShowTracking = ShowTrackingRates = ShowPark = ShowUnpark = ShowSetPark = ShowHome = ShowSync = false;
            ShowAltAz = ShowPulseGuide = ShowGuideRates = ShowRefraction = ShowMoveAxis = ShowSecondaryAxis = HasControls = false;
            TelemetryLines = [];
            InfoLines = [];
            TrackingRateChoices = [];
            OnPropertyChanged(nameof(HasNoControls));
            return;
        }

        ShowTracking = c.CanSetTracking;
        ShowTrackingRates = c.TrackingRates.Count > 1;
        ShowPark = c.CanPark;
        ShowUnpark = c.CanUnpark;
        ShowSetPark = c.CanSetPark;
        ShowHome = c.CanFindHome;
        ShowSync = c.CanSync;
        ShowAltAz = c.CanSlewAltAz || c.CanSlewAltAzAsync;
        ShowPulseGuide = c.CanPulseGuide;
        ShowGuideRates = c.CanSetGuideRates;
        ShowRefraction = c.HasRefractionSetting;
        ShowMoveAxis = c.CanMovePrimaryAxis || c.CanMoveSecondaryAxis;
        ShowSecondaryAxis = c.CanMoveSecondaryAxis;
        HasControls = ShowTracking || ShowTrackingRates || ShowPark || ShowUnpark || ShowSetPark || ShowHome || ShowSync
            || ShowAltAz || ShowPulseGuide || ShowGuideRates || ShowRefraction || ShowMoveAxis;
        OnPropertyChanged(nameof(HasNoControls));
        AxisRateHint = c.AxisRates.Values.SelectMany(r => r).Any()
            ? "Degrees per second, " + string.Join(", ", c.AxisRates.Values.SelectMany(r => r).Select(r => $"{Format(r.Minimum)} to {Format(r.Maximum)}"))
            : string.Empty;

        var t = _mount.Telemetry;
        IsTracking = t?.Tracking ?? false;
        IsParked = t?.AtPark ?? false;
        OnPropertyChanged(nameof(TrackingButtonText));
        if (!_editingRates)
        {
            _loading = true;
            TrackingRateChoices = c.TrackingRates;
            if (t?.Rate is { } rate)
            {
                SelectedTrackingRate = rate;
            }
            OnPropertyChanged(nameof(SelectedTrackingRate));

            if (t?.GuideRates is { } g)
            {
                GuideRateRaText = Format(g.RightAscensionDegreesPerSecond);
                GuideRateDecText = Format(g.DeclinationDegreesPerSecond);
            }

            Refraction = t?.DoesRefraction ?? false;
            _loading = false;
        }

        TelemetryLines = BuildTelemetry(t);
        InfoLines = BuildInfo(c);
    }

    private bool _editingRates;
    private bool _loading;

    partial void OnGuideRateRaTextChanged(string value) => _editingRates |= !_loading;

    partial void OnGuideRateDecTextChanged(string value) => _editingRates |= !_loading;

    partial void OnRefractionChanged(bool value) => _editingRates |= !_loading;

    private static IReadOnlyList<InfoLine> BuildTelemetry(MountTelemetry? t)
    {
        var lines = new List<InfoLine>();
        if (t is null)
        {
            return lines;
        }

        if (t.Horizontal is { } h)
        {
            lines.Add(new("Altitude / azimuth", string.Create(CultureInfo.InvariantCulture, $"{h.AltitudeDegrees:0.##}° / {h.AzimuthDegrees:0.##}°")));
        }

        lines.Add(new("Tracking", t.Tracking ? t.Rate is { } r ? $"On ({r})" : "On" : "Off"));
        lines.Add(new("Parked", t.AtPark ? "Yes" : "No"));
        if (t.AtHome is { } home)
        {
            lines.Add(new("At home", home ? "Yes" : "No"));
        }

        if (t.SideOfPier is { } side && side != PierSide.Unknown)
        {
            lines.Add(new("Side of pier", side.ToString()));
        }

        if (t.SiderealTimeHours is { } lst)
        {
            lines.Add(new("Sidereal time", string.Create(CultureInfo.InvariantCulture, $"{lst:0.####} h")));
        }

        if (t.UtcDate is { } utc)
        {
            lines.Add(new("UTC", utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        if (t.PulseGuiding)
        {
            lines.Add(new("Pulse guiding", "Yes"));
        }

        return lines;
    }

    private IReadOnlyList<InfoLine> BuildInfo(MountCapabilities c)
    {
        var lines = new List<InfoLine>();
        if (_mount.Site is { } site)
        {
            lines.Add(new("Site", string.Create(CultureInfo.InvariantCulture, $"{site.LatitudeDegrees:0.####}° N, {site.LongitudeDegrees:0.####}° E, {site.ElevationMeters:0} m")));
        }

        if (c.Alignment is { } alignment)
        {
            lines.Add(new("Alignment", alignment.ToString()));
        }

        if (c.EquatorialSystem is { } system)
        {
            lines.Add(new("Coordinate system", system.ToString()));
        }

        if (c.ApertureDiameterMeters is { } aperture)
        {
            lines.Add(new("Aperture", string.Create(CultureInfo.InvariantCulture, $"{aperture * 1000:0} mm")));
        }

        if (c.FocalLengthMeters is { } focal)
        {
            lines.Add(new("Focal length", string.Create(CultureInfo.InvariantCulture, $"{focal * 1000:0} mm")));
        }

        lines.Add(new("Slew", c.CanSlewAsync ? "Asynchronous" : c.CanSlew ? "Blocking only" : "Not supported"));
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

    protected override void OnCommandsChanged()
    {
        foreach (var command in new IRelayCommand[]
        {
            ToggleTrackingCommand, ApplyTrackingRateCommand, ParkCommand, UnparkCommand, SetParkCommand, FindHomeCommand, SyncCommand,
            SlewAltAzCommand, PulseCommand, ApplyGuideRatesCommand, ApplyRefractionCommand, MoveAxisCommand, StopAxesCommand,
        })
        {
            command.NotifyCanExecuteChanged();
        }
    }

    partial void OnSelectedTrackingRateChanged(TrackingRate value) => ApplyTrackingRateCommand.NotifyCanExecuteChanged();

    // ---- Commands

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ToggleTrackingAsync() => OperateAsync(() => _mount.SetTrackingAsync(!IsTracking));

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyTrackingRateAsync() => OperateAsync(async () =>
    {
        await _mount.SetTrackingRateAsync(SelectedTrackingRate);
        SavePreferences(existing => DevicePreferences.From(SelectedTrackingRate, null, null, existing));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ParkAsync() => OperateAsync(() => _mount.ParkAsync());

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task UnparkAsync() => OperateAsync(() => _mount.UnparkAsync());

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SetParkAsync() => OperateAsync(() => _mount.SetParkAsync());

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task FindHomeAsync() => OperateAsync(() => _mount.FindHomeAsync());

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SyncAsync() => OperateAsync(() =>
    {
        // The domain type validates the ranges; this only turns the text into numbers.
        if (!TryNumber(SyncRaText, out var ra) || !TryNumber(SyncDecText, out var dec))
        {
            throw new FormatException("Right ascension and declination must be numbers.");
        }

        return _mount.SyncAsync(new CelestialCoordinates(ra, dec));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task SlewAltAzAsync() => OperateAsync(() =>
    {
        if (!TryNumber(AltitudeText, out var altitude) || !TryNumber(AzimuthText, out var azimuth))
        {
            throw new FormatException("Altitude and azimuth must be numbers.");
        }

        return _mount.SlewToAltAzAsync(new HorizontalCoordinates(altitude, azimuth));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task PulseAsync(string? direction) => OperateAsync(() =>
    {
        if (!TryWhole(PulseMillisecondsText, out var milliseconds))
        {
            throw new FormatException("The pulse length must be a whole number of milliseconds.");
        }

        var parsed = Enum.TryParse<GuideDirection>(direction, out var d) ? d : throw new ArgumentException("Unknown direction.");
        return _mount.PulseGuideAsync(parsed, TimeSpan.FromMilliseconds(milliseconds));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyGuideRatesAsync() => OperateAsync(async () =>
    {
        if (!TryNumber(GuideRateRaText, out var ra) || !TryNumber(GuideRateDecText, out var dec))
        {
            throw new FormatException("The guide rates must be numbers.");
        }

        var rates = new GuideRates(ra, dec);
        await _mount.SetGuideRatesAsync(rates);
        _editingRates = false;
        SavePreferences(existing => DevicePreferences.From(null, rates, null, existing));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task ApplyRefractionAsync() => OperateAsync(async () =>
    {
        await _mount.SetRefractionAsync(Refraction);
        _editingRates = false;
        SavePreferences(existing => DevicePreferences.From(null, null, Refraction, existing));
    });

    // axis: "Primary+", "Primary-", "Secondary+", "Secondary-"
    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task MoveAxisAsync(string? which) => OperateAsync(() =>
    {
        if (which is null || which.Length < 2 || !TryNumber(AxisRateText, out var rate))
        {
            throw new FormatException("The rate must be a number of degrees per second.");
        }

        var axis = which.StartsWith("Secondary", StringComparison.Ordinal) ? MountAxis.Secondary : MountAxis.Primary;
        return _mount.MoveAxisAsync(axis, which.EndsWith('-') ? -Math.Abs(rate) : Math.Abs(rate));
    });

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private Task StopAxesAsync() => OperateAsync(async () =>
    {
        var c = _capabilities;
        if (c is null)
        {
            return;
        }

        foreach (var axis in new[] { MountAxis.Primary, MountAxis.Secondary })
        {
            if (c.CanMove(axis))
            {
                await _mount.MoveAxisAsync(axis, 0);
            }
        }
    });

    protected override async Task ApplyPreferencesAsync()
    {
        if (Preferences is null || _mount.Capabilities.Value is not { } c)
        {
            return;
        }

        var stored = Preferences.GetPreferences(Device.DeviceIdText);
        var skipped = new List<string>();
        if (DevicePreferences.TrackingRateOf(stored) is { } rate && _mount.Telemetry?.Rate != rate)
        {
            if (c.TrackingRates.Contains(rate))
            {
                await _mount.SetTrackingRateAsync(rate);
            }
            else
            {
                skipped.Add("tracking rate");
            }
        }

        if (DevicePreferences.GuideRatesOf(stored) is { } guide)
        {
            if (c.CanSetGuideRates)
            {
                await _mount.SetGuideRatesAsync(guide);
            }
            else
            {
                skipped.Add("guide rates");
            }
        }

        if (DevicePreferences.RefractionOf(stored) is { } refraction && _mount.Telemetry?.DoesRefraction != refraction)
        {
            if (c.HasRefractionSetting)
            {
                await _mount.SetRefractionAsync(refraction);
            }
            else
            {
                skipped.Add("refraction");
            }
        }

        if (skipped.Count > 0)
        {
            NoticeText = $"Saved preferences kept but not applied, the mount does not offer them now: {string.Join(", ", skipped)}.";
        }
    }
}

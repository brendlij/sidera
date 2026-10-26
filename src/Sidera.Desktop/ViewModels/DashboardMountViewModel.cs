using System;
using System.Globalization;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;

namespace Sidera.Desktop.ViewModels;

/// <summary>
/// The mount on the dashboard: what it is doing (parked, slewing, tracking and at which rate), where it points (right ascension, declination, altitude, azimuth), the side of the pier, the sidereal time and
/// the site it works with, and when the pointing crosses the meridian: from the mount's own sidereal time, so it needs nothing but the mount. The session says the same for its target, where it has one.
/// It reads the mount and owns nothing of its own; it is read again whenever the mount says it changed and every second.
/// </summary>
public sealed partial class DashboardMountViewModel : ObservableObject, IDisposable
{
    private readonly IMountControl _mount;
    private readonly Action<Action> _postToUi;
    private readonly Func<string>? _sessionMeridian;
    private Timer? _clock;

    public DashboardMountViewModel(IMountControl mount, Action<Action>? postToUi = null, Func<string>? sessionMeridian = null)
    {
        ArgumentNullException.ThrowIfNull(mount);
        _mount = mount;
        _postToUi = postToUi ?? (action => action());
        _sessionMeridian = sessionMeridian;
        mount.StateChanged += OnChanged;
        _clock = new Timer(_ => _postToUi(Refresh), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Refresh();
    }

    public string DeviceIdText => _mount.Id.Value;

    public string Name => _mount.Name;

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    /// <summary>"Tracking · Sidereal", "Slewing", "Parked", "Not tracking", "Not connected".</summary>
    [ObservableProperty]
    public partial string StateText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial StatusKind StateKind { get; private set; }

    /// <summary>"21h 29m 58s  +12° 10′ 03″".</summary>
    [ObservableProperty]
    public partial string PositionText { get; private set; } = string.Empty;

    /// <summary>"Altitude 55.1° · azimuth 179.9°".</summary>
    [ObservableProperty]
    public partial string HorizontalText { get; private set; } = string.Empty;

    /// <summary>"East" or "West": the side of the pier the mount stands on; empty when the driver does not say.</summary>
    [ObservableProperty]
    public partial string PierText { get; private set; } = string.Empty;

    /// <summary>"21h 41m 15s": the sidereal time of the mount, and the time (UTC) it works with, when it says them.</summary>
    [ObservableProperty]
    public partial string TimeText { get; private set; } = string.Empty;

    /// <summary>"48.1351° N · 11.5817° E · 240 m" as the mount has it.</summary>
    [ObservableProperty]
    public partial string SiteText { get; private set; } = string.Empty;

    /// <summary>"In 41 min" / "6 min ago": when the pointing of the mount crosses the meridian.</summary>
    [ObservableProperty]
    public partial string MeridianText { get; private set; } = string.Empty;

    /// <summary>What the session says about its target and the flip ("Meridian: 36:01 until the hold · 41:01 until the crossing"); empty without a target.</summary>
    [ObservableProperty]
    public partial string SessionMeridianText { get; private set; } = string.Empty;

    public bool HasPosition => PositionText.Length > 0;
    public bool HasHorizontal => HorizontalText.Length > 0;
    public bool HasPier => PierText.Length > 0;
    public bool HasTime => TimeText.Length > 0;
    public bool HasSite => SiteText.Length > 0;
    public bool HasMeridian => MeridianText.Length > 0;
    public bool HasSessionMeridian => SessionMeridianText.Length > 0;

    private void OnChanged(object? sender, EventArgs e) => _postToUi(Refresh);

    /// <summary>Reads the mount again.</summary>
    public void Refresh()
    {
        IsConnected = _mount.ConnectionState == DeviceConnectionState.Connected;
        var t = IsConnected ? _mount.Telemetry : null;
        (StateText, StateKind) = Describe(t);
        PositionText = t?.Coordinates is { } c ? $"{Hours(c.RightAscensionHours)}   {Degrees(c.DeclinationDegrees)}" : string.Empty;
        HorizontalText = t?.Horizontal is { } h
            ? string.Create(CultureInfo.InvariantCulture, $"Altitude {h.AltitudeDegrees:0.0}° · azimuth {h.AzimuthDegrees:0.0}°")
            : string.Empty;
        PierText = t?.SideOfPier is { } side && side != PierSide.Unknown ? side.ToString() : string.Empty;
        TimeText = t is { SiderealTimeHours: { } lst } ? "Sidereal " + Hours(lst) + (t.UtcDate is { } utc ? string.Create(CultureInfo.InvariantCulture, $" · {utc:HH:mm:ss} UTC") : string.Empty) : string.Empty;
        SiteText = IsConnected && _mount.Site is { } site
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(site.LatitudeDegrees):0.0000}° {(site.LatitudeDegrees >= 0 ? "N" : "S")} · {Math.Abs(site.LongitudeDegrees):0.0000}° {(site.LongitudeDegrees >= 0 ? "E" : "W")} · {site.ElevationMeters:0} m")
            : string.Empty;
        MeridianText = MeridianOf(t);
        SessionMeridianText = IsConnected ? _sessionMeridian?.Invoke() ?? string.Empty : string.Empty;
        OnPropertyChanged(nameof(HasPosition));
        OnPropertyChanged(nameof(HasHorizontal));
        OnPropertyChanged(nameof(HasPier));
        OnPropertyChanged(nameof(HasTime));
        OnPropertyChanged(nameof(HasSite));
        OnPropertyChanged(nameof(HasMeridian));
        OnPropertyChanged(nameof(HasSessionMeridian));
    }

    private static (string, StatusKind) Describe(MountTelemetry? t)
    {
        if (t is null)
        {
            return ("Not connected", StatusKind.Neutral);
        }

        if (t.Slewing)
        {
            return ("Slewing", StatusKind.Active);
        }

        if (t.AtPark)
        {
            return ("Parked", StatusKind.Neutral);
        }

        return t.Tracking
            ? ("Tracking" + (t.Rate is { } rate ? " · " + rate : string.Empty), StatusKind.Ok)
            : ("Not tracking", StatusKind.Warning);
    }

    // The hour angle of the pointing from the sidereal time of the mount itself, in minutes of the clock (sidereal hours are a little shorter).
    private static string MeridianOf(MountTelemetry? t)
    {
        if (t is not { Coordinates: { } at, SiderealTimeHours: { } lst })
        {
            return string.Empty;
        }

        var hours = ((lst - at.RightAscensionHours + 36) % 24) - 12;
        var seconds = Math.Abs(hours) * 3600 / 1.00273790935;
        return hours < 0 ? "In " + TrackLaneViewModel.FormatDuration(seconds) : TrackLaneViewModel.FormatDuration(seconds) + " ago";
    }

    private static string Hours(double hours)
    {
        var total = (int)Math.Round(((hours % 24) + 24) % 24 * 3600) % 86400;
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600}h {total / 60 % 60:00}m {total % 60:00}s");
    }

    private static string Degrees(double degrees)
    {
        var total = (int)Math.Round(Math.Abs(degrees) * 3600);
        return string.Create(CultureInfo.InvariantCulture, $"{(degrees < 0 ? '−' : '+')}{total / 3600}° {total / 60 % 60:00}′ {total % 60:00}″");
    }

    public void Dispose()
    {
        _mount.StateChanged -= OnChanged;
        _clock?.Dispose();
        _clock = null;
    }
}

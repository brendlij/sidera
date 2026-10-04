using Astra.Core.Devices;
using Astra.Core.Mounts;

namespace Astra.Runtime.Devices;

/// <summary>
/// The capability, telemetry and operations side of the simulated mount: tracking and its rates, park and home, sync,
/// alt-az slews, pulse guiding, guide rates and refraction. Positions in the horizontal system, the sidereal time and the
/// side of pier are computed from the site and the clock; slews take the simulated slew time. Moving an axis at a rate is
/// not simulated, and the capabilities say so.
/// </summary>
public sealed partial class SimulatedMount
{
    private static readonly TimeSpan MaxPulse = TimeSpan.FromSeconds(60);

    private readonly object _control = new();
    private DeviceCapabilities<MountCapabilities> _capabilities = DeviceCapabilities<MountCapabilities>.Unknown;
    private bool _parked;
    private TrackingRate _rate = TrackingRate.Sidereal;
    private CelestialCoordinates _parkPosition = DefaultCoordinates;
    private GuideRates _guideRates = new(0.00209, 0.00209);
    private bool _refraction;
    private bool _pulseGuiding;

    public event EventHandler? CapabilitiesChanged;

    public event EventHandler? StateChanged;

    /// <summary>Where the simulated mount stands on the earth.</summary>
    public MountSite SimulatedSite { get; init; } = new(50.1, 8.6, 120);

    /// <summary>The clock the sidereal time and the horizontal position follow.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public DeviceCapabilities<MountCapabilities> Capabilities
    {
        get { lock (_control) { return _capabilities; } }
    }

    public MountSite? Site => Capabilities.IsAvailable ? SimulatedSite : null;

    public MountTelemetry? Telemetry => Capabilities.IsAvailable ? ReadTelemetry() : null;

    private static MountCapabilities BuildCapabilities() => new()
    {
        Driver = new DriverMetadata("Astra simulated mount", "A mount that only pretends", "Astra.Runtime", "1.0", null),
        CanSlew = true,
        CanSlewAsync = true,
        CanSlewAltAz = true,
        CanSlewAltAzAsync = true,
        CanSync = true,
        CanSyncAltAz = false,
        CanPark = true,
        CanUnpark = true,
        CanSetPark = true,
        CanFindHome = true,
        CanSetTracking = true,
        TrackingRates = [TrackingRate.Sidereal, TrackingRate.Lunar, TrackingRate.Solar, TrackingRate.King],
        CanSetPierSide = false,
        CanPredictPierSide = true,
        CanPulseGuide = true,
        CanSetGuideRates = true,
        EquatorialSystem = EquatorialSystemKind.Topocentric,
        Alignment = AlignmentKind.GermanPolar,
        HasRefractionSetting = true,
        HasSite = true,
        HasSiderealTime = true,
        HasUtcDate = true,
        HasAltAz = true,
        Notes = ["Moving an axis at a rate is not simulated."],
    };

    private void OnConnectionStateSet(DeviceConnectionState state)
    {
        if (state == DeviceConnectionState.Connected)
        {
            lock (_control)
            {
                _capabilities = DeviceCapabilities<MountCapabilities>.Of(BuildCapabilities());
            }
        }
        else if (state == DeviceConnectionState.Disconnected)
        {
            lock (_control)
            {
                _capabilities = DeviceCapabilities<MountCapabilities>.Unknown;
            }
        }
        else
        {
            return;
        }

        CapabilitiesChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RequireConnected()
    {
        if (!Capabilities.IsAvailable)
        {
            throw new InvalidOperationException($"{Name} is not connected.");
        }
    }

    // Called with the gate held.
    private void RequireNotParked(string what)
    {
        if (_parked)
        {
            throw new InvalidOperationException($"{Name} is parked: it cannot {what}. Unpark it first.");
        }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        RaiseStateChanged();
        return Task.CompletedTask;
    }

    private MountTelemetry ReadTelemetry()
    {
        var coordinates = Coordinates;
        var now = UtcNow();
        var lst = SiderealTimeHours(now);
        bool parked;
        TrackingRate rate;
        GuideRates guideRates;
        bool refraction;
        bool pulsing;
        lock (_gate)
        {
            (parked, rate, guideRates, refraction, pulsing) = (_parked, _rate, _guideRates, _refraction, _pulseGuiding);
        }

        return new MountTelemetry
        {
            Coordinates = coordinates,
            Horizontal = ToHorizontal(coordinates, lst),
            Tracking = MotionState == MountMotionState.Tracking,
            Rate = rate,
            Slewing = MotionState == MountMotionState.Slewing,
            AtPark = parked,
            AtHome = !parked && MotionState != MountMotionState.Slewing && SameSpot(coordinates, DefaultCoordinates),
            SideOfPier = PierSideFor(coordinates, lst),
            SiderealTimeHours = lst,
            UtcDate = now,
            PulseGuiding = pulsing,
            GuideRates = guideRates,
            DoesRefraction = refraction,
            Time = DateTimeOffset.UtcNow,
        };
    }

    private static bool SameSpot(CelestialCoordinates a, CelestialCoordinates b) =>
        Math.Abs(a.RightAscensionHours - b.RightAscensionHours) < 1e-6 && Math.Abs(a.DeclinationDegrees - b.DeclinationDegrees) < 1e-6;

    public Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            RequireNotParked("track");
            if (_motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("The mount is slewing.");
            }

            _motionState = enabled ? MountMotionState.Tracking : MountMotionState.Idle;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public Task SetTrackingRateAsync(TrackingRate rate, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            _rate = rate;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task SlewToAltAzAsync(HorizontalCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireConnected();
        var lst = SiderealTimeHours(UtcNow());
        await SlewToAsync(ToEquatorial(target, lst), cancellationToken);
        RaiseStateChanged();
    }

    public Task SyncAsync(CelestialCoordinates coordinates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        RequireConnected();
        lock (_gate)
        {
            RequireNotParked("sync");
            if (_motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("The mount is slewing.");
            }

            _coordinates = coordinates;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task ParkAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        CelestialCoordinates park;
        lock (_gate)
        {
            if (_parked)
            {
                return;
            }

            park = _parkPosition;
        }

        await SlewToAsync(park, cancellationToken);
        lock (_gate)
        {
            _parked = true;
            _motionState = MountMotionState.Idle;
        }

        RaiseStateChanged();
    }

    public Task UnparkAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            _parked = false;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public Task SetParkAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            RequireNotParked("set its park position");
            _parkPosition = _coordinates;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public async Task FindHomeAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            RequireNotParked("find home");
        }

        await SlewToAsync(DefaultCoordinates, cancellationToken);
        lock (_gate)
        {
            _motionState = MountMotionState.Idle;
        }

        RaiseStateChanged();
    }

    public Task<PierSide> PredictPierSideAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireConnected();
        return Task.FromResult(PierSideFor(target, SiderealTimeHours(UtcNow())));
    }

    public async Task PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (duration <= TimeSpan.Zero || duration > MaxPulse)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A pulse guide must last from 1 millisecond to 60 seconds.");
        }

        GuideRates rates;
        lock (_gate)
        {
            RequireNotParked("pulse guide");
            _pulseGuiding = true;
            rates = _guideRates;
        }

        RaiseStateChanged();
        var started = DateTime.UtcNow;
        try
        {
            await Task.Delay(duration, cancellationToken);
        }
        finally
        {
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            lock (_gate)
            {
                _pulseGuiding = false;
                var ra = _coordinates.RightAscensionHours;
                var dec = _coordinates.DeclinationDegrees;
                switch (direction)
                {
                    case GuideDirection.North:
                        dec += rates.DeclinationDegreesPerSecond * seconds;
                        break;
                    case GuideDirection.South:
                        dec -= rates.DeclinationDegreesPerSecond * seconds;
                        break;
                    case GuideDirection.East:
                        ra += rates.RightAscensionDegreesPerSecond * seconds / 15;
                        break;
                    default:
                        ra -= rates.RightAscensionDegreesPerSecond * seconds / 15;
                        break;
                }

                _coordinates = new CelestialCoordinates(((ra % 24) + 24) % 24, Math.Clamp(dec, -90, 90));
            }

            RaiseStateChanged();
        }
    }

    public Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException($"{Name} does not simulate moving an axis at a rate.");

    public Task SetGuideRatesAsync(GuideRates rates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rates);
        RequireConnected();
        if (!(double.IsFinite(rates.RightAscensionDegreesPerSecond) && rates.RightAscensionDegreesPerSecond > 0
            && double.IsFinite(rates.DeclinationDegreesPerSecond) && rates.DeclinationDegreesPerSecond > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rates), "Guide rates must be positive numbers.");
        }

        lock (_gate)
        {
            _guideRates = rates;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    public Task SetRefractionAsync(bool corrects, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        lock (_gate)
        {
            _refraction = corrects;
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    // ---- Astronomy: local sidereal time and the conversion between the equatorial and the horizontal system.

    private double SiderealTimeHours(DateTime utc)
    {
        var days = utc.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        var gmst = 18.697374558 + 24.06570982441908 * days;
        var lst = (gmst + SimulatedSite.LongitudeDegrees / 15) % 24;
        return lst < 0 ? lst + 24 : lst;
    }

    private HorizontalCoordinates ToHorizontal(CelestialCoordinates c, double lstHours)
    {
        var lat = SimulatedSite.LatitudeDegrees * Math.PI / 180;
        var dec = c.DeclinationDegrees * Math.PI / 180;
        var hourAngle = (lstHours - c.RightAscensionHours) * 15 * Math.PI / 180;
        var sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(hourAngle);
        var alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
        var az = Math.Atan2(
            -Math.Cos(dec) * Math.Sin(hourAngle),
            Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Sin(lat) * Math.Cos(hourAngle));
        var azDegrees = ((az * 180 / Math.PI % 360) + 360) % 360;
        return new HorizontalCoordinates(alt * 180 / Math.PI, azDegrees >= 360 ? 0 : azDegrees);
    }

    private CelestialCoordinates ToEquatorial(HorizontalCoordinates h, double lstHours)
    {
        var lat = SimulatedSite.LatitudeDegrees * Math.PI / 180;
        var alt = h.AltitudeDegrees * Math.PI / 180;
        var az = h.AzimuthDegrees * Math.PI / 180;
        var sinDec = Math.Sin(alt) * Math.Sin(lat) + Math.Cos(alt) * Math.Cos(lat) * Math.Cos(az);
        var dec = Math.Asin(Math.Clamp(sinDec, -1, 1));
        var hourAngle = Math.Atan2(
            -Math.Sin(az) * Math.Cos(alt),
            Math.Sin(alt) * Math.Cos(lat) - Math.Cos(alt) * Math.Sin(lat) * Math.Cos(az));
        var ra = lstHours - hourAngle * 180 / Math.PI / 15;
        ra = ((ra % 24) + 24) % 24;
        return new CelestialCoordinates(ra >= 24 ? 0 : ra, dec * 180 / Math.PI);
    }

    // An object west of the meridian (positive hour angle) is reached with the mount on the east side of the pier.
    private static PierSide PierSideFor(CelestialCoordinates c, double lstHours)
    {
        var hourAngle = ((lstHours - c.RightAscensionHours + 36) % 24) - 12;
        return hourAngle >= 0 ? PierSide.East : PierSide.West;
    }
}

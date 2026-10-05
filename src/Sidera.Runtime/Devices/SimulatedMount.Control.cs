using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Devices;

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
    private CancellationTokenSource? _motionStop;
    private double _primaryRate;
    private double _secondaryRate;
    private DateTime _axesSince = DateTime.UtcNow;
    private bool _stopRequested;

    public event EventHandler? CapabilitiesChanged;

    public event EventHandler? StateChanged;

    /// <summary>Where the simulated mount stands on the earth.</summary>
    public MountSite SimulatedSite
    {
        get { lock (_control) { return _simulatedSite; } }
        init => _simulatedSite = value;
    }

    private MountSite _simulatedSite = new(50.1, 8.6, 120);

    /// <summary>Whether the simulated mount takes a site from outside; a mount with a GPS of its own does not.</summary>
    public bool SiteWritable { get; init; } = true;

    /// <summary>A mount that stores its site coarsely (a step of degrees; 0 stores exactly): what it reads back is the rounded value.</summary>
    public double SiteResolutionDegrees { get; init; }

    /// <summary>For tests of a refusal: the property that the simulated mount refuses to take ("latitude", "longitude" or "elevation"); the ones before it were written.</summary>
    public string? RefuseSiteProperty { get; init; }

    /// <summary>The clock the sidereal time and the horizontal position follow.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public DeviceCapabilities<MountCapabilities> Capabilities
    {
        get { lock (_control) { return _capabilities; } }
    }

    public MountSite? Site => Capabilities.IsAvailable ? SimulatedSite : null;

    public MountTelemetry? Telemetry => Capabilities.IsAvailable ? ReadTelemetry() : null;

    private MountCapabilities BuildCapabilities() => new()
    {
        Driver = new DriverMetadata("Sidera simulated mount", "A mount that only pretends", "Sidera.Runtime", "1.0", null),
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
        CanMovePrimaryAxis = true,
        CanMoveSecondaryAxis = true,
        AxisRates = new Dictionary<MountAxis, IReadOnlyList<AxisRate>>
        {
            [MountAxis.Primary] = [new AxisRate(0.001, 4)],
            [MountAxis.Secondary] = [new AxisRate(0.001, 4)],
        },
        EquatorialSystem = EquatorialSystemKind.Topocentric,
        Alignment = AlignmentKind.GermanPolar,
        HasRefractionSetting = true,
        HasSite = true,
        SiteWrite = SiteWritable ? MountSiteWriteSupport.Supported : MountSiteWriteSupport.NotSupported,
        HasSiderealTime = true,
        HasUtcDate = true,
        HasAltAz = true,
        Notes = [],
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
        lock (_gate)
        {
            IntegrateAxes();
        }

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

    /// <summary>Stops whatever the mount is moving (a slew, a park, finding home, a pulse); tracking stays as it was. Always allowed.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        CancellationTokenSource? motion;
        lock (_gate)
        {
            IntegrateAxes();
            (_primaryRate, _secondaryRate) = (0, 0);
            motion = _motionStop;
            _stopRequested = motion is not null;
        }

        try
        {
            motion?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The movement ended by itself at the same moment.
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

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

    /// <summary>How often the mount was synchronized: for tests that show that an operation never does it by itself.</summary>
    public int SyncCount { get; private set; }

    public Task SyncAsync(CelestialCoordinates coordinates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        RequireConnected();
        lock (_gate)
        {
            SyncCount++;
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
        using var pulse = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _motionStop ??= pulse;
        }

        try
        {
            await Task.Delay(duration, pulse.Token);
        }
        finally
        {
            var seconds = (DateTime.UtcNow - started).TotalSeconds;
            lock (_gate)
            {
                _pulseGuiding = false;
                if (ReferenceEquals(_motionStop, pulse))
                {
                    _motionStop = null;
                }

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

    // An axis moved at a rate: the position drifts at that rate until the axis is set back to 0 (or the mount is stopped).
    public Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (axis == MountAxis.Tertiary)
        {
            throw new NotSupportedException($"{Name} has no third axis.");
        }

        if (!double.IsFinite(degreesPerSecond) || Math.Abs(degreesPerSecond) > 4 || (degreesPerSecond != 0 && Math.Abs(degreesPerSecond) < 0.001))
        {
            throw new ArgumentOutOfRangeException(nameof(degreesPerSecond), degreesPerSecond, "The mount moves an axis at 0.001 to 4 degrees per second, or 0 to stop it.");
        }

        lock (_gate)
        {
            if (degreesPerSecond != 0)
            {
                RequireNotParked("move an axis");
                if (_motionState == MountMotionState.Slewing)
                {
                    throw new InvalidOperationException("The mount is slewing.");
                }
            }

            IntegrateAxes();
            if (axis == MountAxis.Primary)
            {
                _primaryRate = degreesPerSecond;
            }
            else
            {
                _secondaryRate = degreesPerSecond;
            }
        }

        RaiseStateChanged();
        return Task.CompletedTask;
    }

    // Called with the gate held: the movement since the last change is added to the position.
    private void IntegrateAxes()
    {
        var now = DateTime.UtcNow;
        var seconds = (now - _axesSince).TotalSeconds;
        _axesSince = now;
        if (seconds <= 0 || (_primaryRate == 0 && _secondaryRate == 0))
        {
            return;
        }

        var ra = _coordinates.RightAscensionHours + _primaryRate * seconds / 15;
        var dec = Math.Clamp(_coordinates.DeclinationDegrees + _secondaryRate * seconds, -90, 90);
        _coordinates = new CelestialCoordinates(((ra % 24) + 24) % 24, dec);
    }

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

    private double Stored(double degrees) =>
        SiteResolutionDegrees > 0 ? Math.Round(degrees / SiteResolutionDegrees) * SiteResolutionDegrees : degrees;

    public Task SetSiteAsync(ObservingSite site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(site);
        RequireConnected();
        if (!SiteWritable)
        {
            throw new MountSiteWriteException("The simulated mount does not take a site (it owns its location).");
        }

        string? refused = null;
        lock (_control)
        {
            var next = _simulatedSite;
            foreach (var property in new[] { "latitude", "longitude", "elevation" })
            {
                if (property == RefuseSiteProperty)
                {
                    refused = property;
                    break;
                }

                next = property switch
                {
                    "latitude" => next with { LatitudeDegrees = Stored(site.LatitudeDegrees) },
                    "longitude" => next with { LongitudeDegrees = Stored(site.LongitudeDegrees) },
                    _ => next with { ElevationMeters = site.ElevationMeters },
                };
            }

            _simulatedSite = next;
        }

        RaiseStateChanged();
        return refused is null
            ? Task.CompletedTask
            : throw new MountSiteWriteException($"The simulated mount refused the {refused} of its site.");
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

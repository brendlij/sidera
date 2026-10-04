using System.Diagnostics;
using Sidera.Ascom.Drivers;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.Mounts;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom.Mounts;

/// <summary>
/// An ASCOM telescope as Sidera's <see cref="IMount"/>. Right ascension is hours and declination degrees, as in ASCOM;
/// the coordinates are handed to the driver unchanged, in whatever equatorial system the driver uses.
/// <para>
/// A slew reads the state of the mount first and refuses a parked mount (Sidera never unparks) and one that cannot slew.
/// It uses the asynchronous slew with polling of <c>Slewing</c> when the driver offers it. A driver that only has the
/// blocking slew is called once and blocks its dispatcher until it returns: such a slew cannot be aborted from Sidera
/// while it runs. Tracking is switched on before the slew when the mount is not tracking and can be told to; a slew is
/// never retried.
/// </para>
/// <para>
/// Cancelling (or a timeout) calls <c>AbortSlew</c>. Whether the mount really stopped is read back and logged, never
/// assumed.
/// </para>
/// </summary>
public sealed class AscomMount : AscomDevice<IAscomMountDriver>, IMountControl
{
    private readonly CapabilityHolder<MountCapabilities> _capabilities = new();
    private MountTelemetry? _telemetry;
    private MountSite? _site;
    private bool _sideraSlewing;
    private int _stops;
    private readonly IAscomDriverFactory _drivers;
    private readonly object _state = new();
    private MountMotionState _motionState = MountMotionState.Idle;
    private CelestialCoordinates _coordinates = new(0, 0);

    public AscomMount(
        DeviceId id,
        string name,
        string progId,
        IAscomDriverFactory drivers,
        IEventPublisher? events = null,
        ILogger? logger = null,
        AscomTimings? timings = null)
        : base(id, name, DeviceType.Mount, progId, events, logger, timings)
    {
        _drivers = drivers;
    }

    public MountMotionState MotionState
    {
        get { lock (_state) { return _motionState; } }
    }

    public CelestialCoordinates Coordinates
    {
        get { lock (_state) { return _coordinates; } }
    }

    // Only what Sidera started keeps a disconnect away: a driver that says it is slewing when it connects (some report that
    // while the mount is not even powered) must not make the device impossible to disconnect.
    protected override bool IsBusy
    {
        get { lock (_state) { return _sideraSlewing; } }
    }

    protected override string BusyDescription => "the mount is slewing";

    // The axes Sidera set moving at a rate and has not set back to 0. Whatever is left when the driver is released is stopped first.
    private readonly HashSet<MountAxis> _movingAxes = [];

    protected override void BeforeRelease(IAscomMountDriver driver)
    {
        MountAxis[] axes;
        lock (_state)
        {
            axes = [.. _movingAxes];
            _movingAxes.Clear();
        }

        foreach (var axis in axes)
        {
            Logger.LogWarning("{Device}: axis {Axis} was still moving at the release; stopping it", Name, axis);
            driver.MoveAxis(axis, 0);
        }
    }

    protected override IAscomMountDriver CreateDriver() => _drivers.CreateMount(ProgId);

    public event EventHandler? StateChanged;

    public event EventHandler? CapabilitiesChanged
    {
        add => _capabilities.Changed += value;
        remove => _capabilities.Changed -= value;
    }

    public DeviceCapabilities<MountCapabilities> Capabilities => _capabilities.Current;

    public MountTelemetry? Telemetry
    {
        get { lock (_state) { return _telemetry; } }
    }

    public MountSite? Site
    {
        get { lock (_state) { return _site; } }
    }

    protected override void OnConnected(IAscomMountDriver driver)
    {
        var tracking = driver.Tracking;
        var slewing = driver.Slewing;
        var coordinates = TryReadCoordinates(driver);
        var capabilities = ProbeCapabilities(driver, coordinates);
        var telemetry = ReadTelemetry(driver, capabilities);
        MountSite? site = null;
        if (capabilities.HasSite)
        {
            site = new MountSite(driver.SiteLatitude, driver.SiteLongitude, driver.SiteElevation);
        }

        lock (_state)
        {
            _motionState = slewing ? MountMotionState.Slewing : tracking ? MountMotionState.Tracking : MountMotionState.Idle;
            if (coordinates is not null)
            {
                _coordinates = coordinates;
            }

            _telemetry = telemetry;
            _site = site;
        }

        _capabilities.Set(capabilities, this);
        foreach (var note in capabilities.Notes)
        {
            Logger.LogDebug("{Device}: capability probe: {Note}", Name, note);
        }

        Logger.LogInformation(
            "{Device}: CanSlew {CanSlew}, CanSlewAsync {CanSlewAsync}, CanSetTracking {CanSetTracking}, CanPark {CanPark}, CanPulseGuide {CanPulseGuide}, tracking {Tracking}, parked {Parked}",
            Name, capabilities.CanSlew, capabilities.CanSlewAsync, capabilities.CanSetTracking, capabilities.CanPark,
            capabilities.CanPulseGuide, tracking, telemetry.AtPark);
    }

    // Reads only. Standard capability flags are read as they are; optional members are tried and noted when missing.
    private MountCapabilities ProbeCapabilities(IAscomMountDriver d, CelestialCoordinates? where)
    {
        var probe = new CapabilityProbe(Name);
        bool Flag(string member, Func<bool> read) => probe.Read(member, read, false);

        var axes = new Dictionary<MountAxis, IReadOnlyList<AxisRate>>();
        var canMove = new Dictionary<MountAxis, bool>();
        foreach (var axis in Enum.GetValues<MountAxis>())
        {
            var can = Flag($"CanMoveAxis({axis})", () => d.CanMoveAxis(axis));
            canMove[axis] = can;
            if (can)
            {
                axes[axis] = probe.TryRef($"AxisRates({axis})", () => d.AxisRates(axis)) ?? [];
            }
        }

        var trackingRates = probe.TryRef("TrackingRates", () => d.TrackingRates) ?? [];
        var canPredict = false;
        if (where is not null)
        {
            var side = probe.Try("DestinationSideOfPier", () => d.DestinationSideOfPier(where.RightAscensionHours, where.DeclinationDegrees));
            canPredict = side is { } s && s != PierSide.Unknown;
        }

        var hasAltAz = probe.Try("Altitude", () => d.Altitude) is not null && probe.Try("Azimuth", () => d.Azimuth) is not null;
        var hasSite = probe.Try("SiteLatitude", () => d.SiteLatitude) is not null
            && probe.Try("SiteLongitude", () => d.SiteLongitude) is not null
            && probe.Try("SiteElevation", () => d.SiteElevation) is not null;
        var apertureArea = probe.Try("ApertureArea", () => d.ApertureArea);
        var apertureDiameter = probe.Try("ApertureDiameter", () => d.ApertureDiameter);
        var focalLength = probe.Try("FocalLength", () => d.FocalLength);

        return new MountCapabilities
        {
            Driver = probe.Read("identity", () => d.Identity, new DriverMetadata()),
            CanSlew = Flag("CanSlew", () => d.CanSlew),
            CanSlewAsync = Flag("CanSlewAsync", () => d.CanSlewAsync),
            CanSlewAltAz = Flag("CanSlewAltAz", () => d.CanSlewAltAz),
            CanSlewAltAzAsync = Flag("CanSlewAltAzAsync", () => d.CanSlewAltAzAsync),
            CanSync = Flag("CanSync", () => d.CanSync),
            CanSyncAltAz = Flag("CanSyncAltAz", () => d.CanSyncAltAz),
            CanPark = Flag("CanPark", () => d.CanPark),
            CanUnpark = Flag("CanUnpark", () => d.CanUnpark),
            CanSetPark = Flag("CanSetPark", () => d.CanSetPark),
            CanFindHome = Flag("CanFindHome", () => d.CanFindHome),
            CanSetTracking = Flag("CanSetTracking", () => d.CanSetTracking),
            TrackingRates = trackingRates,
            CanSetRightAscensionRate = Flag("CanSetRightAscensionRate", () => d.CanSetRightAscensionRate),
            CanSetDeclinationRate = Flag("CanSetDeclinationRate", () => d.CanSetDeclinationRate),
            CanSetPierSide = Flag("CanSetPierSide", () => d.CanSetPierSide),
            CanPredictPierSide = canPredict,
            CanPulseGuide = Flag("CanPulseGuide", () => d.CanPulseGuide),
            CanSetGuideRates = Flag("CanSetGuideRates", () => d.CanSetGuideRates),
            CanMovePrimaryAxis = canMove[MountAxis.Primary],
            CanMoveSecondaryAxis = canMove[MountAxis.Secondary],
            CanMoveTertiaryAxis = canMove[MountAxis.Tertiary],
            AxisRates = axes,
            EquatorialSystem = probe.Try("EquatorialSystem", () => d.EquatorialSystem),
            Alignment = probe.Try("AlignmentMode", () => d.AlignmentMode),
            ApertureAreaSquareMeters = apertureArea is > 0 ? apertureArea : null,
            ApertureDiameterMeters = apertureDiameter is > 0 ? apertureDiameter : null,
            FocalLengthMeters = focalLength is > 0 ? focalLength : null,
            HasRefractionSetting = probe.Try("DoesRefraction", () => d.DoesRefraction) is not null,
            HasSite = hasSite,
            HasSiderealTime = probe.Try("SiderealTime", () => d.SiderealTime) is not null,
            HasUtcDate = probe.Try("UTCDate", () => d.UtcDate) is not null,
            HasAltAz = hasAltAz,
            Notes = probe.Notes.ToList(),
        };
    }

    // The state of the mount now; members it does not offer stay null. Reads only.
    private MountTelemetry ReadTelemetry(IAscomMountDriver d, MountCapabilities c)
    {
        T? Opt<T>(bool offered, Func<T> read) where T : struct
        {
            if (!offered)
            {
                return null;
            }

            try
            {
                return read();
            }
            catch
            {
                return null;
            }
        }

        HorizontalCoordinates? horizontal = null;
        if (c.HasAltAz)
        {
            try
            {
                horizontal = new HorizontalCoordinates(Math.Clamp(d.Altitude, -90, 90), Normalize360(d.Azimuth));
            }
            catch
            {
                horizontal = null;
            }
        }

        GuideRates? guideRates = null;
        if (c.CanSetGuideRates || c.CanPulseGuide)
        {
            try
            {
                guideRates = new GuideRates(d.GuideRateRightAscension, d.GuideRateDeclination);
            }
            catch
            {
                guideRates = null;
            }
        }

        var rate = c.TrackingRates.Count > 0 ? Opt(true, () => d.TrackingRate) : null;
        return new MountTelemetry
        {
            Coordinates = TryReadCoordinates(d),
            Horizontal = horizontal,
            Tracking = d.Tracking,
            Rate = rate,
            Slewing = d.Slewing,
            AtPark = Opt(true, () => d.AtPark) ?? false,
            AtHome = Opt(c.CanFindHome, () => d.AtHome),
            SideOfPier = Opt(true, () => d.SideOfPier),
            SiderealTimeHours = Opt(c.HasSiderealTime, () => d.SiderealTime) is { } lst && lst >= 0 && lst < 24.000001 ? lst : null,
            UtcDate = Opt(c.HasUtcDate, () => d.UtcDate),
            PulseGuiding = c.CanPulseGuide && (Opt(true, () => d.IsPulseGuiding) ?? false),
            GuideRates = guideRates,
            DoesRefraction = Opt(c.HasRefractionSetting, () => d.DoesRefraction),
        };
    }

    private static double Normalize360(double degrees)
    {
        var a = degrees % 360;
        return a < 0 ? a + 360 : a >= 360 ? 0 : a;
    }

    protected override void OnDisconnected()
    {
        lock (_state)
        {
            _motionState = MountMotionState.Idle;
            _telemetry = null;
            _site = null;
            _sideraSlewing = false;
        }

        _capabilities.Reset(this);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        var telemetry = await CallAsync("read the state of", d => ReadTelemetry(d, capabilities), cancellationToken);
        lock (_state)
        {
            _telemetry = telemetry;
            if (telemetry.Coordinates is { } c && _motionState != MountMotionState.Slewing)
            {
                _coordinates = c;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Operations beyond the slew. Each one checks the capability, and the state it depends on, before touching the mount.

    private MountCapabilities Require(string operation, Func<MountCapabilities, bool> supported, string what)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!supported(capabilities))
        {
            throw new AscomUnsupportedException(Id, ProgId, operation, $"{Name} ({ProgId}) {what}.");
        }

        return capabilities;
    }

    private async Task RequireNotParkedAsync(string operation, CancellationToken cancellationToken)
    {
        if (await CallAsync("read the state of", d => d.AtPark, cancellationToken))
        {
            throw new AscomDeviceException(
                Id, ProgId, operation, $"{Name} is parked. Sidera does not unpark a mount on its own; unpark it first.");
        }
    }

    private async Task AfterOperationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("{Device}: the state could not be read after the operation: {Reason}", Name, ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        using var scope = BeginScope();
        Logger.LogWarning("Stop requested for {Device}", Name);
        Interlocked.Increment(ref _stops);
        lock (_state)
        {
            _movingAxes.Clear();
        }

        // AbortSlew first, then every axis that can be moved at a rate is set to 0. Tracking is not touched. Each part is tried on its
        // own: a mount that is parked or idle may refuse one of them, which is only a problem when it still moves afterwards.
        var failures = new List<string>();
        var stillMoving = await CallAsync(
            "stop",
            d =>
            {
                try
                {
                    d.AbortSlew();
                }
                catch (Exception ex)
                {
                    failures.Add("AbortSlew: " + AscomErrors.Describe(ex));
                }

                foreach (var axis in Enum.GetValues<MountAxis>())
                {
                    if (!capabilities.CanMove(axis))
                    {
                        continue;
                    }

                    try
                    {
                        d.MoveAxis(axis, 0);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"MoveAxis {axis}: " + AscomErrors.Describe(ex));
                    }
                }

                try
                {
                    return d.Slewing;
                }
                catch
                {
                    return false;
                }
            },
            cancellationToken);

        foreach (var failure in failures)
        {
            Logger.LogWarning("{Device}: stop: {Failure}", Name, failure);
        }

        await AfterOperationAsync(cancellationToken);
        if (stillMoving)
        {
            throw new AscomDeviceException(
                Id, ProgId, "stop", $"{Name} ({ProgId}) could not be confirmed stopped: it still reports slewing. {string.Join(" ", failures)}".TrimEnd());
        }
    }

    public async Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Require("set tracking on", c => c.CanSetTracking, "cannot switch tracking");
        using var scope = BeginScope();
        if (enabled)
        {
            await RequireNotParkedAsync("start tracking on", cancellationToken);
        }

        var operation = enabled ? "start tracking on" : "stop tracking on";
        await CallAsync(operation, d => d.Tracking = enabled, cancellationToken);

        // The state is read back: a driver may accept the command and not reach it (or not yet).
        var reached = false;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            reached = await CallAsync("read the tracking state of", d => d.Tracking == enabled, cancellationToken);
            if (reached || clock.Elapsed >= Timings.TrackingConfirmWait)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100) < Timings.MountPollInterval ? TimeSpan.FromMilliseconds(100) : Timings.MountPollInterval, cancellationToken);
        }

        await AfterOperationAsync(cancellationToken);
        if (!reached)
        {
            Logger.LogWarning("{Device}: tracking did not become {State}", Name, enabled ? "on" : "off");
            throw new AscomDeviceException(
                Id, ProgId, operation,
                $"{Name} ({ProgId}) accepted the command but still reports tracking {(enabled ? "off" : "on")}.");
        }

        Logger.LogInformation("{Device}: tracking {State} (confirmed by the driver)", Name, enabled ? "on" : "off");
        SetState(enabled ? MountMotionState.Tracking : MountMotionState.Idle);
    }

    public async Task SetTrackingRateAsync(TrackingRate rate, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!capabilities.TrackingRates.Contains(rate))
        {
            throw new AscomUnsupportedException(
                Id, ProgId, "set the tracking rate of", $"{Name} ({ProgId}) does not offer the {rate} tracking rate.");
        }

        using var scope = BeginScope();
        await CallAsync("set the tracking rate of", d => d.TrackingRate = rate, cancellationToken);
        Logger.LogInformation("{Device}: tracking rate {Rate}", Name, rate);
        await AfterOperationAsync(cancellationToken);
    }

    public async Task SlewToAltAzAsync(HorizontalCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var capabilities = Require("slew", c => c.CanSlewAltAz || c.CanSlewAltAzAsync, "cannot slew to altitude and azimuth");
        using var scope = BeginScope();
        await RequireNotParkedAsync("slew", cancellationToken);
        lock (_state)
        {
            if (_motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("The mount is already slewing.");
            }

            _motionState = MountMotionState.Slewing;
            _sideraSlewing = true;
        }

        var announced = false;
        try
        {
            Logger.LogInformation("Slewing {Device} to Alt {Alt:0.###} deg, Az {Az:0.###} deg", Name, target.AltitudeDegrees, target.AzimuthDegrees);
            announced = true;
            await PublishAsync(new MountMotionStateChanged(Id, MountMotionState.Idle, MountMotionState.Slewing, Coordinates), cancellationToken);
            var clock = Stopwatch.StartNew();
            if (capabilities.CanSlewAltAzAsync)
            {
                await CallAsync("slew", d => d.SlewToAltAzAsync(target.AltitudeDegrees, target.AzimuthDegrees), cancellationToken);
                await WaitUntilNotSlewingAsync(clock, cancellationToken);
            }
            else
            {
                var slew = CallAsync("slew", d => d.SlewToAltAz(target.AltitudeDegrees, target.AzimuthDegrees), CancellationToken.None);
                try
                {
                    await slew.WaitAsync(Timings.MountSlewTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw await TimeoutAsync(clock.Elapsed);
                }
            }

            Logger.LogInformation("{Device} arrived after {Seconds:0.0} s", Name, clock.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stopped = await AbortAsync();
            Logger.LogInformation(
                stopped
                    ? "{Device} confirmed stopped after the cancelled slew"
                    : "{Device} could not be confirmed stopped after the cancelled slew; it may still be slewing",
                Name);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "Slew of {Device} failed", Name);
            await AbortAsync();
            throw;
        }
        finally
        {
            await FinishMotionAsync(announced);
        }
    }

    public async Task SyncAsync(CelestialCoordinates coordinates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        Require("sync", c => c.CanSync, "cannot sync to coordinates");
        using var scope = BeginScope();
        await RequireNotParkedAsync("sync", cancellationToken);
        await CallAsync(
            "sync", d => d.SyncToCoordinates(coordinates.RightAscensionHours, coordinates.DeclinationDegrees), cancellationToken);
        Logger.LogInformation("{Device} synced to RA {Ra:0.####} h, Dec {Dec:0.###} deg", Name, coordinates.RightAscensionHours, coordinates.DeclinationDegrees);
        await AfterOperationAsync(cancellationToken);
    }

    public async Task ParkAsync(CancellationToken cancellationToken = default)
    {
        Require("park", c => c.CanPark, "cannot park");
        using var scope = BeginScope();
        Logger.LogInformation("Parking {Device}", Name);
        await RunLongOperationAsync(
            "park", d => d.Park(), d => d.AtPark, "AtPark", cancellationToken);
    }

    public async Task UnparkAsync(CancellationToken cancellationToken = default)
    {
        Require("unpark", c => c.CanUnpark, "cannot unpark");
        using var scope = BeginScope();
        Logger.LogInformation("Unparking {Device}", Name);
        await CallAsync("unpark", d => d.Unpark(), cancellationToken);
        await AfterOperationAsync(cancellationToken);
    }

    public async Task SetParkAsync(CancellationToken cancellationToken = default)
    {
        Require("set the park position of", c => c.CanSetPark, "cannot set its park position");
        using var scope = BeginScope();
        await RequireNotParkedAsync("set the park position of", cancellationToken);
        await CallAsync("set the park position of", d => d.SetPark(), cancellationToken);
        Logger.LogInformation("{Device}: park position set to the current position", Name);
        await AfterOperationAsync(cancellationToken);
    }

    public async Task FindHomeAsync(CancellationToken cancellationToken = default)
    {
        Require("find home", c => c.CanFindHome, "cannot find its home position");
        using var scope = BeginScope();
        await RequireNotParkedAsync("find home", cancellationToken);
        Logger.LogInformation("{Device} is finding home", Name);
        await RunLongOperationAsync("find home", d => d.FindHome(), d => d.AtHome, "AtHome", cancellationToken);
    }

    // Park and find home: start, poll Slewing until the mount is still, then confirm the end state the mount reports.
    // Cancelling aborts with AbortSlew and says honestly whether the mount stopped.
    private async Task RunLongOperationAsync(
        string operation, Action<IAscomMountDriver> start, Func<IAscomMountDriver, bool> reached, string reachedName, CancellationToken cancellationToken)
    {
        lock (_state)
        {
            if (_motionState == MountMotionState.Slewing)
            {
                throw new InvalidOperationException("The mount is already slewing.");
            }

            _motionState = MountMotionState.Slewing;
            _sideraSlewing = true;
        }

        var announced = false;
        try
        {
            announced = true;
            await PublishAsync(new MountMotionStateChanged(Id, MountMotionState.Idle, MountMotionState.Slewing, Coordinates), cancellationToken);
            var clock = Stopwatch.StartNew();
            await CallAsync(operation, start, cancellationToken);
            await WaitUntilNotSlewingAsync(clock, cancellationToken);
            if (!await CallAsync("read the state of", reached, cancellationToken))
            {
                Logger.LogWarning("{Device}: the mount stopped moving but does not report {State}", Name, reachedName);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var stopped = await AbortAsync();
            Logger.LogInformation(
                stopped ? "{Device} confirmed stopped after the cancelled {Operation}" : "{Device} could not be confirmed stopped after the cancelled {Operation}",
                Name, operation);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "{Operation} of {Device} failed", operation, Name);
            await AbortAsync();
            throw;
        }
        finally
        {
            await FinishMotionAsync(announced);
        }
    }

    private async Task WaitUntilNotSlewingAsync(Stopwatch clock, CancellationToken cancellationToken)
    {
        var stops = Volatile.Read(ref _stops);
        while (true)
        {
            await Task.Delay(Timings.MountPollInterval, cancellationToken);
            var slewing = await CallAsync("poll", d => d.Slewing, cancellationToken);
            if (Volatile.Read(ref _stops) != stops)
            {
                throw new OperationCanceledException("The movement was stopped.");
            }

            if (!slewing)
            {
                return;
            }

            if (clock.Elapsed > Timings.MountSlewTimeout)
            {
                throw await TimeoutAsync(clock.Elapsed);
            }
        }
    }

    // Back from slewing to what the mount really does now, read quietly, and the events that tell it.
    private async Task FinishMotionAsync(bool announced)
    {
        var (where, tracking) = await ReadStateQuietlyAsync();
        if (where is not null)
        {
            SetCoordinates(where);
        }

        var next = tracking ? MountMotionState.Tracking : MountMotionState.Idle;
        SetState(next);
        if (announced)
        {
            await PublishAsync(new MountMotionStateChanged(Id, MountMotionState.Slewing, next, Coordinates));
        }

        await AfterOperationAsync(CancellationToken.None);
    }

    public async Task<PierSide> PredictPierSideAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Require("predict the side of pier of", c => c.CanPredictPierSide, "cannot predict the side of pier");
        return await CallAsync(
            "predict the side of pier of", d => d.DestinationSideOfPier(target.RightAscensionHours, target.DeclinationDegrees), cancellationToken);
    }

    public async Task PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Require("pulse guide", c => c.CanPulseGuide, "cannot pulse guide");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "A pulse guide must last from 1 millisecond to 60 seconds.");
        }

        using var scope = BeginScope();
        await RequireNotParkedAsync("pulse guide", cancellationToken);
        var milliseconds = (int)Math.Ceiling(duration.TotalMilliseconds);
        Logger.LogInformation("Pulse guiding {Device} {Direction} for {Milliseconds} ms", Name, direction, milliseconds);
        await CallAsync("pulse guide", d => d.PulseGuide(direction, milliseconds), cancellationToken);
        var clock = Stopwatch.StartNew();
        var limit = duration + Timings.StopWait;
        while (await CallAsync("poll", d => d.IsPulseGuiding, cancellationToken))
        {
            if (clock.Elapsed > limit)
            {
                throw new AscomTimeoutException(
                    Id, ProgId, "pulse guide", $"{Name} still reports pulse guiding {limit.TotalSeconds:0} s after a pulse of {milliseconds} ms.");
            }

            await Task.Delay(Timings.MountPollInterval / 5, cancellationToken);
        }

        await AfterOperationAsync(cancellationToken);
    }

    public async Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default)
    {
        var capabilities = Capabilities.Value ?? throw new InvalidOperationException($"{Name} is not connected.");
        if (!capabilities.CanMove(axis))
        {
            throw new AscomUnsupportedException(Id, ProgId, "move an axis of", $"{Name} ({ProgId}) cannot move the {axis} axis at a rate.");
        }

        if (!double.IsFinite(degreesPerSecond))
        {
            throw new ArgumentOutOfRangeException(nameof(degreesPerSecond), degreesPerSecond, "The rate must be a number.");
        }

        if (degreesPerSecond != 0)
        {
            var magnitude = Math.Abs(degreesPerSecond);
            var ranges = capabilities.AxisRates.TryGetValue(axis, out var list) ? list : [];
            if (!ranges.Any(r => magnitude >= r.Minimum - 1e-9 && magnitude <= r.Maximum + 1e-9))
            {
                var allowed = string.Join(", ", ranges.Select(r => $"{r.Minimum:0.####} to {r.Maximum:0.####}"));
                throw new ArgumentOutOfRangeException(
                    nameof(degreesPerSecond), degreesPerSecond,
                    $"The mount moves this axis at {(allowed.Length == 0 ? "no rate it reports" : allowed)} degrees per second (or 0 to stop).");
            }
        }

        using var scope = BeginScope();
        if (degreesPerSecond != 0)
        {
            await RequireNotParkedAsync("move an axis of", cancellationToken);
        }

        Logger.LogInformation("{Device}: axis {Axis} at {Rate} deg/s", Name, axis, degreesPerSecond);
        if (degreesPerSecond != 0)
        {
            // Noted before the call: a call that is cancelled may still have started the axis.
            lock (_state)
            {
                _movingAxes.Add(axis);
            }
        }

        try
        {
            await CallAsync("move an axis of", d => d.MoveAxis(axis, degreesPerSecond), cancellationToken);
        }
        catch (Exception) when (degreesPerSecond != 0)
        {
            // Cancelled or failed: the axis may be moving, and nobody is holding a button any more. Stop it.
            await StopAxisQuietlyAsync(axis);
            throw;
        }

        if (degreesPerSecond == 0)
        {
            lock (_state)
            {
                _movingAxes.Remove(axis);
            }
        }

        await AfterOperationAsync(cancellationToken);
    }

    private async Task StopAxisQuietlyAsync(MountAxis axis)
    {
        try
        {
            await CallAsync("stop an axis of", d => d.MoveAxis(axis, 0), CancellationToken.None).WaitAsync(Timings.StopWait);
            lock (_state)
            {
                _movingAxes.Remove(axis);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: the {Axis} axis could not be confirmed stopped: {Reason}", Name, axis, ex.Message);
        }
    }

    public async Task SetGuideRatesAsync(GuideRates rates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rates);
        Require("set the guide rates of", c => c.CanSetGuideRates, "cannot set its guide rates");
        if (!(double.IsFinite(rates.RightAscensionDegreesPerSecond) && rates.RightAscensionDegreesPerSecond > 0
            && double.IsFinite(rates.DeclinationDegreesPerSecond) && rates.DeclinationDegreesPerSecond > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rates), "Guide rates must be positive numbers.");
        }

        using var scope = BeginScope();
        await CallAsync(
            "set the guide rates of",
            d =>
            {
                d.GuideRateRightAscension = rates.RightAscensionDegreesPerSecond;
                d.GuideRateDeclination = rates.DeclinationDegreesPerSecond;
            },
            cancellationToken);
        await AfterOperationAsync(cancellationToken);
    }

    public async Task SetRefractionAsync(bool corrects, CancellationToken cancellationToken = default)
    {
        Require("set refraction of", c => c.HasRefractionSetting, "does not report refraction correction");
        using var scope = BeginScope();
        await CallAsync("set refraction of", d => d.DoesRefraction = corrects, cancellationToken);
        await AfterOperationAsync(cancellationToken);
    }

    /// <exception cref="InvalidOperationException">The mount is not connected or is already slewing.</exception>
    public async Task SlewToAsync(CelestialCoordinates target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        Session();

        using var scope = BeginScope();

        // The state first. Nothing physical happens before the mount has said it can do this.
        var snapshot = await CallAsync(
            "read the state of",
            d => new MountSnapshot(d.AtPark, d.Slewing, d.Tracking, d.CanSlew, d.CanSlewAsync, d.CanSetTracking),
            cancellationToken);

        if (!snapshot.CanSlew)
        {
            throw new AscomUnsupportedException(
                Id, ProgId, "slew", $"{Name} ({ProgId}) cannot slew to coordinates.");
        }

        if (snapshot.AtPark)
        {
            throw new AscomDeviceException(
                Id, ProgId, "slew", $"{Name} is parked. Sidera does not unpark a mount; unpark it first.");
        }

        CelestialCoordinates startedAt;
        lock (_state)
        {
            if (_motionState == MountMotionState.Slewing || snapshot.Slewing)
            {
                throw new InvalidOperationException("The mount is already slewing.");
            }

            _motionState = MountMotionState.Slewing;
            _sideraSlewing = true;
            startedAt = _coordinates;
        }

        var ended = false;
        var announced = false;
        try
        {
            Logger.LogInformation(
                "Slewing {Device} to RA {Ra:0.####} h, Dec {Dec:0.###} deg ({Mode})",
                Name, target.RightAscensionHours, target.DeclinationDegrees, snapshot.CanSlewAsync ? "asynchronous" : "blocking");
            announced = true;
            await PublishAsync(
                new MountMotionStateChanged(Id, MountMotionState.Idle, MountMotionState.Slewing, startedAt), cancellationToken);

            if (!snapshot.Tracking && snapshot.CanSetTracking)
            {
                Logger.LogInformation("{Device} was not tracking; switching tracking on for the slew", Name);
                await CallAsync("start tracking on", d => d.Tracking = true, cancellationToken);
            }

            var clock = Stopwatch.StartNew();
            var stopMark = Volatile.Read(ref _stops);
            if (snapshot.CanSlewAsync)
            {
                await CallAsync(
                    "slew", d => d.SlewToCoordinatesAsync(target.RightAscensionHours, target.DeclinationDegrees), cancellationToken);
                while (true)
                {
                    await Task.Delay(Timings.MountPollInterval, cancellationToken);
                    var (slewing, seen) = await CallAsync("poll", d => (d.Slewing, TryReadCoordinates(d)), cancellationToken);
                    if (seen is not null)
                    {
                        SetCoordinates(seen);
                    }

                    if (!slewing)
                    {
                        break;
                    }

                    if (clock.Elapsed > Timings.MountSlewTimeout)
                    {
                        throw await TimeoutAsync(clock.Elapsed);
                    }
                }
            }
            else
            {
                // The driver blocks until the slew is over; Sidera waits, but cannot reach the driver meanwhile.
                var slew = CallAsync(
                    "slew", d => d.SlewToCoordinates(target.RightAscensionHours, target.DeclinationDegrees), CancellationToken.None);
                try
                {
                    await slew.WaitAsync(Timings.MountSlewTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw await TimeoutAsync(clock.Elapsed);
                }
            }

            if (Volatile.Read(ref _stops) != stopMark)
            {
                throw new OperationCanceledException("The slew was stopped.");
            }

            var (final, tracking) = await CallAsync("read the position of", d => (TryReadCoordinates(d), d.Tracking), cancellationToken);
            var arrivedAt = final ?? target;
            SetCoordinates(arrivedAt);
            var next = tracking ? MountMotionState.Tracking : MountMotionState.Idle;
            ended = true;
            SetState(next);
            await PublishAsync(new MountMotionStateChanged(Id, MountMotionState.Slewing, next, arrivedAt));
            Logger.LogInformation("{Device} arrived after {Seconds:0.0} s, now {State}", Name, clock.Elapsed.TotalSeconds, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Logger.LogInformation("Slew of {Device} cancelled; asking the driver to abort", Name);
            var stopped = await AbortAsync();
            Logger.LogInformation(
                stopped
                    ? "{Device} confirmed stopped after the cancelled slew"
                    : "{Device} could not be confirmed stopped after the cancelled slew; it may still be slewing",
                Name);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AscomTimeoutException)
        {
            Logger.LogError(ex, "Slew of {Device} failed", Name);
            await AbortAsync();
            throw;
        }
        finally
        {
            if (!ended)
            {
                var (where, tracking) = await ReadStateQuietlyAsync();
                if (where is not null)
                {
                    SetCoordinates(where);
                }

                var next = tracking ? MountMotionState.Tracking : MountMotionState.Idle;
                SetState(next);
                if (announced)
                {
                    await PublishAsync(new MountMotionStateChanged(Id, MountMotionState.Slewing, next, Coordinates));
                }
            }
        }
    }

    private async Task<AscomTimeoutException> TimeoutAsync(TimeSpan waited)
    {
        var stopped = await AbortAsync();
        return new AscomTimeoutException(
            Id, ProgId, "slew",
            $"{Name} did not finish slewing within {waited.TotalSeconds:0} s. " +
            (stopped
                ? "The driver accepted AbortSlew and the mount reports that it stopped."
                : "The mount could not be confirmed stopped: it may still be slewing."));
    }

    // AbortSlew, then watch Slewing for a bounded time. True only when the mount says it no longer slews after an
    // abort the driver accepted. A blocking slew keeps the dispatcher busy: then the abort waits behind it and the wait
    // here runs out, which is reported as not confirmed.
    private async Task<bool> AbortAsync()
    {
        try
        {
            await CallAsync("abort the slew of", d => d.AbortSlew(), CancellationToken.None).WaitAsync(Timings.StopWait);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: AbortSlew did not work: {Reason}", Name, ex is AscomDeviceException ? ex.Message : AscomErrors.Describe(ex));
            return false;
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timings.StopWait)
        {
            try
            {
                if (!await CallAsync("poll", d => d.Slewing, CancellationToken.None).WaitAsync(Timings.StopWait))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("{Device}: could not tell whether the mount stopped: {Reason}", Name, ex.Message);
                return false;
            }

            await Task.Delay(Timings.MountPollInterval);
        }

        return false;
    }

    private async Task<(CelestialCoordinates? Where, bool Tracking)> ReadStateQuietlyAsync()
    {
        try
        {
            return await CallAsync("read the state of", d => (TryReadCoordinates(d), d.Tracking), CancellationToken.None)
                .WaitAsync(Timings.StopWait);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("{Device}: the state could not be read after the slew: {Reason}", Name, ex.Message);
            return (null, false);
        }
    }

    // Coordinates read while the mount slews are a courtesy; a value the driver reports out of range is not used.
    private CelestialCoordinates? TryReadCoordinates(IAscomMountDriver driver)
    {
        try
        {
            return Normalize(driver.RightAscension, driver.Declination);
        }
        catch (Exception ex)
        {
            Logger.LogDebug("{Device}: coordinates not readable now: {Reason}", Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Sidera's coordinates from the numbers of a driver: right ascension wrapped into 0 to 24 hours (a driver may say
    /// 24 or a hair below 0), declination refused beyond the poles by more than rounding.
    /// </summary>
    internal static CelestialCoordinates Normalize(double rightAscensionHours, double declinationDegrees)
    {
        if (!double.IsFinite(rightAscensionHours) || !double.IsFinite(declinationDegrees))
        {
            throw new ArgumentOutOfRangeException(nameof(rightAscensionHours), "The driver reported coordinates that are not numbers.");
        }

        var ra = rightAscensionHours % 24;
        if (ra < 0)
        {
            ra += 24;
        }

        if (ra >= 24)
        {
            ra = 0;
        }

        if (Math.Abs(declinationDegrees) > 90 + 1e-6)
        {
            throw new ArgumentOutOfRangeException(nameof(declinationDegrees), declinationDegrees, "The driver reported a declination beyond the poles.");
        }

        return new CelestialCoordinates(ra, Math.Clamp(declinationDegrees, -90, 90));
    }

    private void SetCoordinates(CelestialCoordinates coordinates)
    {
        lock (_state)
        {
            _coordinates = coordinates;
        }
    }

    private void SetState(MountMotionState state)
    {
        lock (_state)
        {
            _motionState = state;
            _sideraSlewing = false;
        }
    }

    private readonly record struct MountSnapshot(
        bool AtPark, bool Slewing, bool Tracking, bool CanSlew, bool CanSlewAsync, bool CanSetTracking);
}

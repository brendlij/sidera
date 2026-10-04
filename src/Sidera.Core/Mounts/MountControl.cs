using Sidera.Core.Devices;
using Sidera.Core.Location;

namespace Sidera.Core.Mounts;

/// <summary>The rate a mount tracks at.</summary>
public enum TrackingRate
{
    Sidereal,
    Lunar,
    Solar,
    King,
}

/// <summary>Which side of the pier a German equatorial mount is on; for other mounts the driver says nothing.</summary>
public enum PierSide
{
    Unknown,
    East,
    West,
}

/// <summary>How a mount is built, as the driver reports it.</summary>
public enum AlignmentKind
{
    AltAz,
    Polar,
    GermanPolar,
}

/// <summary>The equatorial coordinate system a driver works in. Coordinates are handed over without conversion.</summary>
public enum EquatorialSystemKind
{
    Other,
    Topocentric,
    J2000,
    J2050,
    B1950,
}

/// <summary>The axes of a mount that can be moved at a rate: primary (RA or azimuth), secondary (Dec or altitude), tertiary.</summary>
public enum MountAxis
{
    Primary,
    Secondary,
    Tertiary,
}

/// <summary>A direction of a pulse guide command.</summary>
public enum GuideDirection
{
    North,
    South,
    East,
    West,
}

/// <summary>The range of rates, in degrees per second, that an axis can be moved at.</summary>
public sealed record AxisRate(double Minimum, double Maximum);

/// <summary>A position in the horizontal system: altitude above the horizon and azimuth from north, in degrees.</summary>
public sealed record HorizontalCoordinates
{
    public HorizontalCoordinates(double altitudeDegrees, double azimuthDegrees)
    {
        if (!double.IsFinite(altitudeDegrees) || altitudeDegrees < -90 || altitudeDegrees > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(altitudeDegrees), altitudeDegrees, "Altitude must be a finite number from -90 to +90 degrees.");
        }

        if (!double.IsFinite(azimuthDegrees) || azimuthDegrees < 0 || azimuthDegrees >= 360)
        {
            throw new ArgumentOutOfRangeException(nameof(azimuthDegrees), azimuthDegrees, "Azimuth must be a finite number from 0 (inclusive) to 360 (exclusive) degrees.");
        }

        AltitudeDegrees = altitudeDegrees;
        AzimuthDegrees = azimuthDegrees;
    }

    public double AltitudeDegrees { get; }
    public double AzimuthDegrees { get; }
}

/// <summary>Where a mount stands on the earth, as the driver knows it.</summary>
public sealed record MountSite(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters)
{
    /// <summary>
    /// The site as an <see cref="ObservingSite"/>, or <c>null</c> when the mount does not report a place: a value out of range, or a latitude and
    /// longitude of exactly 0 and 0, which is what a mount that was never given a site reports (and a point in the Gulf of Guinea, never an
    /// observatory). A site like that is unknown, not a place to compare with.
    /// </summary>
    public ObservingSite? ToObservingSite() =>
        LatitudeDegrees == 0 && LongitudeDegrees == 0 ? null
        : ObservingSite.TryCreate(LatitudeDegrees, LongitudeDegrees, ElevationMeters, null, out var site, out _) ? site : null;
}

/// <summary>Whether a mount takes a new site. The standard has no flag for it: a driver that has a site but owns it (a GPS) refuses the write.</summary>
public enum MountSiteWriteSupport
{
    /// <summary>The mount reports no site, or it said that the site cannot be written.</summary>
    NotSupported,

    /// <summary>The mount has a site and has not been asked to change it yet; the write may still be refused.</summary>
    Unknown,

    /// <summary>The mount takes a site.</summary>
    Supported,
}

/// <summary>
/// What a mount supports. The flags are the standard capability flags of the telescope interface, plus members that were
/// probed after the connection was made without changing anything. A member the driver does not offer is <c>null</c>,
/// <c>false</c> or empty; <see cref="Notes"/> says why where the driver said something.
/// </summary>
public sealed record MountCapabilities
{
    public DriverMetadata Driver { get; init; } = new();

    // Slewing and syncing
    public bool CanSlew { get; init; }
    public bool CanSlewAsync { get; init; }
    public bool CanSlewAltAz { get; init; }
    public bool CanSlewAltAzAsync { get; init; }
    public bool CanSync { get; init; }
    public bool CanSyncAltAz { get; init; }

    // Park and home
    public bool CanPark { get; init; }
    public bool CanUnpark { get; init; }
    public bool CanSetPark { get; init; }
    public bool CanFindHome { get; init; }

    // Tracking
    public bool CanSetTracking { get; init; }
    public IReadOnlyList<TrackingRate> TrackingRates { get; init; } = [];
    public bool CanSetRightAscensionRate { get; init; }
    public bool CanSetDeclinationRate { get; init; }

    // Pier side
    public bool CanSetPierSide { get; init; }

    /// <summary>The driver can predict the side of pier for a target (probed with the current position, which changes nothing).</summary>
    public bool CanPredictPierSide { get; init; }

    // Guiding and moving at rates
    public bool CanPulseGuide { get; init; }
    public bool CanSetGuideRates { get; init; }
    public bool CanMovePrimaryAxis { get; init; }
    public bool CanMoveSecondaryAxis { get; init; }
    public bool CanMoveTertiaryAxis { get; init; }
    public IReadOnlyDictionary<MountAxis, IReadOnlyList<AxisRate>> AxisRates { get; init; } = new Dictionary<MountAxis, IReadOnlyList<AxisRate>>();

    // The mount itself
    public EquatorialSystemKind? EquatorialSystem { get; init; }
    public AlignmentKind? Alignment { get; init; }
    public double? ApertureAreaSquareMeters { get; init; }
    public double? ApertureDiameterMeters { get; init; }
    public double? FocalLengthMeters { get; init; }

    /// <summary>The driver reports whether it corrects for refraction; this is the flag whether the setting can be read.</summary>
    public bool HasRefractionSetting { get; init; }

    public bool HasSite { get; init; }

    /// <summary>Whether Sidera may offer to send a site to the mount.</summary>
    public MountSiteWriteSupport SiteWrite { get; init; }
    public bool HasSiderealTime { get; init; }
    public bool HasUtcDate { get; init; }
    public bool HasAltAz { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool CanMove(MountAxis axis) => axis switch
    {
        MountAxis.Primary => CanMovePrimaryAxis,
        MountAxis.Secondary => CanMoveSecondaryAxis,
        _ => CanMoveTertiaryAxis,
    };
}

/// <summary>The guide rates of a mount in degrees per second, for the right ascension and the declination axis.</summary>
public sealed record GuideRates(double RightAscensionDegreesPerSecond, double DeclinationDegreesPerSecond);

/// <summary>What a mount is doing and where it points now. Members the mount does not report are <c>null</c>.</summary>
public sealed record MountTelemetry
{
    public CelestialCoordinates? Coordinates { get; init; }
    public HorizontalCoordinates? Horizontal { get; init; }
    public bool Tracking { get; init; }
    public TrackingRate? Rate { get; init; }
    public bool Slewing { get; init; }
    public bool AtPark { get; init; }
    public bool? AtHome { get; init; }
    public PierSide? SideOfPier { get; init; }
    public double? SiderealTimeHours { get; init; }
    public DateTime? UtcDate { get; init; }
    public bool PulseGuiding { get; init; }
    public GuideRates? GuideRates { get; init; }
    public bool? DoesRefraction { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A mount that says what it supports and can be operated beyond the slew of <see cref="IMount"/>: tracking and its rate,
/// park and home, sync, alt-az slews, moving an axis, pulse guiding. Every operation checks the capability first and
/// refuses what the mount does not support; none of them is retried. A move that was started (a slew, an axis at a rate) is
/// stopped by the operation that stops it, not by cancelling the wait.
/// </summary>
public interface IMountControl : IMount, ICapable<MountCapabilities>, IObservableDevice
{
    MountTelemetry? Telemetry { get; }

    /// <summary>Where the mount is, once, as the driver knows it; <c>null</c> while not connected or when the driver does not say.</summary>
    MountSite? Site { get; }

    /// <summary>
    /// Stops every movement of the mount: a slew, a park, finding home, an axis moved at a rate. Always allowed: it does not wait for
    /// the operation that is running, and it needs no capability beyond the stop that every telescope has. Tracking is not a movement
    /// and stays as it is. Says honestly when the mount could not be confirmed stopped.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken = default);

    Task SetTrackingRateAsync(TrackingRate rate, CancellationToken cancellationToken = default);

    Task SlewToAltAzAsync(HorizontalCoordinates target, CancellationToken cancellationToken = default);

    /// <summary>Tells the mount that it points at <paramref name="coordinates"/> now. It changes what the mount believes, not where it points.</summary>
    Task SyncAsync(CelestialCoordinates coordinates, CancellationToken cancellationToken = default);

    Task ParkAsync(CancellationToken cancellationToken = default);

    Task UnparkAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes the current position the park position.</summary>
    Task SetParkAsync(CancellationToken cancellationToken = default);

    Task FindHomeAsync(CancellationToken cancellationToken = default);

    /// <summary>The side of pier the mount would be on after slewing to the target; changes nothing.</summary>
    Task<PierSide> PredictPierSideAsync(CelestialCoordinates target, CancellationToken cancellationToken = default);

    Task PulseGuideAsync(GuideDirection direction, TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Moves an axis at a rate in degrees per second (negative is the other way); a rate of 0 stops the axis.</summary>
    Task MoveAxisAsync(MountAxis axis, double degreesPerSecond, CancellationToken cancellationToken = default);

    Task SetGuideRatesAsync(GuideRates rates, CancellationToken cancellationToken = default);

    Task SetRefractionAsync(bool corrects, CancellationToken cancellationToken = default);
}

/// <summary>A mount whose site Sidera can write: the optional part of the site handling beside <see cref="IMountControl"/>.</summary>
public interface IMountSiteControl : IMountControl
{
    /// <summary>
    /// Writes the latitude, the longitude and the elevation to the mount, in the units and with the signs of <see cref="ObservingSite"/>, and
    /// reads them back into <see cref="IMountControl.Site"/>. Never done by itself.
    /// </summary>
    /// <exception cref="MountSiteWriteException">The mount refused a value; <see cref="IMountControl.Site"/> says what it holds now.</exception>
    Task SetSiteAsync(ObservingSite site, CancellationToken cancellationToken = default);
}

/// <summary>The mount refused to take its site, wholly or in part.</summary>
public sealed class MountSiteWriteException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

using System.Globalization;
using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Location;

/// <summary>How the observing site of Sidera and the site of a connected mount relate.</summary>
public enum SiteSituation
{
    /// <summary>Neither Sidera nor the mount has a site: nothing to say.</summary>
    NoSiteAnywhere,

    /// <summary>Sidera has no site and the mount reports one: the mount's can be offered as Sidera's, never taken by itself.</summary>
    MountOffersSite,

    /// <summary>Sidera has a site and the mount reports none (or an invalid one): never compared with zero.</summary>
    MountHasNoSite,

    /// <summary>The same place within the tolerance of <see cref="SiteComparison"/>: no prompt.</summary>
    InSync,

    /// <summary>Two different places: the user chooses.</summary>
    Mismatch,
}

/// <summary>What <see cref="MountSiteSynchronizer.Assess"/> found.</summary>
/// <param name="Sidera">The site of Sidera; <c>null</c> when none is configured.</param>
/// <param name="Mount">The mount's site as a place; <c>null</c> when the mount reports none.</param>
/// <param name="Comparison">Set when both exist.</param>
/// <param name="CanSendToMount">Sidera has a site and the mount may take it (a mount whose driver owns its location is not offered one).</param>
public sealed record SiteAssessment(SiteSituation Situation, ObservingSite? Sidera, ObservingSite? Mount, SiteComparison? Comparison, bool CanSendToMount);

/// <summary>How writing the site to a mount ended.</summary>
/// <param name="Succeeded">The mount holds the site that was sent, as it reported when it was read back.</param>
/// <param name="Problem">What went wrong, in a sentence; <c>null</c> on success.</param>
/// <param name="ReadBack">What the mount reports after the attempt.</param>
public sealed record SiteWriteOutcome(bool Succeeded, string? Problem, MountSite? ReadBack);

/// <summary>
/// The policy for the site of Sidera against the site of a mount: when they are compared, what is offered and how a write is verified. A
/// function of its inputs and the mount, with no view and no store, so it can be tested; nothing here happens by itself. The caller decides
/// when to assess (after a mount connected) and what the user chose.
/// </summary>
public static class MountSiteSynchronizer
{
    /// <summary>The largest difference in latitude or longitude, in degrees, that a read-back may have from what was written: 0.0003° is about 33 m, the rounding of a mount that stores arcseconds.</summary>
    public const double ReadBackToleranceDegrees = 0.0003;

    /// <summary>The largest difference in elevation, in meters, that a read-back may have: a mount that stores whole meters.</summary>
    public const double ReadBackToleranceMeters = 2;

    public static SiteAssessment Assess(ObservingSite? sidera, MountSite? mountSite, MountSiteWriteSupport write)
    {
        var mount = mountSite?.ToObservingSite();
        var canSend = sidera is not null && write != MountSiteWriteSupport.NotSupported;
        if (sidera is null)
        {
            return new SiteAssessment(mount is null ? SiteSituation.NoSiteAnywhere : SiteSituation.MountOffersSite, null, mount, null, false);
        }

        if (mount is null)
        {
            return new SiteAssessment(SiteSituation.MountHasNoSite, sidera, null, null, canSend);
        }

        var comparison = SiteComparison.Compare(sidera, mount);
        return new SiteAssessment(comparison.IsEquivalent ? SiteSituation.InSync : SiteSituation.Mismatch, sidera, mount, comparison, canSend);
    }

    /// <summary>
    /// The site of Sidera after "use the mount location": the mount's place, with the name the user gave the site that is replaced (the mount
    /// has no name). <c>null</c> when the mount reports no place.
    /// </summary>
    public static ObservingSite? AdoptMountSite(MountSite mountSite, ObservingSite? current)
    {
        ArgumentNullException.ThrowIfNull(mountSite);
        return mountSite.ToObservingSite() is { } place ? place.WithName(current?.Name) : null;
    }

    /// <summary>
    /// Sends the site to the mount, reads it back and compares. Reports success only when the mount reports the site that was sent; a refusal, a
    /// partial write and a read-back that differs are failures with their own sentence. Tried once: nothing is retried.
    /// </summary>
    public static async Task<SiteWriteOutcome> SendToMountAsync(IMountSiteControl mount, ObservingSite site, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(site);
        if (mount.Capabilities.Value is not { } capabilities || mount.ConnectionState != Sidera.Core.Devices.DeviceConnectionState.Connected)
        {
            return new SiteWriteOutcome(false, "The mount is not connected.", mount.Site);
        }

        if (!capabilities.HasSite || capabilities.SiteWrite == MountSiteWriteSupport.NotSupported)
        {
            return new SiteWriteOutcome(false, $"{mount.Name} does not take a site from Sidera.", mount.Site);
        }

        try
        {
            await mount.SetSiteAsync(site, cancellationToken).ConfigureAwait(false);
        }
        catch (MountSiteWriteException ex)
        {
            return new SiteWriteOutcome(false, ex.Message, mount.Site);
        }

        var readBack = mount.Site;
        if (readBack is null)
        {
            return new SiteWriteOutcome(false, "The site was written, but the mount does not report it back, so it could not be verified.", null);
        }

        var differences = new List<string>();
        if (Math.Abs(readBack.LatitudeDegrees - site.LatitudeDegrees) > ReadBackToleranceDegrees)
        {
            differences.Add("latitude");
        }

        if (Math.Abs(readBack.LongitudeDegrees - site.LongitudeDegrees) > ReadBackToleranceDegrees)
        {
            differences.Add("longitude");
        }

        if (Math.Abs(readBack.ElevationMeters - site.ElevationMeters) > ReadBackToleranceMeters)
        {
            differences.Add("elevation");
        }

        return differences.Count == 0
            ? new SiteWriteOutcome(true, null, readBack)
            : new SiteWriteOutcome(
                false,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The mount reports a different {string.Join(" and ", differences)} than the one that was sent ({GeoCoordinateFormat.FormatLatitude(readBack.LatitudeDegrees)}, {GeoCoordinateFormat.FormatLongitude(readBack.LongitudeDegrees)}, {GeoCoordinateFormat.FormatElevation(readBack.ElevationMeters)}). It did not take the site."),
                readBack);
    }
}

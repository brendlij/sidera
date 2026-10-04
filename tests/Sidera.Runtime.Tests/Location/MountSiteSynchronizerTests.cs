using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Location;

namespace Sidera.Runtime.Tests.Location;

public sealed class MountSiteSynchronizerTests
{
    private static readonly ObservingSite Home = new(47.7192, 7.8231, 410, "Home Observatory");

    private static async Task<SimulatedMount> ConnectedMountAsync(MountSite? site = null, bool writable = true, string? refuse = null, double resolution = 0)
    {
        var mount = new SimulatedMount(new DeviceId("mount.sim"), slewDuration: TimeSpan.FromMilliseconds(20))
        {
            SimulatedSite = site ?? new MountSite(50.1, 8.6, 120),
            SiteWritable = writable,
            RefuseSiteProperty = refuse,
            SiteResolutionDegrees = resolution,
        };
        await mount.ConnectAsync();
        return mount;
    }

    // ---- What is shown when

    [Fact]
    public void MatchingSites_AreInSync_AndNothingIsOffered()
    {
        var a = MountSiteSynchronizer.Assess(Home, new MountSite(47.7192, 7.8231, 410), MountSiteWriteSupport.Supported);

        Assert.Equal(SiteSituation.InSync, a.Situation);
        Assert.True(a.Comparison!.IsEquivalent);
    }

    [Fact]
    public void ARoundingOfAFewMeters_IsNotAMismatch()
    {
        var a = MountSiteSynchronizer.Assess(Home, new MountSite(47.71925, 7.82316, 412), MountSiteWriteSupport.Supported);

        Assert.Equal(SiteSituation.InSync, a.Situation);
    }

    [Fact]
    public void DifferentSites_AreAMismatch_WithBothPlacesAndTheComparison()
    {
        var a = MountSiteSynchronizer.Assess(Home, new MountSite(48.1372, 11.5756, 520), MountSiteWriteSupport.Supported);

        Assert.Equal(SiteSituation.Mismatch, a.Situation);
        Assert.Equal(Home, a.Sidera);
        Assert.Equal(48.1372, a.Mount!.LatitudeDegrees);
        Assert.Equal(SiteMismatchSeverity.Major, a.Comparison!.Severity);
        Assert.True(a.CanSendToMount);
    }

    [Fact]
    public void AMountThatOwnsItsLocation_IsNotOfferedASite()
    {
        var a = MountSiteSynchronizer.Assess(Home, new MountSite(48.1372, 11.5756, 520), MountSiteWriteSupport.NotSupported);

        Assert.Equal(SiteSituation.Mismatch, a.Situation);
        Assert.False(a.CanSendToMount);
    }

    [Fact]
    public void AMountWithoutASite_IsNeverComparedWithZero()
    {
        foreach (var site in new MountSite?[] { null, new MountSite(0, 0, 0) })
        {
            var a = MountSiteSynchronizer.Assess(Home, site, MountSiteWriteSupport.Unknown);

            Assert.Equal(SiteSituation.MountHasNoSite, a.Situation);
            Assert.Null(a.Comparison);
            Assert.True(a.CanSendToMount); // offered, never done by itself
        }
    }

    [Fact]
    public void WithoutASiteInSidera_TheMountsIsOffered_NotTaken()
    {
        var a = MountSiteSynchronizer.Assess(null, new MountSite(48.1372, 11.5756, 520), MountSiteWriteSupport.Supported);

        Assert.Equal(SiteSituation.MountOffersSite, a.Situation);
        Assert.Null(a.Sidera);
        Assert.False(a.CanSendToMount);
    }

    [Fact]
    public void NoSiteAnywhere_IsNothingToSay()
    {
        Assert.Equal(SiteSituation.NoSiteAnywhere, MountSiteSynchronizer.Assess(null, null, MountSiteWriteSupport.Supported).Situation);
        Assert.Equal(SiteSituation.NoSiteAnywhere, MountSiteSynchronizer.Assess(null, new MountSite(0, 0, 0), MountSiteWriteSupport.Supported).Situation);
    }

    // ---- Mount to Sidera

    [Fact]
    public void UsingTheMountsLocation_KeepsTheNameOfTheSite_AndTheValuesOfTheMount()
    {
        var adopted = MountSiteSynchronizer.AdoptMountSite(new MountSite(48.1372, 11.5756, 520), Home)!;

        Assert.Equal("Home Observatory", adopted.Name);
        Assert.Equal((48.1372, 11.5756, 520), (adopted.LatitudeDegrees, adopted.LongitudeDegrees, adopted.ElevationMeters));
        Assert.Null(MountSiteSynchronizer.AdoptMountSite(new MountSite(48.1372, 11.5756, 520), null)!.Name);
    }

    [Fact]
    public void AMountSiteThatIsNotAPlace_IsNotAdopted()
    {
        Assert.Null(MountSiteSynchronizer.AdoptMountSite(new MountSite(95, 11, 520), Home));
        Assert.Null(MountSiteSynchronizer.AdoptMountSite(new MountSite(0, 0, 0), Home));
    }

    [Fact]
    public void TheWestSignOfAMountSiteSurvivesAdoption()
    {
        var adopted = MountSiteSynchronizer.AdoptMountSite(new MountSite(37.77, -122.42, 16), null)!;

        Assert.Equal(-122.42, adopted.LongitudeDegrees);
        Assert.Equal("122.4200° W", GeoCoordinateFormat.FormatLongitude(adopted.LongitudeDegrees));
    }

    // ---- Sidera to mount

    [Fact]
    public async Task SendingTheSite_WritesItReadsItBack_AndReportsSuccessOnlyWhenTheMountHoldsIt()
    {
        var mount = await ConnectedMountAsync();

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(new MountSite(47.7192, 7.8231, 410), mount.Site);
        Assert.Equal(mount.Site, outcome.ReadBack);
    }

    [Fact]
    public async Task ASiteWestOfGreenwich_IsWrittenWithTheSameSign()
    {
        var mount = await ConnectedMountAsync();

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, new ObservingSite(37.77, -122.42, 16));

        Assert.True(outcome.Succeeded, outcome.Problem);
        Assert.Equal(-122.42, mount.Site!.LongitudeDegrees);
    }

    [Fact]
    public async Task AMountThatDoesNotTakeASite_IsNotWritten_AndSaysSo()
    {
        var mount = await ConnectedMountAsync(writable: false);

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.False(outcome.Succeeded);
        Assert.Contains("does not take a site", outcome.Problem);
        Assert.Equal(new MountSite(50.1, 8.6, 120), mount.Site);
        Assert.Equal(MountSiteWriteSupport.NotSupported, mount.Capabilities.Value!.SiteWrite);
    }

    [Fact]
    public async Task APartialRefusal_IsAFailure_ThatSaysWhatTheMountHoldsNow()
    {
        var mount = await ConnectedMountAsync(refuse: "longitude");

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.False(outcome.Succeeded);
        Assert.Contains("longitude", outcome.Problem);
        // The latitude was written before the refusal, and the read-back shows the mix instead of hiding it.
        Assert.Equal(new MountSite(47.7192, 8.6, 120), outcome.ReadBack);
    }

    [Fact]
    public async Task AMountThatStoresTheSiteTooCoarsely_IsNotReportedAsSynchronized()
    {
        var mount = await ConnectedMountAsync(resolution: 0.1);

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.False(outcome.Succeeded);
        Assert.Contains("different", outcome.Problem);
    }

    [Fact]
    public async Task AMountThatRoundsToAnArcsecond_IsStillVerified()
    {
        var mount = await ConnectedMountAsync(resolution: 1.0 / 3600);

        Assert.True((await MountSiteSynchronizer.SendToMountAsync(mount, Home)).Succeeded);
    }

    [Fact]
    public async Task AMountThatIsNotConnected_CannotBeWritten()
    {
        var mount = new SimulatedMount(new DeviceId("mount.sim"));

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.False(outcome.Succeeded);
        Assert.Contains("not connected", outcome.Problem);
    }

    [Fact]
    public async Task TheSimulatedMountCountsTheOldSite_UntilItIsWritten()
    {
        var mount = await ConnectedMountAsync();

        Assert.Equal(new MountSite(50.1, 8.6, 120), mount.Site);
        await mount.SetSiteAsync(Home);
        Assert.Equal(47.7192, mount.Site!.LatitudeDegrees);
    }
}

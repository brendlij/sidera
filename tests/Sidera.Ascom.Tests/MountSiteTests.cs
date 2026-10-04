using Sidera.Ascom.Infrastructure;
using Sidera.Ascom.Mounts;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Runtime.Location;

namespace Sidera.Ascom.Tests;

/// <summary>Writing the observing site to an ASCOM mount: the signs of the standard, one property at a time, read back, and refusals that are limits and not crashes.</summary>
public class MountSiteTests
{
    private static readonly ObservingSite Home = new(47.7192, 7.8231, 410);

    private static async Task<(AscomMount Mount, FakeMountDriver Driver)> ConnectedAsync(Action<FakeMountDriver>? configure = null)
    {
        FakeMountDriver? driver = null;
        var drivers = new FakeDriverFactory(new CallLog()) { ConfigureMount = d => { driver = d; configure?.Invoke(d); } };
        var mount = new AscomMount(new DeviceId("mount.site"), "Site Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        await mount.ConnectAsync();
        return (mount, driver!);
    }

    [Fact]
    public async Task AMountThatReportsASite_Is_OfferedTheWriteWithoutBeingSureOfIt()
    {
        var (mount, _) = await ConnectedAsync();

        Assert.True(mount.Capabilities.Value!.HasSite);
        Assert.Equal(MountSiteWriteSupport.Unknown, mount.Capabilities.Value.SiteWrite); // ASCOM has no flag: only a write finds out
    }

    [Fact]
    public async Task AMountWithoutASite_IsNotOfferedAWrite()
    {
        var (mount, _) = await ConnectedAsync(d => d.HasSiteValue = false);

        Assert.Equal(MountSiteWriteSupport.NotSupported, mount.Capabilities.Value!.SiteWrite);
    }

    [Fact]
    public async Task TheSiteIsWrittenWithTheSignsOfTheStandard_AndReadBack()
    {
        var (mount, driver) = await ConnectedAsync();

        await mount.SetSiteAsync(new ObservingSite(-33.8688, -122.4194, 16));

        // South and west are negative in ASCOM exactly as in Sidera: nothing is inverted.
        Assert.Equal((-33.8688, -122.4194, 16.0), (driver.SiteLatitudeValue, driver.SiteLongitudeValue, driver.SiteElevationValue));
        Assert.Equal(new MountSite(-33.8688, -122.4194, 16), mount.Site);
    }

    [Fact]
    public async Task NothingIsWrittenByConnecting()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log);
        var mount = new AscomMount(new DeviceId("mount.site"), "Site Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());

        await mount.ConnectAsync();

        Assert.DoesNotContain(log.Names, n => n.StartsWith("SiteLatitude =") || n.StartsWith("SiteLongitude =") || n.StartsWith("SiteElevation ="));
    }

    [Fact]
    public async Task ADriverThatDoesNotImplementTheSetter_IsALimit_NotACrash_AndTheCapabilityBecomesNotSupported()
    {
        var (mount, driver) = await ConnectedAsync(d => d.RefuseSiteWrite = "SiteLatitude");

        var ex = await Assert.ThrowsAsync<MountSiteWriteException>(() => mount.SetSiteAsync(Home));

        Assert.Contains("does not take a site", ex.Message);
        Assert.Equal(MountSiteWriteSupport.NotSupported, mount.Capabilities.Value!.SiteWrite);
        Assert.Equal(50.1, driver.SiteLatitudeValue); // nothing was changed
        Assert.Equal(50.1, mount.Site!.LatitudeDegrees);
    }

    [Fact]
    public async Task AWriteThatFailsInTheMiddle_SaysWhichValue_AndShowsWhatTheMountHoldsNow()
    {
        var (mount, _) = await ConnectedAsync(d => d.RefuseSiteWrite = "SiteLongitude");

        var outcome = await MountSiteSynchronizer.SendToMountAsync(mount, Home);

        Assert.False(outcome.Succeeded);
        Assert.Contains("longitude", outcome.Problem);
        Assert.Equal(new MountSite(47.7192, 8.6, 120), outcome.ReadBack); // the latitude was taken, the rest was not
    }

    [Fact]
    public async Task ARefusalIsNotRetried()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureMount = d => d.RefuseSiteWrite = "SiteLatitude" };
        var mount = new AscomMount(new DeviceId("mount.site"), "Site Mount", "ASCOM.Test.Telescope", drivers, null, null, FastTimings.Create());
        await mount.ConnectAsync();

        await Assert.ThrowsAsync<MountSiteWriteException>(() => mount.SetSiteAsync(Home));

        Assert.Equal(1, log.Names.Count(n => n.StartsWith("SiteLatitude =")));
        Assert.DoesNotContain(log.Names, n => n.StartsWith("SiteLongitude ="));
    }

    [Fact]
    public async Task AfterANotSupportedAnswer_NoFurtherWriteIsAttempted()
    {
        var (mount, _) = await ConnectedAsync(d => d.RefuseSiteWrite = "SiteLatitude");
        await Assert.ThrowsAsync<MountSiteWriteException>(() => mount.SetSiteAsync(Home));

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => mount.SetSiteAsync(Home));
    }
}

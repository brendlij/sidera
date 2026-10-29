using Sidera.Core.Astrometry;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Astrometry;

/// <summary>A catalog position is for J2000; the sky and a mount work with the position of date. The meridian flip counts with the latter.</summary>
public class PrecessionTests
{
    private static readonly DateTime Date = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void M15_IsAboutAMinuteAndAHalfLaterInRightAscensionOfDate_ThanInTheCatalog()
    {
        // M15 is 21h 29m 58s, +12 deg 10 min in J2000; a planetarium shows 21h 31m 17s for the date.
        var ofDate = SkyMath.RightAscensionOfDateHours(21.49954, 12.1670, Date);

        Assert.Equal(21 + 31.0 / 60 + 17.0 / 3600, ofDate, 3);
    }

    [Fact]
    public void AtTheEquinoxJ2000_NothingChanges()
    {
        Assert.Equal(5.5881, SkyMath.RightAscensionOfDateHours(5.5881, -5.39, new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)), 6);
    }

    [Theory]
    [InlineData(0.1, 80)]
    [InlineData(23.95, -60)]
    [InlineData(12, 0)]
    [InlineData(18.6, 38.8)]
    public void TheInverse_GivesTheCatalogPositionBack_AndTheWrapAt24HoursHolds(double ra, double dec)
    {
        var ofDate = SkyMath.RightAscensionOfDateHours(ra, dec, Date);

        var back = SkyMath.RightAscensionJ2000FromDateHours(ofDate, dec, Date);

        Assert.Equal(ra, back, 3);
        Assert.InRange(ofDate, 0, 24);
    }

    [Fact]
    public void TheHourAngleOfAJ2000Target_IsTheOneOfDate_NotOfTheCatalog()
    {
        const double longitude = 11.5817;
        var j2000 = MeridianFlipTiming.HourAngleHours(21.49954, Date, longitude);
        var ofDate = MeridianFlipTiming.HourAngleHoursOfJ2000(21.49954, 12.1670, Date, longitude);

        // About 78 seconds of time: the target is that much closer to the meridian than the catalog position says.
        Assert.InRange((j2000 - ofDate) * 3600, 70, 86);
    }
}

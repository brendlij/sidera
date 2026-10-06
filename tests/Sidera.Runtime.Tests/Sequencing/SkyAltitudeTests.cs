using Sidera.Core.Astronomy;
using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>The altitude of the Sun and of a target, and the times the Sun crosses twilight altitudes: against what geometry and the almanac say.</summary>
public sealed class SkyAltitudeTests
{
    private static readonly ObservingSite Frankfurt = new(50.1, 8.6, 120);
    private static readonly ObservingSite Greenwich = new(51.4779, 0, 45);

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---- a target

    [Theory]
    [InlineData(20, 59.9)] // transit: 90 - |lat - dec|
    [InlineData(50.1, 90)] // the zenith
    [InlineData(-30, 9.9)]
    [InlineData(80, 60.1)] // north of the zenith: 90 - (dec - lat)
    public void ATargetOnTheMeridian_IsAt90MinusTheDifferenceOfLatitudeAndDeclination(double declination, double expected)
    {
        var when = Utc(2026, 3, 1, 22);
        var ra = MeridianFlipTiming.LocalSiderealTimeHours(when, Frankfurt.LongitudeDegrees);

        Assert.Equal(expected, SkyAltitude.TargetDegrees(ra, declination, when, Frankfurt), 1e-6);
    }

    [Fact]
    public void ATargetRises_Transits_AndSets()
    {
        var transit = Utc(2026, 3, 1, 22);
        var ra = MeridianFlipTiming.LocalSiderealTimeHours(transit, Frankfurt.LongitudeDegrees);
        double At(double hours) => SkyAltitude.TargetDegrees(ra, 0, transit.AddHours(hours * 0.9972695663), Frankfurt);

        Assert.InRange(At(-6), -0.001, 0.001); // on the celestial equator, six hours before the meridian: on the horizon, in the east
        Assert.InRange(At(6), -0.001, 0.001);
        Assert.True(At(-3) > 0 && At(-3) < At(0)); // rising
        Assert.True(At(3) > 0 && At(3) < At(0)); // setting
        Assert.True(At(-9) < 0 && At(9) < 0); // below the horizon
        Assert.Equal(At(-3), At(3), 1e-6); // symmetric around the meridian
    }

    [Fact]
    public void AnHourAngleThatWrapsPastMidnightOfRightAscension_IsTheSameAsOneThatDoesNot()
    {
        var site = new ObservingSite(50, 0, 0);
        var lst = MeridianFlipTiming.LocalSiderealTimeHours(Utc(2026, 5, 1, 12), 0);

        // RA 23.9 h with the local sidereal time 0.1 h and RA 0.1 h with 23.9 h are mirror images: the same altitude.
        var east = SkyAltitude.TargetDegrees((lst + 24 - 0.2) % 24, 30, Utc(2026, 5, 1, 12), site);
        var west = SkyAltitude.TargetDegrees((lst + 0.2) % 24, 30, Utc(2026, 5, 1, 12), site);

        Assert.Equal(east, west, 1e-9);
        // and a point whose right ascension is just below 24 while the sidereal time has just passed 0:
        Assert.InRange(SkyAltitude.TargetDegrees(23.9, 30, TimeAtSiderealTime(0.1, site), site), 0, 90);
    }

    private static DateTime TimeAtSiderealTime(double hours, ObservingSite site)
    {
        // The first time on 2026-05-01 that the local sidereal time reads hours, to a minute.
        var t = Utc(2026, 5, 1);
        while (Math.Abs(MeridianFlipTiming.NormalizeHours(MeridianFlipTiming.LocalSiderealTimeHours(t, site.LongitudeDegrees) - hours)) > 0.002)
        {
            t = t.AddSeconds(5);
        }

        return t;
    }

    [Fact]
    public void ANearPolarTarget_StaysNearTheLatitudeAllDay()
    {
        var start = Utc(2026, 3, 1);
        for (var minute = 0; minute < 24 * 60; minute += 20)
        {
            var altitude = SkyAltitude.TargetDegrees(2.5, 89.2, start.AddMinutes(minute), Frankfurt);

            Assert.InRange(altitude, 50.1 - 0.8 - 1e-6, 50.1 + 0.8 + 1e-6); // within the distance to the pole of the latitude
        }
    }

    // ---- the Sun

    [Fact]
    public void TheSun_StandsAlmostInTheZenithAtNoon_OnTheEquatorAtTheEquinox()
    {
        // 2024-03-20, the equinox at 03:06 UTC: noon on the equator at the Greenwich meridian is within a minute of 12:07 (equation of time -7 min).
        var altitude = SkyAltitude.SunDegrees(Utc(2024, 3, 20, 12, 7), new ObservingSite(0, 0, 0));

        Assert.InRange(altitude, 89.5, 90);
    }

    [Fact]
    public void TheSun_StandsAlmostInTheZenithAtTheTropicOfCancer_AtTheSolstice()
    {
        // 2024-06-20, a day before: the declination is 23.44° and noon at the meridian is about 12:01 UTC.
        var altitude = SkyAltitude.SunDegrees(Utc(2024, 6, 20, 12, 1), new ObservingSite(23.44, 0, 0));

        Assert.InRange(altitude, 89.8, 90);
    }

    [Fact]
    public void TheMidnightSun_IsAboveTheHorizonAtMidnight_AtSeventyNorth()
    {
        // lat + dec - 90 = 70 + 23.44 - 90 = 3.44° at local midnight (the Sun is below the pole, at 00:00 UTC on the Greenwich meridian: solar midnight is about 00:02).
        var altitude = SkyAltitude.SunDegrees(Utc(2024, 6, 21, 0, 2), new ObservingSite(70, 0, 0));

        Assert.InRange(altitude, 3.2, 3.7);
    }

    [Fact]
    public void SunriseAtGreenwich_OnTheEquinox_IsWhereTheAlmanacHasIt()
    {
        // London, 2024-03-20: the Sun's upper limb rises at about 06:03 UTC and sets at about 18:14 (centre at -0.833°).
        var rise = SunCrossings.Next(Greenwich, Utc(2024, 3, 20), -0.833, rising: true);
        var set = SunCrossings.Next(Greenwich, Utc(2024, 3, 20, 12), -0.833, rising: false);

        Assert.True(rise.IsFound && set.IsFound);
        Assert.InRange(Math.Abs((rise.Utc!.Value - Utc(2024, 3, 20, 6, 3)).TotalMinutes), 0, 4);
        Assert.InRange(Math.Abs((set.Utc!.Value - Utc(2024, 3, 20, 18, 14)).TotalMinutes), 0, 4);
    }

    [Fact]
    public void TheTwilights_ComeInOrder_InTheEvening_AndInTheMorning()
    {
        var from = Utc(2026, 3, 1, 12);

        var civil = SunCrossings.NextDusk(Frankfurt, from, Twilight.Civil).Utc!.Value;
        var nautical = SunCrossings.NextDusk(Frankfurt, from, Twilight.Nautical).Utc!.Value;
        var astronomical = SunCrossings.NextDusk(Frankfurt, from, Twilight.Astronomical).Utc!.Value;
        Assert.True(civil < nautical && nautical < astronomical);

        var dawnFrom = astronomical;
        var astronomicalDawn = SunCrossings.NextDawn(Frankfurt, dawnFrom, Twilight.Astronomical).Utc!.Value;
        var nauticalDawn = SunCrossings.NextDawn(Frankfurt, dawnFrom, Twilight.Nautical).Utc!.Value;
        var civilDawn = SunCrossings.NextDawn(Frankfurt, dawnFrom, Twilight.Civil).Utc!.Value;
        Assert.True(astronomicalDawn < nauticalDawn && nauticalDawn < civilDawn);

        // in March at 50°N astronomical darkness lasts about 9 to 10 hours (a day is about 11 hours)
        Assert.InRange((astronomicalDawn - astronomical).TotalHours, 8, 11);
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(-12)]
    [InlineData(-18)]
    public void TheSun_IsExactlyAtTheThreshold_AtTheTimeOfACrossing(double threshold)
    {
        var from = Utc(2026, 3, 1, 12);

        var dusk = SunCrossings.Next(Frankfurt, from, threshold, rising: false).Utc!.Value;
        var dawn = SunCrossings.Next(Frankfurt, dusk, threshold, rising: true).Utc!.Value;

        Assert.Equal(threshold, SkyAltitude.SunDegrees(dusk, Frankfurt), 0.02); // to a second of time: a quarter of a degree in a minute
        Assert.Equal(threshold, SkyAltitude.SunDegrees(dawn, Frankfurt), 0.02);
        Assert.True(SkyAltitude.SunDegrees(dusk.AddMinutes(10), Frankfurt) < threshold); // going down
        Assert.True(SkyAltitude.SunDegrees(dawn.AddMinutes(10), Frankfurt) > threshold); // coming up
    }

    [Fact]
    public void TheEveningCrossingAndTheMorningCrossing_AreNotTheSameEvent()
    {
        // At noon the next downward crossing is this evening, the next upward crossing is tomorrow morning: a day does not "dawn" at noon.
        var noon = Utc(2026, 3, 1, 12);
        var dusk = SunCrossings.NextDusk(Frankfurt, noon, Twilight.Astronomical).Utc!.Value;
        var dawn = SunCrossings.NextDawn(Frankfurt, noon, Twilight.Astronomical).Utc!.Value;

        Assert.True(dusk < dawn);
        Assert.InRange((dusk - noon).TotalHours, 5, 9);
        Assert.InRange((dawn - noon).TotalHours, 14, 22);
    }

    [Fact]
    public void WhereTheSunNeverGetsThatLow_ThereIsNoDusk_AndNoTimeIsMadeUp()
    {
        // 55°N at midsummer: the Sun's lowest point is 55 + 23.4 - 90 = -11.6°, so there is no astronomical (-18°) darkness; and nautical (-12°) does not happen either.
        var site = new ObservingSite(55, 10, 0);
        var from = Utc(2026, 6, 21, 12);

        var astronomical = SunCrossings.NextDusk(site, from, Twilight.Astronomical);
        var civil = SunCrossings.NextDusk(site, from, Twilight.Civil);

        Assert.Equal(SunCrossingState.AlwaysAbove, astronomical.State);
        Assert.Null(astronomical.Utc);
        Assert.True(civil.IsFound); // -6° is reached
    }

    [Fact]
    public void InThePolarDay_TheSunNeverGoesBelow_AndInThePolarNight_ItNeverComesUp()
    {
        var north = new ObservingSite(75, 15, 0);

        Assert.Equal(SunCrossingState.AlwaysAbove, SunCrossings.NextDusk(north, Utc(2026, 6, 21), Twilight.Civil).State);
        Assert.Equal(SunCrossingState.AlwaysBelow, SunCrossings.NextDawn(north, Utc(2026, 12, 21), Twilight.Civil).State); // -8.4° at noon: civil daylight does not come
        Assert.True(SunCrossings.NextDawn(north, Utc(2026, 12, 21), Twilight.Nautical).IsFound); // but nautical does
    }
}

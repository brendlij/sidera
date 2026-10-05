using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Mounts;

/// <summary>The sky side of a meridian flip: hour angle with its wrap, the phases of the settings, and the guard that decides whether an exposure may start. No clock is waited for.</summary>
public class MeridianFlipTimingTests
{
    private static readonly MeridianFlipSettings Settings = new() { Enabled = true, PauseBeforeMeridianMinutes = 5, FlipAfterMeridianMinutes = 2, LatestAllowedFlipMinutes = 15 };

    [Fact]
    public void TheLocalSiderealTime_AtJ2000OnTheGreenwichMeridian_IsTheGmstOfThatMoment()
    {
        var lst = MeridianFlipTiming.LocalSiderealTimeHours(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc), 0);

        Assert.Equal(18.697374558, lst, 6);
    }

    [Fact]
    public void ALongitudeEastOfGreenwich_AdvancesTheLocalSiderealTime_AtFifteenDegreesAnHour()
    {
        var utc = new DateTime(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc);
        var greenwich = MeridianFlipTiming.LocalSiderealTimeHours(utc, 0);
        var east = MeridianFlipTiming.LocalSiderealTimeHours(utc, 15);
        var west = MeridianFlipTiming.LocalSiderealTimeHours(utc, -15);

        Assert.Equal(1, MeridianFlipTiming.NormalizeHours(east - greenwich), 9);
        Assert.Equal(-1, MeridianFlipTiming.NormalizeHours(west - greenwich), 9);
        Assert.InRange(greenwich, 0, 24);
    }

    [Theory]
    [InlineData(10.0, 10.0, 0.0)] // exactly on the meridian
    [InlineData(10.0, 11.0, 1.0)] // west of it: it has crossed
    [InlineData(10.0, 9.0, -1.0)] // east of it: it will cross
    [InlineData(23.5, 0.5, 1.0)] // right ascension just before 24 h, sidereal time just after 0 h: wraps forward
    [InlineData(0.5, 23.5, -1.0)] // and the other way
    [InlineData(1.0, 13.0, 12.0)] // opposite: the boundary is +12, not -12
    [InlineData(13.0, 1.0, 12.0)]
    [InlineData(0.0, 23.9, -0.1)] // midnight wrap
    public void TheHourAngle_IsNormalizedAcrossTheWrap(double ra, double lst, double expected)
    {
        Assert.Equal(expected, MeridianFlipTiming.HourAngleHours(ra, lst), 9);
    }

    [Theory]
    [InlineData(-60.0, MeridianFlipPhase.Monitoring)]
    [InlineData(-5.01, MeridianFlipPhase.Monitoring)]
    [InlineData(-5.0, MeridianFlipPhase.Approaching)] // entering the guard
    [InlineData(0.0, MeridianFlipPhase.Approaching)] // exactly on the meridian: not due yet, the flip waits for the minutes after it
    [InlineData(1.99, MeridianFlipPhase.Approaching)]
    [InlineData(2.0, MeridianFlipPhase.FlipDue)] // the flip is due
    [InlineData(15.0, MeridianFlipPhase.FlipDue)]
    [InlineData(15.01, MeridianFlipPhase.Overdue)] // the latest allowed flip has passed
    [InlineData(180.0, MeridianFlipPhase.Overdue)]
    public void ThePhase_FollowsTheMinutesBeforeAndAfterTheMeridian(double hourAngleMinutes, MeridianFlipPhase expected)
    {
        Assert.Equal(expected, MeridianFlipTiming.PhaseOf(Settings, hourAngleMinutes));
    }

    [Fact]
    public void WithAFlipAfterOfZero_TheFlipIsDueOnTheMeridian()
    {
        var settings = Settings with { FlipAfterMeridianMinutes = 0 };

        Assert.Equal(MeridianFlipPhase.FlipDue, MeridianFlipTiming.PhaseOf(settings, 0));
        Assert.Equal(MeridianFlipPhase.Approaching, MeridianFlipTiming.PhaseOf(settings, -0.01));
    }

    [Theory]
    [InlineData(-2.0, 300.0, false)] // the guard: meridian in 2 minutes, 300 s would end 3 minutes past it, after the flip is due
    [InlineData(-2.0, 240.0, true)] // ends at +2.0: exactly when the flip is due
    [InlineData(-2.0, 241.0, false)] // one second too long
    [InlineData(-2.0, 60.0, true)] // a short exposure that ends before the flip
    [InlineData(-5.0, 420.0, true)] // at the start of the guard: ends at +2.0
    [InlineData(-5.0, 421.0, false)]
    [InlineData(-60.0, 300.0, true)] // far before: free
    [InlineData(-6.0, 300.0, true)] // before the guard: ends at -1, fine
    [InlineData(-6.0, 1260.0, true)] // before the guard it only has to end before the latest allowed flip: ends at +15
    [InlineData(-6.0, 1261.0, false)]
    [InlineData(2.0, 1.0, false)] // the flip is due: nothing new starts
    [InlineData(20.0, 1.0, false)] // overdue: nothing new starts either
    public void AnExposure_StartsOnlyWhereItFitsBeforeTheFlip(double hourAngleMinutes, double exposureSeconds, bool allowed)
    {
        Assert.Equal(allowed, MeridianFlipTiming.CanStartExposure(Settings, hourAngleMinutes, exposureSeconds));
    }

    [Fact]
    public void TheMinutesUntilTheGuardAndTheMeridian_CountDown()
    {
        Assert.Equal(7, MeridianFlipTiming.MinutesUntilGuard(Settings, -12));
        Assert.Equal(-1, MeridianFlipTiming.MinutesUntilGuard(Settings, -4));
        Assert.Equal(12, MeridianFlipTiming.MinutesUntilMeridian(-12));
        Assert.Equal(-3, MeridianFlipTiming.MinutesUntilMeridian(3));
    }

    [Fact]
    public void ADisabledPolicy_IsNotChecked_AndAnEnabledOneSaysWhatIsWrong()
    {
        Assert.Empty((Settings with { Enabled = false, LatestAllowedFlipMinutes = -1 }).Problems());

        var bad = Settings with { LatestAllowedFlipMinutes = 1, MaxFlipAttempts = 0, FinishCurrentExposure = false, PauseBeforeMeridianMinutes = -3 };
        var problems = bad.Problems();

        Assert.Contains(problems, p => p.Contains("Pause before the meridian", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("latest allowed flip cannot be earlier", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("flip attempts", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("Aborting an exposure", StringComparison.Ordinal));
        Assert.Empty(Settings.Problems());
    }
}

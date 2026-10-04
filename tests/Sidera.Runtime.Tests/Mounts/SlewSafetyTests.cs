using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Mounts;

public sealed class SlewSafetyTests
{
    [Fact]
    public void TheSeparationOfAPointFromItself_IsZero() =>
        Assert.Equal(0, SlewSafety.SeparationDegrees(new CelestialCoordinates(5.5, 20), new CelestialCoordinates(5.5, 20)), 9);

    [Fact]
    public void OneHourOfRightAscensionOnTheEquator_IsFifteenDegrees() =>
        Assert.Equal(15, SlewSafety.SeparationDegrees(new CelestialCoordinates(3, 0), new CelestialCoordinates(4, 0)), 9);

    [Fact]
    public void TheWrapAroundOfRightAscension_IsTheShortWay() =>
        Assert.Equal(3, SlewSafety.SeparationDegrees(new CelestialCoordinates(23.9, 0), new CelestialCoordinates(0.1, 0)), 9);

    [Fact]
    public void TheTwoPoles_AreOneEightyDegreesApart_WhateverTheRightAscension() =>
        Assert.Equal(180, SlewSafety.SeparationDegrees(new CelestialCoordinates(1, 90), new CelestialCoordinates(17, -90)), 9);

    [Fact]
    public void RightAscensionAtTheCelestialPole_BarelyMovesAnything() =>
        Assert.True(SlewSafety.SeparationDegrees(new CelestialCoordinates(0, 90), new CelestialCoordinates(12, 90)) < 1e-6);

    [Fact]
    public void ADegreeIsNotALargeSlew_AndFiveDegreesIs()
    {
        Assert.False(SlewSafety.IsLargeSlew(new CelestialCoordinates(5, 10), new CelestialCoordinates(5, 11)));
        Assert.True(SlewSafety.IsLargeSlew(new CelestialCoordinates(5, 10), new CelestialCoordinates(5, 15)));
    }

    [Fact]
    public void TheThreshold_CanBeChosen() =>
        Assert.True(SlewSafety.IsLargeSlew(new CelestialCoordinates(5, 10), new CelestialCoordinates(5, 11), thresholdDegrees: 0.5));
}

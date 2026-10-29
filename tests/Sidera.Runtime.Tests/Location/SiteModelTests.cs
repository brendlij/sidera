using Sidera.Core.Location;
using Sidera.Core.Mounts;

namespace Sidera.Runtime.Tests.Location;

public sealed class SiteModelTests
{
    // ---- ObservingSite: the model and its conventions

    [Fact]
    public void ASite_KeepsTheValuesAsSignedDegrees_AndAnOptionalName()
    {
        var site = new ObservingSite(48.1351, 11.5817, 240, "  Home Observatory ");

        Assert.Equal(48.1351, site.LatitudeDegrees);
        Assert.Equal(11.5817, site.LongitudeDegrees);
        Assert.Equal(240, site.ElevationMeters);
        Assert.Equal("Home Observatory", site.Name);
        Assert.Null(new ObservingSite(0, 0, 0, "   ").Name);
    }

    [Theory]
    [InlineData(90.0001)]
    [InlineData(-90.0001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ALatitudeOutOfRange_IsRefused_NotClamped(double latitude)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ObservingSite(latitude, 0, 0));
        Assert.False(ObservingSite.TryCreate(latitude, 0, 0, null, out var site, out var problem));
        Assert.Null(site);
        Assert.Contains("latitude", problem);
    }

    [Theory]
    [InlineData(180.0001)]
    [InlineData(-180.0001)]
    [InlineData(190)]
    [InlineData(double.NaN)]
    public void ALongitudeOutOfRange_IsRefused_NotWrapped(double longitude)
    {
        Assert.False(ObservingSite.TryCreate(0, longitude, 0, null, out _, out var problem));
        Assert.Contains("longitude", problem);
    }

    [Theory]
    [InlineData(-500.01)]
    [InlineData(9000.01)]
    [InlineData(double.NaN)]
    public void AnElevationOutOfRange_IsRefused(double elevation)
    {
        Assert.False(ObservingSite.TryCreate(0, 0, elevation, null, out _, out var problem));
        Assert.Contains("elevation", problem);
    }

    [Theory]
    [InlineData(90, 180, 9000)]
    [InlineData(-90, -180, -500)]
    public void TheLimitsThemselvesAreAllowed(double latitude, double longitude, double elevation)
    {
        Assert.True(ObservingSite.TryCreate(latitude, longitude, elevation, null, out _, out _));
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(540, -180)]
    [InlineData(12.5, 12.5)]
    public void WrappingALongitudeIsAnExplicitHelper(double input, double expected)
    {
        Assert.Equal(expected, ObservingSite.WrapLongitude(input), 9);
    }

    [Fact]
    public void AMountSiteOfZeroZero_OrOutOfRange_IsNotAPlace()
    {
        Assert.Null(new MountSite(0, 0, 0).ToObservingSite());
        Assert.Null(new MountSite(120, 8, 100).ToObservingSite());
        Assert.NotNull(new MountSite(0, 8, 100).ToObservingSite());
        Assert.NotNull(new MountSite(50.1, 8.6, 120).ToObservingSite());
    }

    // ---- Formatting and parsing: the signs are the whole convention

    [Theory]
    [InlineData(48.1351, "48.1351° N")]
    [InlineData(-33.8688, "33.8688° S")]
    [InlineData(0, "0.0000° N")]
    public void ALatitudeIsWrittenWithNorthPositive(double degrees, string expected) =>
        Assert.Equal(expected, GeoCoordinateFormat.FormatLatitude(degrees));

    [Theory]
    [InlineData(11.5817, "11.5817° E")]
    [InlineData(122.4194, "122.4194° E")]
    [InlineData(-122.4194, "122.4194° W")]
    [InlineData(-0.5, "0.5000° W")]
    public void ALongitudeIsWrittenWithEastPositiveAndWestNegative(double degrees, string expected) =>
        Assert.Equal(expected, GeoCoordinateFormat.FormatLongitude(degrees));

    [Theory]
    [InlineData("11.6", 11.6)]
    [InlineData("+11.6", 11.6)]
    [InlineData("11.6 E", 11.6)]
    [InlineData("11.6° E", 11.6)]
    [InlineData("E 11.6", 11.6)]
    [InlineData("-122.4", -122.4)]
    [InlineData("122.4 W", -122.4)]
    [InlineData("122.4° w", -122.4)]
    [InlineData("11,5817", 11.5817)]
    public void APlusSignOrEastIsEast_AMinusSignOrWestIsWest(string text, double expected)
    {
        Assert.True(GeoCoordinateFormat.TryParseLongitude(text, out var degrees, out var problem), problem);
        Assert.Equal(expected, degrees, 9);
    }

    [Theory]
    [InlineData("48.1351° N", 48.1351)]
    [InlineData("33.8688 S", -33.8688)]
    [InlineData("-33.8688", -33.8688)]
    [InlineData("S 33.8688", -33.8688)]
    public void ALatitudeIsReadWithNorthPositive(string text, double expected)
    {
        Assert.True(GeoCoordinateFormat.TryParseLatitude(text, out var degrees, out _));
        Assert.Equal(expected, degrees, 9);
    }

    [Theory]
    [InlineData("-122.4 W")]
    [InlineData("+11.6 W")]
    public void ASignTogetherWithALetter_IsRefused_InsteadOfGuessing(string text)
    {
        Assert.False(GeoCoordinateFormat.TryParseLongitude(text, out _, out var problem));
        Assert.Contains("sign and a letter", problem);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("abc", "number")]
    [InlineData("91", "-90")]
    [InlineData("48.1 E", "number")] // an E is no hemisphere of a latitude
    public void ALatitudeThatIsNotOne_IsRefusedWithAReason(string text, string expected)
    {
        Assert.False(GeoCoordinateFormat.TryParseLatitude(text, out _, out var problem));
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("181")]
    [InlineData("-181")]
    [InlineData("200 W")]
    public void ALongitudeOutOfRange_IsRefusedNotWrapped(string text)
    {
        Assert.False(GeoCoordinateFormat.TryParseLongitude(text, out _, out var problem));
        Assert.Contains("180", problem);
    }

    [Theory]
    [InlineData(48.1351)]
    [InlineData(-33.8688)]
    [InlineData(0)]
    [InlineData(89.9999)]
    public void ALatitudeSurvivesWritingAndReading(double degrees)
    {
        Assert.True(GeoCoordinateFormat.TryParseLatitude(GeoCoordinateFormat.FormatLatitude(degrees), out var back, out _));
        Assert.Equal(degrees, back, 4);
    }

    [Theory]
    [InlineData(11.5817)]
    [InlineData(-122.4194)]
    [InlineData(179.9999)]
    [InlineData(-0.0001)]
    public void ALongitudeSurvivesWritingAndReading(double degrees)
    {
        Assert.True(GeoCoordinateFormat.TryParseLongitude(GeoCoordinateFormat.FormatLongitude(degrees), out var back, out _));
        Assert.Equal(degrees, back, 4);
    }

    [Theory]
    [InlineData("240", 240)]
    [InlineData("240 m", 240)]
    [InlineData("-12.5m", -12.5)]
    public void AnElevationIsReadWithOrWithoutTheUnit(string text, double expected)
    {
        Assert.True(GeoCoordinateFormat.TryParseElevation(text, out var meters, out _));
        Assert.Equal(expected, meters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("high")]
    [InlineData("10000")]
    public void AnElevationThatIsNotOne_IsRefused(string text) =>
        Assert.False(GeoCoordinateFormat.TryParseElevation(text, out _, out var problem) || problem is null);

    // ---- Comparison

    private static ObservingSite Home => new(48.1351, 11.5817, 240);

    [Fact]
    public void TheSamePlace_IsEquivalent_AndNothingIsPrompted()
    {
        var comparison = SiteComparison.Compare(Home, new ObservingSite(48.1351, 11.5817, 240, "other name"));

        Assert.True(comparison.IsEquivalent);
        Assert.Equal(SiteMismatchSeverity.None, comparison.Severity);
        Assert.Equal(0, comparison.HorizontalDistanceMeters, 6);
    }

    [Fact]
    public void AFewMetersApart_IsTheSamePlace()
    {
        // 5 meters north and 3 meters of elevation: GPS noise.
        var comparison = SiteComparison.Compare(Home, new ObservingSite(48.1351 + 5 / MetersPerDegree, 11.5817, 243));

        Assert.True(comparison.IsEquivalent);
        Assert.InRange(comparison.HorizontalDistanceMeters, 4.9, 5.1);
        Assert.Equal(3, comparison.ElevationDifferenceMeters, 9);
    }

    private const double MetersPerDegree = 111_194.93;

    [Theory]
    [InlineData(99.0, true)]
    [InlineData(101.0, false)]
    public void TheHorizontalToleranceIs100Meters(double meters, bool equivalent)
    {
        var comparison = SiteComparison.Compare(Home, new ObservingSite(48.1351 + meters / MetersPerDegree, 11.5817, 240));

        Assert.Equal(equivalent, comparison.IsEquivalent);
        Assert.Equal(equivalent ? SiteMismatchSeverity.None : SiteMismatchSeverity.Minor, comparison.Severity);
    }

    [Theory]
    [InlineData(99.9, true)]
    [InlineData(100.0, false)] // the tolerance is exclusive
    [InlineData(100.1, false)]
    public void TheElevationToleranceIs100Meters(double difference, bool equivalent)
    {
        var comparison = SiteComparison.Compare(Home, new ObservingSite(48.1351, 11.5817, 240 + difference));

        Assert.Equal(equivalent, comparison.IsEquivalent);
    }

    [Fact]
    public void ADifferentCity_IsAMajorMismatch()
    {
        // Munich to Hamburg: about 610 km.
        var comparison = SiteComparison.Compare(Home, new ObservingSite(53.5511, 9.9937, 350));

        Assert.False(comparison.IsEquivalent);
        Assert.Equal(SiteMismatchSeverity.Major, comparison.Severity);
        Assert.InRange(comparison.HorizontalDistanceMeters, 580_000, 640_000);
        Assert.Equal(110, comparison.ElevationDifferenceMeters, 9);
    }

    [Fact]
    public void AnElevationOnly_Mismatch_IsReportedByItself()
    {
        var comparison = SiteComparison.Compare(Home, new ObservingSite(48.1351, 11.5817, 1040));

        Assert.False(comparison.IsEquivalent);
        Assert.Equal(SiteMismatchSeverity.Major, comparison.Severity);
        Assert.Equal(0, comparison.HorizontalDistanceMeters, 6);
        Assert.Equal(800, comparison.ElevationDifferenceMeters, 9);
    }

    [Fact]
    public void TheDistanceIsAGreatCircle_AcrossTheDateLineAndNotJustADifferenceOfDegrees()
    {
        // 0.2° of longitude apart across the antimeridian is about 22 km at the equator, not 359.8 degrees.
        var d = SiteComparison.DistanceMeters(0, 179.9, 0, -179.9);

        Assert.InRange(d, 22_000, 22_500);
    }

    [Fact]
    public void ADegreeOfLatitudeIsAbout111Kilometers() =>
        Assert.InRange(SiteComparison.DistanceMeters(10, 20, 11, 20), 111_000, 111_400);
}

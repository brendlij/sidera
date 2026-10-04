using System.Globalization;
using Sidera.Astap;
using Sidera.Core.Astrometry;
namespace Sidera.Astap.Tests;
public sealed class RealFormatTests
{
    [Fact]
    public void InstalledCliZeroExitWithNotEnoughStarsIsStillFailure()
    {
        var result = AstapResultParser.Parse(0, "PLTSOLVD=F\nERROR=Not enough stars.", 800, 600);
        Assert.False(result.Solved); Assert.Equal(PlateSolveFailure.NotEnoughStars, result.Failure);
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("PLTSOLVD=garbage")]
    [InlineData("PLTSOLVD=T\nCRVAL1=1\nCRVAL2=2\nCD1_1=.001\nCD1_2=.001\nCD2_1=.001\nCD2_2=.001")]
    public void SuccessfulExitWithInvalidOutputIsMalformed(string? ini) =>
        Assert.Equal(PlateSolveFailure.MalformedOutput, AstapResultParser.Parse(0, ini, 100, 100).Failure);
    [Fact]
    public void OfficialSuccessfulIniIsParsedInvariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var result = AstapResultParser.Parse(0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "official-success.ini")), 2329, 1761);
            Assert.True(result.Solved);
            Assert.Equal(154.63033992314939 / 15, result.Center!.RightAscensionHours, 9);
            Assert.Equal(22.039358425145043, result.Center.DeclinationDegrees, 9);
            Assert.Equal(-1.1900321176194073, result.RotationDegrees!.Value, 9);
            Assert.InRange(result.PixelScaleXArcsecPerPixel!.Value, 2.69, 2.70);
            Assert.Equal(PlateSolveParity.Normal, result.Parity);
            Assert.NotNull(result.Wcs);
            Assert.InRange(result.FieldOfViewYDegrees!.Value, 1.31, 1.32);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void InstalledCliImageErrorFixtureIsParsed() =>
        Assert.Equal(PlateSolveFailure.ImageError, AstapResultParser.Parse(16,
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-image-error.ini")), 1, 1).Failure);

    [Fact]
    public void InstalledCliSuccessfulImageHasOriginalScaleAndMirroredParity()
    {
        var result = AstapResultParser.Parse(0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-ic434-success.ini")), 6248, 4176);
        Assert.True(result.Solved);
        Assert.Equal(84.707252286204124 / 15, result.Center!.RightAscensionHours, 9);
        Assert.Equal(-2.3350364913615884, result.Center.DeclinationDegrees, 9);
        Assert.Equal(PlateSolveParity.Mirrored, result.Parity);
        Assert.InRange(result.PixelScaleXArcsecPerPixel!.Value, 2.74, 2.76);
        Assert.InRange(result.FieldOfViewYDegrees!.Value, 3.18, 3.20);
        Assert.Equal(-179.05058735189249, result.RotationDegrees!.Value, 8);
    }
}

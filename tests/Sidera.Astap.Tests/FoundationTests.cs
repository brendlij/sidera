using System.Buffers.Binary;
using System.Text;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Core.Mounts;

namespace Sidera.Astap.Tests;

public sealed class SkyMathTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 6, 0, 90)]
    [InlineData(23.9, 0, 0.1, 0, 3)]
    [InlineData(0, 90, 12, 90, 0)]
    public void Separation(double ra1, double dec1, double ra2, double dec2, double expected) =>
        Assert.Equal(expected, SkyMath.AngularSeparationDegrees(new(ra1, dec1), new(ra2, dec2)), 9);

    [Theory]
    [InlineData(5, 20, 0.2, -0.3)]
    [InlineData(23.999, 0, 0.05, 0.01)]
    [InlineData(10, 89.8, 0.02, -0.03)]
    [InlineData(3, -50, 0.000001, -0.000001)]
    public void TangentRoundTripAndCorrection(double ra, double dec, double xi, double eta)
    {
        var target = new CelestialCoordinates(ra, dec);
        var solved = SkyMath.FromTangentOffset(target, xi, eta);
        var offset = SkyMath.TangentOffsetDegrees(target, solved);
        Assert.Equal(xi, offset.Xi, 9);
        Assert.Equal(eta, offset.Eta, 9);
        var correction = SkyMath.CorrectedTarget(target, solved, target);
        var correctedOffset = SkyMath.TangentOffsetDegrees(target, correction);
        Assert.Equal(-xi, correctedOffset.Xi, 9);
        Assert.Equal(-eta, correctedOffset.Eta, 9);
    }
}

public sealed class DownsamplePlannerTests
{
    [Theory]
    [InlineData(800, 600, null, 1)]
    [InlineData(6248, 4176, null, 2)]
    [InlineData(6248, 4176, 3.0, 1)]
    [InlineData(12000, 8000, 0.5, 4)]
    [InlineData(30000, 600, null, 1)]
    public void AutoIsDeterministic(int width, int height, double? scale, int expected)
    {
        Assert.Equal(expected, DownsamplePlanner.Resolve(DownsamplePolicy.Auto, width, height, scale));
        Assert.Equal(expected, DownsamplePlanner.Resolve(DownsamplePolicy.Auto, width, height, scale));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void ExplicitFactorIsRetained(int factor) =>
        Assert.Equal(factor, DownsamplePlanner.Resolve(DownsamplePolicy.Of(factor), 100, 100, 10));
}

public sealed class FitsImageWriterTests
{
    [Fact]
    public void LongInstrumentStringKeepsItsClosingQuoteAndCardBoundary()
    {
        var header = Encoding.ASCII.GetString(FitsImageWriter.Write(Frame, new FitsMetadata { Instrument = new string('a', 200) }), 0, 2880);
        var card = Enumerable.Range(0, 36).Select(i => header.Substring(i * 80, 80)).Single(c => c.StartsWith("INSTRUME"));
        Assert.Equal('\'', card[79]);
        Assert.Contains("CREATOR", header);
    }
    private static CameraFrame Frame => new(2, 2, [0, 32768, 65535, 1], TimeSpan.FromSeconds(2));

    [Fact]
    public void HeaderStructureAndUnsignedPixelsAreValid()
    {
        var bytes = FitsImageWriter.Write(Frame);
        Assert.Equal(5760, bytes.Length);
        var header = Encoding.ASCII.GetString(bytes, 0, 2880);
        Assert.StartsWith("SIMPLE  =                    T", header);
        Assert.Contains("BITPIX  =                   16", header);
        Assert.Contains("NAXIS1  =                    2", header);
        Assert.Contains("NAXIS2  =                    2", header);
        Assert.Contains("BZERO   =                32768", header);
        Assert.Contains("BSCALE  =                    1", header);
        var end = Enumerable.Range(0, 36).First(i => header.Substring(i * 80, 3) == "END");
        Assert.All(header[((end + 1) * 80)..], c => Assert.Equal(' ', c));
        ushort[] expected = [65535, 1, 0, 32768];
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], (ushort)(BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(2880 + i * 2, 2)) + 32768));
        Assert.All(bytes[2888..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void KnownMetadataIsIncludedWithoutFakeWcs()
    {
        var header = Encoding.ASCII.GetString(FitsImageWriter.Write(Frame, new FitsMetadata
        {
            FocalLengthMm = 500, PixelSizeXMicrons = 3.76, RightAscensionDegrees = 12,
            DeclinationDegrees = -4, Instrument = "Camera", ObservedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        }), 0, 2880);
        foreach (var key in new[] { "FOCALLEN", "XPIXSZ", "RA      ", "DEC     ", "INSTRUME", "DATE-OBS" }) Assert.Contains(key, header);
        foreach (var key in new[] { "CRVAL", "CRPIX", "CDELT", "CTYPE", "CD1_", "YPIXSZ" }) Assert.DoesNotContain(key, header);
    }

    [Fact]
    public void UnknownMetadataIsOmitted()
    {
        var header = Encoding.ASCII.GetString(FitsImageWriter.Write(Frame), 0, 2880);
        foreach (var key in new[] { "FOCALLEN", "XPIXSZ", "YPIXSZ", "DATE-OBS", "INSTRUME", "CRVAL" }) Assert.DoesNotContain(key, header);
    }

    [Theory]
    [InlineData(1440, 1, 5760)]
    [InlineData(1441, 1, 8640)]
    public void DataPaddingUsesWholeBlocks(int width, int height, int length) =>
        Assert.Equal(length, FitsImageWriter.Write(new CameraFrame(width, height, new ushort[width * height], TimeSpan.Zero)).Length);
}

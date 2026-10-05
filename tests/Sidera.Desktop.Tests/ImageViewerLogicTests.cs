using System.IO.Compression;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Desktop.Imaging;

namespace Sidera.Desktop.Tests;

/// <summary>The arithmetic of the image viewer (fit, zoom around the pointer, pan, limits), the display stretch, and the PNG and FITS files that are saved: none of it needs a window.</summary>
public sealed class ImageViewerLogicTests
{
    // ---- The view transform

    [Fact]
    public void Fit_ScalesTheImageIntoTheView_AndCentersIt()
    {
        var t = ImageViewTransform.Fit(6000, 4000, 1200, 800);

        Assert.Equal(0.2, t.Scale, 9);
        Assert.Equal(0, t.OffsetX, 9);
        Assert.Equal(0, t.OffsetY, 9);

        var wide = ImageViewTransform.Fit(1000, 500, 1000, 1000);
        Assert.Equal(1, wide.Scale);
        Assert.Equal(0, wide.OffsetX, 9);
        Assert.Equal(250, wide.OffsetY, 9); // centered
    }

    [Fact]
    public void Fit_NeverZoomsInBeyondOneToOne_AndKeepsAMargin()
    {
        Assert.Equal(1, ImageViewTransform.Fit(100, 100, 1000, 1000).Scale);
        Assert.Equal(0.2, ImageViewTransform.Fit(6000, 4000, 1200 + 2 * 30, 800 + 2 * 30, margin: 30).Scale, 9);
    }

    [Fact]
    public void ActualSize_IsOnePixelForOne_Centered()
    {
        var t = ImageViewTransform.ActualSize(800, 600, 1000, 1000);

        Assert.True(t.IsActualSize);
        Assert.Equal(100, t.OffsetX, 9);
        Assert.Equal(200, t.OffsetY, 9);
    }

    [Fact]
    public void Zooming_KeepsThePointUnderThePointer_Where_ItWas()
    {
        var t = ImageViewTransform.Fit(6000, 4000, 1200, 800);
        var (imageX, imageY) = t.ToImage(300, 200);

        var zoomed = t.ZoomAt(300, 200, 4);

        Assert.Equal(0.8, zoomed.Scale, 9);
        var (afterX, afterY) = zoomed.ToImage(300, 200);
        Assert.Equal(imageX, afterX, 6);
        Assert.Equal(imageY, afterY, 6);
    }

    [Fact]
    public void ZoomingOutAndInAgain_ReturnsToWhereItStarted()
    {
        var t = new ImageViewTransform(1, 17, -9);

        var back = t.ZoomAt(400, 300, 2).ZoomAt(400, 300, 0.5);

        Assert.Equal(t.Scale, back.Scale, 9);
        Assert.Equal(t.OffsetX, back.OffsetX, 6);
        Assert.Equal(t.OffsetY, back.OffsetY, 6);
    }

    [Fact]
    public void TheZoomHasLimits_AndAtALimitNothingMoves()
    {
        var max = new ImageViewTransform(ImageViewTransform.MaxScale, 5, 5);
        var min = new ImageViewTransform(ImageViewTransform.MinScale, 5, 5);

        Assert.Equal(max, max.ZoomAt(100, 100, 2));
        Assert.Equal(min, min.ZoomAt(100, 100, 0.5));
        Assert.Equal(ImageViewTransform.MaxScale, new ImageViewTransform(1, 0, 0).ZoomAt(0, 0, 1e9).Scale);
        Assert.Equal(ImageViewTransform.MinScale, new ImageViewTransform(1, 0, 0).ZoomAt(0, 0, 1e-9).Scale);
        Assert.Equal(new ImageViewTransform(2, 3, 4), new ImageViewTransform(2, 3, 4).ZoomAt(1, 1, double.NaN));
        Assert.Equal(new ImageViewTransform(2, 3, 4), new ImageViewTransform(2, 3, 4).ZoomAt(1, 1, -1));
    }

    [Fact]
    public void Panning_MovesTheImage_AndTheImageCannotBeDraggedOutOfReach()
    {
        var t = new ImageViewTransform(1, 0, 0).Pan(30, -20);
        Assert.Equal((30, -20), (t.OffsetX, t.OffsetY));

        var far = new ImageViewTransform(1, 0, 0).Pan(100000, -100000).Clamp(1000, 800, 600, 400, keepVisible: 48);

        Assert.Equal(600 - 48, far.OffsetX, 9);
        Assert.Equal(48 - 800, far.OffsetY, 9);
    }

    [Fact]
    public void ImageAndViewPoints_RoundTrip()
    {
        var t = new ImageViewTransform(2.5, 40, -12);

        var (vx, vy) = t.ToView(100, 50);
        var (ix, iy) = t.ToImage(vx, vy);

        Assert.Equal((100, 50), (ix, iy));
    }

    [Fact]
    public void ADegenerateView_IsHandled()
    {
        Assert.Equal(new ImageViewTransform(1, 0, 0), ImageViewTransform.Fit(100, 100, 0, 0));
        Assert.Equal(new ImageViewTransform(1, 0, 0), ImageViewTransform.Fit(0, 100, 100, 100));
    }

    // ---- The stretch

    // A background of about 1000 ADU with some noise, and a few bright stars.
    private static CameraFrame SkyFrame(int size = 200)
    {
        var random = new Random(7);
        var pixels = new ushort[size * size];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (ushort)(1000 + random.Next(-40, 41));
        }

        foreach (var (x, y) in new[] { (size / 4, size / 4), (size * 3 / 5, size * 2 / 5), (size * 3 / 20, size * 3 / 4) })
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    pixels[(y + dy) * size + x + dx] = (ushort)(40000 - 6000 * (Math.Abs(dx) + Math.Abs(dy)));
                }
            }
        }

        return new CameraFrame(size, size, pixels, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TheAutoStretch_DoesNotChangeTheFrame()
    {
        var frame = SkyFrame();
        var before = frame.Pixels.ToArray();

        ImageStretch.ToGray8(frame, autoStretch: true);
        ImageStretch.ToGray8(frame, autoStretch: false);

        Assert.Equal(before, frame.Pixels.ToArray());
    }

    [Fact]
    public void TheAutoStretch_IsDeterministic()
    {
        var frame = SkyFrame();

        Assert.Equal(ImageStretch.ToGray8(frame, true), ImageStretch.ToGray8(frame, true));
        Assert.Equal(ImageStretch.AutoParameters(frame), ImageStretch.AutoParameters(frame));
    }

    [Fact]
    public void TheAutoStretch_BringsTheBackgroundToAQuarterOfTheRange()
    {
        var frame = SkyFrame();

        var gray = ImageStretch.ToGray8(frame, true);

        var median = gray.Order().ElementAt(gray.Length / 2);
        Assert.InRange(median, 0.25 * 255 - 6, 0.25 * 255 + 6);
    }

    [Fact]
    public void TheAutoStretch_PutsTheBlackPointBelowTheBackground_AndTheWhitePointAtTheStars()
    {
        var p = ImageStretch.AutoParameters(SkyFrame());

        Assert.InRange(p.Black, 900, 1000); // a little below the background, within the noise
        Assert.True(p.White > 30000);
        Assert.InRange(p.Midtone, 0, 1);
    }

    [Fact]
    public void TheStretchedPicture_IsBrighterThanTheLinearOne_ForADimSky()
    {
        var frame = SkyFrame();

        var stretched = ImageStretch.ToGray8(frame, true);
        var linear = ImageStretch.ToGray8(frame, false);

        Assert.True(stretched.Average(b => b) > linear.Average(b => b) * 3);
    }

    [Fact]
    public void TheLinearPicture_IsProportional_FromTheDarkestToTheBrightest()
    {
        var frame = new CameraFrame(4, 1, [100, 200, 300, 500], TimeSpan.Zero);

        Assert.Equal([0, 64, 128, 255], ImageStretch.ToGray8(frame, false));
    }

    [Fact]
    public void AFlatFrame_DoesNotBreakEitherStretch()
    {
        var flat = new CameraFrame(10, 10, Enumerable.Repeat((ushort)777, 100).ToArray(), TimeSpan.Zero);

        Assert.Equal(100, ImageStretch.ToGray8(flat, true).Length);
        Assert.All(ImageStretch.ToGray8(flat, false), b => Assert.Equal(0, b));
    }

    [Fact]
    public void TheMidtoneFunction_KeepsTheEnds_AndMapsTheMidtoneToOneHalf()
    {
        Assert.Equal(0, ImageStretch.MidtonesTransfer(0.2, 0));
        Assert.Equal(1, ImageStretch.MidtonesTransfer(0.2, 1));
        Assert.Equal(0.5, ImageStretch.MidtonesTransfer(0.2, 0.2), 9);
        Assert.Equal(0.3, ImageStretch.MidtonesTransfer(0.5, 0.3), 9);
    }

    // ---- PNG

    [Fact]
    public void APng_HasTheSignature_TheSize_AndPixelsThatComeBack()
    {
        var gray = Enumerable.Range(0, 12).Select(i => (byte)(i * 20)).ToArray();

        var png = PngImageWriter.Write(gray, 4, 3);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png.Take(8));
        Assert.Equal(4, ReadInt32(png, 16)); // IHDR width
        Assert.Equal(3, ReadInt32(png, 20)); // IHDR height
        Assert.Equal(8, png[24]); // bit depth
        Assert.Equal(0, png[25]); // grayscale
        Assert.Equal(gray, Decode(png, 4, 3));
    }

    [Fact]
    public void ThePngChunks_HaveValidChecksums()
    {
        var png = PngImageWriter.Write([1, 2, 3, 4], 2, 2);

        var offset = 8;
        var chunks = new List<string>();
        while (offset < png.Length)
        {
            var length = ReadInt32(png, offset);
            var typeAndData = png.AsSpan(offset + 4, 4 + length).ToArray();
            var stored = (uint)ReadInt32(png, offset + 8 + length);
            Assert.Equal(Crc32(typeAndData), stored);
            chunks.Add(System.Text.Encoding.ASCII.GetString(png, offset + 4, 4));
            offset += 12 + length;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks);
    }

    [Fact]
    public void APngOfTheWrongSize_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => PngImageWriter.Write([1, 2, 3], 2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => PngImageWriter.Write([], 0, 0));
    }

    private static int ReadInt32(byte[] data, int offset) => data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3];

    private static byte[] Decode(byte[] png, int width, int height)
    {
        var offset = 8;
        var idat = new MemoryStream();
        while (offset < png.Length)
        {
            var length = ReadInt32(png, offset);
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
            {
                idat.Write(png, offset + 8, length);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new MemoryStream();
        zlib.CopyTo(raw);
        var rows = raw.ToArray();
        var result = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(0, rows[y * (width + 1)]); // no filter
            Array.Copy(rows, y * (width + 1) + 1, result, y * width, width);
        }

        return result;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }

    // ---- FITS

    private static string Header(byte[] fits) => System.Text.Encoding.ASCII.GetString(fits, 0, fits.Length >= 2880 ? 2880 : fits.Length);

    [Fact]
    public void TheFits_HoldsTheFrameAsItWasTaken_NotStretched()
    {
        var frame = SkyFrame(64);

        var fits = FitsImageWriter.Write(frame);

        // After the header (one block) the data are the pixels, bottom row first, as signed 16-bit with BZERO 32768.
        var pixels = frame.Pixels.Span;
        for (var y = 0; y < 64; y += 21)
        {
            for (var x = 0; x < 64; x += 13)
            {
                var offset = 2880 + ((63 - y) * 64 + x) * 2;
                var stored = (short)(fits[offset] << 8 | fits[offset + 1]);
                Assert.Equal(pixels[y * 64 + x], stored + 32768);
            }
        }
    }

    [Fact]
    public void TheFitsHeader_SaysWhatIsKnown_AndNothingThatIsNot()
    {
        var frame = new CameraFrame(8, 8, new ushort[64], TimeSpan.FromSeconds(30))
        {
            Acquisition = new FrameAcquisition { FrameType = FrameType.Dark, Gain = 100, Offset = 30, BinX = 2, BinY = 2 },
        };

        var header = Header(FitsImageWriter.Write(frame, new FitsMetadata
        {
            Instrument = "ASI2600MC Pro", Telescope = "Main Rig", FocalLengthMm = 750, PixelSizeXMicrons = 7.52, PixelSizeYMicrons = 7.52,
            RightAscensionDegrees = 10.68, DeclinationDegrees = 41.27, SiteLatitudeDegrees = 48.1, SiteLongitudeDegrees = 11.6, SiteElevationMeters = 520,
        }));

        foreach (var expected in new[] { "EXPTIME", "XBINNING", "YBINNING", "XPIXSZ", "FOCALLEN", "GAIN", "OFFSET", "INSTRUME", "TELESCOP", "'Dark Frame", "SITELAT", "SITELONG", "SITEELEV", "RA      =", "DEC     =" })
        {
            Assert.Contains(expected, header);
        }

        var bare = Header(FitsImageWriter.Write(new CameraFrame(8, 8, new ushort[64], TimeSpan.FromSeconds(1))));
        foreach (var absent in new[] { "FOCALLEN", "XPIXSZ", "SITELAT", "SITELONG", "SITEELEV", "TELESCOP", "INSTRUME", "RA      =", "DEC     =", "GAIN", "DATE-OBS" })
        {
            Assert.DoesNotContain(absent, bare);
        }
    }

    [Fact]
    public void TheFitsFrameType_ComesFromTheFrame_WhenTheMetadataDoesNotSayAndAFrameWithoutOneHasNone()
    {
        var flat = new CameraFrame(4, 4, new ushort[16], TimeSpan.FromSeconds(1)) { Acquisition = new FrameAcquisition { FrameType = FrameType.Flat } };

        Assert.Contains("'Flat Field", Header(FitsImageWriter.Write(flat)));
        Assert.DoesNotContain("IMAGETYP", Header(FitsImageWriter.Write(new CameraFrame(4, 4, new ushort[16], TimeSpan.FromSeconds(1)))));
    }
}

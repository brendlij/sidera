using Sidera.Ascom.Cameras;

namespace Sidera.Ascom.Tests;

/// <summary>The one place that knows the layout of ASCOM images. Strict by design: what is not understood is rejected.</summary>
public class ImageConverterTests
{
    // [x, y] with the value 10 * x + y: every pixel says where it came from.
    private static int[,] Marked(int width, int height)
    {
        var image = new int[width, height];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                image[x, y] = 10 * x + y;
            }
        }

        return image;
    }

    [Fact]
    public void AnInt32Image_BecomesRows_WithTheFirstIndexAsTheWidth()
    {
        var converted = AscomImageConverter.Convert(Marked(4, 3), 4, 3);

        Assert.Equal((4, 3), (converted.Width, converted.Height));
        Assert.Equal(12, converted.Pixels.Length);
        // Row y = 0 holds x = 0..3, row y = 1 the next four, and so on.
        Assert.Equal<ushort>([0, 10, 20, 30, 1, 11, 21, 31, 2, 12, 22, 32], converted.Pixels);
        Assert.Equal(0, converted.Clamped);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    [InlineData(5, 1)]
    public void ThePixelAtXY_IsAtYTimesWidthPlusX(int x, int y)
    {
        var converted = AscomImageConverter.Convert(Marked(6, 4));

        Assert.Equal((ushort)(10 * x + y), converted.Pixels[y * converted.Width + x]);
    }

    [Fact]
    public void ANonSquareImage_KeepsItsDimensions_NotTransposed()
    {
        var converted = AscomImageConverter.Convert(Marked(7, 2), 7, 2);

        Assert.Equal((7, 2), (converted.Width, converted.Height));
    }

    [Fact]
    public void TheFullRange_IsKeptExactly()
    {
        var image = new int[2, 1];
        image[0, 0] = 0;
        image[1, 0] = 65535;

        var converted = AscomImageConverter.Convert(image);

        Assert.Equal<ushort>([0, 65535], converted.Pixels);
        Assert.Equal(0, converted.Clamped);
    }

    [Fact]
    public void NegativeValues_AreClampedToZero_AndCounted()
    {
        var image = new int[3, 1];
        (image[0, 0], image[1, 0], image[2, 0]) = (-1, int.MinValue, 7);

        var converted = AscomImageConverter.Convert(image);

        Assert.Equal<ushort>([0, 0, 7], converted.Pixels);
        Assert.Equal(2, converted.Clamped);
    }

    [Fact]
    public void ValuesAbove65535_AreClampedToTheMaximum_AndCounted()
    {
        var image = new int[3, 1];
        (image[0, 0], image[1, 0], image[2, 0]) = (65536, int.MaxValue, 65535);

        var converted = AscomImageConverter.Convert(image);

        Assert.Equal<ushort>([65535, 65535, 65535], converted.Pixels);
        Assert.Equal(2, converted.Clamped);
    }

    [Fact]
    public void ARankThreeImage_IsRejected_AsColour()
    {
        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(new int[4, 3, 3]));

        Assert.Contains("rank 3", failure.Message);
        Assert.Contains("Colour", failure.Detail);
    }

    [Fact]
    public void ARankOneArray_IsRejected()
    {
        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(new int[12]));

        Assert.Contains("rank 1", failure.Message);
    }

    [Theory]
    [MemberData(nameof(UnsupportedElementTypes))]
    public void AnythingButInt32_IsRejected_NamingTheTypeInTheMessage_AndInTheDetail(object image, string typeName)
    {
        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(image));

        Assert.Contains(typeName, failure.Message);
        Assert.Contains(image.GetType().ToString(), failure.Detail);
        Assert.Contains("rejected rather than guessed", failure.Detail);
    }

    public static TheoryData<object, string> UnsupportedElementTypes() => new()
    {
        { new short[4, 3], "Int16" },
        { new ushort[4, 3], "UInt16" },
        { new double[4, 3], "Double" },
        { new float[4, 3], "Single" },
        { new long[4, 3], "Int64" },
        { new object[4, 3], "Object" },
        { new byte[4, 3], "Byte" },
    };

    [Fact]
    public void Null_IsRejected()
    {
        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(null));

        Assert.Contains("no image", failure.Message);
    }

    [Fact]
    public void SomethingThatIsNotAnArray_IsRejected()
    {
        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert("image"));

        Assert.Contains("System.String", failure.Detail);
    }

    [Fact]
    public void AnEmptyImage_IsRejected()
    {
        Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(new int[0, 3]));
        Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(new int[3, 0]));
    }

    [Fact]
    public void ADimensionThatDoesNotMatchWhatTheDriverAnnounced_IsRejected_NotTransposed()
    {
        var transposed = Marked(3, 4);

        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(transposed, 4, 3));

        Assert.Contains("3 x 4", failure.Message);
        Assert.Contains("4 x 3", failure.Message);
        Assert.Contains("not transposed", failure.Detail);
    }

    [Fact]
    public void WithoutAnnouncedDimensions_TheArraysOwnDimensionsAreUsed()
    {
        Assert.Equal(12, AscomImageConverter.Convert(Marked(4, 3), 0, 0).Pixels.Length);
        Assert.Equal(12, AscomImageConverter.Convert(Marked(4, 3), null, null).Pixels.Length);
    }

    [Fact]
    public void AnArrayThatDoesNotStartAtZero_IsRejected()
    {
        var shifted = Array.CreateInstance(typeof(int), [4, 3], [1, 1]);

        var failure = Assert.Throws<ImageConversionException>(() => AscomImageConverter.Convert(shifted));

        Assert.Contains("do not start at 0", failure.Message);
    }
}

using System.Globalization;

namespace Astra.Ascom.Cameras;

/// <summary>An ImageArray that Astra does not accept. <see cref="Exception.Message"/> is short; <see cref="Detail"/> is for the log.</summary>
public sealed class ImageConversionException(string message, string detail) : Exception(message)
{
    public string Detail { get; } = detail;
}

/// <summary>
/// The pixels of a converted ImageArray in Astra's layout, and what the conversion had to do to the values.
/// </summary>
/// <param name="Pixels">Row-major: index = y * Width + x.</param>
/// <param name="Clamped">How many values were outside 0 to 65535 and were clamped. Zero for a driver that honours its MaxADU.</param>
public sealed record ConvertedImage(int Width, int Height, ushort[] Pixels, long Clamped);

/// <summary>
/// Turns the ImageArray of an ASCOM camera into Astra's pixels. This is the one place that knows the layout of ASCOM
/// images, and it is deliberately strict: a representation that is not understood is rejected, never guessed.
/// <para>
/// <b>Supported:</b> <c>int[,]</c>, which is what the ASCOM ImageArray property is defined to return and what the ASCOM
/// Camera Simulator and the OmniSim camera were seen to return, with the dimensions <c>[x, y]</c>: the first index runs
/// along the width. Astra stores rows, so pixel (x, y) goes to index <c>y * Width + x</c>; the image is not flipped and
/// the columns are not swapped.
/// </para>
/// <para>
/// <b>Not supported</b> (rejected with the runtime type in the message): any other element type (short, ushort, double,
/// object ...), any rank other than 2 (a colour image has 3 planes), and arrays that do not start at 0. Values are
/// clamped to 0 to 65535 and counted; a camera whose values leave that range is told so in the log.
/// </para>
/// </summary>
public static class AscomImageConverter
{
    /// <summary>The most pixels Astra accepts in one image; far above any real sensor, below what an array can hold.</summary>
    public const long MaxPixels = 1L << 28;

    /// <param name="imageArray">What the driver returned.</param>
    /// <param name="expectedWidth">The width the driver announced (NumX); <c>null</c> or not positive when unknown.</param>
    /// <param name="expectedHeight">The height the driver announced (NumY); <c>null</c> or not positive when unknown.</param>
    /// <exception cref="ImageConversionException">The array is not an image Astra can take.</exception>
    public static ConvertedImage Convert(object? imageArray, int? expectedWidth = null, int? expectedHeight = null)
    {
        if (imageArray is null)
        {
            throw new ImageConversionException("The camera returned no image.", "ImageArray was null.");
        }

        if (imageArray is not Array array)
        {
            throw new ImageConversionException(
                "The camera returned something that is not an image array.",
                $"ImageArray has the runtime type {imageArray.GetType()}, which is not an array.");
        }

        if (array.Rank != 2)
        {
            throw new ImageConversionException(
                string.Create(CultureInfo.InvariantCulture, $"The camera returned an image of rank {array.Rank}; only single-plane images are supported."),
                $"ImageArray has the runtime type {array.GetType()} (rank {array.Rank}). Colour (3-plane) images are not supported.");
        }

        if (array is not int[,] values)
        {
            throw new ImageConversionException(
                $"The camera returned an image of type {array.GetType().GetElementType()?.Name}[,]; only Int32 images are supported.",
                $"ImageArray has the runtime type {array.GetType()}. Only int[,] is supported: the ASCOM ImageArray is defined as Int32, " +
                "and that is what the ASCOM and OmniSim camera simulators return. Other element types are rejected rather than guessed.");
        }

        if (array.GetLowerBound(0) != 0 || array.GetLowerBound(1) != 0)
        {
            throw new ImageConversionException(
                "The camera returned an image whose indices do not start at 0.",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"ImageArray has the lower bounds [{array.GetLowerBound(0)}, {array.GetLowerBound(1)}]; only zero-based arrays are supported."));
        }

        var width = values.GetLength(0);
        var height = values.GetLength(1);
        if (width <= 0 || height <= 0)
        {
            throw new ImageConversionException(
                string.Create(CultureInfo.InvariantCulture, $"The camera returned an empty image ({width} x {height})."),
                $"ImageArray ({array.GetType()}) has the dimensions [{width}, {height}].");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new ImageConversionException(
                string.Create(CultureInfo.InvariantCulture, $"The camera returned an image of {width} x {height} pixels, which is more than Astra accepts."),
                $"ImageArray ({array.GetType()}) has the dimensions [{width}, {height}]; the limit is {MaxPixels} pixels.");
        }

        if (expectedWidth is > 0 && expectedHeight is > 0 && (width != expectedWidth || height != expectedHeight))
        {
            throw new ImageConversionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The camera returned an image of {width} x {height} pixels, but it announced {expectedWidth} x {expectedHeight}."),
                $"ImageArray ({array.GetType()}) has the dimensions [{width}, {height}] (x, y); the driver announced NumX={expectedWidth}, NumY={expectedHeight}. " +
                "The image is not transposed or reinterpreted to make it fit.");
        }

        var pixels = new ushort[width * height];
        long clamped = 0;
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                var value = values[x, y];
                if (value < 0)
                {
                    value = 0;
                    clamped++;
                }
                else if (value > ushort.MaxValue)
                {
                    value = ushort.MaxValue;
                    clamped++;
                }

                pixels[y * width + x] = (ushort)value;
            }
        }

        return new ConvertedImage(width, height, pixels, clamped);
    }
}

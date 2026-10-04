using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Sidera.Core.Devices;

namespace Sidera.Core.Imaging;

/// <summary>
/// What is known about how an image was taken, for the header of a FITS file. Everything is optional: a value that is not known is not written,
/// and nothing is made up. No world coordinate system is ever part of it: an image that has not been solved has no WCS, and a wrong one is worse than none.
/// </summary>
public sealed record FitsMetadata
{
    public double? ExposureSeconds { get; init; }
    public int? BinX { get; init; }
    public int? BinY { get; init; }

    /// <summary>The size of a pixel of this image in micrometers (with the binning applied), as FITS <c>XPIXSZ</c> and <c>YPIXSZ</c>.</summary>
    public double? PixelSizeXMicrons { get; init; }

    public double? PixelSizeYMicrons { get; init; }
    public double? FocalLengthMm { get; init; }

    /// <summary>Approximate pointing in degrees (not hours), from the mount: <c>RA</c> and <c>DEC</c>; only when it is known.</summary>
    public double? RightAscensionDegrees { get; init; }

    public double? DeclinationDegrees { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public int? Gain { get; init; }
    public int? Offset { get; init; }
    public string? Instrument { get; init; }
}

/// <summary>
/// The smallest FITS writer that a plate solver can read: one primary HDU with a two-dimensional image of 16-bit unsigned pixels (as signed
/// 16-bit with <c>BZERO</c> = 32768, the convention for unsigned data), big-endian, in 2880-byte blocks. Rows are written bottom to top, as FITS
/// has its origin at the lower left: the top row of the frame is the top of the image. This is not a FITS library: no extensions, no compression, no color.
/// </summary>
public static class FitsImageWriter
{
    public const int BlockSize = 2880;
    private const int CardLength = 80;

    public static byte[] Write(CameraFrame frame, FitsMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        using var stream = new MemoryStream();
        Write(stream, frame, metadata);
        return stream.ToArray();
    }

    public static void Write(Stream stream, CameraFrame frame, FitsMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(frame);
        metadata ??= new FitsMetadata();

        var cards = Header(frame, metadata);
        var header = new StringBuilder();
        foreach (var card in cards)
        {
            header.Append(card.PadRight(CardLength));
        }

        header.Append("END".PadRight(CardLength));
        while (header.Length % BlockSize != 0)
        {
            header.Append(' ');
        }

        stream.Write(Encoding.ASCII.GetBytes(header.ToString()));

        var width = frame.Width;
        var pixels = frame.Pixels.Span;
        var row = new byte[width * 2];
        for (var y = frame.Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++)
            {
                BinaryPrimitives.WriteInt16BigEndian(row.AsSpan(x * 2), unchecked((short)(pixels[y * width + x] - 32768)));
            }

            stream.Write(row);
        }

        var dataLength = (long)width * frame.Height * 2;
        var padding = (int)((BlockSize - dataLength % BlockSize) % BlockSize);
        if (padding > 0)
        {
            stream.Write(new byte[padding]);
        }
    }

    // The cards in the order FITS asks for: the structure first, then what is known about the exposure.
    private static List<string> Header(CameraFrame frame, FitsMetadata m)
    {
        var cards = new List<string>
        {
            Logical("SIMPLE", true, "conforms to FITS standard"),
            Integer("BITPIX", 16, "16-bit integers"),
            Integer("NAXIS", 2, "two-dimensional image"),
            Integer("NAXIS1", frame.Width, "width in pixels"),
            Integer("NAXIS2", frame.Height, "height in pixels"),
            Integer("BZERO", 32768, "offset of unsigned 16-bit data"),
            Integer("BSCALE", 1, "no scaling"),
        };

        var exposure = m.ExposureSeconds ?? frame.ExposureDuration.TotalSeconds;
        if (exposure > 0)
        {
            cards.Add(Real("EXPTIME", exposure, "[s] exposure time"));
        }

        var bx = m.BinX ?? frame.Acquisition?.BinX;
        var by = m.BinY ?? frame.Acquisition?.BinY;
        if (bx is > 0)
        {
            cards.Add(Integer("XBINNING", bx.Value, "binning in x"));
        }

        if (by is > 0)
        {
            cards.Add(Integer("YBINNING", by.Value, "binning in y"));
        }

        AddReal(cards, "XPIXSZ", m.PixelSizeXMicrons, "[um] pixel width (binned)");
        AddReal(cards, "YPIXSZ", m.PixelSizeYMicrons, "[um] pixel height (binned)");
        AddReal(cards, "FOCALLEN", m.FocalLengthMm, "[mm] focal length");
        AddReal(cards, "RA", m.RightAscensionDegrees, "[deg] approximate pointing, not a solution");
        AddReal(cards, "DEC", m.DeclinationDegrees, "[deg] approximate pointing, not a solution");
        if (m.ObservedAt is { } at)
        {
            cards.Add(Text("DATE-OBS", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture), "UTC start of the exposure"));
        }

        var gain = m.Gain ?? frame.Acquisition?.Gain;
        if (gain is { } g)
        {
            cards.Add(Integer("GAIN", g, "camera gain"));
        }

        var offset = m.Offset ?? frame.Acquisition?.Offset;
        if (offset is { } o)
        {
            cards.Add(Integer("OFFSET", o, "camera offset"));
        }

        if (!string.IsNullOrWhiteSpace(m.Instrument))
        {
            cards.Add(Text("INSTRUME", m.Instrument, "camera"));
        }

        cards.Add(Text("CREATOR", "Sidera", "software that wrote this file"));
        return cards;
    }

    private static void AddReal(List<string> cards, string key, double? value, string comment)
    {
        if (value is { } v && double.IsFinite(v))
        {
            cards.Add(Real(key, v, comment));
        }
    }

    private static string Key(string key) => key.PadRight(8) + "= ";

    private static string Comment(string comment) => " / " + comment;

    private static string Logical(string key, bool value, string comment) =>
        Key(key) + (value ? "T" : "F").PadLeft(20) + Comment(comment);

    private static string Integer(string key, long value, string comment) =>
        Key(key) + value.ToString(CultureInfo.InvariantCulture).PadLeft(20) + Comment(comment);

    private static string Real(string key, double value, string comment) =>
        Key(key) + value.ToString("0.0#########", CultureInfo.InvariantCulture).PadLeft(20) + Comment(comment);

    private static string Text(string key, string value, string comment)
    {
        // A string value is in quotes with a quote doubled, at least 8 characters inside, and fits the card.
        var escaped = value.Replace("'", "''", StringComparison.Ordinal);
        if (escaped.Length > CardLength - 12)
        {
            escaped = escaped[..(CardLength - 12)];
            // Never split an escaped quote pair at the end of the field.
            if (escaped.EndsWith('\'')) escaped = escaped.TrimEnd('\'');
        }
        var quoted = "'" + escaped.PadRight(8) + "'";
        var card = Key(key) + quoted;
        var withComment = card + Comment(comment);
        return withComment.Length <= CardLength ? withComment : card.Length <= CardLength ? card : card[..CardLength];
    }
}

using System.IO.Compression;

namespace Sidera.Core.Imaging;

/// <summary>
/// The smallest PNG writer: an 8-bit grayscale image, no interlacing, no ancillary chunks. Used to save what is shown on the screen. A PNG made from a camera frame is a picture of the
/// display (stretched or not), never the data: the FITS file is the data.
/// </summary>
public static class PngImageWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="gray">One byte per pixel, row by row, top row first.</param>
    public static void Write(Stream stream, byte[] gray, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(gray);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);
        if (gray.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels for {width}x{height}, got {gray.Length}.", nameof(gray));
        }

        stream.Write(Signature);
        var header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8; // bit depth
        header[9] = 0; // grayscale
        Chunk(stream, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[width + 1]; // each row starts with its filter type: 0, none
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(gray, y * width, row, 1, width);
                zlib.Write(row, 0, row.Length);
            }
        }

        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
    }

    public static byte[] Write(byte[] gray, int width, int height)
    {
        using var stream = new MemoryStream();
        Write(stream, gray, width, height);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt32(length, 0, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = Crc(typeBytes, data);
        var crcBytes = new byte[4];
        WriteInt32(crcBytes, 0, unchecked((int)crc));
        stream.Write(crcBytes);
    }

    private static void WriteInt32(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint Crc(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}

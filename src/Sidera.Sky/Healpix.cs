namespace Sidera.Sky;

/// <summary>
/// The HEALPix pixelization in the nested scheme, as far as HiPS needs it: the pixel of a position on the sky and, for a HiPS tile of a given width, the tile that holds
/// it and the place inside the tile. The sky is cut into 12 base pixels; each order divides every pixel into four, so the order <c>k</c> has <c>12 * 4^k</c> pixels
/// and the pixels are numbered so that the four children of a pixel follow each other. A HiPS tile of order <c>k</c> is the set of the pixels of order
/// <c>k + log2(tile width)</c> that lie in the pixel <c>n</c> of order <c>k</c>: its image is that block, with the pixels in Z-order.
/// </summary>
public static class HealpixNested
{
    /// <summary>The nested index of the pixel of <paramref name="order"/> that holds the position (right ascension and declination in degrees).</summary>
    public static long AngleToPixel(int order, double rightAscensionDegrees, double declinationDegrees)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(order);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(order, 29);
        var nside = 1L << order;
        var z = Math.Sin(declinationDegrees * Math.PI / 180.0);
        var phi = rightAscensionDegrees * Math.PI / 180.0;
        var za = Math.Abs(z);
        var tt = phi % (2 * Math.PI);
        if (tt < 0)
        {
            tt += 2 * Math.PI;
        }

        tt *= 2.0 / Math.PI; // 0 to 4

        long face;
        long ix;
        long iy;
        if (za <= 2.0 / 3.0)
        {
            // The equatorial belt.
            var temp1 = nside * (0.5 + tt);
            var temp2 = nside * z * 0.75;
            var jp = (long)(temp1 - temp2);
            var jm = (long)(temp1 + temp2);
            var ifp = jp / nside;
            var ifm = jm / nside;
            face = ifp == ifm ? (ifp | 4) : ifp < ifm ? ifp : ifm + 8;
            ix = jm & (nside - 1);
            iy = nside - (jp & (nside - 1)) - 1;
        }
        else
        {
            // The polar caps.
            var ntt = Math.Min((long)tt, 3);
            var tp = tt - ntt;
            var tmp = nside * Math.Sqrt(3 * (1 - za));
            var jp = Math.Min((long)(tp * tmp), nside - 1);
            var jm = Math.Min((long)((1.0 - tp) * tmp), nside - 1);
            if (z >= 0)
            {
                face = ntt;
                ix = nside - jm - 1;
                iy = nside - jp - 1;
            }
            else
            {
                face = ntt + 8;
                ix = jp;
                iy = jm;
            }
        }

        return face * nside * nside + SpreadBits(ix) + (SpreadBits(iy) << 1);
    }

    /// <summary>
    /// The tile and the pixel inside it for a position: the tile of order <paramref name="tileOrder"/> that holds it, and the column and row of the pixel in the tile image
    /// of width <c>2^tileWidthLog2</c>. The pixel is the pixel of order <c>tileOrder + tileWidthLog2</c>.
    /// </summary>
    public static (long Tile, int Column, int Row) ToTilePixel(int tileOrder, int tileWidthLog2, double rightAscensionDegrees, double declinationDegrees)
    {
        var pixel = AngleToPixel(tileOrder + tileWidthLog2, rightAscensionDegrees, declinationDegrees);
        var tile = pixel >> (2 * tileWidthLog2);
        var local = pixel & ((1L << (2 * tileWidthLog2)) - 1);
        var x = (int)CompactBits(local);
        var y = (int)CompactBits(local >> 1);
        var width = 1 << tileWidthLog2;
        // Checked against the real tiles of a survey (M31 and its companions sit where they are on the sky): the odd bits of the local index count the columns and
        // the even bits the rows of the tile image, from its top left corner.
        _ = width;
        return (tile, y, x);
    }

    // The bits of a value moved to the even positions: 0b101 becomes 0b10001.
    internal static long SpreadBits(long value)
    {
        var v = value & 0xFFFFFFFFL;
        v = (v | (v << 16)) & 0x0000FFFF0000FFFFL;
        v = (v | (v << 8)) & 0x00FF00FF00FF00FFL;
        v = (v | (v << 4)) & 0x0F0F0F0F0F0F0F0FL;
        v = (v | (v << 2)) & 0x3333333333333333L;
        v = (v | (v << 1)) & 0x5555555555555555L;
        return v;
    }

    // The inverse: the bits at the even positions packed together.
    internal static long CompactBits(long value)
    {
        var v = value & 0x5555555555555555L;
        v = (v | (v >> 1)) & 0x3333333333333333L;
        v = (v | (v >> 2)) & 0x0F0F0F0F0F0F0F0FL;
        v = (v | (v >> 4)) & 0x00FF00FF00FF00FFL;
        v = (v | (v >> 8)) & 0x0000FFFF0000FFFFL;
        v = (v | (v >> 16)) & 0x00000000FFFFFFFFL;
        return v;
    }
}

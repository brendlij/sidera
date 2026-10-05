using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using Sidera.Sky;

namespace Sidera.Desktop.Imaging;

/// <summary>Decodes the JPEG and PNG tiles of a survey with the image library that the application already has (Skia), into straight RGBA.</summary>
public sealed class SkiaTileDecoder : ITileDecoder
{
    public DecodedTile? Decode(byte[] bytes)
    {
        try
        {
            using var decoded = SKBitmap.Decode(bytes);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
            {
                return null;
            }

            using var rgba = decoded.Copy(SKColorType.Rgba8888);
            if (rgba is null)
            {
                return null;
            }

            var buffer = new byte[rgba.Width * rgba.Height * 4];
            Marshal.Copy(rgba.GetPixels(), buffer, 0, buffer.Length);
            return new DecodedTile(rgba.Width, rgba.Height, buffer);
        }
        catch (Exception)
        {
            // Not an image the library can read: the tile counts as not there.
            return null;
        }
    }
}

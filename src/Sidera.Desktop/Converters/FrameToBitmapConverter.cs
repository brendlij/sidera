using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sidera.Core.Devices;
using Sidera.Desktop.Imaging;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Sidera.Desktop.Converters;

/// <summary>
/// The picture of a frame for a small view (the dashboard): the frame and whether the display is stretched, so that it looks as it does in the viewer of the imaging page (the same stretch, the same
/// setting). The picture is made once for each of the two looks, however many views show it.
/// </summary>
public sealed class FrameToBitmapConverter : IMultiValueConverter
{
    public static FrameToBitmapConverter Instance { get; } = new();

    private sealed class Pictures
    {
        public WriteableBitmap? Stretched { get; set; }
        public WriteableBitmap? Linear { get; set; }
    }

    private readonly ConditionalWeakTable<CameraFrame, Pictures> _bitmaps = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not CameraFrame frame)
        {
            return null;
        }

        var stretched = values.Count < 2 || values[1] is not bool autoStretch || autoStretch;
        var pictures = _bitmaps.GetOrCreateValue(frame);
        lock (pictures)
        {
            return stretched ? pictures.Stretched ??= ToBitmap(frame, true) : pictures.Linear ??= ToBitmap(frame, false);
        }
    }

    private static WriteableBitmap ToBitmap(CameraFrame frame, bool autoStretch)
    {
        var gray = ImageStretch.ToGray8(frame, autoStretch);

        // Expanded to opaque BGRA so it works on every rendering backend.
        var bitmap = new WriteableBitmap(
            new PixelSize(frame.Width, frame.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var framebuffer = bitmap.Lock();
        var row = new byte[frame.Width * 4];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var value = gray[y * frame.Width + x];
                var offset = x * 4;
                row[offset] = value;
                row[offset + 1] = value;
                row[offset + 2] = value;
                row[offset + 3] = 255;
            }

            Marshal.Copy(row, 0, framebuffer.Address + y * framebuffer.RowBytes, row.Length);
        }

        return bitmap;
    }
}

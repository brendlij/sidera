using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Astra.Core.Devices;
using Astra.Desktop.Imaging;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Astra.Desktop.Converters;

public sealed class FrameToBitmapConverter : IValueConverter
{
    public static FrameToBitmapConverter Instance { get; } = new();

    // The picture of a frame is made once, however many views show it (the dashboard and the imaging page).
    private readonly ConditionalWeakTable<CameraFrame, WriteableBitmap> _bitmaps = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is CameraFrame frame ? _bitmaps.GetValue(frame, ToBitmap) : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static WriteableBitmap ToBitmap(CameraFrame frame)
    {
        var gray = DisplayStretch.ToGray8(frame);

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

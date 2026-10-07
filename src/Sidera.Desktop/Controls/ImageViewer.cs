using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Desktop.Imaging;

namespace Sidera.Desktop.Controls;

/// <summary>
/// Shows a camera frame and lets the user look at it: the wheel zooms around the pointer, dragging pans, <see cref="IsActualSize"/> shows it at 1:1 and neither it nor
/// <see cref="IsCustomView"/> shows it fitted. The picture is made once for the frame (stretched by <see cref="AutoStretch"/> or linear) at the frame's own size and the view scales it; no zoom step
/// resamples the frame or allocates a new picture. The stretch is for display only: the frame is never changed. It is reusable: the imaging page, autofocus, plate solving and the frame
/// quality views can show their frames with it. Where the arithmetic is (fit, zoom, pan, limits) is <see cref="ImageViewTransform"/>.
/// </summary>
public sealed class ImageViewer : Control
{
    public static readonly StyledProperty<CameraFrame?> FrameProperty = AvaloniaProperty.Register<ImageViewer, CameraFrame?>(nameof(Frame));
    public static readonly StyledProperty<bool> AutoStretchProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(AutoStretch), true);
    public static readonly StyledProperty<IReadOnlyList<DetectedStar>?> StarsProperty = AvaloniaProperty.Register<ImageViewer, IReadOnlyList<DetectedStar>?>(nameof(Stars));
    public static readonly StyledProperty<bool> ShowStarsProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(ShowStars));
    public static readonly StyledProperty<bool> IsActualSizeProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(IsActualSize), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsCustomViewProperty = AvaloniaProperty.Register<ImageViewer, bool>(nameof(IsCustomView), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<string> ZoomTextProperty = AvaloniaProperty.Register<ImageViewer, string>(nameof(ZoomText), string.Empty, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

    private static readonly IPen UsablePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x6F, 0xCB, 0x9F)), 1);
    private static readonly IPen SkippedPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE3, 0xB2, 0x5E)), 1);

    // One picture per frame, for the stretch that is shown: changing the stretch makes the other one and lets the first go, so two pictures of a big frame are never held at once.
    private sealed class Picture(WriteableBitmap bitmap, bool autoStretch)
    {
        public WriteableBitmap Bitmap { get; } = bitmap;
        public bool AutoStretch { get; } = autoStretch;
    }

    private readonly ConditionalWeakTable<CameraFrame, Picture> _pictures = new();
    private ImageViewTransform _transform = new(1, 0, 0);
    private bool _hasTransform;
    private bool _dragging;
    private Point _last;

    static ImageViewer()
    {
        AffectsRender<ImageViewer>(FrameProperty, AutoStretchProperty, StarsProperty, ShowStarsProperty);
        FocusableProperty.OverrideDefaultValue<ImageViewer>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ImageViewer>(true);
    }

    public ImageViewer() => this.RedrawWithTheme();

    public CameraFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }

    /// <summary>Show the frame with the automatic stretch; off, it is shown linear.</summary>
    public bool AutoStretch { get => GetValue(AutoStretchProperty); set => SetValue(AutoStretchProperty, value); }

    public IReadOnlyList<DetectedStar>? Stars { get => GetValue(StarsProperty); set => SetValue(StarsProperty, value); }

    public bool ShowStars { get => GetValue(ShowStarsProperty); set => SetValue(ShowStarsProperty, value); }

    /// <summary>The frame is shown pixel for pixel.</summary>
    public bool IsActualSize { get => GetValue(IsActualSizeProperty); set => SetValue(IsActualSizeProperty, value); }

    /// <summary>The user zoomed or panned: the view is neither fitted nor at 1:1 until one of them is asked for again.</summary>
    public bool IsCustomView { get => GetValue(IsCustomViewProperty); set => SetValue(IsCustomViewProperty, value); }

    /// <summary>The zoom as a percentage ("100 %" is 1:1), for a label; written by the viewer.</summary>
    public string ZoomText { get => GetValue(ZoomTextProperty); private set => SetValue(ZoomTextProperty, value); }

    public ImageViewTransform Transform => _transform;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FrameProperty)
        {
            // A new frame of the same size keeps where the user looked; another size starts from the mode that is set.
            var before = change.OldValue as CameraFrame;
            var after = change.NewValue as CameraFrame;
            if (before is null || after is null || before.Width != after.Width || before.Height != after.Height)
            {
                _hasTransform = false;
            }

            InvalidateVisual();
        }
        else if (change.Property == IsActualSizeProperty || change.Property == IsCustomViewProperty)
        {
            if (!IsCustomView)
            {
                _hasTransform = false;
            }

            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!IsCustomView)
        {
            _hasTransform = false;
        }
    }

    private void EnsureTransform(CameraFrame frame)
    {
        if (_hasTransform)
        {
            return;
        }

        _transform = IsActualSize
            ? ImageViewTransform.ActualSize(frame.Width, frame.Height, Bounds.Width, Bounds.Height)
            : ImageViewTransform.Fit(frame.Width, frame.Height, Bounds.Width, Bounds.Height, 4);
        _hasTransform = true;
        ZoomText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_transform.Scale * 100:0} %");
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // so that the whole area takes the pointer
        if (Frame is not { } frame || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        EnsureTransform(frame);
        var picture = PictureOf(frame);
        var destination = new Rect(_transform.OffsetX, _transform.OffsetY, frame.Width * _transform.Scale, frame.Height * _transform.Scale);

        // Zoomed in far enough that pixels are bigger than a few view pixels, they are shown as squares, not blurred together.
        var interpolation = _transform.Scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
        {
            context.DrawImage(picture.Bitmap, new Rect(0, 0, frame.Width, frame.Height), destination);
        }

        if (ShowStars && Stars is { Count: > 0 } stars)
        {
            // Thin and a little transparent: the overlay marks the stars, it does not compete with them.
            var usablePen = this.Pen("SideraOnPreviewOkColor", Color.FromRgb(0x6F, 0xCB, 0x9F), 1, 0xC8);
            var skippedPen = this.Pen("SideraOnPreviewWarnColor", Color.FromRgb(0xE3, 0xB2, 0x5E), 1, 0xC8);
            foreach (var star in stars)
            {
                var (x, y) = _transform.ToView(star.X + 0.5, star.Y + 0.5);
                var radius = Math.Max(star.Hfr * _transform.Scale, 4);
                context.DrawEllipse(null, star.IsUsable ? usablePen : skippedPen, new Point(x, y), radius, radius);
            }
        }
    }

    private Picture PictureOf(CameraFrame frame)
    {
        if (_pictures.TryGetValue(frame, out var existing) && existing.AutoStretch == AutoStretch)
        {
            return existing;
        }

        _pictures.Remove(frame);
        var made = new Picture(ToBitmap(frame, AutoStretch), AutoStretch);
        _pictures.Add(frame, made);
        return made;
    }

    private static WriteableBitmap ToBitmap(CameraFrame frame, bool autoStretch)
    {
        var gray = ImageStretch.ToGray8(frame, autoStretch);

        // Expanded to opaque BGRA so it works on every rendering backend.
        var bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var buffer = bitmap.Lock();
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

            Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
        }

        return bitmap;
    }

    private void Changed(ImageViewTransform next)
    {
        if (Frame is not { } frame)
        {
            return;
        }

        _transform = next.Clamp(frame.Width, frame.Height, Bounds.Width, Bounds.Height);
        _hasTransform = true;
        ZoomText = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_transform.Scale * 100:0} %");
        if (!IsCustomView)
        {
            // The user took the view into their own hands: neither Fit nor 1:1 describes it any more.
            IsCustomView = true;
        }

        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Frame is null)
        {
            return;
        }

        EnsureTransform(Frame);
        var at = e.GetPosition(this);
        var factor = Math.Pow(1.2, Math.Clamp(e.Delta.Y, -4, 4));
        Changed(_transform.ZoomAt(at.X, at.Y, factor));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Frame is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && !e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            return;
        }

        _dragging = true;
        _last = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || Frame is null)
        {
            return;
        }

        var now = e.GetPosition(this);
        EnsureTransform(Frame);
        Changed(_transform.Pan(now.X - _last.X, now.Y - _last.Y));
        _last = now;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
        }
    }
}

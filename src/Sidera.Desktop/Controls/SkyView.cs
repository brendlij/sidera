using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Desktop.ViewModels;
using Sidera.Sky;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The sky of the framing workspace: a survey picture with the field of the rig drawn over it. The picture and the field are both placed from the same view of the sky
/// (<see cref="Viewport"/>), so the field is a rotated rectangle on the sky and not a box of coordinates, and it keeps its place while the picture is replaced. Dragging the
/// field moves the framing, dragging elsewhere pans, the wheel zooms and Ctrl and the wheel turn the frame. It computes nothing about the sky itself and touches no network:
/// it draws what it is given and reports what the user did through commands.
/// </summary>
public sealed class SkyView : Control
{
    public static readonly StyledProperty<SkyImage?> ImageProperty = AvaloniaProperty.Register<SkyView, SkyImage?>(nameof(Image));
    public static readonly StyledProperty<SkyViewport?> ImageViewportProperty = AvaloniaProperty.Register<SkyView, SkyViewport?>(nameof(ImageViewport));
    public static readonly StyledProperty<long> ImageVersionProperty = AvaloniaProperty.Register<SkyView, long>(nameof(ImageVersion));
    public static readonly StyledProperty<SkyViewport?> ViewportProperty = AvaloniaProperty.Register<SkyView, SkyViewport?>(nameof(Viewport));
    public static readonly StyledProperty<RigField?> FieldProperty = AvaloniaProperty.Register<SkyView, RigField?>(nameof(Field));
    public static readonly StyledProperty<FramingTarget?> TargetProperty = AvaloniaProperty.Register<SkyView, FramingTarget?>(nameof(Target));
    public static readonly StyledProperty<double> BrightnessProperty = AvaloniaProperty.Register<SkyView, double>(nameof(Brightness));
    public static readonly StyledProperty<string?> NoteProperty = AvaloniaProperty.Register<SkyView, string?>(nameof(Note));
    public static readonly StyledProperty<ICommand?> PanCommandProperty = AvaloniaProperty.Register<SkyView, ICommand?>(nameof(PanCommand));
    public static readonly StyledProperty<ICommand?> ZoomCommandProperty = AvaloniaProperty.Register<SkyView, ICommand?>(nameof(ZoomCommand));
    public static readonly StyledProperty<ICommand?> MoveTargetCommandProperty = AvaloniaProperty.Register<SkyView, ICommand?>(nameof(MoveTargetCommand));
    public static readonly StyledProperty<ICommand?> RotateCommandProperty = AvaloniaProperty.Register<SkyView, ICommand?>(nameof(RotateCommand));

    private WriteableBitmap? _bitmap;
    private long _bitmapVersion = -1;
    private double _bitmapBrightness = -1;
    private bool _dragTarget;
    private bool _panning;
    private Point _last;
    private (double X, double Y) _grabOffset;
    private readonly Dictionary<string, FormattedText> _texts = [];

    static SkyView()
    {
        AffectsRender<SkyView>(ImageProperty, ImageViewportProperty, ImageVersionProperty, ViewportProperty, FieldProperty, TargetProperty, NoteProperty, BrightnessProperty);
        FocusableProperty.OverrideDefaultValue<SkyView>(true);
    }

    public SkyImage? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    /// <summary>The view the picture was made for; the picture is drawn in its place even when the view has moved on since.</summary>
    public SkyViewport? ImageViewport { get => GetValue(ImageViewportProperty); set => SetValue(ImageViewportProperty, value); }

    public long ImageVersion { get => GetValue(ImageVersionProperty); set => SetValue(ImageVersionProperty, value); }

    public SkyViewport? Viewport { get => GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }

    public RigField? Field { get => GetValue(FieldProperty); set => SetValue(FieldProperty, value); }

    public FramingTarget? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <summary>How much the picture is lifted, from 0 (as the survey is) to 1.</summary>
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    public string? Note { get => GetValue(NoteProperty); set => SetValue(NoteProperty, value); }

    public ICommand? PanCommand { get => GetValue(PanCommandProperty); set => SetValue(PanCommandProperty, value); }

    public ICommand? ZoomCommand { get => GetValue(ZoomCommandProperty); set => SetValue(ZoomCommandProperty, value); }

    public ICommand? MoveTargetCommand { get => GetValue(MoveTargetCommandProperty); set => SetValue(MoveTargetCommandProperty, value); }

    public ICommand? RotateCommand { get => GetValue(RotateCommandProperty); set => SetValue(RotateCommandProperty, value); }

    // The picture is as large as the view in pixels and is fitted into the control, letterboxed; this maps between the two.
    private (double Scale, double OffsetX, double OffsetY)? Layout()
    {
        if (Viewport is not { } view || Bounds.Width < 10 || Bounds.Height < 10)
        {
            return null;
        }

        var scale = Math.Min(Bounds.Width / view.WidthPixels, Bounds.Height / view.HeightPixels);
        return (scale, (Bounds.Width - view.WidthPixels * scale) / 2, (Bounds.Height - view.HeightPixels * scale) / 2);
    }

    private Point ToControl((double X, double Y) pixel, (double Scale, double OffsetX, double OffsetY) layout) =>
        new(pixel.X * layout.Scale + layout.OffsetX, pixel.Y * layout.Scale + layout.OffsetY);

    private (double X, double Y) ToViewPixel(Point point, (double Scale, double OffsetX, double OffsetY) layout) =>
        ((point.X - layout.OffsetX) / layout.Scale, (point.Y - layout.OffsetY) / layout.Scale);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(8, 9, 14)), new Rect(Bounds.Size));
        if (Layout() is not { } layout || Viewport is not { } view)
        {
            return;
        }

        using (context.PushClip(new Rect(Bounds.Size)))
        {
            DrawImage(context, view, layout);
            DrawField(context, view, layout);
            DrawNote(context);
        }
    }

    private void DrawImage(DrawingContext context, SkyViewport view, (double Scale, double OffsetX, double OffsetY) layout)
    {
        if (Image is not { } image || ImageViewport is not { } made || image.HasNoImagery)
        {
            return;
        }

        if (_bitmap is null || _bitmapVersion != ImageVersion || _bitmapBrightness != Brightness || _bitmap.PixelSize.Width != image.Width)
        {
            _bitmap?.Dispose();
            _bitmap = ToBitmap(image, Brightness);
            _bitmapVersion = ImageVersion;
            _bitmapBrightness = Brightness;
        }

        // Where the picture belongs in the current view: its center is a position on the sky, and its scale may differ if the view was zoomed since.
        if (view.ToPixel(made.Center) is not { } center)
        {
            return;
        }

        var ratio = made.DegreesPerPixel / view.DegreesPerPixel;
        var width = image.Width * ratio;
        var height = image.Height * ratio;
        var topLeft = ToControl((center.X - width / 2, center.Y - height / 2), layout);
        context.DrawImage(_bitmap, new Rect(0, 0, image.Width, image.Height), new Rect(topLeft.X, topLeft.Y, width * layout.Scale, height * layout.Scale));
    }

    private static WriteableBitmap ToBitmap(SkyImage image, double brightness)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var buffer = bitmap.Lock();
        var rowBytes = image.Width * 4;
        var source = image.Rgba;
        if (brightness > 0.001)
        {
            // The picture is lifted through a table of 256 values; the survey's own pixels are not changed, so the slider can go back.
            var table = FramingLabels.BrightnessTable(brightness);
            source = new byte[image.Rgba.Length];
            for (var i = 0; i < source.Length; i += 4)
            {
                source[i] = table[image.Rgba[i]];
                source[i + 1] = table[image.Rgba[i + 1]];
                source[i + 2] = table[image.Rgba[i + 2]];
                source[i + 3] = image.Rgba[i + 3];
            }
        }

        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(source, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        }

        return bitmap;
    }

    private void DrawField(DrawingContext context, SkyViewport view, (double Scale, double OffsetX, double OffsetY) layout)
    {
        if (Target is not { } target)
        {
            return;
        }

        var accent = new SolidColorBrush(Color.FromRgb(124, 126, 255));
        if (view.ToPixel(target.Center) is { } middle)
        {
            var c = ToControl(middle, layout);
            var pen = new Pen(accent, 1);
            context.DrawLine(pen, new Point(c.X - 8, c.Y), new Point(c.X + 8, c.Y));
            context.DrawLine(pen, new Point(c.X, c.Y - 8), new Point(c.X, c.Y + 8));
        }

        if (Field is not { } field || view.Outline(target.Center, field, target.DesiredRotationDegrees) is not { Count: 4 } outline)
        {
            return;
        }

        var points = new List<Point>(4);
        foreach (var corner in outline)
        {
            points.Add(ToControl(corner, layout));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], true);
            for (var i = 1; i < 4; i++)
            {
                g.LineTo(points[i]);
            }

            g.EndFigure(true);
        }

        // Only the border: the sky is seen through the frame.
        context.DrawGeometry(null, new Pen(accent, 2), geometry);

        // The size of the field, written on the frame just inside its lower edge.
        var label = FramingLabels.Field(field);
        if (label.Length > 0)
        {
            var bottomMiddle = new Point((points[2].X + points[3].X) / 2, (points[2].Y + points[3].Y) / 2);
            var toCenter = new Vector((points[0].X + points[2].X) / 2 - bottomMiddle.X, (points[0].Y + points[2].Y) / 2 - bottomMiddle.Y);
            if (toCenter.Length > 1)
            {
                Text(context, label, accent, bottomMiddle + toCenter / toCenter.Length * 14, centered: true, backdrop: true);
            }
        }

        // The top of the frame: a short mark from the middle of the top edge, so that the rotation can be seen.
        var topMiddle = new Point((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2);
        var frameMiddle = new Point((points[0].X + points[2].X) / 2, (points[0].Y + points[2].Y) / 2);
        var outward = new Vector(topMiddle.X - frameMiddle.X, topMiddle.Y - frameMiddle.Y);
        if (outward.Length > 1)
        {
            var unit = outward / outward.Length;
            context.DrawLine(new Pen(accent, 2), topMiddle, topMiddle + unit * 14);
            Text(context, FramingLabels.Top(target.DesiredRotationDegrees), accent, topMiddle + unit * 28, centered: true, backdrop: true);
        }
    }

    private void DrawNote(DrawingContext context)
    {
        if (Target is null)
        {
            return;
        }

        var note = Image is null || Image.HasNoImagery ? (Note is { Length: > 0 } n ? n : "No imagery for this view") : Note;
        if (!string.IsNullOrEmpty(note))
        {
            Text(context, note, new SolidColorBrush(Color.FromRgb(160, 163, 180)), new Point(Bounds.Width / 2, Bounds.Height - 16), centered: true);
        }
    }

    private void Text(DrawingContext context, string value, IBrush brush, Point at, bool centered, bool backdrop = false)
    {
        var key = value + "|" + (brush as ISolidColorBrush)?.Color;
        if (!_texts.TryGetValue(key, out var text))
        {
            if (_texts.Count > 32)
            {
                _texts.Clear();
            }

            text = _texts[key] = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 13, brush);
        }

        var origin = new Point(centered ? at.X - text.Width / 2 : at.X, at.Y - text.Height / 2);
        if (backdrop)
        {
            // A dark plate behind the words, so that they can be read on a bright part of the sky.
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 8, 9, 14)), null, new Rect(origin.X - 4, origin.Y - 1, text.Width + 8, text.Height + 2), 3, 3);
        }

        context.DrawText(text, origin);
    }

    // ---- Pointer

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Layout() is not { } layout || Viewport is not { } view || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        var pixel = ToViewPixel(point, layout);
        _last = point;
        _dragTarget = false;
        if (Target is { } target && Field is { } field && view.Outline(target.Center, field, target.DesiredRotationDegrees) is { Count: 4 } outline && Inside(outline, pixel)
            && view.ToPixel(target.Center) is { } middle)
        {
            _dragTarget = true;
            _grabOffset = (middle.X - pixel.X, middle.Y - pixel.Y);
        }
        else
        {
            _panning = true;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Layout() is not { } layout || Viewport is not { } view || (!_dragTarget && !_panning))
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_dragTarget)
        {
            var pixel = ToViewPixel(point, layout);
            var sky = view.ToSky(pixel.X + _grabOffset.X, pixel.Y + _grabOffset.Y);
            if (MoveTargetCommand?.CanExecute(sky) == true)
            {
                MoveTargetCommand.Execute(sky);
            }
        }
        else
        {
            var delta = new ViewDelta((point.X - _last.X) / layout.Scale, (point.Y - _last.Y) / layout.Scale);
            if (PanCommand?.CanExecute(delta) == true)
            {
                PanCommand.Execute(delta);
            }
        }

        _last = point;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragTarget = false;
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5.0 : 1.0;
            var degrees = e.Delta.Y > 0 ? step : -step;
            if (RotateCommand?.CanExecute(degrees) == true)
            {
                RotateCommand.Execute(degrees);
            }
        }
        else
        {
            var factor = e.Delta.Y > 0 ? 0.8 : 1.25;
            if (ZoomCommand?.CanExecute(factor) == true)
            {
                ZoomCommand.Execute(factor);
            }
        }

        e.Handled = true;
    }

    // Whether a point is inside the quadrilateral (the field): the crossings of a ray to the right.
    private static bool Inside(IReadOnlyList<(double X, double Y)> polygon, (double X, double Y) point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (xi, yi) = polygon[i];
            var (xj, yj) = polygon[j];
            if (yi > point.Y != yj > point.Y && point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}

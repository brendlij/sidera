using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sidera.Desktop.Help;

namespace Sidera.Desktop.Controls;

/// <summary>
/// A picture of Help with numbered markers over it. The picture is a file of <c>Assets/Help</c>; the markers come from the article (fractions of the picture), so a picture can be taken again without marking
/// it again. A picture that is missing leaves nothing: the article reads without it.
/// </summary>
public sealed class CalloutImage : Control
{
    public static readonly StyledProperty<string?> ResourceProperty = AvaloniaProperty.Register<CalloutImage, string?>(nameof(Resource));

    public static readonly StyledProperty<IReadOnlyList<HelpCallout>?> CalloutsProperty = AvaloniaProperty.Register<CalloutImage, IReadOnlyList<HelpCallout>?>(nameof(Callouts));

    private const double PictureMaxWidth = 900;
    private const double MarkerRadius = 10;
    private Bitmap? _bitmap;

    static CalloutImage()
    {
        AffectsMeasure<CalloutImage>(ResourceProperty);
        AffectsRender<CalloutImage>(ResourceProperty, CalloutsProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<CalloutImage>(false);
        ClipToBoundsProperty.OverrideDefaultValue<CalloutImage>(true);
    }

    public CalloutImage() => this.RedrawWithTheme();

    public string? Resource { get => GetValue(ResourceProperty); set => SetValue(ResourceProperty, value); }

    public IReadOnlyList<HelpCallout>? Callouts { get => GetValue(CalloutsProperty); set => SetValue(CalloutsProperty, value); }

    /// <summary>Whether a picture was found for <see cref="Resource"/>.</summary>
    public bool HasPicture => _bitmap is not null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResourceProperty)
        {
            _bitmap?.Dispose();
            _bitmap = Load(Resource);
        }
    }

    private static Bitmap? Load(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
        {
            return null;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Sidera.Desktop/Assets/Help/" + resource));
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is System.IO.FileNotFoundException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_bitmap is null)
        {
            return default;
        }

        var width = Math.Min(double.IsInfinity(availableSize.Width) ? PictureMaxWidth : availableSize.Width, PictureMaxWidth);
        return new Size(width, width * _bitmap.PixelSize.Height / _bitmap.PixelSize.Width);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is null || Bounds.Width < 20)
        {
            return;
        }

        var frame = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(frame, 6)))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), frame);
        }

        context.DrawRectangle(null, this.Pen("SideraLineStrongColor", Colors.Gray, 1), frame.Deflate(0.5), 6, 6);

        var fill = this.Brush("SideraAccentColor", Colors.CornflowerBlue);
        var ring = this.Pen("SideraBg0Color", Colors.Black, 2);
        var family = TextElement.GetFontFamily(this);
        foreach (var callout in Callouts ?? [])
        {
            var centre = new Point(Math.Clamp(callout.X, 0, 1) * frame.Width, Math.Clamp(callout.Y, 0, 1) * frame.Height);
            context.DrawEllipse(fill, ring, centre, MarkerRadius, MarkerRadius);
            var number = new FormattedText(
                callout.Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(family, FontStyle.Normal, FontWeight.Bold), 13, this.Brush("SideraOnAccentColor", Colors.White));
            context.DrawText(number, new Point(centre.X - number.Width / 2, centre.Y - number.Height / 2));
        }
    }
}

using System;
using System.Collections.Generic;
using Sidera.Core.Imaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sidera.Desktop.Controls;

/// <summary>
/// Draws a circle of the star's half flux radius around every detected star of a frame that is shown with
/// <c>Stretch="Uniform"</c> in the same space. Only presentation: the stars come from the analysis, nothing is measured here.
/// Usable stars are green; the ones that were left out (saturated, at the edge, elongated) are amber.
/// </summary>
public sealed class StarOverlay : Control
{
    public static readonly StyledProperty<IReadOnlyList<DetectedStar>?> StarsProperty =
        AvaloniaProperty.Register<StarOverlay, IReadOnlyList<DetectedStar>?>(nameof(Stars));

    public static readonly StyledProperty<double> FrameWidthProperty =
        AvaloniaProperty.Register<StarOverlay, double>(nameof(FrameWidth));

    public static readonly StyledProperty<double> FrameHeightProperty =
        AvaloniaProperty.Register<StarOverlay, double>(nameof(FrameHeight));

    // Thin and a little transparent: the overlay marks the stars, it does not compete with them.
    private static readonly IPen UsablePen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x6F, 0xCB, 0x9F)), 1);
    private static readonly IPen SkippedPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0xE3, 0xB2, 0x5E)), 1);

    static StarOverlay()
    {
        AffectsRender<StarOverlay>(StarsProperty, FrameWidthProperty, FrameHeightProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<StarOverlay>(false);
    }

    public IReadOnlyList<DetectedStar>? Stars
    {
        get => GetValue(StarsProperty);
        set => SetValue(StarsProperty, value);
    }

    public double FrameWidth
    {
        get => GetValue(FrameWidthProperty);
        set => SetValue(FrameWidthProperty, value);
    }

    public double FrameHeight
    {
        get => GetValue(FrameHeightProperty);
        set => SetValue(FrameHeightProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Stars is not { Count: > 0 } stars || FrameWidth <= 0 || FrameHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(Bounds.Width / FrameWidth, Bounds.Height / FrameHeight);
        var offsetX = (Bounds.Width - FrameWidth * scale) / 2;
        var offsetY = (Bounds.Height - FrameHeight * scale) / 2;
        foreach (var star in stars)
        {
            // Pixel centres are at +0.5 in the picture; the analysis counts them at whole numbers.
            var centre = new Point(offsetX + (star.X + 0.5) * scale, offsetY + (star.Y + 0.5) * scale);
            var radius = Math.Max(star.Hfr * scale, 4);
            context.DrawEllipse(null, star.IsUsable ? UsablePen : SkippedPen, centre, radius, radius);
        }
    }
}

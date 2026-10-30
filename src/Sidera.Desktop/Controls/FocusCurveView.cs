using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The samples of an autofocus run as points (focuser position across, HFR up), joined in the order of the positions, with axes and a line at the best position once the run has one. It draws what it is
/// given: the curve of the fit is not drawn because the run does not hand it over, and no point is made up. Small on purpose: not a plotting library.
/// </summary>
public sealed class FocusCurveView : Control
{
    public static readonly StyledProperty<IReadOnlyList<FocusSampleRow>?> SamplesProperty =
        AvaloniaProperty.Register<FocusCurveView, IReadOnlyList<FocusSampleRow>?>(nameof(Samples));

    public static readonly StyledProperty<int?> BestPositionProperty = AvaloniaProperty.Register<FocusCurveView, int?>(nameof(BestPosition));

    static FocusCurveView()
    {
        AffectsRender<FocusCurveView>(SamplesProperty, BestPositionProperty);
    }

    public FocusCurveView() => this.RedrawWithTheme();

    public IReadOnlyList<FocusSampleRow>? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    public int? BestPosition { get => GetValue(BestPositionProperty); set => SetValue(BestPositionProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Samples is not { Count: > 0 } samples || Bounds.Width < 60 || Bounds.Height < 60)
        {
            return;
        }

        // Room for the labels of the axes: the position below, the HFR on the left.
        const double top = 8, right = 10, bottom = 22, left = 38;
        var plot = new Rect(left, top, Bounds.Width - left - right, Bounds.Height - top - bottom);
        var gridPen = this.Pen("SideraTextSecondaryColor", Color.FromRgb(0x8F, 0xA3, 0xC0), 1, 0x30);
        var linePen = this.Pen("SideraTextSecondaryColor", Color.FromRgb(0x8F, 0xA3, 0xC0), 1.5, 0xB0);
        var bestPen = this.Pen("SideraOkColor", Color.FromRgb(0x6F, 0xCB, 0x9F), 1, 0xC8, DashStyle.Dash);
        var labelBrush = this.Brush("SideraTextSecondaryColor", Color.FromRgb(0x8F, 0xA3, 0xC0));
        var pointBrush = this.Brush("SideraTextColor", Color.FromRgb(0xC8, 0xD3, 0xE2));
        var bestBrush = this.Brush("SideraOkColor", Color.FromRgb(0x6F, 0xCB, 0x9F));

        // The curve is read by position, not by the order the samples were taken in (a search goes back and forth).
        var ordered = samples.OrderBy(s => s.Position).ToList();
        var minX = ordered[0].Position;
        var maxX = ordered[^1].Position;
        if (BestPosition is { } best)
        {
            minX = Math.Min(minX, best);
            maxX = Math.Max(maxX, best);
        }

        var spanX = Math.Max(maxX - minX, 1);

        // The HFR axis starts at 0: a flat curve then looks flat, and a deep one deep.
        var maxHfr = Math.Max(ordered.Max(s => s.Hfr) * 1.1, 0.5);
        double X(int position) => plot.Left + (position - minX) / (double)spanX * plot.Width;
        double Y(double hfr) => plot.Bottom - hfr / maxHfr * plot.Height;

        for (var i = 0; i <= 3; i++)
        {
            var hfr = maxHfr * i / 3;
            context.DrawLine(gridPen, new Point(plot.Left, Y(hfr)), new Point(plot.Right, Y(hfr)));
            var text = Label(hfr.ToString("0.0", CultureInfo.InvariantCulture), labelBrush);
            context.DrawText(text, new Point(plot.Left - 4 - text.Width, Y(hfr) - text.Height / 2));
        }

        var minText = Label(minX.ToString(CultureInfo.InvariantCulture), labelBrush);
        var maxText = Label(maxX.ToString(CultureInfo.InvariantCulture), labelBrush);
        context.DrawText(minText, new Point(plot.Left, plot.Bottom + 4));
        context.DrawText(maxText, new Point(plot.Right - maxText.Width, plot.Bottom + 4));

        if (BestPosition is { } bestPosition)
        {
            context.DrawLine(bestPen, new Point(X(bestPosition), plot.Top), new Point(X(bestPosition), plot.Bottom));
            var bestText = Label(bestPosition.ToString(CultureInfo.InvariantCulture), bestBrush);
            var x = Math.Clamp(X(bestPosition) - bestText.Width / 2, plot.Left, plot.Right - bestText.Width);
            context.DrawText(bestText, new Point(x, plot.Bottom + 4));
        }

        var points = ordered.Select(s => new Point(X(s.Position), Y(s.Hfr))).ToList();
        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(linePen, points[i - 1], points[i]);
        }

        foreach (var point in points)
        {
            context.DrawEllipse(pointBrush, null, point, 3.5, 3.5);
        }

        if (BestPosition is { } marked)
        {
            var nearest = ordered.OrderBy(s => Math.Abs(s.Position - marked)).First();
            context.DrawEllipse(bestBrush, null, new Point(X(nearest.Position), Y(nearest.Hfr)), 5, 5);
        }
    }

    private static FormattedText Label(string value, IBrush brush) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
}

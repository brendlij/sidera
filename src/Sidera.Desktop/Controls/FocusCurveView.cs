using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The samples of an autofocus run as points (focuser position across, HFR up), joined in the order they were taken, with a line at the best position once the run has one. It draws what it is
/// given: the curve of the fit is not drawn because the run does not hand it over, and no point is made up. Small on purpose: not a plotting library.
/// </summary>
public sealed class FocusCurveView : Control
{
    public static readonly StyledProperty<IReadOnlyList<FocusSampleRow>?> SamplesProperty =
        AvaloniaProperty.Register<FocusCurveView, IReadOnlyList<FocusSampleRow>?>(nameof(Samples));

    public static readonly StyledProperty<int?> BestPositionProperty = AvaloniaProperty.Register<FocusCurveView, int?>(nameof(BestPosition));

    private static readonly IPen LinePen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x8F, 0xA3, 0xC0)), 1);
    private static readonly IPen BestPen = new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0x6F, 0xCB, 0x9F)), 1, DashStyle.Dash);
    private static readonly IBrush PointBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xD3, 0xE2));
    private static readonly IBrush BestBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xCB, 0x9F));

    static FocusCurveView()
    {
        AffectsRender<FocusCurveView>(SamplesProperty, BestPositionProperty);
    }

    public IReadOnlyList<FocusSampleRow>? Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    public int? BestPosition { get => GetValue(BestPositionProperty); set => SetValue(BestPositionProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Samples is not { Count: > 0 } samples || Bounds.Width < 20 || Bounds.Height < 20)
        {
            return;
        }

        const double margin = 8;
        var minX = samples.Min(s => s.Position);
        var maxX = samples.Max(s => s.Position);
        if (BestPosition is { } best)
        {
            minX = Math.Min(minX, best);
            maxX = Math.Max(maxX, best);
        }

        var maxHfr = samples.Max(s => s.Hfr);
        var minHfr = samples.Min(s => s.Hfr);
        var spanX = Math.Max(maxX - minX, 1);
        var spanHfr = Math.Max(maxHfr - minHfr, 0.01);
        double X(int position) => margin + (position - minX) / spanX * (Bounds.Width - 2 * margin);
        double Y(double hfr) => Bounds.Height - margin - (hfr - minHfr) / spanHfr * (Bounds.Height - 2 * margin);

        if (BestPosition is { } bestPosition)
        {
            context.DrawLine(BestPen, new Point(X(bestPosition), margin), new Point(X(bestPosition), Bounds.Height - margin));
        }

        var points = samples.Select(s => new Point(X(s.Position), Y(s.Hfr))).ToList();
        for (var i = 1; i < points.Count; i++)
        {
            context.DrawLine(LinePen, points[i - 1], points[i]);
        }

        foreach (var point in points)
        {
            context.DrawEllipse(PointBrush, null, point, 3, 3);
        }

        if (BestPosition is { } marked && samples.OrderBy(s => Math.Abs(s.Position - marked)).First() is { } nearest)
        {
            context.DrawEllipse(BestBrush, null, new Point(X(nearest.Position), Y(nearest.Hfr)), 4, 4);
        }
    }
}

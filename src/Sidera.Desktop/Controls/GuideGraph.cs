using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sidera.Core.Guiding;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The guide graph: the right ascension and the declination error of the recent guide samples against time, with a line at zero. Flat and
/// quiet: no frame, a few faint grid lines, the two series in two colours. It draws what <see cref="History"/> holds, over the last
/// <see cref="WindowSeconds"/>, and draws again when <see cref="Version"/> changes; it keeps one list of samples that it reuses, and draws
/// with line segments, so a frame allocates next to nothing. It runs on the UI thread and never touches a guider.
/// </summary>
public sealed class GuideGraph : Control
{
    public static readonly StyledProperty<GuidingHistory?> HistoryProperty =
        AvaloniaProperty.Register<GuideGraph, GuidingHistory?>(nameof(History));

    public static readonly StyledProperty<double> WindowSecondsProperty =
        AvaloniaProperty.Register<GuideGraph, double>(nameof(WindowSeconds), 120);

    public static readonly StyledProperty<long> VersionProperty =
        AvaloniaProperty.Register<GuideGraph, long>(nameof(Version));

    public static readonly StyledProperty<bool> InArcsecondsProperty =
        AvaloniaProperty.Register<GuideGraph, bool>(nameof(InArcseconds), true);

    private static readonly double[] Ranges = [0.5, 1, 2, 3, 5, 10, 20, 50];

    private readonly List<GuidingSample> _samples = new(1024);
    private readonly Dictionary<string, FormattedText> _labels = new();

    static GuideGraph()
    {
        AffectsRender<GuideGraph>(HistoryProperty, WindowSecondsProperty, VersionProperty, InArcsecondsProperty);
    }

    public GuideGraph() => this.RedrawWithTheme();

    public GuidingHistory? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>How many seconds the graph shows, ending with the newest sample.</summary>
    public double WindowSeconds
    {
        get => GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    /// <summary>Changes whenever the history did: the graph draws again then.</summary>
    public long Version
    {
        get => GetValue(VersionProperty);
        set => SetValue(VersionProperty, value);
    }

    /// <summary>The errors are drawn in arcseconds; when they are not known the pixels are drawn.</summary>
    public bool InArcseconds
    {
        get => GetValue(InArcsecondsProperty);
        set => SetValue(InArcsecondsProperty, value);
    }

    /// <summary>The range the graph chose for the samples it showed last: from minus this to plus this, in the unit of the graph.</summary>
    public double VisibleRange { get; private set; } = Ranges[1];

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 200 : Math.Min(availableSize.Height, 220));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var size = Bounds.Size;
        if (size.Width < 80 || size.Height < 60)
        {
            return;
        }

        var text = Brush("SideraTextSecondary", Brushes.Gray);
        var line = Brush("SideraLine", Brushes.DimGray);
        var strong = Brush("SideraLineStrong", Brushes.Gray);
        var raBrush = Brush("SideraAccent", Brushes.CornflowerBlue);
        var decBrush = Brush("SideraWarn", Brushes.Orange);

        const double left = 46, right = 10, top = 10, bottom = 24;
        var plot = new Rect(left, top, size.Width - left - right, size.Height - top - bottom);

        _samples.Clear();
        var window = TimeSpan.FromSeconds(Math.Max(10, WindowSeconds));
        History?.CopyRecent(_samples, window);

        var arcsec = InArcseconds;
        double Ra(GuidingSample s) => (arcsec ? s.RaErrorArcsec : s.RaErrorPixels) ?? double.NaN;
        double Dec(GuidingSample s) => (arcsec ? s.DecErrorArcsec : s.DecErrorPixels) ?? double.NaN;

        var largest = 0.0;
        foreach (var s in _samples)
        {
            var ra = Ra(s);
            var dec = Dec(s);
            largest = Math.Max(largest, Math.Max(double.IsNaN(ra) ? 0 : Math.Abs(ra), double.IsNaN(dec) ? 0 : Math.Abs(dec)));
        }

        var range = Ranges[^1];
        foreach (var candidate in Ranges)
        {
            if (largest * 1.05 <= candidate)
            {
                range = candidate;
                break;
            }
        }

        VisibleRange = range;
        var unit = arcsec ? "\"" : " px";
        double Y(double value) => plot.Center.Y - Math.Clamp(value / range, -1, 1) * plot.Height / 2;

        // Grid: the zero line, and the lines at half and at the full range, faint.
        var zeroPen = new Pen(strong, 1);
        var gridPen = new Pen(line, 1, dashStyle: new DashStyle([2, 4], 0));
        context.DrawLine(gridPen, new Point(plot.Left, Y(range)), new Point(plot.Right, Y(range)));
        context.DrawLine(gridPen, new Point(plot.Left, Y(-range)), new Point(plot.Right, Y(-range)));
        context.DrawLine(gridPen, new Point(plot.Left, Y(range / 2)), new Point(plot.Right, Y(range / 2)));
        context.DrawLine(gridPen, new Point(plot.Left, Y(-range / 2)), new Point(plot.Right, Y(-range / 2)));
        context.DrawLine(zeroPen, new Point(plot.Left, Y(0)), new Point(plot.Right, Y(0)));
        Label(context, text, string.Create(CultureInfo.InvariantCulture, $"+{range:0.##}{unit}"), new Point(plot.Left - 6, Y(range)), alignRight: true);
        Label(context, text, "0" + unit, new Point(plot.Left - 6, Y(0)), alignRight: true);
        Label(context, text, string.Create(CultureInfo.InvariantCulture, $"-{range:0.##}{unit}"), new Point(plot.Left - 6, Y(-range)), alignRight: true);
        Label(context, text, string.Create(CultureInfo.InvariantCulture, $"last {window.TotalSeconds:0} s"), new Point(plot.Center.X, plot.Bottom + 14), centered: true);

        if (_samples.Count > 1)
        {
            var end = _samples[^1].Time;
            double X(DateTimeOffset time) => plot.Right - (end - time).TotalSeconds / window.TotalSeconds * plot.Width;
            var raPen = new Pen(raBrush, 1.5);
            var decPen = new Pen(decBrush, 1.5);
            for (var i = 1; i < _samples.Count; i++)
            {
                var a = _samples[i - 1];
                var b = _samples[i];
                if ((b.Time - a.Time).TotalSeconds > 10)
                {
                    continue; // a gap: nothing was measured in between, nothing is drawn
                }

                Segment(context, raPen, X(a.Time), Ra(a), X(b.Time), Ra(b), Y);
                Segment(context, decPen, X(a.Time), Dec(a), X(b.Time), Dec(b), Y);
            }
        }

        Label(context, raBrush, "RA", new Point(plot.Right - 56, plot.Top + 8), centered: false);
        Label(context, decBrush, "Dec", new Point(plot.Right - 28, plot.Top + 8), centered: false);
    }

    private static void Segment(DrawingContext context, IPen pen, double x1, double v1, double x2, double v2, Func<double, double> y)
    {
        if (double.IsNaN(v1) || double.IsNaN(v2))
        {
            return; // unknown is not zero: the line is not drawn through it
        }

        context.DrawLine(pen, new Point(x1, y(v1)), new Point(x2, y(v2)));
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : fallback;

    // Labels are formatted once per text and kept; a graph that keeps its range makes none after the first frame.
    private void Label(DrawingContext context, IBrush brush, string value, Point at, bool alignRight = false, bool centered = false)
    {
        var key = value + "|" + (brush as ISolidColorBrush)?.Color;
        if (!_labels.TryGetValue(key, out var formatted))
        {
            if (_labels.Count > 64)
            {
                _labels.Clear();
            }

            formatted = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, brush);
            _labels[key] = formatted;
        }

        var x = alignRight ? at.X - formatted.Width : centered ? at.X - formatted.Width / 2 : at.X;
        context.DrawText(formatted, new Point(x, at.Y - formatted.Height / 2));
    }
}

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Sidera.Desktop.Themes;

namespace Sidera.Desktop.Controls;

/// <summary>
/// A small page in the colours of a theme, for the choice of the theme. It draws itself from the palette of that theme (read from the resources of the application for the variant of the theme), so it
/// shows the theme whatever theme is running.
/// </summary>
public sealed class ThemePreview : Control
{
    public static readonly StyledProperty<SideraTheme?> ShownProperty = AvaloniaProperty.Register<ThemePreview, SideraTheme?>(nameof(Shown));

    static ThemePreview()
    {
        AffectsRender<ThemePreview>(ShownProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<ThemePreview>(false);
    }

    public SideraTheme? Shown { get => GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Shown is not { } theme || Bounds.Width < 80 || Bounds.Height < 60)
        {
            return;
        }

        IBrush Fill(string key) => new SolidColorBrush(Palette(theme, key));
        IPen Line() => new Pen(Fill("SideraLineColor"), 1);
        var family = TextElement.GetFontFamily(this);
        FormattedText Text(string value, string key, double size, FontWeight weight = FontWeight.Normal) =>
            new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, Fill(key));

        var frame = new Rect(0.5, 0.5, Bounds.Width - 1, Bounds.Height - 1);
        using (context.PushClip(new RoundedRect(frame, 6)))
        {
            context.DrawRectangle(Fill("SideraBg0Color"), null, frame);

            // The sidebar: one entry in the accent, two in the muted colour.
            context.DrawRectangle(Fill("SideraBg1Color"), null, new Rect(0, 0, 32, Bounds.Height));
            context.DrawLine(Line(), new Point(32, 0), new Point(32, Bounds.Height));
            for (var i = 0; i < 3; i++)
            {
                context.DrawRectangle(Fill(i == 0 ? "SideraAccentColor" : "SideraMutedColor"), null, new Rect(8, 12 + i * 12, 16, 4), 2, 2);
            }

            context.DrawText(Text("Sequence", "SideraTextColor", 13, FontWeight.SemiBold), new Point(44, 10));

            var chip = new Rect(44, 32, Math.Max(Bounds.Width - 44 - 12, 40), 24);
            context.DrawRectangle(Fill("SideraBg2Color"), Line(), chip, 4, 4);
            var chipText = Text("M 31 · 12 of 40", "SideraTextSecondaryColor", 12);
            context.DrawText(chipText, new Point(chip.X + 8, chip.Y + (chip.Height - chipText.Height) / 2));

            var button = new Rect(44, 64, 56, 24);
            context.DrawRectangle(Fill("SideraAccentColor"), null, button, 4, 4);
            var buttonText = Text("Start", "SideraOnAccentColor", 12);
            context.DrawText(buttonText, new Point(button.X + (button.Width - buttonText.Width) / 2, button.Y + (button.Height - buttonText.Height) / 2));

            var dots = new[] { "SideraOkColor", "SideraWarnColor", "SideraDangerColor" };
            for (var i = 0; i < dots.Length; i++)
            {
                context.DrawEllipse(Fill(dots[i]), null, new Point(button.Right + 16 + i * 16, button.Center.Y), 4, 4);
            }
        }

        context.DrawRectangle(null, Line(), frame, 6, 6);
    }

    private static Color Palette(SideraTheme theme, string key) =>
        Application.Current is { } application && application.TryGetResource(key, theme.Variant, out var found) && found is Color color ? color : Colors.Magenta;
}

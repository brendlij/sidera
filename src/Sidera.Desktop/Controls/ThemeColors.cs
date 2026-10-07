using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sidera.Desktop.Controls;

/// <summary>
/// The colours of the theme that is running, for the controls that draw themselves. A control asks for the key of a colour of the palette (for example <c>SideraOkColor</c>) and gets the
/// fallback only where it is not inside a window; a control that reads them in <c>Render</c> and calls <see cref="RedrawWithTheme"/> follows a change of theme.
/// </summary>
internal static class ThemeColors
{
    /// <summary>Draws the control again when the theme changes.</summary>
    public static void RedrawWithTheme(this Control control) => control.ActualThemeVariantChanged += (_, _) => control.InvalidateVisual();

    public static Color Find(this Control control, string key, Color fallback) =>
        control.TryFindResource(key, control.ActualThemeVariant, out var found) && found is Color color ? color : fallback;

    public static IBrush Brush(this Control control, string key, Color fallback, byte alpha = 255) => new SolidColorBrush(control.Find(key, fallback), alpha / 255.0);

    public static IPen Pen(this Control control, string key, Color fallback, double thickness, byte alpha = 255, IDashStyle? dash = null) =>
        new Pen(control.Brush(key, fallback, alpha), thickness, dash);
}

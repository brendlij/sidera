using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Styling;

namespace Sidera.Desktop.Themes;

/// <summary>
/// The theme variants of Sidera that Avalonia does not have: each is a dark variant (so that the Fluent controls draw on a dark base) with a palette of its own in <c>Styles/Themes</c>. The keys of the
/// palettes in <c>Tokens.axaml</c> are these objects.
/// </summary>
public static class SideraThemeVariants
{
    public static readonly ThemeVariant Midnight = new("Midnight", ThemeVariant.Dark);
    public static readonly ThemeVariant Graphite = new("Graphite", ThemeVariant.Dark);
    public static readonly ThemeVariant RedNight = new("RedNight", ThemeVariant.Dark);
}

/// <summary>One theme the user can choose: what it is called and said to be, and whether Fluent should draw its controls on a light base.</summary>
public sealed record SideraTheme(string Id, string Name, string Description, bool IsLight)
{
    /// <summary>The variant of the application that shows this theme.</summary>
    public ThemeVariant Variant => Id switch
    {
        "light" => ThemeVariant.Light,
        "midnight" => SideraThemeVariants.Midnight,
        "graphite" => SideraThemeVariants.Graphite,
        "rednight" => SideraThemeVariants.RedNight,
        _ => ThemeVariant.Dark,
    };
}

/// <summary>The themes of Sidera, in the order they are offered. The colours of each are in <c>Styles/Themes/*.axaml</c>; this only names them.</summary>
public static class SideraThemes
{
    public const string DefaultId = "dark";

    public static IReadOnlyList<SideraTheme> All { get; } =
    [
        new("dark", "Sidera Dark", "The default: a near-black with a violet-blue accent.", false),
        new("midnight", "Midnight", "A deep blue-black with a cyan accent.", false),
        new("graphite", "Graphite", "Neutral grays that keep colour out of the way, with a teal accent.", false),
        new("rednight", "Red Night", "Black and red only, to keep your eyes dark-adapted at the telescope.", false),
        new("light", "Light", "For the day: light surfaces and a darker accent. Images stay on a dark preview.", true),
    ];

    public static SideraTheme Default => All[0];

    /// <summary>The theme with this id; <c>null</c> when there is none.</summary>
    public static SideraTheme? Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));
}

/// <summary>Shows a theme: sets the variant of the running application. Implemented by the application; the settings do not know Avalonia.</summary>
public interface IThemeApplier
{
    void Apply(SideraTheme theme);
}

/// <summary>Applies a theme to the Avalonia application.</summary>
public sealed class AvaloniaThemeApplier(Avalonia.Application application) : IThemeApplier
{
    public void Apply(SideraTheme theme) => application.RequestedThemeVariant = theme.Variant;
}

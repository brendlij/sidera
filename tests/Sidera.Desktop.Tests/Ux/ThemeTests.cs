using System.Globalization;
using System.Text.RegularExpressions;
using Sidera.Desktop.Settings;
using Sidera.Desktop.Themes;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The themes: every theme has a palette with the same keys (so that no theme leaves a colour to another), the palettes can be read (contrast), the choice is stored and shown as soon as it is made,
/// and Discard goes back to the saved one.
/// </summary>
public sealed partial class ThemeTests : IDisposable
{
    private sealed class RecordingApplier : IThemeApplier
    {
        public List<string> Applied { get; } = [];

        public void Apply(SideraTheme theme) => Applied.Add(theme.Id);
    }

    private static readonly Dictionary<string, string> PaletteFiles = new()
    {
        ["dark"] = "Dark", ["midnight"] = "Midnight", ["graphite"] = "Graphite", ["rednight"] = "RedNight", ["light"] = "Light",
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-themes-" + Guid.NewGuid().ToString("N"));

    public ThemeTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private static string Styles()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sidera.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Sidera.Desktop", "Styles");
            }
        }

        throw new DirectoryNotFoundException("The solution folder was not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("<Color x:Key=\"(?<key>[A-Za-z0-9]+)\">#(?<hex>[0-9A-Fa-f]{6,8})</Color>")]
    private static partial Regex ColorLine();

    private static Dictionary<string, string> Palette(string theme) =>
        ColorLine().Matches(File.ReadAllText(Path.Combine(Styles(), "Themes", PaletteFiles[theme] + ".axaml"))).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["hex"].Value);

    private static double Luminance(string hex)
    {
        double Channel(int at)
        {
            var value = int.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    private static double Contrast(string a, string b)
    {
        var (light, dark) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (light + 0.05) / (dark + 0.05);
    }

    private SiteService NewSettings()
    {
        var service = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        service.Load();
        return service;
    }

    // ---- the catalog and the palettes

    [Fact]
    public void TheThemes_HaveUniqueIds_AndTheDefaultIsTheFirst()
    {
        Assert.Equal(SideraThemes.All.Count, SideraThemes.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(SideraThemes.DefaultId, SideraThemes.All[0].Id);
        Assert.Same(SideraThemes.Default, SideraThemes.Find(SideraThemes.DefaultId));
        Assert.Null(SideraThemes.Find("neon"));
        Assert.Null(SideraThemes.Find(null));
        Assert.All(SideraThemes.All, theme => Assert.Contains(theme.Id, PaletteFiles.Keys));
        Assert.Equal(PaletteFiles.Count, SideraThemes.All.Count);
    }

    [Fact]
    public void EveryPalette_HasTheSameKeys_AsTheDefault()
    {
        var expected = Palette("dark").Keys.Order().ToList();

        Assert.True(expected.Count > 30);
        foreach (var theme in PaletteFiles.Keys)
        {
            Assert.Equal(expected, Palette(theme).Keys.Order().ToList());
        }
    }

    [Fact]
    public void TheTokens_IncludeThePaletteOfEveryTheme()
    {
        var tokens = File.ReadAllText(Path.Combine(Styles(), "Tokens.axaml"));

        foreach (var file in PaletteFiles.Values)
        {
            Assert.Contains($"Styles/Themes/{file}.axaml", tokens, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheVariants_OfTheDarkThemesAreDark_AndOfTheLightThemeIsLight()
    {
        Assert.All(SideraThemes.All, theme =>
        {
            Assert.Equal(theme.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark, theme.Variant.InheritVariant ?? theme.Variant);
        });
        Assert.Equal(SideraThemes.All.Count, SideraThemes.All.Select(t => t.Variant).Distinct().Count());
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("midnight")]
    [InlineData("graphite")]
    [InlineData("rednight")]
    [InlineData("light")]
    public void TheTextCanBeRead_OnEverySurface_AndTheAccentsAndStatusesOnThePanel(string theme)
    {
        var p = Palette(theme);

        foreach (var surface in new[] { "SideraBg0Color", "SideraBg1Color", "SideraBg2Color", "SideraBg3Color" })
        {
            Assert.True(Contrast(p["SideraTextColor"], p[surface]) >= 4.5, $"{theme}: text on {surface}");
            Assert.True(Contrast(p["SideraTextSecondaryColor"], p[surface]) >= 4.5, $"{theme}: secondary text on {surface}");
        }

        Assert.True(Contrast(p["SideraMutedColor"], p["SideraBg1Color"]) >= 3, $"{theme}: muted text");
        Assert.True(Contrast(p["SideraOnAccentColor"], p["SideraAccentColor"]) >= 4.5, $"{theme}: text on the accent");
        Assert.True(Contrast(p["SideraOnAccentColor"], p["SideraAccentHoverColor"]) >= 4.5, $"{theme}: text on the accent while hovering");
        foreach (var colour in new[] { "SideraAccentColor", "SideraOkColor", "SideraWarnColor", "SideraDangerColor" })
        {
            Assert.True(Contrast(p[colour], p["SideraBg1Color"]) >= 3, $"{theme}: {colour} on the panel");
        }
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("midnight")]
    [InlineData("graphite")]
    [InlineData("rednight")]
    [InlineData("light")]
    public void WhatIsDrawnOnThePreviewSurface_CanBeSeenOnIt(string theme)
    {
        var p = Palette(theme);

        Assert.True(Contrast(p["SideraOnPreviewTextColor"], p["SideraPreviewColor"]) >= 4.5, $"{theme}: text on the preview");
        foreach (var colour in new[] { "SideraOnPreviewAccentColor", "SideraOnPreviewOkColor", "SideraOnPreviewWarnColor", "SideraOnPreviewDangerColor" })
        {
            Assert.True(Contrast(p[colour], p["SideraPreviewColor"]) >= 3, $"{theme}: {colour} on the preview");
        }
    }

    [Fact]
    public void RedNight_HasNoBlueAndNoGreen_Anywhere()
    {
        // Night vision: nothing but red and orange, so that no colour of the interface reaches the eyes' short wavelengths.
        foreach (var (key, hex) in Palette("rednight"))
        {
            var red = int.Parse(hex.AsSpan(hex.Length - 6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var green = int.Parse(hex.AsSpan(hex.Length - 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var blue = int.Parse(hex.AsSpan(hex.Length - 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            Assert.True(blue <= green && green <= red, $"{key} #{hex} is not red");
            Assert.True(green * 100 <= red * 65 && blue * 100 <= red * 65, $"{key} #{hex} has too much green or blue for red");
        }
    }

    // ---- the setting

    [Fact]
    public void WithoutAFile_TheThemeIsTheDefault_AndAnOldFileWithoutTheSectionLoadsWithIt()
    {
        new SideraSettingsStore(Path.Combine(_directory, "settings.json")).Save(new SideraSettings(new Sidera.Core.Location.ObservingSite(47.7, 7.8, 410)));

        Assert.Equal(SideraThemes.DefaultId, NewSettings().Appearance.ThemeId);
    }

    [Fact]
    public void TheTheme_IsStored_AndComesBackFromTheSameFile()
    {
        var settings = NewSettings();
        Assert.Null(settings.SetAppearance(new AppearanceSettings { ThemeId = "rednight" }).Problem);

        Assert.Equal("rednight", NewSettings().Appearance.ThemeId);
    }

    [Fact]
    public void AThemeThatDoesNotExist_IsRefused_AndNothingIsChanged()
    {
        var settings = NewSettings();
        Assert.Null(settings.SetAppearance(new AppearanceSettings { ThemeId = "midnight" }).Problem);

        var result = settings.SetAppearance(new AppearanceSettings { ThemeId = "neon" });

        Assert.False(result.Succeeded);
        Assert.Contains("Choose one of the themes", result.Problem, StringComparison.Ordinal);
        Assert.Equal("midnight", NewSettings().Appearance.ThemeId);
    }

    [Fact]
    public void AFileWithAnUnknownTheme_CannotBeUsed_AndIsLeftAsItIs()
    {
        var path = Path.Combine(_directory, "settings.json");
        var saved = System.Text.Encoding.UTF8.GetString(SideraSettingsSerializer.Serialize(SideraSettings.Empty));
        File.WriteAllText(path, Regex.Replace(saved, @"""theme""\s*:\s*""dark""", @"""theme"": ""neon"""));

        var exception = Assert.Throws<SideraSettingsException>(() => new SideraSettingsStore(path).Load());

        Assert.Contains("Choose one of the themes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("neon", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---- the tab

    [Fact]
    public void ChoosingATheme_ShowsItAtOnce_AndMakesTheTabDirty_WithoutSavingIt()
    {
        var settings = NewSettings();
        var applier = new RecordingApplier();
        var tab = new AppearanceSettingsViewModel(settings, applier);

        Assert.Equal("dark", tab.Selected.Id);
        Assert.False(tab.IsDirty);
        Assert.Equal(["dark"], applier.Applied);

        tab.Choices.Single(c => c.Id == "midnight").IsChecked = true;

        Assert.Equal("midnight", tab.Selected.Id);
        Assert.True(tab.IsDirty);
        Assert.Equal(["dark", "midnight"], applier.Applied);
        Assert.Single(tab.Choices, c => c.IsChosen);
        Assert.Equal("dark", NewSettings().Appearance.ThemeId);
    }

    [Fact]
    public void Save_KeepsTheTheme_AndDiscard_GoesBackToTheSavedOne()
    {
        var settings = NewSettings();
        var applier = new RecordingApplier();
        var tab = new AppearanceSettingsViewModel(settings, applier);

        tab.Choices.Single(c => c.Id == "graphite").IsChecked = true;
        tab.SaveCommand.Execute(null);

        Assert.False(tab.IsDirty);
        Assert.Equal("graphite", NewSettings().Appearance.ThemeId);

        tab.Choices.Single(c => c.Id == "light").IsChecked = true;
        Assert.True(tab.IsDirty);
        tab.DiscardCommand.Execute(null);

        Assert.False(tab.IsDirty);
        Assert.Equal("graphite", tab.Selected.Id);
        Assert.Equal("graphite", applier.Applied[^1]);
        Assert.Equal("graphite", NewSettings().Appearance.ThemeId);
    }

    [Fact]
    public void TheTabStartsWithTheSavedTheme()
    {
        var settings = NewSettings();
        settings.SetAppearance(new AppearanceSettings { ThemeId = "light" });
        var applier = new RecordingApplier();

        var tab = new AppearanceSettingsViewModel(settings, applier);

        Assert.Equal("light", tab.Selected.Id);
        Assert.True(tab.Choices.Single(c => c.Id == "light").IsChosen);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void TheSettingsHaveAnAppearanceTab_NotAReadOnlyRow()
    {
        var settings = new SettingsViewModel(site: NewSettings());

        Assert.Contains(settings.Tabs, tab => tab.Key == SettingsViewModel.AppearanceTab && tab.Title == "Appearance");
        Assert.NotNull(settings.Appearance);
        Assert.DoesNotContain(settings.Groups, group => group.Title == "Appearance");

        settings.SelectTab(SettingsViewModel.AppearanceTab);
        Assert.True(settings.IsAppearance);
        Assert.False(settings.IsGeneral);
    }
}

using System.Text.RegularExpressions;

namespace Astra.Desktop.Tests.Ux;

/// <summary>
/// The look of Astra comes from the theme. These tests read the views as text and keep them to that: no colour, no
/// font size and no odd spacing of their own, and no view that grows into the one big window file again.
/// </summary>
public class ThemeDisciplineTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Astra.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The solution folder was not found.");
    }

    private static IEnumerable<string> Views() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src", "Astra.Desktop", "Views"), "*.axaml", SearchOption.AllDirectories);

    [Fact]
    public void NoViewHasAColourOfItsOwn()
    {
        var offenders = Views().Where(path => Regex.IsMatch(File.ReadAllText(path), "#[0-9A-Fa-f]{6,8}\\b")).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoViewSetsAFontSize_TheTypeScaleDoes()
    {
        var offenders = Views().Where(path => Regex.IsMatch(File.ReadAllText(path), "FontSize=\"[0-9]")).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoViewUsesAnOddSpacing()
    {
        // The scale is 4 8 12 16 24 32 (and the halves of it for tight places); 7, 9, 11, 13, 15, 17, 19 are not on it.
        var odd = new Regex("(Margin|Padding|Spacing|ItemSpacing|LineSpacing|ColumnSpacing|RowSpacing)=\"(?:[^\"]*[,\\s])?(7|9|11|13|15|17|19)(?:[,\\s][^\"]*)?\"");
        var offenders = Views().Where(path => odd.IsMatch(File.ReadAllText(path))).ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheShellIsSmall_ThePagesLiveInTheirOwnViews()
    {
        var views = Path.Combine(Root(), "src", "Astra.Desktop", "Views");

        Assert.True(File.ReadAllLines(Path.Combine(views, "Shell", "MainWindow.axaml")).Length < 60);
        Assert.All(Views(), path => Assert.True(File.ReadAllLines(path).Length < 400, path));
        foreach (var page in new[]
                 {
                     "Dashboard/DashboardView", "Session/SessionView", "Session/WorkflowView", "Session/StepInspectorView",
                     "Session/MultiRigEditorView", "Session/ExecutionTracksView", "Equipment/EquipmentView", "Equipment/EquipmentModeSelector",
                     "Equipment/DeviceBrowserView", "Equipment/SelectedDeviceDetailsView", "Equipment/DeviceDetailShell",
                     "Equipment/RigBrowserView", "Equipment/RigDetailsView", "Equipment/DeviceDetails/CameraDetailsView",
                     "Equipment/DeviceDetails/FocuserDetailsView", "Equipment/DeviceDetails/FilterWheelDetailsView",
                     "Equipment/DeviceDetails/MountDetailsView", "Equipment/DeviceDetails/GuiderDetailsView",
                     "Imaging/ImagingView", "Imaging/FrameMetricsView",
                     "Diagnostics/DiagnosticsView", "Settings/SettingsView", "Shell/SidebarView",
                 })
        {
            Assert.True(File.Exists(Path.Combine(views, page + ".axaml")), page);
        }
    }

    [Fact]
    public void TheThemeIsSplitIntoItsPartsAndTheTokensCarryTheColours()
    {
        var styles = Path.Combine(Root(), "src", "Astra.Desktop", "Styles");

        foreach (var file in new[] { "AstraTheme", "Tokens", "Typography", "Surfaces", "Buttons", "Inputs", "Lists", "CustomControls" })
        {
            Assert.True(File.Exists(Path.Combine(styles, file + ".axaml")), file);
        }

        // Colours are defined once, in the tokens.
        foreach (var path in Directory.EnumerateFiles(styles, "*.axaml").Where(p => !p.EndsWith("Tokens.axaml", StringComparison.Ordinal)))
        {
            var hex = Regex.Matches(File.ReadAllText(path), "#[0-9A-Fa-f]{6,8}\\b").Select(m => m.Value).Distinct().ToList();
            Assert.True(hex.Count <= 3, $"{Path.GetFileName(path)} has its own colours: {string.Join(", ", hex)}");
        }
    }
}

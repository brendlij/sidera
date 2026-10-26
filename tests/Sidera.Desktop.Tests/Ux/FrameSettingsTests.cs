using Sidera.Core.Imaging;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>The part of Settings → Imaging that decides how frames are saved: the patterns, the example under them, and that a pattern that cannot be used is refused.</summary>
public sealed class FrameSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-framesettings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ImagingSettingsViewModel Tab()
    {
        Directory.CreateDirectory(_directory);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        return new ImagingSettingsViewModel(settings);
    }

    [Fact]
    public void ThePageStartsWithTheDefaults_AndShowsHowAFileWouldBeNamed()
    {
        var tab = Tab();

        Assert.True(tab.SaveSessionFrames);
        Assert.Equal(FrameNaming.DefaultFolderPattern, tab.FolderPatternText);
        Assert.Equal(FrameNaming.DefaultFilePattern, tab.FilePatternText);
        Assert.StartsWith("For example: ", tab.ExampleText);
        Assert.EndsWith(Path.Combine("M31", "2026-10-12", "Light", "M31_Light_Ha_300s_0007.fits"), tab.ExampleText);
        Assert.Contains("{target}", tab.PartsText);
    }

    [Fact]
    public void TheExampleFollowsWhatIsTyped_AndSaysWhatIsWrongInsteadOfAnExample()
    {
        var tab = Tab();

        tab.SaveDirectoryText = @"D:\Astro";
        tab.FolderPatternText = "{night}/{target}";
        tab.FilePatternText = "{target}-{n}";
        Assert.Equal("For example: " + Path.Combine(@"D:\Astro", "2026-10-12", "M31", "M31-0007.fits"), tab.ExampleText);

        tab.FilePatternText = "{target}_{nope}";
        Assert.Contains("unknown part {nope}", tab.ExampleText);
    }

    [Fact]
    public void ThePatternsAreSaved_AndKeptForTheNextStart()
    {
        var tab = Tab();
        tab.SaveDirectoryText = _directory;
        tab.SaveSessionFrames = false;
        tab.FolderPatternText = "{type}";
        tab.FilePatternText = "{target}_{n}";

        tab.SaveCommand.Execute(null);

        Assert.False(tab.ResultIsProblem);
        var reloaded = Tab();
        Assert.False(reloaded.SaveSessionFrames);
        Assert.Equal("{type}", reloaded.FolderPatternText);
        Assert.Equal("{target}_{n}", reloaded.FilePatternText);
    }

    [Fact]
    public void APatternThatCannotBeUsed_IsRefused_AndNothingIsSaved()
    {
        var tab = Tab();
        tab.FilePatternText = "{bogus}";

        tab.SaveCommand.Execute(null);

        Assert.True(tab.ResultIsProblem);
        Assert.Contains("unknown part {bogus}", tab.ResultText);
        Assert.True(Tab().FilePatternText == FrameNaming.DefaultFilePattern);
    }
}

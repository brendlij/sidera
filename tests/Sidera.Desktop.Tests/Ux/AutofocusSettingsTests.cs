using System.Text;
using Sidera.Core.Focusing;
using Sidera.Core.Rigs;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>How autofocus searches, as the settings say it: saved, read back, refused when it makes no sense, turned into the strategy of a run, and where the positions that were in focus are kept.</summary>
public sealed class AutofocusSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-afsettings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private AutofocusSettingsViewModel Tab()
    {
        Directory.CreateDirectory(_directory);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        return new AutofocusSettingsViewModel(settings);
    }

    [Fact]
    public void TheSearchIsOnByDefault_WithAHyperbola_AndTheLastPositionRemembered()
    {
        var defaults = new AutofocusDefaults();

        Assert.True(defaults.SearchForFocus);
        Assert.Equal(FocusFitMethod.Hyperbola, defaults.FitMethod);
        Assert.True(defaults.UseRememberedPosition);
        Assert.Equal(AutofocusMode.Search, defaults.Strategy(null).Mode);
        Assert.Equal(AutofocusMode.Pattern, (defaults with { SearchForFocus = false }).Strategy(null).Mode);
    }

    [Fact]
    public void TheSettingsBecomeTheStrategyOfARun_WithTheHistory()
    {
        var history = new FocusHistory();
        var strategy = new AutofocusDefaults { FitMethod = FocusFitMethod.TrendLines, MaxSamples = 30, MaxFailedSamples = 5, UseRememberedPosition = false }.Strategy(history);

        Assert.Equal(FocusFitMethod.TrendLines, strategy.FitMethod);
        Assert.Equal((30, 5, false), (strategy.MaxSamples, strategy.MaxFailedSamples, strategy.UseRememberedPosition));
        Assert.Same(history, strategy.History);
    }

    [Fact]
    public void TheSettings_RoundTrip_AndAnOldFileGetsTheDefaults()
    {
        var settings = SideraSettings.Empty with
        {
            Autofocus = new AutofocusDefaults { SearchForFocus = false, FitMethod = FocusFitMethod.TrendLines, MaxSamples = 25, MaxFailedSamples = 1, UseRememberedPosition = false },
        };

        var loaded = SideraSettingsSerializer.Deserialize(SideraSettingsSerializer.Serialize(settings)).Autofocus;

        Assert.Equal(settings.Autofocus, loaded);
        var old = SideraSettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"format\":\"sidera-settings\",\"version\":1,\"autofocus\":{\"stepSize\":500}}")).Autofocus;
        Assert.True(old.SearchForFocus);
        Assert.Equal(500, old.StepSize);
        Assert.Equal(FocusFitMethod.Hyperbola, old.FitMethod);
    }

    [Theory]
    [InlineData(3, 7, "most samples")]
    [InlineData(101, 7, "most samples")]
    public void TheMostSamples_MustHoldTheSamplesOfAPattern_AndBeReasonable(int most, int samples, string expected)
    {
        Assert.Contains(expected, new AutofocusDefaults { SampleCount = samples, MaxSamples = most }.Problem);
        Assert.Null(new AutofocusDefaults { SampleCount = samples, MaxSamples = 0 }.Problem);
        Assert.Null(new AutofocusDefaults { SampleCount = samples, MaxSamples = 30 }.Problem);
        Assert.Contains("may fail", new AutofocusDefaults { MaxFailedSamples = 11 }.Problem);
    }

    [Fact]
    public void ThePage_ShowsTheSearchSettings_SavesThem_AndKeepsThemForTheNextStart()
    {
        var tab = Tab();
        Assert.True(tab.SearchForFocus);
        Assert.False(tab.FitTrendLines);
        Assert.Equal("0", tab.MaxSamplesText);
        Assert.Equal("3", tab.MaxFailedText);

        tab.SearchForFocus = false;
        tab.FitTrendLines = true;
        tab.MaxSamplesText = "20";
        tab.MaxFailedText = "2";
        tab.UseRememberedPosition = false;
        tab.SaveCommand.Execute(null);

        Assert.False(tab.ResultIsProblem);
        var reloaded = Tab();
        Assert.False(reloaded.SearchForFocus);
        Assert.True(reloaded.FitTrendLines);
        Assert.Equal("20", reloaded.MaxSamplesText);
        Assert.Equal("2", reloaded.MaxFailedText);
        Assert.False(reloaded.UseRememberedPosition);
    }

    [Fact]
    public void ALimitThatMakesNoSense_IsRefused_AtTheField()
    {
        var tab = Tab();
        tab.MaxFailedText = "50";

        tab.SaveCommand.Execute(null);

        Assert.True(tab.ResultIsProblem);
        Assert.Contains("may fail", tab.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePositionsInFocus_AreSavedBesideTheSettings_AndComeBackAtTheNextStart()
    {
        var store = FocusHistoryStore.BesideSettings(Path.Combine(_directory, "settings.json"));
        var history = new FocusHistory();
        history.Record(new RigId("rig.main"), 20123, 1.9, new DateTimeOffset(2026, 10, 12, 21, 0, 0, TimeSpan.Zero));
        history.Record(new RigId("rig.main"), 20150, 1.8, new DateTimeOffset(2026, 10, 12, 22, 0, 0, TimeSpan.Zero));
        history.Record(new RigId("rig.wide"), 4000, 2.4, new DateTimeOffset(2026, 10, 12, 22, 5, 0, TimeSpan.Zero));

        store.Save(history.All());

        Assert.Equal(Path.Combine(_directory, "focus-history.json"), store.Path);
        var loaded = new FocusHistory();
        loaded.Load(store.Load());
        Assert.Equal(20150, loaded.LastPosition(new RigId("rig.main")));
        Assert.Equal([20123, 20150], loaded.Of(new RigId("rig.main")).Select(e => e.Position));
        Assert.Equal(4000, loaded.LastPosition(new RigId("rig.wide")));
    }

    [Fact]
    public void AMissingOrBrokenHistoryFile_IsNothingToWorryAbout()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "focus-history.json");
        var store = new FocusHistoryStore(path);
        Assert.Empty(store.Load());

        File.WriteAllText(path, "{ this is not a history");

        Assert.Empty(store.Load());
    }
}

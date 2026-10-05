using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Diagnostics;
using Sidera.Runtime.Imaging;
using Avalonia.Controls.Primitives;
using Microsoft.Extensions.Logging;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>The imaging workspace, the diagnostics page and the settings page.</summary>
public class ImagingDiagnosticsSettingsTests
{
    // Imaging

    [Fact]
    public async Task WithoutAFrame_TheImagingPageIsEmpty_AndSaysWhatToDo()
    {
        await using var app = await UxApp.Create(UxSetup.Simple);
        var imaging = app.Vm.Imaging;

        Assert.False(imaging.HasFrame);
        Assert.False(imaging.HasMetrics);
        Assert.Equal(0, imaging.FrameWidth);
        Assert.Equal(0, imaging.FrameHeight);
        Assert.Equal(string.Empty, imaging.AnalysisSummaryText);
        Assert.False(imaging.ShowStars);
        Assert.False(imaging.HasUsableStars);
    }

    [Fact]
    public async Task TheFrameIsFittedToTheView_UntilOneToOneIsChosen()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), action => action());
        imaging.Publish(new SimulatedSky(1).Render(2.0, TimeSpan.FromSeconds(1), 1), "manual");

        Assert.True(imaging.IsFitToView);
        Assert.False(imaging.IsActualSize);
        Assert.True(double.IsNaN(imaging.DisplayWidth));
        Assert.True(double.IsNaN(imaging.DisplayHeight));
        Assert.Equal(ScrollBarVisibility.Disabled, imaging.ScrollVisibility);

        imaging.ActualSizeCommand.Execute(null);

        Assert.True(imaging.IsActualSize);
        Assert.Equal(800, imaging.DisplayWidth);
        Assert.Equal(600, imaging.DisplayHeight);
        Assert.Equal(ScrollBarVisibility.Auto, imaging.ScrollVisibility);
        Assert.Equal(800, imaging.FrameWidth);

        imaging.FitCommand.Execute(null);

        Assert.True(imaging.IsFitToView);
        Assert.True(double.IsNaN(imaging.DisplayWidth));
        await imaging.AnalysisCompletion;
    }

    [Fact]
    public async Task TheSummaryOfTheFrame_SaysHowManyStarsAndTheHfr_OnlyOnceItIsMeasured()
    {
        var analyzer = new FrameAnalyzer();
        var imaging = new ImagingViewModel(analyzer, action => action());

        imaging.Publish(new SimulatedSky(1).Render(2.0, TimeSpan.FromSeconds(1), 1), "manual");
        await imaging.AnalysisCompletion;

        Assert.Matches(@"^\d+ stars · HFR \d\.\d\d px$", imaging.AnalysisSummaryText);
        Assert.True(imaging.HasUsableStars);

        imaging.Publish(new SimulatedSky(1, new SimulatedSkyOptions(StarCount: 0)).Render(2.0, TimeSpan.FromSeconds(1), 2), "empty");
        await imaging.AnalysisCompletion;

        Assert.Equal("No stars were detected in this frame.", imaging.AnalysisSummaryText);
        Assert.False(imaging.HasUsableStars);
    }

    [Fact]
    public async Task TheOverlay_IsOffUntilAskedFor_AndIsSeparateFromTheAnalysis()
    {
        var imaging = new ImagingViewModel(new FrameAnalyzer(), action => action());
        imaging.Publish(new SimulatedSky(1).Render(2.0, TimeSpan.FromSeconds(1), 1), "manual");
        await imaging.AnalysisCompletion;

        Assert.False(imaging.ShowStars);
        Assert.NotEmpty(imaging.Stars);

        imaging.ShowStars = true;
        Assert.True(imaging.ShowStars);
    }

    // Diagnostics

    private sealed class RecordingClipboard : IClipboardService
    {
        public List<string> Texts { get; } = [];

        public Task SetTextAsync(string text)
        {
            Texts.Add(text);
            return Task.CompletedTask;
        }
    }

    private static LogInfo Info(LogSummary? summary = null) =>
        new("C:\\logs", "C:\\logs\\sidera-2026-10-03-221530.log", LogLevel.Debug, "af57f5b4", summary);

    [Fact]
    public void TheDiagnostics_ShowTheFileByItsName_AndTheFolderByItsPath()
    {
        var vm = new DiagnosticsViewModel(Info());

        Assert.Equal("sidera-2026-10-03-221530.log", vm.LogFileName);
        Assert.Equal("C:\\logs\\sidera-2026-10-03-221530.log", vm.LogFileText);
        Assert.Equal("C:\\logs", vm.LogFolderText);
        Assert.Equal("Debug", vm.LevelText);
        Assert.Equal("af57f5b4", vm.SessionText);
        Assert.DoesNotContain("+", vm.VersionText.Split(' ')[0]); // the version for people, not the whole commit
    }

    [Fact]
    public async Task CopyingThePath_PutsTheFileOnTheClipboard()
    {
        var clipboard = new RecordingClipboard();
        var vm = new DiagnosticsViewModel(Info(), clipboard: clipboard);

        await vm.CopyPathCommand.ExecuteAsync(null);

        Assert.Equal(["C:\\logs\\sidera-2026-10-03-221530.log"], clipboard.Texts);
    }

    [Fact]
    public void WithoutLogging_NothingCanBeCopiedOrOpened_AndTheNameSaysSo()
    {
        var vm = new DiagnosticsViewModel(null);

        Assert.False(vm.CopyPathCommand.CanExecute(null));
        Assert.False(vm.OpenLogFolderCommand.CanExecute(null));
        Assert.Equal("Not configured", vm.LogFileName);
        Assert.False(vm.HasSummary);
    }

    [Fact]
    public void TheWarningsAndErrorsOfTheSession_AreCountedAndTheLatestAreListed_NewestFirst()
    {
        var summary = new LogSummary();
        var vm = new DiagnosticsViewModel(Info(summary));
        Assert.True(vm.HasSummary);
        Assert.Equal("No warnings or errors", vm.ProblemsText);
        Assert.False(vm.HasProblems);

        var logger = summary.CreateLogger("Sidera.Runtime.Resources.ResourceManager");
        logger.LogInformation("not a problem");
        logger.LogWarning("Resource wait exceeded 10000 ms");
        summary.CreateLogger("Sidera.Runtime.Sequencing.SequenceRunner").LogError(new InvalidOperationException("boom"), "Sequence s failed");

        Assert.Equal("1 warning · 1 error", vm.ProblemsText);
        Assert.True(vm.HasProblems);
        Assert.Equal(["Sequence s failed", "Resource wait exceeded 10000 ms"], vm.RecentProblems.Select(p => p.Message));
        Assert.Equal([StatusKind.Error, StatusKind.Warning], vm.RecentProblems.Select(p => p.Kind));
        Assert.Equal(["SequenceRunner", "ResourceManager"], vm.RecentProblems.Select(p => p.Source));
    }

    [Fact]
    public void OnlyTheLatestFiveProblemsAreKept_ButAllAreCounted()
    {
        var summary = new LogSummary();
        var vm = new DiagnosticsViewModel(Info(summary));
        var logger = summary.CreateLogger("X");

        for (var i = 1; i <= 8; i++)
        {
            logger.LogWarning("warning {Number}", i);
        }

        Assert.Equal("8 warnings", vm.ProblemsText);
        Assert.Equal(5, vm.RecentProblems.Count);
        Assert.Equal("warning 8", vm.RecentProblems[0].Message);
        Assert.Equal("warning 4", vm.RecentProblems[^1].Message);
    }

    [Fact]
    public void AnInformationOrDebugEntry_IsNeverCountedAsAProblem()
    {
        var summary = new LogSummary();
        var logger = summary.CreateLogger("X");

        logger.LogTrace("t");
        logger.LogDebug("d");
        logger.LogInformation("i");

        Assert.Equal(0, summary.WarningCount);
        Assert.Equal(0, summary.ErrorCount);
        Assert.Empty(summary.Recent);
    }

    [Fact]
    public void AProblemLoggedFromAnotherThread_IsShownThroughTheUiThreadPost()
    {
        var summary = new LogSummary();
        var posted = new List<Action>();
        using var vm = new DiagnosticsViewModel(Info(summary), postToUi: posted.Add);

        summary.CreateLogger("X").LogError("failed");

        Assert.Single(posted);
        Assert.Equal(0, vm.ErrorCount); // not read until the UI thread runs it
        posted[0]();
        Assert.Equal(1, vm.ErrorCount);
    }

    // Settings

    [Fact]
    public async Task TheSettingsPage_ListsTheGroupsTheSettingsWillLiveIn_AndOffersOnlyTheSiteToChange()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, logInfo: Info());
        var settings = app.Vm.Settings;

        Assert.Equal(["General", "Appearance", "Logging", "Equipment defaults"], settings.Groups.Select(g => g.Title));
        Assert.DoesNotContain(settings.Groups, g => g.IsEditable); // no fake switches
        Assert.Contains("observing site", settings.NoteText);
    }

    [Fact]
    public async Task TheLoggingGroup_ReportsTheLevelAndTheFolderOfTheSession()
    {
        await using var app = await UxApp.Create(UxSetup.Simple, logInfo: Info());

        var logging = app.Vm.Settings.Groups.Single(g => g.Title == "Logging");

        Assert.Equal(["Level", "Kept for", "Folder"], logging.Rows.Select(r => r.Label));
        Assert.Equal("Debug", logging.Rows[0].Value);
        Assert.Equal("14 days", logging.Rows[1].Value);
        Assert.Equal("C:\\logs", logging.Rows[2].Value);
    }

    [Fact]
    public void WithoutLogging_TheLoggingGroupIsThereButEmpty()
    {
        var logging = new SettingsViewModel(null).Groups.Single(g => g.Title == "Logging");

        Assert.False(logging.HasRows);
    }

    [Fact]
    public void TheSettingsTabs_ExistOnlyWithContent_AndExactlyOneIsShown()
    {
        var withoutStore = new SettingsViewModel(null);
        Assert.Equal(["General"], withoutStore.Tabs.Select(t => t.Title)); // no site store: nothing editable, so no editable tabs
        Assert.True(withoutStore.IsGeneral);

        var path = Path.Combine(Path.GetTempPath(), "astra-settings-tabs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsViewModel(null, new Sidera.Desktop.Settings.SiteService(new Sidera.Desktop.Settings.SideraSettingsStore(path)));
            Assert.Equal(["General", "Observatory", "Plate solving", "Framing"], settings.Tabs.Select(t => t.Title));
            Assert.Equal(["General"], settings.Tabs.Where(t => t.IsSelected).Select(t => t.Title));

            settings.Tabs.Single(t => t.Title == "Plate solving").IsSelected = true; // what the tab button does

            Assert.True(settings.IsPlateSolving);
            Assert.False(settings.IsGeneral);
            Assert.Equal(["Plate solving"], settings.Tabs.Where(t => t.IsSelected).Select(t => t.Title));
            Assert.Contains("own Save button", settings.SaveNoteText);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

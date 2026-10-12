using System.Text.RegularExpressions;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Help;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The Help of the application: the articles as data (every id, every related article, every button and every picture is checked), the context of each "?", the search, the page, and that the buttons
/// of an article lead where they say. An article that points at something that is not there fails here, not in front of a user.
/// </summary>
public sealed class HelpTests : IAsyncLifetime
{
    private sealed class NoDiscovery : IAscomDiscovery
    {
        public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default) => Task.FromResult(new AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : Sidera.Ascom.IAscomSetupService
    {
        public Task<Sidera.Ascom.AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) => Task.FromResult(new Sidera.Ascom.AscomSetupResult(true, null));
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-help-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public HelpTests() => Directory.CreateDirectory(_directory);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private static string AssetsOfHelp()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sidera.slnx")))
            {
                return Path.Combine(directory.FullName, "src", "Sidera.Desktop", "Assets", "Help");
            }
        }

        throw new DirectoryNotFoundException("The solution folder was not found above " + AppContext.BaseDirectory);
    }

    private MainViewModel Create(bool withSetups = true)
    {
        var host = new SideraRuntimeHost();
        if (withSetups)
        {
            DemoSetup.AddDemoEquipment(host);
            DemoSetup.AddDemoRigs(host);
        }
        else
        {
            host.AddSimulatedCamera(DemoSetup.MainCameraId, "Camera"); // one camera and no imaging setup: there is no section for the imaging setups
        }

        var options = new DemoOptions();
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]);
        var service = new EquipmentService(host, new EquipmentConfigurationStore(Path.Combine(_directory, "equipment.json")), factories);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_directory, "settings.json")));
        settings.Load();
        var vm = new MainViewModel(host, a => a(), options, equipmentManagement: new EquipmentManagement(service, new NoDiscovery(), new NoSetup(), settings), withDemoSequence: false);
        _apps.Add((host, vm));
        return vm;
    }

    // ---- the articles

    [Fact]
    public void EveryArticle_HasAUniqueId_AUniqueTitle_AndASectionThatExists()
    {
        var all = HelpCatalog.All;

        Assert.True(all.Count >= 40);
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(all.Count, all.Select(a => a.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, a => Assert.Contains(HelpCatalog.Sections, s => s.Id == a.SectionId));
        Assert.All(HelpCatalog.Sections, s => Assert.NotEmpty(HelpCatalog.In(s.Id)));
        Assert.All(all, a => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", a.Id));
    }

    [Fact]
    public void ThePlanOfTheHelp_IsThere_StartingToTroubleshootingToReference()
    {
        var required = new[]
        {
            "getting-started", "first-session", "equipment-setup", "sequencer-basics", "blocks", "actions", "repeat", "automation", "limits", "multi-target", "multi-camera", "lrgb-session",
            "manual-capture", "autofocus", "filters", "dithering", "plate-solving", "slew-center", "framing", "rotation", "guiding", "meridian-flip", "shared-mounts", "domes", "dome-actions",
            "camera-wont-connect", "focuser-missing", "plate-solve-fails", "astap-database", "guiding-unavailable", "autofocus-cannot-start", "meridian-flip-disabled", "dome-wont-connect",
            "rotator-calibration", "multiple-imaging-paths", "keyboard-shortcuts", "file-locations", "logs-and-diagnostics", "about",
        };

        Assert.All(required, id => Assert.NotNull(HelpCatalog.Find(id)));
        Assert.Equal(HelpCatalog.Sections.Select(s => s.Id), HelpCatalog.All.Select(a => a.SectionId).Distinct());
    }

    [Fact]
    public void EveryArticle_SaysSomething_AndRelatesOnlyToArticlesThatExist()
    {
        foreach (var article in HelpCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(article.Intro), article.Id);
            Assert.True(article.Steps.Count > 0 || article.Problem is not null, $"{article.Id} has neither steps nor a problem");
            Assert.All(article.Steps.Concat(article.Notes), text => Assert.False(string.IsNullOrWhiteSpace(text), article.Id));
            Assert.All(article.Related, id =>
            {
                Assert.NotNull(HelpCatalog.Find(id));
                Assert.NotEqual(article.Id, id);
            });
            Assert.Equal(article.Related.Count, article.Related.Distinct().Count());
        }
    }

    [Fact]
    public void ATroubleshootingArticle_HasTheProblem_TheLikelyReason_AndWhatToCheck()
    {
        var entries = HelpCatalog.In(HelpCatalog.TroubleshootingSection).ToList();

        Assert.True(entries.Count >= 9);
        Assert.All(entries, a =>
        {
            Assert.NotNull(a.Problem);
            Assert.False(string.IsNullOrWhiteSpace(a.Problem!.Problem), a.Id);
            Assert.False(string.IsNullOrWhiteSpace(a.Problem.Reason), a.Id);
            Assert.NotEmpty(a.Problem.Checks);
            Assert.NotEmpty(a.Actions); // and a button to the page that has it
        });
    }

    [Fact]
    public void TheWordsOfTheHelp_FollowTheWordingOfTheApplication()
    {
        var technical = new Regex(@"\b(Rigs?|Tracks?|Standalone|DeviceId|SetupId|BindingId|Pointing setup|Frames counted on|Mount group|Resource group)\b", RegexOptions.IgnoreCase);

        foreach (var article in HelpCatalog.All)
        {
            var text = string.Join('\n', new[] { article.Title, article.Intro }.Concat(article.Steps).Concat(article.Notes).Concat(article.Actions.Select(a => a.Label))
                .Concat(article.Problem is { } p ? new[] { p.Problem, p.Reason }.Concat(p.Checks) : []));
            Assert.False(technical.IsMatch(text), $"{article.Id}: {technical.Match(text).Value}");
        }
    }

    // ---- the pictures

    [Fact]
    public void EveryPicture_IsAFileOfTheAssets_WithMarkersThatAreNumberedInOrderAndInsideThePicture()
    {
        var folder = AssetsOfHelp();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var article in HelpCatalog.All.Where(a => a.Screenshot is not null))
        {
            var shot = article.Screenshot!;
            var path = Path.Combine(folder, shot.Resource);
            Assert.True(File.Exists(path), $"{article.Id}: {shot.Resource} is missing");
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], File.ReadAllBytes(path).Take(4).Select(b => (int)b).ToArray()); // a PNG
            Assert.True(new FileInfo(path).Length < 200_000, $"{shot.Resource} is too big");
            Assert.False(string.IsNullOrWhiteSpace(shot.Caption));
            Assert.Equal(Enumerable.Range(1, shot.Callouts.Count), shot.Callouts.Select(c => c.Number));
            Assert.All(shot.Callouts, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Label));
                Assert.InRange(c.X, 0, 1);
                Assert.InRange(c.Y, 0, 1);
            });
            used.Add(shot.Resource);
        }

        var orphans = Directory.EnumerateFiles(folder, "*.png").Select(Path.GetFileName).Where(f => !used.Contains(f!)).ToList();
        Assert.Empty(orphans); // no picture that nothing shows
    }

    // ---- the buttons

    [Fact]
    public void EveryButton_PointsAtAPageATabOrASectionThatExists()
    {
        var tabs = new[]
        {
            SettingsViewModel.GeneralTab, SettingsViewModel.AppearanceTab, SettingsViewModel.ObservatoryTab, SettingsViewModel.PlateSolvingTab, SettingsViewModel.FramingTab,
            SettingsViewModel.ImagingTab, SettingsViewModel.AutofocusTab, SettingsViewModel.GuidingTab, SettingsViewModel.MeridianFlipTab, SettingsViewModel.SequencerTab, SettingsViewModel.AdvancedTab,
        };

        foreach (var action in HelpCatalog.All.SelectMany(a => a.Actions))
        {
            switch (action.Target)
            {
                case HelpPageTarget page:
                    Assert.True(Enum.IsDefined(page.Page), action.Label);
                    break;
                case HelpSettingsTarget settings:
                    Assert.Contains(settings.Tab, tabs);
                    break;
                case HelpEquipmentTarget equipment:
                    Assert.True(Enum.IsDefined(equipment.Section), action.Label);
                    break;
                default:
                    Assert.Fail("A button with a target that nothing knows: " + action.Label);
                    break;
            }

            Assert.StartsWith("Open ", action.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryButtonOfEveryArticle_GoesWhereItSays_FromTheHelpPage()
    {
        var vm = Create();
        foreach (var article in HelpCatalog.All)
        {
            foreach (var action in article.Actions)
            {
                vm.OpenHelp(article.Id);
                Assert.Equal(AppPage.Help, vm.SelectedPage);
                vm.Help.Open(article.Id);
                var button = vm.Help.Article.Actions.Single(a => a.Label == action.Label && a.Target == action.Target);

                button.GoCommand.Execute(null);

                Assert.False(vm.Help.HasNotice, $"{article.Id}: {action.Label} led nowhere");
                Assert.Equal(
                    action.Target switch
                    {
                        HelpPageTarget p => p.Page,
                        HelpSettingsTarget => AppPage.Settings,
                        _ => AppPage.Equipment,
                    },
                    vm.SelectedPage);
                if (action.Target is HelpSettingsTarget settings)
                {
                    Assert.Equal(settings.Tab, vm.Settings.SelectedTabKey);
                }

                if (action.Target is HelpEquipmentTarget equipment)
                {
                    Assert.Equal(equipment.Section, vm.Equipment.SelectedSection);
                }
            }
        }
    }

    [Fact]
    public void AButtonThatLeadsNowhere_SaysSo_InsteadOfDoingNothing()
    {
        var vm = Create(withSetups: false); // no imaging setup: there is no such tab
        vm.Help.Open("multi-camera");

        vm.Help.Article.Actions.Single(a => a.Label == "Open Imaging Setups").GoCommand.Execute(null);

        Assert.True(vm.Help.HasNotice);
        Assert.Contains("Imaging Setups", vm.Help.NoticeText, StringComparison.Ordinal);
    }

    // ---- the "?"

    [Fact]
    public void EveryPageWithAHelpButton_HasAnArticle_AndEverySettingsTabToo()
    {
        foreach (var page in new[] { "dashboard", "session", "imaging", "framing", "platesolve", "equipment", "equipment:dome", "equipment:setups", "diagnostics", "settings", "help" })
        {
            Assert.NotNull(HelpCatalog.Find(HelpContexts.ArticleFor(page)));
            Assert.True(HelpContexts.Articles.ContainsKey(page), page);
        }

        foreach (var tab in new[] { "general", "appearance", "observatory", "imaging", "autofocus", "guiding", "platesolving", "framing", "meridianflip", "sequencer", "advanced" })
        {
            Assert.True(HelpContexts.Articles.ContainsKey("settings:" + tab), tab);
        }

        Assert.All(HelpContexts.Articles.Values, id => Assert.NotNull(HelpCatalog.Find(id)));
    }

    [Theory]
    [InlineData("session", "sequencer-basics")]
    [InlineData("equipment:dome", "domes")]
    [InlineData("settings:platesolving", "plate-solving")]
    [InlineData("settings:meridianflip", "meridian-flip")]
    [InlineData("settings:somethingnew", "file-locations")] // an unknown tab: the page
    [InlineData("somewhere", "getting-started")] // an unknown context: the first article
    [InlineData(null, "getting-started")]
    public void TheArticleOfAContext_IsTheOneOfThatPage_AndTheFirstWhenThereIsNone(string? context, string expected) =>
        Assert.Equal(expected, HelpContexts.ArticleFor(context));

    [Fact]
    public void ThePagesSayWhereTheirHelpIs_AndFollowWhatIsOpen()
    {
        var vm = Create();

        vm.SelectedPageFor(AppPage.Settings);
        Assert.Equal("settings:general", vm.Settings.HelpContext);
        vm.Settings.SelectTab(SettingsViewModel.PlateSolvingTab);
        Assert.Equal("settings:platesolving", vm.Settings.HelpContext);

        vm.Equipment.ShowSection(EquipmentSection.Domes);
        Assert.Equal("equipment:dome", vm.Equipment.HelpContext);
        vm.Equipment.ShowSection(EquipmentSection.Overview);
        Assert.Equal("equipment", vm.Equipment.HelpContext);
    }

    [Fact]
    public void PressingTheQuestionMark_OpensHelpAtThatPage_AndCloseHelpGoesBack()
    {
        var vm = Create();
        vm.NavigateCommand.Execute(AppPage.PlateSolve);

        vm.OpenHelp("settings:platesolving");

        Assert.Equal(AppPage.Help, vm.SelectedPage);
        Assert.Equal("plate-solving", vm.Help.Article.Article.Id);
        Assert.Contains(vm.Help.Groups.SelectMany(g => g.Links), l => l.IsSelected && l.Article.Id == "plate-solving");
        vm.Help.ReturnCommand.Execute(null);
        Assert.Equal(AppPage.PlateSolve, vm.SelectedPage);
    }

    [Fact]
    public void HelpIsInTheSidebar_JustAboveSettings()
    {
        var vm = Create();

        Assert.Equal(["Diagnostics", "Help", "Settings"], vm.SecondaryNavigation.Select(n => n.Title));
        vm.SecondaryNavigation[1].Command.Execute(null);
        Assert.Equal(AppPage.Help, vm.SelectedPage);
        Assert.Equal("getting-started", vm.Help.Article.Article.Id);
        Assert.True(vm.SecondaryNavigation[1].IsSelected);
    }

    // ---- the page

    [Fact]
    public void TheArticleThatIsOpen_IsMarkedInTheTree_AndBackGoesToTheOneBefore()
    {
        var vm = Create();
        var help = vm.Help;

        help.Open("blocks");
        help.Open("automation");

        Assert.Equal("automation", help.Article.Article.Id);
        Assert.Single(help.Groups.SelectMany(g => g.Links), l => l.IsSelected);
        Assert.True(help.CanGoBack);
        help.BackCommand.Execute(null);
        Assert.Equal("blocks", help.Article.Article.Id);
        help.BackCommand.Execute(null);
        Assert.Equal("getting-started", help.Article.Article.Id);
        Assert.False(help.CanGoBack);
    }

    [Fact]
    public void AnArticleThatDoesNotExist_LeavesThePageAsItIs()
    {
        var vm = Create();

        Assert.False(vm.Help.Open("nonsense"));
        Assert.False(vm.Help.Open(null));
        Assert.Equal("getting-started", vm.Help.Article.Article.Id);
    }

    [Fact]
    public void ARelatedLink_OpensThatArticle()
    {
        var vm = Create();
        vm.Help.Open("automation");

        vm.Help.Article.Related.First(r => r.Title == "Meridian Flip").OpenCommand.Execute(null);

        Assert.Equal("meridian-flip", vm.Help.Article.Article.Id);
    }

    [Fact]
    public void TheNumberedSteps_AreNumberedFromOne()
    {
        var vm = Create();

        vm.Help.Open("getting-started");

        Assert.Equal(Enumerable.Range(1, 6), vm.Help.Article.Steps.Select(s => s.Number));
    }

    // ---- the search

    [Fact]
    public void TheSearch_FindsByTitle_ByText_AndByAKeyword_AndAllWordsMustMatch()
    {
        Assert.Contains(HelpCatalog.Search("meridian"), a => a.Id == "meridian-flip");
        Assert.Contains(HelpCatalog.Search("roll-off"), a => a.Id == "domes"); // text
        Assert.Contains(HelpCatalog.Search("roof"), a => a.Id == "domes"); // a keyword
        Assert.Contains(HelpCatalog.Search("ASTAP database"), a => a.Id == "astap-database");
        Assert.DoesNotContain(HelpCatalog.Search("astap zzzz"), a => a.Id == "astap-database");
        Assert.Empty(HelpCatalog.Search("zzzzzz"));
        Assert.Equal(HelpCatalog.All.Count, HelpCatalog.Search("  ").Count);
        Assert.Equal(HelpCatalog.All.Count, HelpCatalog.Search(null).Count);
    }

    [Fact]
    public void TheSearchBox_NarrowsTheTree_SaysHowMany_AndShowsTheFirstResult()
    {
        var vm = Create();

        vm.Help.SearchText = "dither";

        Assert.True(vm.Help.HasResultsText);
        Assert.EndsWith("articles", vm.Help.ResultsText, StringComparison.Ordinal);
        var shown = vm.Help.Groups.SelectMany(g => g.Links).Select(l => l.Article.Id).ToList();
        Assert.Contains("dithering", shown);
        Assert.DoesNotContain("keyboard-shortcuts", shown);
        Assert.Contains(vm.Help.Article.Article.Id, shown); // the page is never empty next to its list

        vm.Help.SearchText = "zzzzzz";
        Assert.Empty(vm.Help.Groups);
        Assert.Equal("No article matches.", vm.Help.ResultsText);

        vm.Help.ClearSearchCommand.Execute(null);
        Assert.False(vm.Help.HasResultsText);
        Assert.Equal(HelpCatalog.All.Count, vm.Help.Groups.Sum(g => g.Links.Count));
    }
}

internal static class HelpTestExtensions
{
    /// <summary>Goes to a page the way the sidebar does.</summary>
    public static void SelectedPageFor(this MainViewModel vm, AppPage page) => vm.NavigateCommand.Execute(page);
}

using Sidera.Core.Mounts;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The session modes (blocks unless the settings or the file say otherwise, the tree only after a confirmed second click, back only where it is exact), the settings that were added (stored,
/// validated, saved by their tab), and what a session takes from them and what it does not.
/// </summary>
public sealed class ProductSettingsTests : IAsyncLifetime
{
    private sealed class Picker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }

    private sealed class NoDiscovery : Sidera.Ascom.Discovery.IAscomDiscovery
    {
        public Task<Sidera.Ascom.Discovery.AscomDiscoveryResult> DiscoverAsync(Sidera.Ascom.Discovery.AscomDeviceKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Sidera.Ascom.Discovery.AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : Sidera.Ascom.IAscomSetupService
    {
        public Task<Sidera.Ascom.AscomSetupResult> ShowAsync(Sidera.Ascom.Discovery.AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Sidera.Ascom.AscomSetupResult(true, null));
    }

    private sealed record App(MainViewModel Vm, SiteService Settings, Picker Picker);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-product-settings-" + Guid.NewGuid().ToString("N"));
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];

    public ProductSettingsTests() => Directory.CreateDirectory(_directory);

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

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    private SiteService NewSettings()
    {
        var service = new SiteService(new SideraSettingsStore(SettingsFile));
        service.Load();
        return service;
    }

    private App Create(SiteService? settings = null, bool startInSettingsMode = true)
    {
        settings ??= NewSettings();
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var options = new DemoOptions();
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]);
        var equipment = new EquipmentService(host, new EquipmentConfigurationStore(Path.Combine(_directory, "equipment.json")), factories);
        var picker = new Picker();
        var vm = new MainViewModel(
            host, a => a(), options, SequenceDocumentStore.CreateDefault(), picker,
            equipmentManagement: new EquipmentManagement(equipment, new NoDiscovery(), new NoSetup(), settings), withDemoSequence: false, startWithSettingsMode: startInSettingsMode);
        _apps.Add((host, vm));
        return new App(vm, settings, picker);
    }

    private static async Task OpenAsync(App app, string path)
    {
        app.Picker.OpenPath = path;
        await app.Vm.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (app.Vm.SequenceDocument.IsConfirmingDiscard)
        {
            await app.Vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }
    }

    private static async Task SaveAsync(string path, SequenceDocument document)
    {
        await using var stream = File.Create(path);
        await new JsonSequenceDocumentSerializer().SaveAsync(stream, document, CancellationToken.None);
    }

    private static string Fingerprint(App app) => SequenceDocumentStore.Fingerprint(SequenceDocumentMapper.ToDocument(app.Vm.SequenceDraft.Snapshot()));

    // ---- the session mode

    [Fact]
    public void ANewSession_OpensAsBlocks_FollowingTheApplicationsMeridianFlip()
    {
        var app = Create();

        Assert.True(app.Vm.SessionEditor.IsStructured);
        Assert.False(app.Vm.SessionEditor.IsTree);
        Assert.True(app.Vm.SessionEditor.Session!.Automation.UsesDefaultFlip);
    }

    [Fact]
    public void WithTheTreeAsTheDefaultMode_ANewSessionOpensAsTheExplicitTree()
    {
        var settings = NewSettings();
        Assert.Null(settings.SetSequencer(new SequencerSettings { DefaultSessionMode = SessionMode.Advanced }).Problem);

        var app = Create(settings);

        Assert.True(app.Vm.SessionEditor.IsTree);
        Assert.False(app.Vm.SessionEditor.IsStructured);
    }

    [Fact]
    public async Task ASessionFile_OpensAsBlocks_AndALegacySequence_AsTheTree()
    {
        var app = Create();
        app.Vm.SessionEditor.AddTargetCommand.Execute(null);
        var sessionFile = Path.Combine(_directory, "session.astraseq");
        await SaveAsync(sessionFile, new SequenceDocument("S", [], null, null, app.Vm.SessionEditor.Session!));
        var legacyFile = Path.Combine(_directory, "legacy.astraseq");
        await SaveAsync(legacyFile, SequenceDocumentMapper.ToDocument([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]));

        await OpenAsync(app, legacyFile);
        Assert.True(app.Vm.SessionEditor.IsTree);

        await OpenAsync(app, sessionFile);
        Assert.True(app.Vm.SessionEditor.IsStructured);
    }

    [Fact]
    public void OpeningTheTree_AsksFirst_WithTheWarning_AndThenKeepsEveryStep()
    {
        var app = Create();
        var editor = app.Vm.SessionEditor;
        editor.AddTargetCommand.Execute(null);
        var before = Fingerprint(app);

        editor.ShowTreeCommand.Execute(null); // the first click only asks

        Assert.True(editor.IsConfirmingTree);
        Assert.True(editor.IsStructured);
        Assert.Contains("cannot be made again", editor.TreeWarningText, StringComparison.Ordinal);

        editor.CancelShowTreeCommand.Execute(null);
        Assert.True(editor.IsStructured);
        Assert.False(editor.IsConfirmingTree);

        editor.ShowTreeCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null); // the second one opens it

        Assert.True(editor.IsTree);
        Assert.Equal(before, Fingerprint(app)); // not one step was lost or changed
    }

    [Fact]
    public void ATreeThatWasASession_ReturnsToItsBlocks_OnlyWhileItsStepsAreUntouched()
    {
        var app = Create();
        var editor = app.Vm.SessionEditor;
        editor.AddTargetCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null);

        Assert.True(editor.CanReturnToStructured);
        Assert.Equal(string.Empty, editor.WhyNotStructuredText);

        app.Vm.SequenceDraft.AddStepDraft(new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.5)); // the tree is no longer what a session compiled to
        Assert.False(editor.CanReturnToStructured);
        Assert.Equal("This sequence cannot be shown as blocks: its steps are not those of a session.", editor.WhyNotStructuredText);
        Assert.False(editor.SwitchToStructuredCommand.CanExecute(null));
    }

    [Fact]
    public async Task ALegacySequence_CannotBecomeBlocks_AndSaysSo()
    {
        var app = Create();
        var legacyFile = Path.Combine(_directory, "legacy.astraseq");
        await SaveAsync(legacyFile, SequenceDocumentMapper.ToDocument([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]));

        await OpenAsync(app, legacyFile);

        Assert.False(app.Vm.SessionEditor.CanReturnToStructured);
        Assert.False(app.Vm.SessionEditor.SwitchToStructuredCommand.CanExecute(null));
        Assert.Equal("This sequence cannot be shown as blocks: its steps are not those of a session.", app.Vm.SessionEditor.WhyNotStructuredText);
    }

    [Fact]
    public void AnEmptyTree_CanBecomeBlocks()
    {
        var settings = NewSettings();
        settings.SetSequencer(new SequencerSettings { DefaultSessionMode = SessionMode.Advanced });
        var app = Create(settings);

        Assert.True(app.Vm.SessionEditor.CanReturnToStructured);
        app.Vm.SessionEditor.SwitchToStructuredCommand.Execute(null);

        Assert.True(app.Vm.SessionEditor.IsStructured);
    }

    // ---- the settings

    [Fact]
    public void WithoutAFile_EverySettingHasItsDefault_AndAnOldFileWithoutTheNewSectionsLoadsWithThem()
    {
        new SideraSettingsStore(SettingsFile).Save(new SideraSettings(new Sidera.Core.Location.ObservingSite(47.7, 7.8, 410)));

        var settings = NewSettings();

        Assert.Equal(SessionMode.Workflow, settings.Sequencer.DefaultSessionMode);
        Assert.True(settings.Imaging.AutoStretch);
        Assert.True(settings.Imaging.FitOnCapture);
        Assert.Equal(new AutofocusDefaults(), settings.Autofocus);
        Assert.Equal(new GuidingDefaults(), settings.Guiding);
        Assert.Equal(new MeridianFlipSettings(), settings.MeridianFlip);
        Assert.NotNull(settings.Site);
    }

    [Fact]
    public void TheNewSettings_AreStored_AndComeBackFromTheSameFile()
    {
        var settings = NewSettings();
        Assert.Null(settings.SetSequencer(new SequencerSettings { DefaultSessionMode = SessionMode.Advanced }).Problem);
        Assert.Null(settings.SetImaging(new ImagingSettings { SaveDirectory = _directory, AutoStretch = false, FitOnCapture = false, ManualExposureSeconds = 4 }).Problem);
        Assert.Null(settings.SetAutofocus(new AutofocusDefaults { ExposureSeconds = 2, StepSize = 250, SampleCount = 9, PolicyEnabled = true, PolicyIntervalMinutes = 45 }).Problem);
        Assert.Null(settings.SetGuiding(new GuidingDefaults { StartBeforeImaging = false, DitherByDefault = true, DitherEveryNFrames = 5, DitherAmplitudePixels = 2 }).Problem);
        Assert.Null(settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 7 }).Problem);

        var again = NewSettings();

        Assert.Equal(SessionMode.Advanced, again.Sequencer.DefaultSessionMode);
        Assert.Equal(_directory, again.Imaging.SaveDirectory);
        Assert.False(again.Imaging.AutoStretch);
        Assert.Equal(4, again.Imaging.ManualExposureSeconds);
        Assert.Equal(250, again.Autofocus.StepSize);
        Assert.True(again.Autofocus.PolicyEnabled);
        Assert.False(again.Guiding.StartBeforeImaging);
        Assert.Equal(5, again.Guiding.DitherEveryNFrames);
        Assert.True(again.MeridianFlip.Enabled);
        Assert.Equal(7, again.MeridianFlip.PauseBeforeMeridianMinutes);
    }

    [Fact]
    public void TheAutofocusHoldingTheMountStill_IsOffByDefault_AndIsStored()
    {
        var settings = NewSettings();
        Assert.False(settings.Autofocus.HoldMountStable);

        Assert.Null(settings.SetAutofocus(new AutofocusDefaults { HoldMountStable = true }).Problem);

        Assert.True(NewSettings().Autofocus.HoldMountStable);
    }

    [Fact]
    public void WrongValues_AreRefused_WithASentence_AndNothingIsChanged()
    {
        var settings = NewSettings();

        Assert.NotNull(settings.SetImaging(new ImagingSettings { ManualExposureSeconds = 0 }).Problem);
        Assert.NotNull(settings.SetAutofocus(new AutofocusDefaults { SampleCount = 1 }).Problem);
        Assert.NotNull(settings.SetGuiding(new GuidingDefaults { DitherEveryNFrames = 0 }).Problem);
        Assert.NotNull(settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = -1 }).Problem);

        Assert.Equal(new ImagingSettings(), settings.Imaging);
        Assert.Equal(new GuidingDefaults(), settings.Guiding);
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void TheSettingsPage_HasTheTabsOfTheProduct_AndNoOtherWithoutContent()
    {
        var app = Create();

        Assert.Equal(
            ["General", "Observatory", "Imaging", "Autofocus", "Guiding", "Plate solving", "Framing", "Meridian Flip", "Sequencer", "Advanced"],
            app.Vm.Settings.Tabs.Select(t => t.Title));
        app.Vm.Settings.SelectTab(SettingsViewModel.MeridianFlipTab);
        Assert.True(app.Vm.Settings.IsMeridianFlip);
        Assert.False(app.Vm.Settings.IsGeneral);
    }

    [Fact]
    public void ATabSaves_OnlyWhenToldTo_SaysSo_AndAWrongValueIsRefusedInOneSentence()
    {
        var app = Create();
        var tab = app.Vm.Settings.Imaging!;

        tab.ManualExposureText = "6";
        Assert.True(tab.IsDirty);
        Assert.Equal(2, app.Settings.Imaging.ManualExposureSeconds); // typing applies nothing

        tab.SaveCommand.Execute(null);
        Assert.StartsWith("Saved.", tab.ResultText);
        Assert.False(tab.ResultIsProblem);
        Assert.False(tab.IsDirty);
        Assert.Equal(6, app.Settings.Imaging.ManualExposureSeconds);

        tab.ManualExposureText = "abc";
        tab.SaveCommand.Execute(null);
        Assert.True(tab.ResultIsProblem);
        Assert.Contains("must be a number", tab.ResultText);
        Assert.Equal(6, app.Settings.Imaging.ManualExposureSeconds);

        tab.DiscardCommand.Execute(null);
        Assert.Equal("6", tab.ManualExposureText);
        Assert.False(tab.IsDirty);
    }

    [Fact]
    public void ImagingSettings_AreWhatTheImagingPageStartsWith()
    {
        var settings = NewSettings();
        settings.SetImaging(new ImagingSettings { AutoStretch = false, FitOnCapture = false, ManualExposureSeconds = 4, SaveDirectory = _directory });

        var app = Create(settings);

        Assert.False(app.Vm.Imaging.AutoStretch);
        Assert.False(app.Vm.Imaging.FitOnCapture);
        Assert.Equal(_directory, app.Vm.Imaging.SaveDirectory!());
    }

    // ---- what a session takes from the settings

    [Fact]
    public void ANewTarget_StartsFromTheGuidingDefaults_AndExistingOnesAreNotChanged()
    {
        var settings = NewSettings();
        settings.SetGuiding(new GuidingDefaults { StartBeforeImaging = false, StopWhenDone = false, DitherByDefault = true, DitherEveryNFrames = 5 });
        var app = Create(settings);
        var editor = app.Vm.SessionEditor;
        editor.AddTargetCommand.Execute(null);

        var session = editor.Session!;
        var target = session.Targets[0];
        Assert.DoesNotContain(target.Preparation, a => a.Kind == SessionActionKind.StartGuiding);
        Assert.DoesNotContain(session.End, a => a.Kind == SessionActionKind.StopGuiding);
        Assert.Equal(5, target.Lanes[0].Blocks[0].Automation.Dither!.EveryFrames);

        settings.SetGuiding(new GuidingDefaults()); // a later change of the defaults

        Assert.Same(session, editor.Session); // the session is as it was
    }

    [Fact]
    public void ANewTarget_StartsFromTheAutofocusDefaults()
    {
        var settings = NewSettings();
        settings.SetAutofocus(new AutofocusDefaults { ExposureSeconds = 3, StepSize = 123, SampleCount = 9, PolicyEnabled = true, PolicyIntervalMinutes = 30 });
        var app = Create(settings);

        app.Vm.SessionEditor.AddTargetCommand.Execute(null);

        var target = app.Vm.SessionEditor.Session!.Targets[0];
        var step = target.Preparation.OfType<AutofocusAction>().First();
        Assert.Equal(123, step.Settings.StepSize);
        Assert.Equal(9, step.Settings.SampleCount);
        Assert.Equal(30, target.Lanes[0].Blocks[0].Automation.Focus!.EveryMinutes);
    }

    // ---- the meridian flip: the defaults, and the session that has its own

    [Fact]
    public void ASessionUsingTheDefaults_FollowsThem_WithASummary_AndSavesNoCopy()
    {
        var settings = NewSettings();
        settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 5, FlipAfterMeridianMinutes = 2, RecenterAfterFlip = true, RestartGuidingAfterFlip = true });
        var app = Create(settings);
        var editor = app.Vm.SessionEditor;
        editor.AddTargetCommand.Execute(null);
        editor.SelectFlip();
        var drawer = Assert.IsType<FlipEditorViewModel>(editor.Drawer);

        Assert.True(drawer.UsesDefaults);
        Assert.False(drawer.IsCustom);
        Assert.StartsWith("Using defaults · Hold \u22125m · Flip +2m", drawer.DefaultsSummary, StringComparison.Ordinal);
        Assert.Contains("Recenter", drawer.DefaultsSummary, StringComparison.Ordinal);
        Assert.Contains("Guiding", drawer.DefaultsSummary, StringComparison.Ordinal);

        settings.SetMeridianFlip(settings.MeridianFlip with { PauseBeforeMeridianMinutes = 9 });

        Assert.StartsWith("Using defaults · Hold \u22129m", drawer.DefaultsSummary, StringComparison.Ordinal); // it followed
        Assert.Null(editor.Session!.Automation.Flip); // and nothing of it was copied into the session
        Assert.True(editor.Session.Automation.UsesDefaultFlip);
    }

    [Fact]
    public void ASessionWithItsOwnFlip_IsNotChangedByTheDefaults()
    {
        var settings = NewSettings();
        settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 5 });
        var app = Create(settings);
        var editor = app.Vm.SessionEditor;
        editor.AddTargetCommand.Execute(null);
        editor.SelectFlip();
        var drawer = Assert.IsType<FlipEditorViewModel>(editor.Drawer);

        drawer.CustomizeCommand.Execute(null); // starts from the defaults it followed

        Assert.True(drawer.IsCustom);
        Assert.False(drawer.UsesDefaults);
        Assert.True(drawer.Enabled);
        Assert.Equal(5, editor.Session!.Automation.Flip!.PauseBeforeMeridianMinutes);

        settings.SetMeridianFlip(settings.MeridianFlip with { PauseBeforeMeridianMinutes = 11, Enabled = false });

        Assert.Equal(5, editor.Session.Automation.Flip!.PauseBeforeMeridianMinutes);
        Assert.True(editor.Session.Automation.Flip.Enabled);

        drawer.UseDefaultSettingsCommand.Execute(null); // and back: the session follows again
        Assert.True(drawer.UsesDefaults);
        Assert.Null(editor.Session!.Automation.Flip);
    }

    [Fact]
    public async Task TheChoiceOfDefaultsOrCustom_IsSavedInTheSessionFile_AndComesBack()
    {
        var serializer = new JsonSequenceDocumentSerializer();

        async Task<SessionDefinition> RoundTrip(SessionDefinition session)
        {
            using var stream = new MemoryStream();
            await serializer.SaveAsync(stream, new SequenceDocument("T", [], null, null, session), CancellationToken.None);
            stream.Position = 0;
            return (await serializer.LoadAsync(stream, CancellationToken.None)).Session!;
        }

        var followsDefaults = await RoundTrip(SessionDefinition.Empty);
        Assert.True(followsDefaults.Automation.UsesDefaultFlip);
        Assert.Null(followsDefaults.Automation.Flip);

        var own = await RoundTrip(SessionDefinition.Empty with { Automation = new SessionAutomation(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 8 }) });
        Assert.False(own.Automation.UsesDefaultFlip);
        Assert.Equal(8, own.Automation.Flip!.PauseBeforeMeridianMinutes);
    }

    [Fact]
    public async Task WhatTheDefaultsSay_IsWhatTheCompiledSessionDoes_AndACustomOneIgnoresThem()
    {
        var flip = new MeridianFlipSettings { Enabled = true };
        var lane = Sessions.SessionFixture.Lane(null, Sessions.SessionFixture.Block(null, 60, 3));
        var withDefaults = Sessions.SessionFixture.Session(Sessions.SessionFixture.Target("M31", [lane]));
        var custom = withDefaults with { Automation = new SessionAutomation(new MeridianFlipSettings { Enabled = false }) };
        await using var fixture = Sessions.SessionFixture.Create();

        Assert.NotNull(Flip(SessionCompiler.Compile(withDefaults, fixture.Catalog, flip)));
        Assert.Null(Flip(SessionCompiler.Compile(custom, fixture.Catalog, flip)));

        static MeridianFlipPolicyDraft? Flip(SessionCompilation compiled) => compiled.Steps.OfType<MultiRigStepDraft>().Single().MeridianFlip;
    }

    // ---- the advanced tab

    [Fact]
    public void TheAdvancedTab_ShowsPaths_AndOnlyTheVariablesTheApplicationReads_AndForgetsTheAnswers()
    {
        var app = Create();
        var advanced = app.Vm.Settings.Advanced!;

        Assert.Equal(app.Settings.FilePath, advanced.SettingsFileText);
        Assert.All(advanced.EnvironmentText, line => Assert.Matches("^SIDERA_(EQUIPMENT|SETTINGS)_FILE", line));
        Assert.Equal(2, advanced.EnvironmentText.Length);
        Assert.True(advanced.HasSafety);

        advanced.ForgetCommand.Execute(null);
        Assert.Equal(0, app.Vm.Safety.AnsweredCount);
    }
}

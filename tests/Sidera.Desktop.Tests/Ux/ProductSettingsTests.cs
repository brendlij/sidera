using Sidera.Core.Mounts;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The session modes (a workflow unless the settings or the file say otherwise, Advanced only after a confirmed conversion, back only where it is exact), the settings that were added (stored,
/// validated, saved by their tab), and what a workflow takes from them and what it does not.
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
    public void ANewSession_OpensAsAWorkflow_FollowingTheApplicationsMeridianFlip()
    {
        var app = Create();

        Assert.True(app.Vm.Workflow.IsWorkflowMode);
        Assert.False(app.Vm.Workflow.IsAdvancedMode);
        Assert.True(app.Vm.Workflow.FlipUsesDefaults);
    }

    [Fact]
    public void WithAdvancedAsTheDefaultMode_ANewSessionOpensAsTheExplicitTree()
    {
        var settings = NewSettings();
        Assert.Null(settings.SetSequencer(new SequencerSettings { DefaultSessionMode = SessionMode.Advanced }).Problem);

        var app = Create(settings);

        Assert.True(app.Vm.Workflow.IsAdvancedMode);
        Assert.False(app.Vm.Workflow.IsWorkflowMode);
    }

    [Fact]
    public async Task AWorkflowFile_OpensAsAWorkflow_AndALegacySequence_AsAdvanced()
    {
        var app = Create();
        app.Vm.Workflow.StartFromTemplateCommand.Execute(null);
        var workflow = app.Vm.Workflow.Definition!;
        var workflowFile = Path.Combine(_directory, "workflow.astraseq");
        await SaveAsync(workflowFile, new SequenceDocument("W", [], null, workflow));
        var legacyFile = Path.Combine(_directory, "legacy.astraseq");
        await SaveAsync(legacyFile, SequenceDocumentMapper.ToDocument([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]));

        await OpenAsync(app, legacyFile);
        Assert.True(app.Vm.Workflow.IsAdvancedMode);

        await OpenAsync(app, workflowFile);
        Assert.True(app.Vm.Workflow.IsWorkflowMode);
    }

    [Fact]
    public void ConvertingToAdvanced_AsksFirst_WithTheWarning_AndThenKeepsEveryAction()
    {
        var app = Create();
        var editor = app.Vm.Workflow;
        editor.StartFromTemplateCommand.Execute(null);
        var before = Fingerprint(app);

        editor.ConvertToAdvancedCommand.Execute(null); // the first click only asks

        Assert.True(editor.IsConfirmingAdvanced);
        Assert.True(editor.IsWorkflowMode);
        Assert.Equal(
            "Converting to Advanced exposes the explicit action tree. Workflow policies will no longer be editable through the high-level workflow model.", editor.AdvancedWarningText);

        editor.CancelConvertToAdvancedCommand.Execute(null);
        Assert.True(editor.IsWorkflowMode);
        Assert.False(editor.IsConfirmingAdvanced);

        editor.ConvertToAdvancedCommand.Execute(null);
        editor.ConvertToAdvancedCommand.Execute(null); // the second one converts

        Assert.True(editor.IsAdvancedMode);
        Assert.Equal(before, Fingerprint(app)); // not one action was lost or changed
    }

    [Fact]
    public void AConvertedSequence_ReturnsToItsWorkflow_OnlyWhileItsStepsAreUntouched()
    {
        var app = Create();
        var editor = app.Vm.Workflow;
        editor.StartFromTemplateCommand.Execute(null);
        editor.ConvertToAdvancedCommand.Execute(null);
        editor.ConvertToAdvancedCommand.Execute(null);

        Assert.True(editor.CanReturnToWorkflow);
        Assert.Equal(string.Empty, editor.WhyNotWorkflowText);

        app.Vm.SequenceDraft.AddStepDraft(new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.5)); // the tree is no longer what a workflow compiled to
        Assert.False(editor.CanReturnToWorkflow);
        Assert.Equal("This sequence cannot be represented as a Workflow.", editor.WhyNotWorkflowText);
        Assert.False(editor.SwitchToWorkflowCommand.CanExecute(null));
    }

    [Fact]
    public async Task ALegacySequence_CannotBecomeAWorkflow_AndSaysSo()
    {
        var app = Create();
        var legacyFile = Path.Combine(_directory, "legacy.astraseq");
        await SaveAsync(legacyFile, SequenceDocumentMapper.ToDocument([new ExposureStepDraft(Guid.NewGuid(), DemoSetup.MainCameraId, 0.1)]));

        await OpenAsync(app, legacyFile);

        Assert.False(app.Vm.Workflow.CanReturnToWorkflow);
        Assert.False(app.Vm.Workflow.SwitchToWorkflowCommand.CanExecute(null));
        Assert.Equal("This sequence cannot be represented as a Workflow.", app.Vm.Workflow.WhyNotWorkflowText);
    }

    [Fact]
    public void AnEmptyAdvancedSequence_CanBecomeAWorkflow()
    {
        var settings = NewSettings();
        settings.SetSequencer(new SequencerSettings { DefaultSessionMode = SessionMode.Advanced });
        var app = Create(settings);

        Assert.True(app.Vm.Workflow.CanReturnToWorkflow);
        app.Vm.Workflow.SwitchToWorkflowCommand.Execute(null);

        Assert.True(app.Vm.Workflow.IsWorkflowMode);
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

    // ---- what a workflow takes from the settings

    [Fact]
    public void ANewWorkflow_StartsFromTheGuidingDefaults_AndExistingOnesAreNotChanged()
    {
        var settings = NewSettings();
        settings.SetGuiding(new GuidingDefaults { StartBeforeImaging = false, StopWhenDone = false, DitherByDefault = true, DitherEveryNFrames = 5 });
        var app = Create(settings);
        app.Vm.Workflow.StartFromTemplateCommand.Execute(null);

        var definition = app.Vm.Workflow.Definition!;
        Assert.DoesNotContain(definition.Prepare, s => s.Kind == WorkflowStepKind.StartGuiding);
        Assert.DoesNotContain(definition.Finish, s => s.Kind == WorkflowStepKind.StopGuiding);
        Assert.True(definition.Dither.Enabled);
        Assert.Equal(5, definition.Dither.EveryNFrames);

        settings.SetGuiding(new GuidingDefaults()); // a later change of the defaults

        var after = app.Vm.Workflow.Definition!; // the workflow is as it was
        Assert.Equal(definition.Dither, after.Dither);
        Assert.Equal(definition.Prepare.Select(x => x.Kind), after.Prepare.Select(x => x.Kind));
        Assert.Equal(definition.Finish.Select(x => x.Kind), after.Finish.Select(x => x.Kind));
    }

    [Fact]
    public void ANewWorkflow_StartsFromTheAutofocusDefaults()
    {
        var settings = NewSettings();
        settings.SetAutofocus(new AutofocusDefaults { ExposureSeconds = 3, StepSize = 123, SampleCount = 9, PolicyEnabled = true, PolicyIntervalMinutes = 30 });
        var app = Create(settings);

        app.Vm.Workflow.StartFromTemplateCommand.Execute(null);

        var step = app.Vm.Workflow.Definition!.Prepare.First(s => s.Kind == WorkflowStepKind.Autofocus);
        Assert.Equal(123, step.Autofocus!.StepSize);
        Assert.Equal(9, step.Autofocus.SampleCount);
        var policy = Assert.Single(app.Vm.Workflow.Definition.AutofocusPolicies);
        Assert.Equal(30, policy.IntervalMinutes);
    }

    // ---- the meridian flip: the defaults, and the workflow that has its own

    [Fact]
    public void AWorkflowUsingTheDefaults_FollowsThem_WithASummary_AndSavesNoCopy()
    {
        var settings = NewSettings();
        settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 5, FlipAfterMeridianMinutes = 2, RecenterAfterFlip = true, RestartGuidingAfterFlip = true });
        var app = Create(settings);
        var editor = app.Vm.Workflow;
        editor.StartFromTemplateCommand.Execute(null);

        Assert.True(editor.FlipUsesDefaults);
        Assert.False(editor.FlipIsCustom);
        Assert.StartsWith("Using defaults · Hold -5m · Flip +2m", editor.FlipDefaultsSummary);
        Assert.Contains("Recenter", editor.FlipDefaultsSummary);
        Assert.Contains("Guiding", editor.FlipDefaultsSummary);

        settings.SetMeridianFlip(settings.MeridianFlip with { PauseBeforeMeridianMinutes = 9 });

        Assert.StartsWith("Using defaults · Hold -9m", editor.FlipDefaultsSummary); // it followed
        Assert.Null(editor.Definition!.MeridianFlip); // and nothing of it was copied into the workflow
        Assert.True(editor.Definition.MeridianFlipUsesDefaults);
    }

    [Fact]
    public void AWorkflowWithItsOwnFlip_IsNotChangedByTheDefaults()
    {
        var settings = NewSettings();
        settings.SetMeridianFlip(new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 5 });
        var app = Create(settings);
        var editor = app.Vm.Workflow;
        editor.StartFromTemplateCommand.Execute(null);

        editor.CustomizeFlipCommand.Execute(null); // starts from the defaults it followed

        Assert.True(editor.FlipIsCustom);
        Assert.False(editor.FlipUsesDefaults);
        Assert.True(editor.FlipEnabled);
        Assert.Equal(5, editor.Definition!.MeridianFlip!.PauseBeforeMeridianMinutes);

        settings.SetMeridianFlip(settings.MeridianFlip with { PauseBeforeMeridianMinutes = 11, Enabled = false });

        Assert.Equal(5, editor.Definition.MeridianFlip!.PauseBeforeMeridianMinutes);
        Assert.True(editor.Definition.MeridianFlip.Enabled);

        editor.UseFlipDefaultsCommand.Execute(null); // and back: the workflow follows again
        Assert.True(editor.FlipUsesDefaults);
        Assert.Null(editor.Definition!.MeridianFlip);
    }

    [Fact]
    public async Task TheChoiceOfDefaultsOrCustom_IsSavedInTheWorkflowFile_AndComesBack()
    {
        var serializer = new JsonSequenceDocumentSerializer();

        async Task<WorkflowDefinition> RoundTrip(WorkflowDefinition workflow)
        {
            using var stream = new MemoryStream();
            await serializer.SaveAsync(stream, new SequenceDocument("T", [], null, workflow), CancellationToken.None);
            stream.Position = 0;
            return (await serializer.LoadAsync(stream, CancellationToken.None)).Workflow!;
        }

        var followsDefaults = await RoundTrip(WorkflowDefinition.NewEmpty());
        Assert.True(followsDefaults.MeridianFlipUsesDefaults);
        Assert.Null(followsDefaults.MeridianFlip);

        var own = await RoundTrip(WorkflowDefinition.NewEmpty() with { MeridianFlipUsesDefaults = false, MeridianFlip = new MeridianFlipSettings { Enabled = true, PauseBeforeMeridianMinutes = 8 } });
        Assert.False(own.MeridianFlipUsesDefaults);
        Assert.Equal(8, own.MeridianFlip!.PauseBeforeMeridianMinutes);
    }

    [Fact]
    public void WhatTheDefaultsSay_IsWhatTheCompiledWorkflowDoes_AndACustomOneIgnoresThem()
    {
        var flip = new MeridianFlipSettings { Enabled = true };
        var withDefaults = WorkflowDefinition.NewEmpty();
        var custom = WorkflowDefinition.NewEmpty() with { MeridianFlipUsesDefaults = false, MeridianFlip = new MeridianFlipSettings { Enabled = false } };

        Assert.True(withDefaults.EffectiveFlip(flip).Enabled);
        Assert.False(custom.EffectiveFlip(flip).Enabled);
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

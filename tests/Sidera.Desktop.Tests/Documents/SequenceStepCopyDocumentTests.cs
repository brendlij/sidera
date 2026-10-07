using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>Duplicate, Copy and Paste together with the document, the sequencer and real files.</summary>
public sealed class SequenceStepCopyDocumentTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private static readonly DemoOptions Fast = new()
    {
        ManualExposure = TimeSpan.FromMilliseconds(30),
        SequenceExposure = TimeSpan.FromMilliseconds(30),
        SequenceWait = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        GuiderDitherDuration = TimeSpan.FromMilliseconds(20),
        DitherAmplitudePixels = 0.6,
        SettleStableDuration = TimeSpan.FromMilliseconds(100),
        SettleTimeout = TimeSpan.FromSeconds(5),
    };

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-copy-tests-" + Guid.NewGuid().ToString("N"));

    public SequenceStepCopyDocumentTests()
    {
        Directory.CreateDirectory(_directory);
    }

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

    private string PathOf(string name) => Path.Combine(_directory, name);

    private sealed class Picker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult(SavePath);
    }

    private sealed class App(SideraRuntimeHost host, MainViewModel vm, Picker picker) : IAsyncDisposable
    {
        public SideraRuntimeHost Host { get; } = host;
        public MainViewModel Vm { get; } = vm;
        public Picker Picker { get; } = picker;
        public SequenceDocumentViewModel Document => Vm.SequenceDocument;
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }

        public async Task ConnectEverything()
        {
            await Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Mounts[0].ConnectCommand.ExecuteAsync(null);
            await Vm.Equipment.Guiders[0].ConnectCommand.ExecuteAsync(null);
        }

        public async Task SaveAs(string path)
        {
            Picker.SavePath = path;
            await Document.SaveAsCommand.ExecuteAsync(null);
        }

        public async Task Open(string path)
        {
            Picker.OpenPath = path;
            await Document.OpenCommand.ExecuteAsync(null);
            if (Document.IsConfirmingDiscard)
            {
                await Document.ConfirmDiscardCommand.ExecuteAsync(null);
            }
        }
    }

    private static App Create()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        var picker = new Picker();
        return new App(host, new MainViewModel(host, a => a(), Fast, SequenceDocumentStore.CreateDefault(), picker), picker);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static IEnumerable<Guid> Ids(IEnumerable<SequenceStepDraft> steps) =>
        steps.SelectMany(step => step is RepeatStepDraft repeat ? repeat.Children.Select(c => c.Id).Prepend(repeat.Id) : [step.Id]);

    // The demo draft with a Repeat added at the end: Repeat × 3 [Exposure 0.03 s, Delay].
    private static RepeatStepDraftViewModel AddRepeat(App app)
    {
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        var repeat = (RepeatStepDraftViewModel)app.Draft.SelectedStep!;
        repeat.CountText = "3";
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        ((ExposureStepDraftViewModel)app.Draft.SelectedStep!).ExposureText = "0.03";
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Delay);
        ((DelayStepDraftViewModel)app.Draft.SelectedStep!).DurationText = "0.03";
        return repeat;
    }

    // The modified state

    [Fact]
    public async Task Copy_AndMovingTheSelection_DoNotMarkTheDocumentModified_ButDuplicateAndPasteDo()
    {
        await using var app = Create();
        await app.SaveAs(PathOf("A.astraseq"));
        Assert.False(app.Document.IsDirty);
        var exposure = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();

        app.Draft.SelectedStep = exposure;
        app.Draft.CopyStepCommand.Execute(null);
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.SelectedStep = null;
        app.Draft.SelectedStep = exposure;

        Assert.True(app.Draft.Clipboard.HasContent);
        Assert.False(app.Document.IsDirty);

        app.Draft.DuplicateStepCommand.Execute(null);
        Assert.True(app.Document.IsDirty);

        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.False(app.Document.IsDirty);

        app.Draft.PasteStepCommand.Execute(null); // what was copied before the save can be pasted after it
        Assert.True(app.Document.IsDirty);
        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task ACopyThatIsPasted_IsPartOfTheDocumentLikeAnyOtherStep()
    {
        await using var app = Create();
        app.Draft.SelectedStep = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();
        app.Draft.CopyStepCommand.Execute(null);
        app.Draft.PasteStepCommand.Execute(null);
        await app.SaveAs(PathOf("Pasted"));

        var saved = await SequenceDocumentStore.CreateDefault().LoadAsync(PathOf("Pasted.astraseq"));

        Assert.Equal(9, saved.Steps.Count);
        Assert.Equal(saved.Steps.Count, saved.Steps.Select(s => s.Id).Distinct().Count());
    }

    // Persistence

    [Fact]
    public async Task ADuplicatedRepeat_IsSavedAndOpenedAsTwoRepeats_WithDistinctIdsAndTheSameValues()
    {
        await using var app = Create();
        var repeat = AddRepeat(app);
        app.Draft.SelectedStep = repeat;
        app.Draft.DuplicateStepCommand.Execute(null);
        var duplicate = (RepeatStepDraftViewModel)app.Draft.SelectedStep!;
        var ids = Ids(app.Draft.Snapshot()).ToList();
        await app.SaveAs(PathOf("Twice"));

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);
        Assert.True(app.Draft.IsEmpty);
        await app.Open(PathOf("Twice.astraseq"));

        var repeats = app.Draft.Steps.OfType<RepeatStepDraftViewModel>().ToList();
        Assert.Equal(2, repeats.Count);
        Assert.Equal([repeat.Id, duplicate.Id], repeats.Select(r => r.Id));
        Assert.NotEqual(repeats[0].Id, repeats[1].Id);
        Assert.Equal(["3", "3"], repeats.Select(r => r.CountText));
        Assert.Equal(ids, Ids(app.Draft.Snapshot()));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(
            repeats[0].Children.Select(c => c.Summary), repeats[1].Children.Select(c => c.Summary));
        Assert.Empty(repeats[0].Children.Select(c => c.Id).Intersect(repeats[1].Children.Select(c => c.Id)));
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task TheDocumentContainsNoClipboard_NothingBeyondTheSequence()
    {
        await using var app = Create();
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.CopyStepCommand.Execute(null);
        await app.SaveAs(PathOf("Plain"));

        var text = await File.ReadAllTextAsync(PathOf("Plain.astraseq"));

        Assert.DoesNotContain("clipboard", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.Equal(8, (await SequenceDocumentStore.CreateDefault().LoadAsync(PathOf("Plain.astraseq"))).Steps.Count);
    }

    // Across documents

    [Fact]
    public async Task AStepCopiedInOneDocument_CanBePastedIntoAnotherOne_WithNewIdsAndTheSameValues()
    {
        await using var app = Create();
        var first = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();
        first.ExposureText = "123";
        app.Draft.SelectedStep = first;
        app.Draft.CopyStepCommand.Execute(null);
        await app.SaveAs(PathOf("A"));
        var other = new SequenceDocument("B",
        [
            new DelayDocumentStep(Guid.NewGuid(), 1),
            new DelayDocumentStep(Guid.NewGuid(), 2),
        ]);
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("B.astraseq"), other);

        await app.Open(PathOf("B.astraseq"));
        Assert.Equal(2, app.Draft.Steps.Count);
        Assert.True(app.Draft.PasteStepCommand.CanExecute(null)); // the clipboard survived Open

        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<ExposureStepDraftViewModel>(app.Draft.Steps[1]);
        Assert.Equal("123", pasted.ExposureText);
        Assert.NotEqual(first.Id, pasted.Id);
        Assert.Equal(3, app.Draft.Steps.Count);
        Assert.True(app.Document.IsDirty);
        Assert.Equal("B.astraseq", app.Document.DisplayName);
    }

    [Fact]
    public async Task TheClipboard_AlsoSurvivesNew()
    {
        await using var app = Create();
        app.Draft.SelectedStep = app.Draft.Steps.OfType<DitherStepDraftViewModel>().First();
        app.Draft.CopyStepCommand.Execute(null);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);
        Assert.True(app.Draft.IsEmpty);
        app.Draft.PasteStepCommand.Execute(null);

        Assert.IsType<DitherStepDraftViewModel>(Assert.Single(app.Draft.Steps));
    }

    [Fact]
    public async Task ARepeatCopiedInOneDocument_IsPastedIntoAnotherAsAWholeRepeat()
    {
        await using var app = Create();
        var repeat = AddRepeat(app);
        app.Draft.SelectedStep = repeat;
        app.Draft.CopyStepCommand.Execute(null);
        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        app.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<RepeatStepDraftViewModel>(Assert.Single(app.Draft.Steps));
        Assert.Equal("3", pasted.CountText);
        Assert.Equal(2, pasted.Children.Count);
        Assert.NotEqual(repeat.Id, pasted.Id);
    }

    [Fact]
    public async Task AStepWithADeviceThisInstallationDoesNotHave_IsPastedInvalid_AndCanBeRepaired()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var elsewhere = new SequenceDocument("Observatory",
        [
            new ExposureDocumentStep(Guid.NewGuid(), "camera.observatory", 0.03),
        ]);
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Observatory.astraseq"), elsewhere);
        await app.Open(PathOf("Observatory.astraseq"));
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.CopyStepCommand.Execute(null);
        app.Draft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 0.03)]);
        app.Sequencer.RefreshReadiness();
        Assert.True(app.Sequencer.CanRun);

        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<ExposureStepDraftViewModel>(app.Draft.SelectedStep);
        Assert.Equal(new DeviceId("camera.observatory"), pasted.Camera.SelectedId); // not replaced
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Sequencer.CanRun);

        pasted.Camera.Selected = pasted.Camera.Options.Single(o => o.IdText == "camera.main");
        app.Sequencer.RefreshReadiness();

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Sequencer.CanRun);
    }

    // Readiness and running

    [Fact]
    public async Task DuplicatedAndPastedSteps_AreInThePreview_AndRunWithTheSequence()
    {
        await using var app = Create();
        await app.ConnectEverything();
        var repeat = AddRepeat(app);
        app.Draft.SelectedStep = repeat;
        app.Draft.DuplicateStepCommand.Execute(null);
        app.Draft.CopyStepCommand.Execute(null);
        app.Draft.PasteStepCommand.Execute(null); // and one pasted copy of the duplicate

        Assert.Equal(["Repeat × 3", "Exposure", "Delay"], app.Sequencer.Definition.Skip(8).Take(3).Select(r => r.Title));
        Assert.Equal(8 + 3 * 3, app.Sequencer.Definition.Count);
        Assert.True(app.Sequencer.CanRun);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(3 + 3 * 3, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task ADuplicateThatNeedsADeviceNobodyConnected_ChangesTheReadiness()
    {
        await using var app = Create();
        await app.Vm.Equipment.Cameras[0].ConnectCommand.ExecuteAsync(null);
        app.Draft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.main"), 0.03)]);
        app.Sequencer.RefreshReadiness();
        Assert.True(app.Sequencer.CanRun);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        app.Sequencer.RefreshReadiness();
        Assert.False(app.Sequencer.CanRun); // the guider is not connected

        app.Draft.SelectedStep = app.Draft.Steps[1];
        app.Draft.RemoveStepCommand.Execute(null);
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.DuplicateStepCommand.Execute(null);
        app.Sequencer.RefreshReadiness();

        Assert.True(app.Sequencer.CanRun);
    }

    // While a sequence runs

    private static (bool Duplicate, bool Copy, bool Paste, bool Add) Commands(App app) =>
        (app.Draft.DuplicateStepCommand.CanExecute(null), app.Draft.CopyStepCommand.CanExecute(null),
         app.Draft.PasteStepCommand.CanExecute(null), app.Draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));

    [Fact]
    public async Task DuplicateCopyAndPaste_AreLockedWhileRunningPausingAndPaused_AndFreeAfterwards()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.CopyStepCommand.Execute(null);
        var vm = app.Sequencer;
        var before = app.Draft.Snapshot().Select(s => s.Id).ToList();
        Assert.Equal((true, true, true, true), Commands(app));

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.Equal((false, false, false, false), Commands(app));

        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Pausing, "pausing");
        Assert.Equal((false, false, false, false), Commands(app));
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, false, false), Commands(app));

        // Forcing the commands does nothing either.
        app.Draft.DuplicateStepCommand.Execute(null);
        app.Draft.PasteStepCommand.Execute(null);
        Assert.Equal(before, app.Draft.Snapshot().Select(s => s.Id));

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal((true, true, true, true), Commands(app));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task DuplicateCopyAndPaste_AreFreeAgainAfterACancelledOrAFailedRun(string how)
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.SelectedStep = app.Draft.Steps[0];
        app.Draft.CopyStepCommand.Execute(null);
        if (how == "failed")
        {
            var dither = app.Draft.Steps.OfType<DitherStepDraftViewModel>().First();
            dither.SettleThresholdText = "0.31";
            dither.SettleStableText = "0.1";
            dither.SettleTimeoutText = "0.3";
            dither.AmplitudeText = "5";
        }
        else
        {
            app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        }

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        if (how == "cancelled")
        {
            await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
            Assert.Equal((false, false, false, false), Commands(app));
            app.Sequencer.CancelCommand.Execute(null);
        }

        await run.WaitAsync(Bound);

        Assert.Equal(how == "failed" ? SequenceState.Failed : SequenceState.Cancelled, app.Sequencer.State);
        Assert.Equal((true, true, true, true), Commands(app));
    }
}

using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>New, Open, Save and Save As of the sequence document, and the unsaved state, without any real file.</summary>
public class SequenceDocumentViewModelTests
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

    // Documents in memory, by path. Can be told to fail, or to wait inside a save.
    private sealed class FakeStore : ISequenceDocumentStore
    {
        public Dictionary<string, SequenceDocument> Files { get; } = new();
        public List<string> Saved { get; } = [];
        public List<string> Loaded { get; } = [];
        public Exception? FailLoad { get; set; }
        public Exception? FailSave { get; set; }
        public Task? HoldSave { get; set; }

        public Task<SequenceDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
        {
            Loaded.Add(path);
            if (FailLoad is not null)
            {
                return Task.FromException<SequenceDocument>(FailLoad);
            }

            return Task.FromResult(Files.TryGetValue(path, out var document)
                ? document
                : throw new SequenceDocumentException(SequenceDocumentErrorKind.Container, "Could not open sequence: the file was not found."));
        }

        public async Task SaveAsync(string path, SequenceDocument document, CancellationToken cancellationToken = default)
        {
            if (HoldSave is not null)
            {
                await HoldSave;
            }

            if (FailSave is not null)
            {
                throw FailSave;
            }

            Saved.Add(path);
            Files[path] = document;
        }
    }

    private sealed class FakePicker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }
        public int OpenAsked { get; private set; }
        public List<string> SaveAsked { get; } = [];

        public Task<string?> PickOpenPathAsync()
        {
            OpenAsked++;
            return Task.FromResult(OpenPath);
        }

        public Task<string?> PickSavePathAsync(string suggestedFileName)
        {
            SaveAsked.Add(suggestedFileName);
            return Task.FromResult(SavePath);
        }
    }

    private sealed class App : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public required FakeStore Store { get; init; }
        public required FakePicker Picker { get; init; }
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

        /// <summary>Saves the sequence to a path of the fake store, so that the document is clean.</summary>
        public async Task MakeClean(string path = "C:\\seq\\Clean.astraseq")
        {
            Picker.SavePath = path;
            await Document.SaveAsCommand.ExecuteAsync(null);
            Assert.False(Document.IsDirty);
        }
    }

    private static App Create()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        var store = new FakeStore();
        var picker = new FakePicker();
        return new App
        {
            Host = host, Store = store, Picker = picker,
            Vm = new MainViewModel(host, action => action(), Fast, store, picker),
        };
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

    private static IReadOnlyList<Guid> Ids(SequenceDraftViewModel draft) => draft.Rows.Select(r => r.Id).ToList();

    // Start

    [Fact]
    public async Task AtStart_TheDemoSequenceIsUntitled_AndNotModified()
    {
        await using var app = Create();

        Assert.Null(app.Document.FilePath);
        Assert.Equal("Untitled Session", app.Document.DisplayName);
        Assert.False(app.Document.IsDirty);
        Assert.False(app.Document.HasFile);
        Assert.Equal(8, app.Draft.Steps.Count);
    }

    // New

    [Fact]
    public async Task New_MakesAnEmptyUnsavedDocument_ThatCountsAsModified()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\Old.astraseq");

        await app.Document.NewCommand.ExecuteAsync(null);

        Assert.True(app.Draft.IsEmpty);
        Assert.Null(app.Document.FilePath);
        Assert.Equal("Untitled Session", app.Document.DisplayName);
        Assert.True(app.Document.IsDirty);
        Assert.False(app.Sequencer.CanRun);
        Assert.False(app.Document.IsConfirmingDiscard);
    }

    [Fact]
    public async Task AfterNew_StepsAreAddedWithTheCurrentDefaults()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);

        app.Draft.AddStepCommand.Execute(SequenceStepKind.Exposure);

        var exposure = Assert.IsType<ExposureStepDraftViewModel>(Assert.Single(app.Draft.Steps));
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        Assert.Equal("0.03", exposure.ExposureText);
    }

    [Fact]
    public async Task NewOnAModifiedDocument_AsksBeforeDiscarding_AndChangesNothingUntilConfirmed()
    {
        await using var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        var before = Ids(app.Draft);
        Assert.True(app.Document.IsDirty);

        await app.Document.NewCommand.ExecuteAsync(null);

        Assert.True(app.Document.IsConfirmingDiscard);
        Assert.Equal(before, Ids(app.Draft));

        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        Assert.False(app.Document.IsConfirmingDiscard);
        Assert.True(app.Draft.IsEmpty);
    }

    [Fact]
    public async Task CancellingTheDiscardQuestion_KeepsEverything()
    {
        await using var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        var before = Ids(app.Draft);

        await app.Document.NewCommand.ExecuteAsync(null);
        app.Document.CancelDiscardCommand.Execute(null);

        Assert.False(app.Document.IsConfirmingDiscard);
        Assert.Equal(before, Ids(app.Draft));
        Assert.True(app.Document.IsDirty);
        Assert.False(app.Document.ConfirmDiscardCommand.CanExecute(null));
    }

    // Save and Save As

    [Fact]
    public async Task SaveWithoutAFile_AsksWhereToSave_AddsTheExtension_WritesTheDraft_AndIsClean()
    {
        await using var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        app.Picker.SavePath = "C:\\seq\\M42";

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Equal(["Sequence.astraseq"], app.Picker.SaveAsked);
        Assert.Equal(["C:\\seq\\M42.astraseq"], app.Store.Saved);
        Assert.Equal("C:\\seq\\M42.astraseq", app.Document.FilePath);
        Assert.Equal("M42.astraseq", app.Document.DisplayName);
        Assert.False(app.Document.IsDirty);
        Assert.Null(app.Document.ErrorMessage);
    }

    [Fact]
    public async Task TheDocumentThatIsSaved_IsTheDraftWithItsIds_AndTheFileNameAsItsName()
    {
        await using var app = Create();
        var ids = Ids(app.Draft);
        app.Picker.SavePath = "C:\\seq\\Test Session.astraseq";

        await app.Document.SaveCommand.ExecuteAsync(null);

        var document = app.Store.Files["C:\\seq\\Test Session.astraseq"];
        Assert.Equal("Test Session", document.Name);
        Assert.Equal(ids.Count, document.Steps.Count + document.Steps.OfType<RepeatDocumentStep>().Sum(r => r.Children.Count));
        Assert.Equal(
            app.Draft.Steps.Select(s => s.Id),
            document.Steps.Select(s => s.Id));
    }

    [Fact]
    public async Task SaveWithAFile_WritesThereWithoutAskingAgain()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\A.astraseq");
        app.Picker.SaveAsked.Clear();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        Assert.True(app.Document.IsDirty);

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Empty(app.Picker.SaveAsked);
        Assert.Equal(["C:\\seq\\A.astraseq", "C:\\seq\\A.astraseq"], app.Store.Saved);
        Assert.False(app.Document.IsDirty);
        Assert.Equal(9, app.Store.Files["C:\\seq\\A.astraseq"].Steps.Count);
    }

    [Fact]
    public async Task SaveWhenTheUserCancelsThePicker_ChangesNothing()
    {
        await using var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        app.Picker.SavePath = null;

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Empty(app.Store.Saved);
        Assert.Null(app.Document.FilePath);
        Assert.True(app.Document.IsDirty);
        Assert.Null(app.Document.ErrorMessage);
    }

    [Fact]
    public async Task SaveThatFails_ShowsAConciseError_KeepsTheDocumentModified_AndTheFileUnchanged()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\A.astraseq");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        app.Store.FailSave = new SequenceDocumentException(
            SequenceDocumentErrorKind.Container, "Could not save sequence.", new IOException("disk full"));

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Could not save sequence.", app.Document.ErrorMessage);
        Assert.True(app.Document.IsDirty);
        Assert.Equal("C:\\seq\\A.astraseq", app.Document.FilePath);
        Assert.IsType<IOException>(app.Document.LastException!.InnerException); // the cause is kept for diagnostics
        Assert.Equal(8, app.Store.Files["C:\\seq\\A.astraseq"].Steps.Count);
    }

    [Fact]
    public async Task SaveThatFailsUnexpectedly_AlsoKeepsTheDocumentModified_WithoutExposingTheException()
    {
        await using var app = Create();
        app.Picker.SavePath = "C:\\seq\\A.astraseq";
        app.Store.FailSave = new InvalidOperationException("secret internal detail\n   at Somewhere.Deep()");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Could not save sequence.", app.Document.ErrorMessage);
        Assert.True(app.Document.IsDirty);
        Assert.Null(app.Document.FilePath);
    }

    [Fact]
    public async Task ASuccessfulSaveAfterAFailedOne_ClearsTheError()
    {
        await using var app = Create();
        app.Picker.SavePath = "C:\\seq\\A.astraseq";
        app.Store.FailSave = new IOException("x");
        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(app.Document.ErrorMessage);

        app.Store.FailSave = null;
        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Null(app.Document.ErrorMessage);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task SaveAs_WritesToTheNewPath_AndMakesItTheDocument()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\A.astraseq");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        app.Picker.SavePath = "C:\\seq\\B";

        await app.Document.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal("A.astraseq", app.Picker.SaveAsked.Last());
        Assert.Equal("C:\\seq\\B.astraseq", app.Document.FilePath);
        Assert.False(app.Document.IsDirty);
        Assert.Equal(9, app.Store.Files["C:\\seq\\B.astraseq"].Steps.Count);
        Assert.Equal(8, app.Store.Files["C:\\seq\\A.astraseq"].Steps.Count); // the old file is as it was
    }

    [Fact]
    public async Task SaveAs_Cancelled_KeepsPathAndModifiedState()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\A.astraseq");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        app.Picker.SavePath = null;

        await app.Document.SaveAsCommand.ExecuteAsync(null);

        Assert.Equal("C:\\seq\\A.astraseq", app.Document.FilePath);
        Assert.True(app.Document.IsDirty);
    }

    [Fact]
    public async Task SaveWithAValueThatIsNotANumber_IsRefused_RatherThanSavingAMadeUpValue()
    {
        await using var app = Create();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "abc";
        app.Picker.SavePath = "C:\\seq\\A.astraseq";

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Fix the marked values before saving.", app.Document.ErrorMessage);
        Assert.Empty(app.Store.Saved);
        Assert.True(app.Document.IsDirty);
    }

    [Fact]
    public async Task ADraftThatCannotRun_CanStillBeSaved_BecauseItIsWorkInProgress()
    {
        await using var app = Create();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "0";
        Assert.False(app.Draft.IsValid);
        app.Picker.SavePath = "C:\\seq\\A.astraseq";

        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.Null(app.Document.ErrorMessage);
        Assert.False(app.Document.IsDirty);
        Assert.Contains(
            app.Store.Files["C:\\seq\\A.astraseq"].Steps.OfType<ExposureDocumentStep>(),
            s => s.ExposureSeconds == 0);
    }

    [Fact]
    public async Task EditsMadeWhileTheFileIsBeingWritten_KeepTheDocumentModified()
    {
        await using var app = Create();
        var gate = new TaskCompletionSource();
        app.Store.HoldSave = gate.Task;
        app.Picker.SavePath = "C:\\seq\\A.astraseq";

        var saving = app.Document.SaveCommand.ExecuteAsync(null);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay); // not in the file that is being written
        gate.SetResult();
        await saving;

        Assert.Equal("C:\\seq\\A.astraseq", app.Document.FilePath);
        Assert.True(app.Document.IsDirty);
    }

    // Open

    private static SequenceDocument DocumentWithRepeat(out Guid repeatId, out Guid exposureId)
    {
        repeatId = Guid.NewGuid();
        exposureId = Guid.NewGuid();
        return new SequenceDocument("Loaded",
        [
            new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
            new RepeatDocumentStep(repeatId, 4,
            [
                new ExposureDocumentStep(exposureId, "camera.main", 45),
                new DelayDocumentStep(Guid.NewGuid(), 3),
            ]),
            new StopGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        ]);
    }

    [Fact]
    public async Task Open_ReplacesTheDraft_RestoresIdsAndValues_SetsTheFile_AndIsClean()
    {
        await using var app = Create();
        var document = DocumentWithRepeat(out var repeatId, out var exposureId);
        app.Store.Files["C:\\seq\\Loaded.astraseq"] = document;
        app.Picker.OpenPath = "C:\\seq\\Loaded.astraseq";

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal("C:\\seq\\Loaded.astraseq", app.Document.FilePath);
        Assert.Equal("Loaded.astraseq", app.Document.DisplayName);
        Assert.False(app.Document.IsDirty);
        Assert.Null(app.Document.ErrorMessage);
        Assert.Equal(3, app.Draft.Steps.Count);
        var repeat = Assert.IsType<RepeatStepDraftViewModel>(app.Draft.Steps[1]);
        Assert.Equal(repeatId, repeat.Id);
        Assert.Equal("4", repeat.CountText);
        var exposure = Assert.IsType<ExposureStepDraftViewModel>(repeat.Children[0]);
        Assert.Equal(exposureId, exposure.Id);
        Assert.Equal("45", exposure.ExposureText);
        Assert.Equal("Main Camera · 45 s · Camera defaults", exposure.Summary);
        Assert.Equal(["1", "2", "2.1", "2.2", "3"], app.Draft.Rows.Select(r => r.NumberLabel));
        Assert.Same(app.Draft.Steps[0], app.Draft.SelectedStep); // a sensible first row
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
    }

    [Fact]
    public async Task Open_RefreshesTheOutlineAndTheReadiness()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Store.Files["C:\\seq\\Loaded.astraseq"] = DocumentWithRepeat(out _, out _);
        app.Picker.OpenPath = "C:\\seq\\Loaded.astraseq";

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal(
            ["Start Guiding", "Repeat × 4", "Exposure", "Delay", "Stop Guiding"],
            app.Sequencer.Definition.Select(r => r.Title));
        Assert.True(app.Sequencer.CanRun);
    }

    [Fact]
    public async Task Open_OfAnEmptySequence_ShowsTheEmptyState()
    {
        await using var app = Create();
        app.Store.Files["C:\\seq\\Empty.astraseq"] = new SequenceDocument(null, []);
        app.Picker.OpenPath = "C:\\seq\\Empty.astraseq";

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.True(app.Draft.IsEmpty);
        Assert.Null(app.Draft.SelectedStep);
        Assert.False(app.Document.IsDirty);
        Assert.True(app.Sequencer.IsEmpty);
    }

    [Fact]
    public async Task OpenOnAModifiedDocument_AsksFirst_AndOnlyThenAsksForAFile()
    {
        await using var app = Create();
        app.Store.Files["C:\\seq\\Loaded.astraseq"] = DocumentWithRepeat(out _, out _);
        app.Picker.OpenPath = "C:\\seq\\Loaded.astraseq";
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        var before = Ids(app.Draft);

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.True(app.Document.IsConfirmingDiscard);
        Assert.Equal(0, app.Picker.OpenAsked);
        Assert.Equal(before, Ids(app.Draft));

        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        Assert.Equal(1, app.Picker.OpenAsked);
        Assert.Equal("Loaded.astraseq", app.Document.DisplayName);
        Assert.Equal(3, app.Draft.Steps.Count);
    }

    [Fact]
    public async Task OpenOnACleanDocument_DoesNotAsk()
    {
        await using var app = Create();
        app.Store.Files["C:\\seq\\Loaded.astraseq"] = DocumentWithRepeat(out _, out _);
        app.Picker.OpenPath = "C:\\seq\\Loaded.astraseq";

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.False(app.Document.IsConfirmingDiscard);
        Assert.Equal("Loaded.astraseq", app.Document.DisplayName);
    }

    [Fact]
    public async Task OpenCancelledInThePicker_ChangesNothing()
    {
        await using var app = Create();
        var before = Ids(app.Draft);
        app.Picker.OpenPath = null;

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal(before, Ids(app.Draft));
        Assert.Empty(app.Store.Loaded);
        Assert.Null(app.Document.ErrorMessage);
    }

    [Theory]
    [InlineData(SequenceDocumentErrorKind.Container, "Invalid Sidera sequence document.")]
    [InlineData(SequenceDocumentErrorKind.Structure, "Unknown sequence step type 'autofocus'.")]
    [InlineData(SequenceDocumentErrorKind.NewerVersion, "This sequence was created by a newer Sidera version.")]
    public async Task AFailedOpen_LeavesDraftFileAndModifiedStateAsTheyWere_AndShowsTheReason(
        SequenceDocumentErrorKind kind, string message)
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\Current.astraseq");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        var ids = Ids(app.Draft);
        var selected = app.Draft.SelectedStep;
        app.Picker.OpenPath = "C:\\seq\\Broken.astraseq";
        app.Store.FailLoad = new SequenceDocumentException(kind, message);
        // Open asks first, because the document is modified.
        await app.Document.OpenCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        Assert.Equal(message, app.Document.ErrorMessage);
        Assert.Equal(ids, Ids(app.Draft));
        Assert.Same(selected, app.Draft.SelectedStep);
        Assert.Equal("C:\\seq\\Current.astraseq", app.Document.FilePath);
        Assert.True(app.Document.IsDirty);
        Assert.False(app.Document.IsConfirmingDiscard);
    }

    [Fact]
    public async Task AFailedOpenOfACleanDocument_KeepsItClean()
    {
        await using var app = Create();
        await app.MakeClean("C:\\seq\\Current.astraseq");
        var ids = Ids(app.Draft);
        app.Picker.OpenPath = "C:\\seq\\Missing.astraseq"; // not in the store

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal("Could not open sequence: the file was not found.", app.Document.ErrorMessage);
        Assert.Equal(ids, Ids(app.Draft));
        Assert.False(app.Document.IsDirty);
        Assert.Equal("Current.astraseq", app.Document.DisplayName);
    }

    [Fact]
    public async Task AnUnexpectedFailureWhileOpening_IsAlsoReportedConcisely_AndChangesNothing()
    {
        await using var app = Create();
        var ids = Ids(app.Draft);
        app.Picker.OpenPath = "C:\\seq\\X.astraseq";
        app.Store.FailLoad = new InvalidOperationException("internal\n   at Deep.Stack()");

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal("Could not open sequence.", app.Document.ErrorMessage);
        Assert.Equal(ids, Ids(app.Draft));
    }

    [Fact]
    public async Task AFailedOpenThatIsFollowedByASuccessfulOne_ClearsTheError()
    {
        await using var app = Create();
        app.Picker.OpenPath = "C:\\seq\\Missing.astraseq";
        await app.Document.OpenCommand.ExecuteAsync(null);
        Assert.NotNull(app.Document.ErrorMessage);

        app.Store.Files["C:\\seq\\Loaded.astraseq"] = DocumentWithRepeat(out _, out _);
        app.Picker.OpenPath = "C:\\seq\\Loaded.astraseq";
        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Null(app.Document.ErrorMessage);
    }

    [Fact]
    public async Task ASavedSequence_OpensAsTheSameSequence()
    {
        await using var app = Create();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "123";
        var ids = Ids(app.Draft);
        var summaries = app.Draft.Rows.Select(r => r.Summary).ToList();
        await app.MakeClean("C:\\seq\\Round.astraseq");

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);
        Assert.True(app.Draft.IsEmpty);
        app.Picker.OpenPath = "C:\\seq\\Round.astraseq";
        await app.Document.OpenCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        Assert.Equal(ids, Ids(app.Draft));
        Assert.Equal(summaries, app.Draft.Rows.Select(r => r.Summary));
        Assert.False(app.Document.IsDirty);
    }

    // The modified state

    public static TheoryData<string> Edits => new()
    {
        "add", "add repeat", "add child", "remove", "remove child", "move", "move child",
        "repeat count", "nested parameter", "top-level parameter", "device selection", "nested device selection",
    };

    // Edits of the demo sequence plus a Repeat with children, so that every kind of edit has something to act on.
    private static async Task<App> CreateWithRepeat()
    {
        var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Delay);
        await app.MakeClean();
        return app;
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task EveryKindOfEditOfTheDraft_MarksTheDocumentModified(string edit)
    {
        await using var app = await CreateWithRepeat();
        var repeat = app.Draft.Steps.OfType<RepeatStepDraftViewModel>().Single();
        app.Host.AddSimulatedCamera(new DeviceId("camera.second"), "Second Camera");
        app.Draft.RefreshDevices();
        Assert.False(app.Document.IsDirty);

        switch (edit)
        {
            case "add":
                app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
                break;
            case "add repeat":
                app.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
                break;
            case "add child":
                app.Draft.SelectedStep = repeat;
                app.Draft.AddChildCommand.Execute(SequenceStepKind.Slew);
                break;
            case "remove":
                app.Draft.SelectedStep = app.Draft.Steps[0];
                app.Draft.RemoveStepCommand.Execute(null);
                break;
            case "remove child":
                app.Draft.SelectedStep = repeat.Children[1];
                app.Draft.RemoveStepCommand.Execute(null);
                break;
            case "move":
                app.Draft.SelectedStep = app.Draft.Steps[1];
                app.Draft.MoveStepUpCommand.Execute(null);
                break;
            case "move child":
                app.Draft.SelectedStep = repeat.Children[1];
                app.Draft.MoveStepUpCommand.Execute(null);
                break;
            case "repeat count":
                repeat.CountText = "9";
                break;
            case "nested parameter":
                ((DelayStepDraftViewModel)repeat.Children[1]).DurationText = "7";
                break;
            case "top-level parameter":
                app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "11";
                break;
            case "device selection":
                var top = app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First();
                top.Camera.Selected = top.Camera.Options.Single(o => o.IdText == "camera.second");
                break;
            case "nested device selection":
                var nested = (ExposureStepDraftViewModel)repeat.Children[0];
                nested.Camera.Selected = nested.Camera.Options.Single(o => o.IdText == "camera.second");
                break;
            default:
                throw new InvalidOperationException(edit);
        }

        Assert.True(app.Document.IsDirty, edit);
    }

    [Fact]
    public async Task ReadingTheEquipmentAgain_DoesNotMarkTheDocumentModified()
    {
        await using var app = Create();
        await app.MakeClean();

        app.Host.AddSimulatedCamera(new DeviceId("camera.second"), "Second Camera");
        app.Draft.RefreshDevices();
        app.Sequencer.RefreshReadiness();
        app.Host.DeviceRegistry.Unregister(new DeviceId("camera.second"));
        app.Draft.RefreshDevices();

        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task RunningPausingResumingAndCompleting_DoNotMarkTheDocumentModified()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        await app.MakeClean();
        var vm = app.Sequencer;

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.False(app.Document.IsDirty);

        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.False(app.Document.IsDirty);

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task ACancelledRun_DoesNotMarkTheDocumentModified()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        await app.MakeClean();

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        app.Sequencer.CancelCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Cancelled, app.Sequencer.State);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task EditingAfterARun_MarksTheDocumentModified_AndSaveWritesTheDraftNotTheRun()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.03");
        await app.MakeClean();
        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);
        Assert.False(app.Document.IsDirty);

        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "0.05";
        Assert.True(app.Document.IsDirty);
        await app.Document.SaveCommand.ExecuteAsync(null);

        Assert.False(app.Document.IsDirty);
        var saved = app.Store.Files["C:\\seq\\Clean.astraseq"];
        Assert.Equal(8, saved.Steps.Count);
        Assert.Equal(0.05, saved.Steps.OfType<ExposureDocumentStep>().First().ExposureSeconds);
    }

    // While a sequence runs

    private static (bool New, bool Open, bool Save, bool SaveAs) Commands(App app) =>
        (app.Document.NewCommand.CanExecute(null), app.Document.OpenCommand.CanExecute(null),
         app.Document.SaveCommand.CanExecute(null), app.Document.SaveAsCommand.CanExecute(null));

    [Fact]
    public async Task DocumentCommands_AreLockedWhileRunningPausingAndPaused_AndFreeAfterwards()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        var vm = app.Sequencer;
        Assert.Equal((true, true, true, true), Commands(app));

        var run = vm.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.State == SequenceState.Running, "running");
        Assert.Equal((false, false, false, false), Commands(app));
        Assert.True(vm.PauseCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.PauseCommand.Execute(null);
        await WaitUntil(() => vm.State == SequenceState.Pausing, "pausing");
        Assert.Equal((false, false, false, false), Commands(app));
        await WaitUntil(() => vm.State == SequenceState.Paused, "paused");
        Assert.Equal((false, false, false, false), Commands(app));
        Assert.True(vm.ResumeCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, vm.State);
        Assert.Equal((true, true, true, true), Commands(app));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task DocumentCommands_AreFreeAgainAfterACancelledOrAFailedRun(string how)
    {
        await using var app = Create();
        await app.ConnectEverything();
        if (how == "failed")
        {
            // The guide error can never get below this threshold within the timeout: the settle wait fails.
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

    [Fact]
    public async Task OpeningWhileARunIsActive_EvenBypassingTheDisabledCommand_LeavesTheRunAndTheDraftAlone()
    {
        await using var app = Create();
        await app.ConnectEverything();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().ToList().ForEach(e => e.ExposureText = "0.3");
        app.Store.Files["C:\\seq\\Other.astraseq"] = DocumentWithRepeat(out _, out _);
        app.Picker.OpenPath = "C:\\seq\\Other.astraseq";
        var ids = Ids(app.Draft);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.State == SequenceState.Running, "running");
        await app.Document.OpenCommand.ExecuteAsync(null); // direct call: the button is disabled

        Assert.Equal(ids, Ids(app.Draft));
        Assert.Equal(8, app.Sequencer.Definition.Count);
        await run.WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Sequencer.State);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
        Assert.Equal(ids, Ids(app.Draft));
        Assert.Null(app.Document.FilePath);
    }

    [Fact]
    public async Task AQuestionThatIsOpenWhenARunStarts_IsWithdrawn()
    {
        await using var app = Create();
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay); // modified
        await app.Document.NewCommand.ExecuteAsync(null);
        Assert.True(app.Document.IsConfirmingDiscard);

        app.Draft.IsEditable = false; // what a starting run does

        Assert.False(app.Document.IsConfirmingDiscard);
        Assert.False(app.Document.ConfirmDiscardCommand.CanExecute(null));
    }
}

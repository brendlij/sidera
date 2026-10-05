using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>The document commands with real files (the real store and serializer) in a temporary folder.</summary>
public sealed class SequenceDocumentFilesIntegrationTests : IDisposable
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

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-doc-tests-" + Guid.NewGuid().ToString("N"));

    public SequenceDocumentFilesIntegrationTests()
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
    }

    // The real store: files on disk.
    private static App Create()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        var picker = new Picker();
        return new App(host, new MainViewModel(host, action => action(), Fast, SequenceDocumentStore.CreateDefault(), picker), picker);
    }

    [Fact]
    public async Task ASequenceSavedAsAFile_IsReadableAndIdentifiesItself_AndOpensAgainAsTheSameSequence()
    {
        await using var app = Create();
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "123";
        var ids = app.Draft.Rows.Select(r => r.Id).ToList();
        var summaries = app.Draft.Rows.Select(r => r.Summary).ToList();
        app.Picker.SavePath = PathOf("Test Session");

        await app.Document.SaveCommand.ExecuteAsync(null);

        var path = PathOf("Test Session.astraseq");
        Assert.True(File.Exists(path));
        Assert.Equal("Test Session.astraseq", app.Document.DisplayName);
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"format\": \"astra-sequence\"", text, StringComparison.Ordinal);
        Assert.Contains("\"version\": 8", text, StringComparison.Ordinal);
        Assert.Contains("\"exposureSeconds\": 123", text, StringComparison.Ordinal);

        // Change things, then open the file again: the saved state comes back.
        app.Draft.Steps.OfType<ExposureStepDraftViewModel>().First().ExposureText = "5";
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Delay);
        Assert.True(app.Document.IsDirty);
        app.Picker.OpenPath = path;
        await app.Document.OpenCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null);

        Assert.False(app.Document.IsDirty);
        Assert.Equal(ids, app.Draft.Rows.Select(r => r.Id));
        Assert.Equal(summaries, app.Draft.Rows.Select(r => r.Summary));
    }

    [Fact]
    public async Task ASequenceWithARepeat_RoundTripsThroughAFile_AndRuns()
    {
        await using var app = Create();
        await app.ConnectEverything();
        await app.Document.NewCommand.ExecuteAsync(null);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Repeat);
        ((RepeatStepDraftViewModel)app.Draft.SelectedStep!).CountText = "3";
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
        ((ExposureStepDraftViewModel)app.Draft.SelectedStep!).ExposureText = "0.03";
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Delay);
        app.Draft.AddChildCommand.Execute(SequenceStepKind.Dither);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.StopGuiding);
        app.Picker.SavePath = PathOf("Repeat");
        await app.Document.SaveCommand.ExecuteAsync(null);
        var ids = app.Draft.Rows.Select(r => r.Id).ToList();

        await app.Document.NewCommand.ExecuteAsync(null);
        app.Picker.OpenPath = PathOf("Repeat.astraseq");
        await app.Document.OpenCommand.ExecuteAsync(null);
        await app.Document.ConfirmDiscardCommand.ExecuteAsync(null); // a new sequence counts as modified

        Assert.Equal(ids, app.Draft.Rows.Select(r => r.Id));
        Assert.Equal(["1", "2", "2.1", "2.2", "2.3", "3"], app.Draft.Rows.Select(r => r.NumberLabel));
        Assert.True(app.Vm.Sequencer.CanRun);

        await app.Vm.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Vm.Sequencer.State);
        Assert.Equal(3, app.Vm.Imaging.FrameCount);
        Assert.False(app.Document.IsDirty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an sidera file")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":9,\"steps\":[]}")]
    public async Task OpeningAFileThatIsNotUsable_LeavesTheDraftAndTheDocumentAsTheyWere(string content)
    {
        await using var app = Create();
        app.Picker.SavePath = PathOf("Good");
        await app.Document.SaveCommand.ExecuteAsync(null);
        var ids = app.Draft.Rows.Select(r => r.Id).ToList();
        await File.WriteAllTextAsync(PathOf("Bad.astraseq"), content);
        app.Picker.OpenPath = PathOf("Bad.astraseq");

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.NotNull(app.Document.ErrorMessage);
        Assert.Equal(ids, app.Draft.Rows.Select(r => r.Id));
        Assert.Equal("Good.astraseq", app.Document.DisplayName);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task AFutureVersion_IsReportedAsSuch()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Future.astraseq"), "{\"format\":\"astra-sequence\",\"version\":9,\"steps\":[]}");
        app.Picker.OpenPath = PathOf("Future.astraseq");

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Equal("This sequence was created by a newer Sidera version.", app.Document.ErrorMessage);
    }

    // Documents that name equipment this installation does not have

    private static SequenceDocument ObservatoryDocument(out Guid exposureId)
    {
        exposureId = Guid.NewGuid();
        return new SequenceDocument("Observatory",
        [
            new StartGuidingDocumentStep(Guid.NewGuid(), "guider.observatory"),
            new SlewDocumentStep(Guid.NewGuid(), "mount.observatory", 5.588, -5.39),
            new RepeatDocumentStep(Guid.NewGuid(), 2,
            [
                new ExposureDocumentStep(exposureId, "camera.observatory", 0.03),
                new DitherDocumentStep(
                    Guid.NewGuid(), "guider.observatory", "mount.observatory", "camera.observatory", 0.6, 0.5, 0.1, 5),
            ]),
            new StopGuidingDocumentStep(Guid.NewGuid(), "guider.observatory"),
        ]);
    }

    [Fact]
    public async Task ADocumentThatNamesUnavailableDevices_Loads_KeepsTheIds_ButCannotRun_UntilTheyAreReplaced()
    {
        await using var app = Create();
        await app.ConnectEverything();
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Observatory.astraseq"), ObservatoryDocument(out var exposureId));
        app.Picker.OpenPath = PathOf("Observatory.astraseq");

        await app.Document.OpenCommand.ExecuteAsync(null);

        // It loads: only a malformed file is refused.
        Assert.Null(app.Document.ErrorMessage);
        Assert.Equal("Observatory.astraseq", app.Document.DisplayName);
        Assert.Equal(["1", "2", "3", "3.1", "3.2", "4"], app.Draft.Rows.Select(r => r.NumberLabel));
        var start = Assert.IsType<StartGuidingStepDraftViewModel>(app.Draft.Steps[0]);
        var slew = Assert.IsType<SlewStepDraftViewModel>(app.Draft.Steps[1]);
        var repeat = Assert.IsType<RepeatStepDraftViewModel>(app.Draft.Steps[2]);
        var exposure = Assert.IsType<ExposureStepDraftViewModel>(repeat.Children[0]);
        var dither = Assert.IsType<DitherStepDraftViewModel>(repeat.Children[1]);
        Assert.Equal(exposureId, exposure.Id);

        // The ids are kept, and shown as missing; nothing was replaced.
        Assert.Equal(new DeviceId("guider.observatory"), start.Guider.SelectedId);
        Assert.Equal(new DeviceId("mount.observatory"), slew.Mount.SelectedId);
        Assert.Equal(new DeviceId("camera.observatory"), exposure.Camera.SelectedId);
        Assert.Equal(new DeviceId("guider.observatory"), dither.Guider.SelectedId);
        Assert.Equal(new DeviceId("mount.observatory"), dither.Mount.SelectedId);
        Assert.Equal(new DeviceId("camera.observatory"), dither.Camera.SelectedId);
        Assert.True(exposure.Camera.Selected!.IsMissing);
        Assert.Equal("camera.observatory (not available)", exposure.Camera.Selected.Name);

        // The draft is invalid, says why, and cannot run.
        Assert.False(app.Draft.IsValid);
        Assert.Equal(["The guider 'guider.observatory' is not available."], start.Problems);
        Assert.Equal(["The mount 'mount.observatory' is not available."], slew.Problems);
        Assert.Equal(["The camera 'camera.observatory' is not available."], exposure.Problems);
        Assert.Equal(3, dither.Problems.Count);
        Assert.True(repeat.HasProblems);
        Assert.False(app.Vm.Sequencer.CanRun);
        Assert.False(app.Vm.Sequencer.RunCommand.CanExecute(null));
        Assert.False(app.Document.IsDirty); // opening is not an edit
    }

    [Fact]
    public async Task ReplacingTheMissingDevices_MakesTheLoadedSequenceRunnable()
    {
        await using var app = Create();
        await app.ConnectEverything();
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Observatory.astraseq"), ObservatoryDocument(out _));
        app.Picker.OpenPath = PathOf("Observatory.astraseq");
        await app.Document.OpenCommand.ExecuteAsync(null);
        var start = (StartGuidingStepDraftViewModel)app.Draft.Steps[0];
        var slew = (SlewStepDraftViewModel)app.Draft.Steps[1];
        var repeat = (RepeatStepDraftViewModel)app.Draft.Steps[2];
        var exposure = (ExposureStepDraftViewModel)repeat.Children[0];
        var dither = (DitherStepDraftViewModel)repeat.Children[1];
        var stop = (StopGuidingStepDraftViewModel)app.Draft.Steps[3];

        DeviceOption Pick(DevicePickerViewModel picker, string id) => picker.Options.Single(o => o.IdText == id);

        // A replacement is offered next to the missing device, which is not removed until it is replaced.
        Assert.Contains(start.Guider.Options, o => o.IsMissing);
        Assert.Contains(start.Guider.Options, o => o.IdText == "guider.main");

        start.Guider.Selected = Pick(start.Guider, "guider.main");
        Assert.False(app.Draft.IsValid);
        slew.Mount.Selected = Pick(slew.Mount, "mount.eq6");
        exposure.Camera.Selected = Pick(exposure.Camera, "camera.main");
        dither.Guider.Selected = Pick(dither.Guider, "guider.main");
        dither.Mount.Selected = Pick(dither.Mount, "mount.eq6");
        dither.Camera.Selected = Pick(dither.Camera, "camera.main");
        Assert.False(app.Draft.IsValid); // the Stop Guiding is still on the missing guider
        stop.Guider.Selected = Pick(stop.Guider, "guider.main");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Document.IsDirty);
        Assert.True(app.Vm.Sequencer.CanRun);

        await app.Vm.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Completed, app.Vm.Sequencer.State);
        Assert.Equal(2, app.Vm.Imaging.FrameCount);
    }

    [Fact]
    public async Task AnUnavailableDevice_SurvivesSavingAgain_AsTheSameId()
    {
        await using var app = Create();
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Observatory.astraseq"), ObservatoryDocument(out _));
        app.Picker.OpenPath = PathOf("Observatory.astraseq");
        await app.Document.OpenCommand.ExecuteAsync(null);
        app.Draft.Steps.OfType<RepeatStepDraftViewModel>().Single().CountText = "9";

        await app.Document.SaveCommand.ExecuteAsync(null);

        var saved = await SequenceDocumentStore.CreateDefault().LoadAsync(PathOf("Observatory.astraseq"));
        Assert.Equal("guider.observatory", Assert.IsType<StartGuidingDocumentStep>(saved.Steps[0]).GuiderId);
        var repeat = Assert.IsType<RepeatDocumentStep>(saved.Steps[2]);
        Assert.Equal(9, repeat.Count);
        Assert.Equal("camera.observatory", Assert.IsType<ExposureDocumentStep>(repeat.Children[0]).CameraId);
    }

    [Fact]
    public async Task ADocumentWithDevicesOfTheWrongKind_LoadsAndIsInvalid()
    {
        await using var app = Create();
        var document = new SequenceDocument(null,
        [
            new ExposureDocumentStep(Guid.NewGuid(), "mount.eq6", 1),
        ]);
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Wrong.astraseq"), document);
        app.Picker.OpenPath = PathOf("Wrong.astraseq");

        await app.Document.OpenCommand.ExecuteAsync(null);

        var exposure = Assert.IsType<ExposureStepDraftViewModel>(Assert.Single(app.Draft.Steps));
        Assert.Equal(new DeviceId("mount.eq6"), exposure.Camera.SelectedId);
        Assert.Equal(["'mount.eq6' is not a camera."], exposure.Problems);
    }

    [Fact]
    public async Task ADocumentWithInvalidValues_LoadsAndIsInvalid_ItIsOnlyTheDraftThatSaysSo()
    {
        await using var app = Create();
        var document = new SequenceDocument(null,
        [
            new DelayDocumentStep(Guid.NewGuid(), -5),
            new SlewDocumentStep(Guid.NewGuid(), "mount.eq6", 99, 0),
            new RepeatDocumentStep(Guid.NewGuid(), 0, [new DelayDocumentStep(Guid.NewGuid(), 1)]),
        ]);
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Values.astraseq"), document);
        app.Picker.OpenPath = PathOf("Values.astraseq");

        await app.Document.OpenCommand.ExecuteAsync(null);

        Assert.Null(app.Document.ErrorMessage);
        Assert.Equal(3, app.Draft.Steps.Count);
        Assert.All(app.Draft.Steps, step => Assert.True(step.HasProblems));
        Assert.Equal(["Repeat count must be at least 1."], app.Draft.Steps[2].Problems.Take(1));
        Assert.False(app.Vm.Sequencer.CanRun);
    }
}

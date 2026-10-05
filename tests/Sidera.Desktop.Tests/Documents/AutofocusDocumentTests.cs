using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// Format version 4 adds autofocus. An Sidera of version 3 rejects the step types as unknown, which is why the version
/// changes; documents of versions 1 to 3 mean exactly what they did.
/// </summary>
public sealed class AutofocusDocumentTests : IDisposable
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-autofocus-tests-" + Guid.NewGuid().ToString("N"));

    public AutofocusDocumentTests()
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

    private static async Task<string> Write(SequenceDocument document)
    {
        using var stream = new MemoryStream();
        await Serializer.SaveAsync(stream, document, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task<SequenceDocument> Read(string text) =>
        Serializer.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private static async Task<SequenceDocumentException> Rejects(string text)
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static string Doc(int version, string steps) =>
        "{\"format\":\"astra-sequence\",\"version\":" + version + ",\"steps\":[" + steps + "]}";

    private static string Autofocus(Guid id) =>
        $"{{\"type\":\"autofocus\",\"id\":\"{id}\",\"rigId\":\"rig.main\",\"exposureSeconds\":2,\"stepSize\":300,\"samples\":7}}";

    private static string RigAutofocus(Guid id) =>
        $"{{\"type\":\"rigAutofocus\",\"id\":\"{id}\",\"exposureSeconds\":2,\"stepSize\":300,\"samples\":7}}";

    private static string RigExposure(Guid id) => $"{{\"type\":\"rigExposure\",\"id\":\"{id}\",\"exposureSeconds\":1}}";

    private static string Track(Guid id, params string[] steps) =>
        $"{{\"id\":\"{id}\",\"rigId\":\"rig.main\",\"steps\":[{string.Join(",", steps)}]}}";

    private static string Block(Guid id, params string[] tracks) =>
        $"{{\"type\":\"multiRig\",\"id\":\"{id}\",\"tracks\":[{string.Join(",", tracks)}]}}";

    private static string Repeat(Guid id, params string[] children) =>
        $"{{\"type\":\"repeat\",\"id\":\"{id}\",\"count\":2,\"children\":[{string.Join(",", children)}]}}";

    private static SequenceDocument Everything() => new(
        "Focus",
        [
            new AutofocusDocumentStep(A, "rig.main", 2, 300, 7),
            new RepeatDocumentStep(B, 2, [new AutofocusDocumentStep(C, null, 1.5, 250, 9)]),
            new MultiRigDocumentStep(D,
            [
                new RigTrackDocument(Guid.Parse("00000000-0000-0000-0000-000000000010"), "rig.main",
                [
                    new RigAutofocusDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000011"), 2, 300, 7),
                    new RepeatDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000012"), 3,
                    [
                        new RigAutofocusDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000013"), 1, 200, 5),
                        new RigExposureDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000014"), 60),
                    ]),
                ]),
            ]),
        ]);

    // Version 4

    [Fact]
    public async Task AutofocusSteps_SurviveAWriteAndARead_WithEveryIdRigAndValue()
    {
        var text = await Write(Everything());

        var loaded = await Read(text);

        Assert.Equal(new AutofocusDocumentStep(A, "rig.main", 2, 300, 7), loaded.Steps[0]);
        var repeat = Assert.IsType<RepeatDocumentStep>(loaded.Steps[1]);
        Assert.Equal(new AutofocusDocumentStep(C, null, 1.5, 250, 9), repeat.Children[0]);
        var track = Assert.Single(Assert.IsType<MultiRigDocumentStep>(loaded.Steps[2]).Tracks);
        Assert.Equal(new RigAutofocusDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000011"), 2, 300, 7), track.Steps[0]);
        var inner = Assert.IsType<RepeatDocumentStep>(track.Steps[1]);
        Assert.Equal(new RigAutofocusDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000013"), 1, 200, 5), inner.Children[0]);
        Assert.Equal(text, await Write(loaded));
    }

    [Fact]
    public async Task TheWireContract_NamesTheStableDiscriminatorsAndFields_AndTheRigStepRepeatsNoRig()
    {
        using var json = JsonDocument.Parse(await Write(Everything()));
        var steps = json.RootElement.GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["type", "id", "rigId", "exposureSeconds", "stepSize", "samples"], steps[0].EnumerateObject().Select(p => p.Name));
        var track = steps[2].GetProperty("tracks")[0].GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("rigAutofocus", track[0].GetProperty("type").GetString());
        Assert.Equal(["type", "id", "exposureSeconds", "stepSize", "samples"], track[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheDocumentHoldsOnlyTheUsersIntent_NoResultNoCameraNoFocuserNoProgress()
    {
        var text = await Write(Everything());

        foreach (var forbidden in new[] { "cameraId", "focuserId", "hfr", "Hfr", "bestPosition", "progress", "AutofocusDocumentStep", "$type" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheDocumentedExample_IsTheTextTheSerializerWrites()
    {
        var document = new SequenceDocument(
            null, [new AutofocusDocumentStep(Guid.Parse("11111111-1111-4111-8111-111111111111"), "rig.main", 2, 300, 7)]);

        var text = await Write(document);

        Assert.Equal(
            """
            {
              "format": "astra-sequence",
              "version": 8,
              "steps": [
                {
                  "type": "autofocus",
                  "id": "11111111-1111-4111-8111-111111111111",
                  "rigId": "rig.main",
                  "exposureSeconds": 2,
                  "stepSize": 300,
                  "samples": 7
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            text);
    }

    // Older versions

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AnOlderDocument_CannotUseWhatVersion4Added(int version)
    {
        Assert.Equal("Unknown sequence step type 'autofocus'.", (await Rejects(Doc(version, Autofocus(A)))).Message);
        Assert.Equal("Unknown sequence step type 'rigAutofocus'.", (await Rejects(Doc(version, RigAutofocus(A)))).Message);
    }

    [Fact]
    public async Task AVersion3Document_StillLoads_AsItWas_AndIsWrittenAsTheCurrentVersion()
    {
        var text = Doc(3,
            "{\"type\":\"moveFocuser\",\"id\":\"" + A + "\",\"focuserId\":\"focuser.main\",\"position\":18350},"
            + "{\"type\":\"changeFilter\",\"id\":\"" + B + "\",\"filterWheelId\":\"filterwheel.main\",\"slotIndex\":4}");

        var loaded = await Read(text);
        var rewritten = await Write(loaded);

        Assert.Equal(new MoveFocuserDocumentStep(A, "focuser.main", 18350), loaded.Steps[0]);
        Assert.Equal(new ChangeFilterDocumentStep(B, "filterwheel.main", 4), loaded.Steps[1]);
        using var json = JsonDocument.Parse(rewritten);
        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(loaded.Steps, (await Read(rewritten)).Steps);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(9)]
    public async Task ANewerVersionThanFive_IsRejectedClearly(int version)
    {
        var ex = await Rejects(Doc(version, Autofocus(A)));

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
        Assert.Equal("This sequence was created by a newer Sidera version.", ex.Message);
    }

    // Structure

    [Fact]
    public async Task ARigAutofocus_IsOnlyAStepOfATrack_AndAnAutofocusNeverIs()
    {
        Assert.Equal("A 'rigAutofocus' step can only be used inside a rig track.", (await Rejects(Doc(4, RigAutofocus(A)))).Message);
        Assert.Equal("A 'rigAutofocus' step can only be used inside a rig track.", (await Rejects(Doc(4, Repeat(A, RigAutofocus(B))))).Message);
        Assert.Equal("A 'autofocus' step cannot be used inside a rig track.", (await Rejects(Doc(4, Block(A, Track(B, Autofocus(C)))))).Message);
        Assert.Equal("A 'autofocus' step cannot be used inside a rig track.", (await Rejects(Doc(4, Block(A, Track(B, Repeat(C, Autofocus(D))))))).Message);
    }

    [Fact]
    public async Task AnAutofocus_IsAStepOfASequenceAndOfARepeat_AndARigAutofocusOfATrackAndItsRepeat()
    {
        var inTrack = Guid.Parse("00000000-0000-0000-0000-000000000020");
        var repeatInTrack = Guid.Parse("00000000-0000-0000-0000-000000000021");
        var inTrackRepeat = Guid.Parse("00000000-0000-0000-0000-000000000022");
        var track = Track(E, RigAutofocus(inTrack), Repeat(repeatInTrack, RigAutofocus(inTrackRepeat)));

        var loaded = await Read(Doc(4, Autofocus(A) + "," + Repeat(B, Autofocus(C)) + "," + Block(D, track)));

        Assert.Equal(3, loaded.Steps.Count);
    }

    [Fact]
    public async Task TheSameIdTwice_IsRejected()
    {
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(4, Autofocus(A) + "," + Autofocus(A)))).Message);
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(4, Autofocus(A) + "," + Block(B, Track(C, RigAutofocus(A)))))).Message);
    }

    [Theory]
    [InlineData("exposureSeconds")]
    [InlineData("stepSize")]
    [InlineData("samples")]
    [InlineData("rigId")]
    public async Task AMissingMember_IsRejected_AndNamed(string member)
    {
        var step = Autofocus(A);
        var removed = member switch
        {
            "exposureSeconds" => step.Replace(",\"exposureSeconds\":2", ""),
            "stepSize" => step.Replace(",\"stepSize\":300", ""),
            "samples" => step.Replace(",\"samples\":7", ""),
            _ => step.Replace(",\"rigId\":\"rig.main\"", ""),
        };

        Assert.Equal($"A 'autofocus' step is missing '{member}'.", (await Rejects(Doc(4, removed))).Message);
    }

    [Theory]
    [InlineData("\"stepSize\":300", "\"stepSize\":1.5", "stepSize")]
    [InlineData("\"stepSize\":300", "\"stepSize\":\"300\"", "stepSize")]
    [InlineData("\"samples\":7", "\"samples\":null", "samples")]
    [InlineData("\"samples\":7", "\"samples\":3000000000", "samples")]
    public async Task AWholeNumberThatIsNone_IsRejected(string find, string replace, string member)
    {
        var ex = await Rejects(Doc(4, Autofocus(A).Replace(find, replace)));

        Assert.Equal($"'{member}' of a 'autofocus' step must be a whole number.", ex.Message);
    }

    [Fact]
    public async Task AnExposureThatIsNoNumber_IsRejected()
    {
        var ex = await Rejects(Doc(4, Autofocus(A).Replace("\"exposureSeconds\":2", "\"exposureSeconds\":\"2\"")));

        Assert.Equal("'exposureSeconds' of a 'autofocus' step must be a number.", ex.Message);
    }

    [Fact]
    public async Task ValuesThatNoAutofocusCanRun_AreStillLoaded_ForTheEditorToReport()
    {
        var loaded = await Read(Doc(4, Autofocus(A).Replace("\"stepSize\":300", "\"stepSize\":-5").Replace("\"samples\":7", "\"samples\":4")));

        var step = (AutofocusDocumentStep)loaded.Steps[0];
        Assert.Equal((-5, 4), (step.StepSize, step.SampleCount));
    }

    [Fact]
    public async Task ARigThatIsNullOrUnknown_IsKept()
    {
        var none = await Read(Doc(4, Autofocus(A).Replace("\"rig.main\"", "null")));
        var other = await Read(Doc(4, Autofocus(A).Replace("rig.main", "rig.observatory")));

        Assert.Null(((AutofocusDocumentStep)none.Steps[0]).RigId);
        Assert.Equal("rig.observatory", ((AutofocusDocumentStep)other.Steps[0]).RigId);
    }

    [Fact]
    public async Task ALeafStepCannotHaveChildren()
    {
        Assert.Equal(
            "A 'autofocus' step cannot contain other steps.",
            (await Rejects(Doc(4, Autofocus(A).Replace("}", ",\"children\":[]}")))).Message);
    }

    // The mapper

    [Fact]
    public void TheMapper_CarriesTheStepsBothWays_WithoutChangingAnything()
    {
        var steps = new SequenceStepDraft[]
        {
            new AutofocusStepDraft(A, new RigId("rig.main"), 2, 300, 7),
            new AutofocusStepDraft(B, null, 1, 100, 5),
            new MultiRigStepDraft(C, [new RigTrackDraft(D, null, [new RigAutofocusStepDraft(E, 1.5, 200, 9)])]),
        };

        var back = SequenceDocumentMapper.ToDrafts(SequenceDocumentMapper.ToDocument(steps));

        Assert.Equal(steps[0], back[0]);
        Assert.Equal(steps[1], back[1]);
        Assert.Equal(new RigAutofocusStepDraft(E, 1.5, 200, 9), ((MultiRigStepDraft)back[2]).Tracks[0].Steps[0]);
    }

    // Files and the editor

    private sealed class Picker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }
        public string? SavePath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult(SavePath);
    }

    private sealed class App(SideraRuntimeHost host, MainViewModel vm, Picker picker) : IAsyncDisposable
    {
        public MainViewModel Vm { get; } = vm;
        public Picker Picker { get; } = picker;
        public SequenceDocumentViewModel Document => Vm.SequenceDocument;
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await host.DisposeAsync();
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
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var picker = new Picker();
        return new App(host, new MainViewModel(host, a => a(), new DemoOptions(), SequenceDocumentStore.CreateDefault(), picker), picker);
    }

    [Fact]
    public async Task AutofocusSettingsSetInTheEditor_AreSaved_AndComeBackIntoTheEditor()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        app.Draft.ReplaceSteps([]);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.Autofocus);
        var top = Assert.IsType<AutofocusStepDraftViewModel>(app.Draft.SelectedStep);
        top.Rig.Selected = top.Rig.Options.Single(o => o.IdText == "rig.narrow");
        top.ExposureText = "1.5";
        top.StepSizeText = "250";
        top.SamplesText = "9";
        app.Draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(app.Draft.SelectedStep);
        foreach (var rig in new[] { "rig.main", "rig.wide" })
        {
            app.Draft.SelectedStep = block;
            app.Draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(app.Draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            app.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
        }

        app.Draft.SelectedStep = block.Children[0];
        app.Draft.AddTrackStepCommand.Execute(SequenceStepKind.Autofocus);
        var local = Assert.IsType<RigAutofocusStepDraftViewModel>(app.Draft.SelectedStep);
        local.SamplesText = "5";
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var before = app.Draft.Snapshot();
        app.Picker.SavePath = PathOf("Focus");
        await app.Document.SaveCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(PathOf("Focus.astraseq"));
        Assert.Contains("\"version\": 8", text, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"rigAutofocus\"", text, StringComparison.Ordinal);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Focus.astraseq"));

        Assert.False(app.Document.IsDirty);
        var after = app.Draft.Snapshot();
        Assert.Equal(before[0], after[0]);
        var reopenedTop = app.Draft.Rows.OfType<AutofocusStepDraftViewModel>().Single();
        Assert.Equal(new RigId("rig.narrow"), reopenedTop.Rig.SelectedId);
        Assert.Equal(("1.5", "250", "9"), (reopenedTop.ExposureText, reopenedTop.StepSizeText, reopenedTop.SamplesText));
        Assert.Equal("5", app.Draft.Rows.OfType<RigAutofocusStepDraftViewModel>().Single().SamplesText);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
    }

    [Fact]
    public async Task AVersion3File_OpensInTheEditor_AndIsSavedAsTheCurrentVersion()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Old.astraseq"), Doc(3,
            "{\"type\":\"moveFocuser\",\"id\":\"" + A + "\",\"focuserId\":\"focuser.main\",\"position\":18350}"));

        await app.Open(PathOf("Old.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        Assert.False(app.Document.IsDirty);
        app.Draft.Rows.OfType<MoveFocuserStepDraftViewModel>().Single().PositionText = "19000";
        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.Contains("\"version\": 8", await File.ReadAllTextAsync(PathOf("Old.astraseq")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARigThatDoesNotExistHere_LoadsAsARepairableDraft()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Obs.astraseq"), Doc(4, Autofocus(A).Replace("rig.main", "rig.observatory")));

        await app.Open(PathOf("Obs.astraseq"));

        var step = Assert.IsType<AutofocusStepDraftViewModel>(app.Draft.Rows.Single());
        Assert.Equal(new RigId("rig.observatory"), step.Rig.SelectedId);
        Assert.True(step.Rig.Selected!.IsMissing);
        Assert.Equal(["The rig 'rig.observatory' is not available."], step.Problems);
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Document.IsDirty);

        step.Rig.Selected = step.Rig.Options.Single(o => o.IdText == "rig.main");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Document.IsDirty);
    }
}

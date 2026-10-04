using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// Format version 3 adds the focuser and filter wheel steps. What an older Sidera rejects as an unknown step type is
/// only ever written by this one, and the documents of versions 1 and 2 mean exactly what they did.
/// </summary>
public sealed class HardwareStepDocumentTests : IDisposable
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static readonly Guid F = Guid.Parse("00000000-0000-0000-0000-00000000000f");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-hardware-tests-" + Guid.NewGuid().ToString("N"));

    public HardwareStepDocumentTests()
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

    private static string MoveFocuser(Guid id) => $"{{\"type\":\"moveFocuser\",\"id\":\"{id}\",\"focuserId\":\"focuser.main\",\"position\":18350}}";
    private static string ChangeFilter(Guid id) => $"{{\"type\":\"changeFilter\",\"id\":\"{id}\",\"filterWheelId\":\"filterwheel.main\",\"slotIndex\":4}}";
    private static string RigMoveFocuser(Guid id) => $"{{\"type\":\"rigMoveFocuser\",\"id\":\"{id}\",\"position\":18350}}";
    private static string RigChangeFilter(Guid id) => $"{{\"type\":\"rigChangeFilter\",\"id\":\"{id}\",\"slotIndex\":4}}";
    private static string RigExposure(Guid id) => $"{{\"type\":\"rigExposure\",\"id\":\"{id}\",\"exposureSeconds\":1}}";

    private static string Track(Guid id, params string[] steps) =>
        $"{{\"id\":\"{id}\",\"rigId\":\"rig.main\",\"steps\":[{string.Join(",", steps)}]}}";

    private static string Block(Guid id, params string[] tracks) =>
        $"{{\"type\":\"multiRig\",\"id\":\"{id}\",\"tracks\":[{string.Join(",", tracks)}]}}";

    private static string Repeat(Guid id, params string[] children) =>
        $"{{\"type\":\"repeat\",\"id\":\"{id}\",\"count\":2,\"children\":[{string.Join(",", children)}]}}";

    private static SequenceDocument Everything() => new(
        "Hardware",
        [
            new MoveFocuserDocumentStep(A, "focuser.main", 18350),
            new ChangeFilterDocumentStep(B, "filterwheel.main", 4),
            new RepeatDocumentStep(C, 2, [new MoveFocuserDocumentStep(D, null, 100), new ChangeFilterDocumentStep(E, null, 0)]),
            new MultiRigDocumentStep(F,
            [
                new RigTrackDocument(Guid.Parse("00000000-0000-0000-0000-000000000010"), "rig.main",
                [
                    new RigChangeFilterDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000011"), 4),
                    new RigMoveFocuserDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000012"), 18350),
                    new RepeatDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000013"), 3,
                    [
                        new RigMoveFocuserDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000014"), 17000),
                        new RigChangeFilterDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000015"), 1),
                        new RigExposureDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000016"), 60),
                    ]),
                ]),
            ]),
        ]);

    // Version 3

    [Fact]
    public async Task EveryNewStep_SurvivesAWriteAndARead_WithEveryIdDeviceAndValue()
    {
        var document = Everything();

        var text = await Write(document);
        var loaded = await Read(text);

        Assert.Equal(new MoveFocuserDocumentStep(A, "focuser.main", 18350), loaded.Steps[0]);
        Assert.Equal(new ChangeFilterDocumentStep(B, "filterwheel.main", 4), loaded.Steps[1]);
        var repeat = Assert.IsType<RepeatDocumentStep>(loaded.Steps[2]);
        Assert.Equal(new MoveFocuserDocumentStep(D, null, 100), repeat.Children[0]);
        Assert.Equal(new ChangeFilterDocumentStep(E, null, 0), repeat.Children[1]);
        var track = Assert.Single(Assert.IsType<MultiRigDocumentStep>(loaded.Steps[3]).Tracks);
        Assert.Equal(4, Assert.IsType<RigChangeFilterDocumentStep>(track.Steps[0]).SlotIndex);
        Assert.Equal(18350, Assert.IsType<RigMoveFocuserDocumentStep>(track.Steps[1]).Position);
        var inner = Assert.IsType<RepeatDocumentStep>(track.Steps[2]);
        Assert.Equal([typeof(RigMoveFocuserDocumentStep), typeof(RigChangeFilterDocumentStep), typeof(RigExposureDocumentStep)], inner.Children.Select(c => c.GetType()));
        Assert.Equal(text, await Write(loaded));
    }

    [Fact]
    public async Task TheWireContract_NamesTheStableDiscriminatorsAndFields()
    {
        using var json = JsonDocument.Parse(await Write(Everything()));
        var steps = json.RootElement.GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["type", "id", "focuserId", "position"], steps[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["type", "id", "filterWheelId", "slotIndex"], steps[1].EnumerateObject().Select(p => p.Name));
        var track = steps[3].GetProperty("tracks")[0].GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal("rigChangeFilter", track[0].GetProperty("type").GetString());
        Assert.Equal(["type", "id", "slotIndex"], track[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("rigMoveFocuser", track[1].GetProperty("type").GetString());
        Assert.Equal(["type", "id", "position"], track[1].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task ARigStepPersistsTheIntent_NotTheDevice_SoTheSequenceSurvivesAChangedRigMapping()
    {
        var text = await Write(Everything());
        using var json = JsonDocument.Parse(text);
        var track = json.RootElement.GetProperty("steps")[3].GetProperty("tracks")[0].GetProperty("steps");

        Assert.False(track[0].TryGetProperty("filterWheelId", out _));
        Assert.False(track[1].TryGetProperty("focuserId", out _));
        Assert.False(track[0].TryGetProperty("name", out _)); // the slot is the identity, not a filter name
    }

    [Fact]
    public async Task TheDocumentHoldsNoFilterNames_NoDeviceNames_AndNoClassNames()
    {
        var text = await Write(Everything());

        foreach (var forbidden in new[] { "$type", "DocumentStep", "Main Focuser", "Main Filter Wheel", "\"Ha\"", "ViewModel" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheDocumentedExample_IsTheTextTheSerializerWrites()
    {
        var document = new SequenceDocument(
            null,
            [
                new ChangeFilterDocumentStep(Guid.Parse("11111111-1111-4111-8111-111111111111"), "filterwheel.main", 4),
                new MoveFocuserDocumentStep(Guid.Parse("22222222-2222-4222-8222-222222222222"), "focuser.main", 18350),
            ]);

        var text = await Write(document);

        Assert.Equal(
            """
            {
              "format": "astra-sequence",
              "version": 7,
              "steps": [
                {
                  "type": "changeFilter",
                  "id": "11111111-1111-4111-8111-111111111111",
                  "filterWheelId": "filterwheel.main",
                  "slotIndex": 4
                },
                {
                  "type": "moveFocuser",
                  "id": "22222222-2222-4222-8222-222222222222",
                  "focuserId": "focuser.main",
                  "position": 18350
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
    public async Task AnOlderDocument_CannotUseWhatVersion3Added(int version)
    {
        foreach (var type in new[] { "moveFocuser", "changeFilter", "rigMoveFocuser", "rigChangeFilter" })
        {
            var step = type switch
            {
                "moveFocuser" => MoveFocuser(A),
                "changeFilter" => ChangeFilter(A),
                "rigMoveFocuser" => RigMoveFocuser(A),
                _ => RigChangeFilter(A),
            };

            Assert.Equal($"Unknown sequence step type '{type}'.", (await Rejects(Doc(version, step))).Message);
        }
    }

    [Fact]
    public async Task AVersion2Document_StillLoads_AsItWas_AndIsWrittenAsTheCurrentVersionWithTheSameSteps()
    {
        var text = Doc(2, Block(A, Track(B, RigExposure(C)), Track(D, RigExposure(E))));

        var loaded = await Read(text);
        var rewritten = await Write(loaded);

        Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps));
        using var json = JsonDocument.Parse(rewritten);
        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(loaded.Steps, (await Read(rewritten)).Steps.ToList(), new StructuralComparer());
    }

    private sealed class StructuralComparer : IEqualityComparer<DocumentStep>
    {
        public bool Equals(DocumentStep? x, DocumentStep? y) => x?.Id == y?.Id && x?.GetType() == y?.GetType();
        public int GetHashCode(DocumentStep obj) => obj.Id.GetHashCode();
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public async Task ANewerVersionThanFive_IsRejectedClearly(int version)
    {
        var ex = await Rejects(Doc(version, MoveFocuser(A)));

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
        Assert.Equal("This sequence was created by a newer Sidera version.", ex.Message);
    }

    // Structure of version 3

    [Fact]
    public async Task RigStepsAreOnlyStepsOfATrack()
    {
        Assert.Equal("A 'rigMoveFocuser' step can only be used inside a rig track.", (await Rejects(Doc(3, RigMoveFocuser(A)))).Message);
        Assert.Equal("A 'rigChangeFilter' step can only be used inside a rig track.", (await Rejects(Doc(3, RigChangeFilter(A)))).Message);
        Assert.Equal(
            "A 'rigMoveFocuser' step can only be used inside a rig track.",
            (await Rejects(Doc(3, Repeat(A, RigMoveFocuser(B))))).Message);
    }

    [Fact]
    public async Task TheStepsWithADevice_AreNotStepsOfATrack()
    {
        Assert.Equal(
            "A 'moveFocuser' step cannot be used inside a rig track.",
            (await Rejects(Doc(3, Block(A, Track(B, MoveFocuser(C)))))).Message);
        Assert.Equal(
            "A 'changeFilter' step cannot be used inside a rig track.",
            (await Rejects(Doc(3, Block(A, Track(B, Repeat(C, ChangeFilter(D))))))).Message);
    }

    [Fact]
    public async Task TheStepsWithADevice_AreStepsOfASequenceAndOfARepeat()
    {
        var loaded = await Read(Doc(3, MoveFocuser(A) + "," + Repeat(B, ChangeFilter(C), MoveFocuser(D))));

        Assert.Equal(2, loaded.Steps.Count);
    }

    [Fact]
    public async Task TheSameIdTwice_IsRejected_AlsoForTheNewSteps()
    {
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(3, MoveFocuser(A) + "," + ChangeFilter(A)))).Message);
        Assert.Equal(
            "Duplicate sequence step ID.",
            (await Rejects(Doc(3, MoveFocuser(A) + "," + Block(B, Track(C, RigMoveFocuser(A)))))).Message);
    }

    [Theory]
    [InlineData("position")]
    [InlineData("focuserId")]
    public async Task AMissingMemberOfMoveFocuser_IsRejected_AndNamed(string member)
    {
        var step = MoveFocuser(A).Replace(member == "position" ? ",\"position\":18350" : ",\"focuserId\":\"focuser.main\"", "");

        Assert.Equal($"A 'moveFocuser' step is missing '{member}'.", (await Rejects(Doc(3, step))).Message);
    }

    [Theory]
    [InlineData("slotIndex")]
    [InlineData("filterWheelId")]
    public async Task AMissingMemberOfChangeFilter_IsRejected_AndNamed(string member)
    {
        var step = ChangeFilter(A).Replace(member == "slotIndex" ? ",\"slotIndex\":4" : ",\"filterWheelId\":\"filterwheel.main\"", "");

        Assert.Equal($"A 'changeFilter' step is missing '{member}'.", (await Rejects(Doc(3, step))).Message);
    }

    [Theory]
    [InlineData("\"position\":18350", "\"position\":1.5")]
    [InlineData("\"position\":18350", "\"position\":\"18350\"")]
    [InlineData("\"position\":18350", "\"position\":null")]
    [InlineData("\"position\":18350", "\"position\":3000000000")]
    public async Task APositionThatIsNoWholeNumber_IsRejected(string find, string replace)
    {
        var ex = await Rejects(Doc(3, MoveFocuser(A).Replace(find, replace)));

        Assert.Equal("'position' of a 'moveFocuser' step must be a whole number.", ex.Message);
    }

    [Theory]
    [InlineData("\"slotIndex\":4", "\"slotIndex\":0.5")]
    [InlineData("\"slotIndex\":4", "\"slotIndex\":true")]
    public async Task ASlotThatIsNoWholeNumber_IsRejected(string find, string replace)
    {
        var ex = await Rejects(Doc(3, ChangeFilter(A).Replace(find, replace)));

        Assert.Equal("'slotIndex' of a 'changeFilter' step must be a whole number.", ex.Message);
    }

    [Fact]
    public async Task ValuesThatNoDeviceHas_AreStillLoaded_ForTheEditorToReport()
    {
        var loaded = await Read(Doc(3, MoveFocuser(A).Replace("18350", "-5") + "," + ChangeFilter(B).Replace("\"slotIndex\":4", "\"slotIndex\":-2")));

        Assert.Equal(-5, ((MoveFocuserDocumentStep)loaded.Steps[0]).Position);
        Assert.Equal(-2, ((ChangeFilterDocumentStep)loaded.Steps[1]).SlotIndex);
    }

    [Fact]
    public async Task ADeviceThatIsNull_IsKept()
    {
        var loaded = await Read(Doc(3, MoveFocuser(A).Replace("\"focuser.main\"", "null")));

        Assert.Null(((MoveFocuserDocumentStep)loaded.Steps[0]).FocuserId);
    }

    [Fact]
    public async Task ALeafStepCannotHaveChildren()
    {
        var step = MoveFocuser(A).Replace("}", ",\"children\":[]}");

        Assert.Equal("A 'moveFocuser' step cannot contain other steps.", (await Rejects(Doc(3, step))).Message);
    }

    // The mapper

    [Fact]
    public void TheMapper_CarriesTheStepsBothWays_WithoutChangingAnything()
    {
        var steps = new SequenceStepDraft[]
        {
            new MoveFocuserStepDraft(A, new DeviceId("focuser.main"), 18350),
            new ChangeFilterStepDraft(B, null, 4),
            new MultiRigStepDraft(C,
            [
                new RigTrackDraft(D, null,
                [
                    new RigMoveFocuserStepDraft(E, 100),
                    new RepeatStepDraft(F, 2, [new RigChangeFilterStepDraft(Guid.Parse("00000000-0000-0000-0000-000000000020"), 2)]),
                ]),
            ]),
        };

        var back = SequenceDocumentMapper.ToDrafts(SequenceDocumentMapper.ToDocument(steps));

        Assert.Equal(steps[0], back[0]);
        Assert.Equal(steps[1], back[1]);
        var track = ((MultiRigStepDraft)back[2]).Tracks[0];
        Assert.Equal(new RigMoveFocuserStepDraft(E, 100), track.Steps[0]);
        Assert.Equal(2, ((RigChangeFilterStepDraft)((RepeatStepDraft)track.Steps[1]).Children[0]).SlotIndex);
    }

    // Files, and the editor

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
    public async Task ASessionWithFocuserAndFilterSteps_IsSaved_AndOpensAsExactlyTheSameSequence()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        app.Draft.ReplaceSteps([]);
        app.Draft.AddStepCommand.Execute(SequenceStepKind.ChangeFilter);
        var change = Assert.IsType<ChangeFilterStepDraftViewModel>(app.Draft.SelectedStep);
        change.Filter.Selected = change.Filter.Options.Single(o => o.Name == "Ha");
        app.Draft.AddStepCommand.Execute(SequenceStepKind.MoveFocuser);
        var move = Assert.IsType<MoveFocuserStepDraftViewModel>(app.Draft.SelectedStep);
        move.PositionText = "18350";
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
        app.Draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        Assert.IsType<RigChangeFilterStepDraftViewModel>(app.Draft.SelectedStep);
        app.Draft.AddTrackStepCommand.Execute(SequenceStepKind.MoveFocuser);
        Assert.IsType<RigMoveFocuserStepDraftViewModel>(app.Draft.SelectedStep).PositionText = "19000";
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var before = app.Draft.Snapshot();
        app.Picker.SavePath = PathOf("Hardware");
        await app.Document.SaveCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(PathOf("Hardware.astraseq"));
        Assert.Contains("\"version\": 7", text, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"rigMoveFocuser\"", text, StringComparison.Ordinal);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Hardware.astraseq"));

        Assert.False(app.Document.IsDirty);
        Assert.Equal(before, app.Draft.Snapshot(), new DraftComparer());
        var reopened = app.Draft.Rows.OfType<ChangeFilterStepDraftViewModel>().Single();
        Assert.Equal("Ha", reopened.Filter.Selected!.Name);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
    }

    private sealed class DraftComparer : IEqualityComparer<SequenceStepDraft>
    {
        public bool Equals(SequenceStepDraft? x, SequenceStepDraft? y) => Describe(x) == Describe(y);
        public int GetHashCode(SequenceStepDraft obj) => Describe(obj).GetHashCode();

        private static string Describe(SequenceStepDraft? step) => step switch
        {
            MultiRigStepDraft m => $"{m.Id}[{string.Join(";", m.Tracks.Select(Describe))}]",
            RigTrackDraft t => $"{t.Id}:{t.RigId}[{string.Join(";", t.Steps.Select(Describe))}]",
            RepeatStepDraft r => $"{r.Id}x{r.Count}[{string.Join(";", r.Children.Select(Describe))}]",
            _ => step?.ToString() ?? string.Empty,
        };
    }

    [Fact]
    public async Task AVersion2File_OpensInTheEditor_AndIsSavedAsTheCurrentVersion()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Old.astraseq"), Doc(2, Block(A, Track(B, RigExposure(C)), Track(D, RigExposure(E)))));

        await app.Open(PathOf("Old.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        Assert.False(app.Document.IsDirty);
        app.Picker.SavePath = PathOf("Old2");
        app.Draft.Rows.OfType<RigExposureStepDraftViewModel>().First().ExposureText = "5";
        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.Contains("\"version\": 7", await File.ReadAllTextAsync(PathOf("Old.astraseq")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentWithAFocuserThatDoesNotExistHere_LoadsAsARepairableDraft()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Obs.astraseq"), Doc(3, MoveFocuser(A).Replace("focuser.main", "focuser.observatory")));

        await app.Open(PathOf("Obs.astraseq"));

        var step = Assert.IsType<MoveFocuserStepDraftViewModel>(app.Draft.Rows.Single());
        Assert.Equal(new DeviceId("focuser.observatory"), step.Focuser.SelectedId);
        Assert.True(step.Focuser.Selected!.IsMissing);
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Document.IsDirty);

        step.Focuser.Selected = step.Focuser.Options.Single(o => o.IdText == "focuser.main");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Document.IsDirty);
    }
}

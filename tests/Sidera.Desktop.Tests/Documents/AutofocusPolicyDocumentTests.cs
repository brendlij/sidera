using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// Format version 5 adds the autofocus policy of a Setup Sequence. It changes what a sequence does, so a version 4 Sidera must
/// not open it as if the policy were not there: that is why the version changes although the member is optional.
/// </summary>
public sealed class AutofocusPolicyDocumentTests : IDisposable
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-afpolicy-tests-" + Guid.NewGuid().ToString("N"));

    public AutofocusPolicyDocumentTests()
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

    private const string Members =
        "\"enabled\":true,\"atTrackStart\":true,\"afterFilterChange\":false,\"exposureSeconds\":0.5,\"stepSize\":400,\"samples\":7";

    private static string Block(string? policy, Guid? id = null) =>
        $"{{\"type\":\"multiRig\",\"id\":\"{id ?? A}\",\"tracks\":[{{\"id\":\"{B}\",\"rigId\":\"rig.main\",\"steps\":["
        + $"{{\"type\":\"rigExposure\",\"id\":\"{C}\",\"exposureSeconds\":2}},"
        + $"{{\"type\":\"rigExposure\",\"id\":\"{D}\",\"exposureSeconds\":1}}]"
        + (policy is null ? "" : ",\"autofocusPolicy\":" + policy) + "}]}";

    private static SequenceDocument WithPolicy(AutofocusPolicyDocument? policy) => new(
        "Focus",
        [
            new MultiRigDocumentStep(A,
            [
                new RigTrackDocument(B, "rig.main",
                [
                    new RigChangeFilterDocumentStep(C, 4),
                    new RigExposureDocumentStep(D, 1),
                ],
                policy),
                new RigTrackDocument(E, "rig.wide", [new RigExposureDocumentStep(Guid.Parse("00000000-0000-0000-0000-000000000010"), 1)]),
            ]),
        ]);

    // Version 5

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task EveryCombinationOfTheSwitches_SurvivesAWriteAndARead_WithItsValues(bool enabled, bool start, bool filter)
    {
        var policy = new AutofocusPolicyDocument(enabled, start, filter, 0.5, 350, 9);
        var text = await Write(WithPolicy(policy));

        var loaded = await Read(text);

        var track = Assert.IsType<MultiRigDocumentStep>(loaded.Steps[0]).Tracks[0];
        Assert.Equal(policy, track.AutofocusPolicy);
        Assert.Null(Assert.IsType<MultiRigDocumentStep>(loaded.Steps[0]).Tracks[1].AutofocusPolicy); // another track has none
        Assert.Equal(text, await Write(loaded));
    }

    [Fact]
    public async Task TheWireContract_PutsThePolicyOnTheTrack_AfterItsSteps_WithStableNames()
    {
        using var json = JsonDocument.Parse(await Write(WithPolicy(new AutofocusPolicyDocument(true, true, true, 0.5, 400, 7))));
        var track = json.RootElement.GetProperty("steps")[0].GetProperty("tracks")[0];

        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["id", "rigId", "steps", "autofocusPolicy"], track.EnumerateObject().Select(p => p.Name));
        var policy = track.GetProperty("autofocusPolicy");
        Assert.Equal(["enabled", "atTrackStart", "afterFilterChange", "exposureSeconds", "stepSize", "samples"], policy.EnumerateObject().Select(p => p.Name));
        Assert.True(policy.GetProperty("enabled").GetBoolean());
        Assert.Equal(0.5, policy.GetProperty("exposureSeconds").GetDouble());
        Assert.Equal((400, 7), (policy.GetProperty("stepSize").GetInt32(), policy.GetProperty("samples").GetInt32()));
    }

    [Fact]
    public async Task OnlyTheStepsTheUserWrote_AreStored_NeverTheAutofocusTheBuildGenerates()
    {
        var text = await Write(WithPolicy(new AutofocusPolicyDocument(true, true, true, 0.5, 400, 7)));
        using var json = JsonDocument.Parse(text);

        var types = json.RootElement.GetProperty("steps")[0].GetProperty("tracks")[0].GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("type").GetString()).ToList();

        Assert.Equal(["rigChangeFilter", "rigExposure"], types);
        Assert.DoesNotContain("rigAutofocus", text, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "hfr", "Hfr", "bestPosition", "measurements", "progress", "result", "$type", "AutofocusPolicyDocument" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoPolicy_WritesNoMember()
    {
        var text = await Write(WithPolicy(null));

        Assert.DoesNotContain("autofocusPolicy", text, StringComparison.Ordinal);
        Assert.Null(Assert.IsType<MultiRigDocumentStep>((await Read(text)).Steps[0]).Tracks[0].AutofocusPolicy);
    }

    [Fact]
    public async Task TheDocumentedExample_IsTheTextTheSerializerWrites()
    {
        var document = new SequenceDocument(
            null,
            [
                new MultiRigDocumentStep(Guid.Parse("22222222-2222-4222-8222-222222222222"),
                [
                    new RigTrackDocument(Guid.Parse("33333333-3333-4333-8333-333333333333"), "rig.main", [],
                        new AutofocusPolicyDocument(true, true, true, 0.5, 400, 7)),
                ]),
            ]);

        var text = await Write(document);

        Assert.Equal(
            """
            {
              "format": "astra-sequence",
              "version": 9,
              "steps": [
                {
                  "type": "multiRig",
                  "id": "22222222-2222-4222-8222-222222222222",
                  "tracks": [
                    {
                      "id": "33333333-3333-4333-8333-333333333333",
                      "rigId": "rig.main",
                      "steps": [],
                      "autofocusPolicy": {
                        "enabled": true,
                        "atTrackStart": true,
                        "afterFilterChange": true,
                        "exposureSeconds": 0.5,
                        "stepSize": 400,
                        "samples": 7
                      }
                    }
                  ]
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            text);
    }

    // Older versions

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ADocumentOfVersion2To4_HasNoPolicy_AndMeansWhatItMeant(int version)
    {
        var loaded = await Read(Doc(version, Block(null)));

        var track = Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps)).Tracks[0];
        Assert.Null(track.AutofocusPolicy);
        var draft = (MultiRigStepDraft)SequenceDocumentMapper.ToDrafts(loaded).Single();
        Assert.True(draft.Tracks[0].AutofocusPolicy is null or { Enabled: false });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AMemberOfThePolicy_IsNotPartOfAnOlderVersion_AndIsIgnoredThere(int version)
    {
        var loaded = await Read(Doc(version, Block("{" + Members + "}")));

        Assert.Null(Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps)).Tracks[0].AutofocusPolicy);
    }

    [Fact]
    public async Task AVersion4Document_IsWrittenAsVersion5_WithoutAPolicy_ByTheSameSteps()
    {
        var loaded = await Read(Doc(4, Block(null)));

        var rewritten = await Write(loaded);

        using var json = JsonDocument.Parse(rewritten);
        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.DoesNotContain("autofocusPolicy", rewritten, StringComparison.Ordinal);
        Assert.Equal(rewritten, await Write(await Read(rewritten))); // and it reads and writes the same again
    }

    [Fact]
    public async Task APolicyIsAlwaysWrittenAsTheCurrentVersion_SoAnOlderSideraRefusesItAsNewer()
    {
        var text = await Write(WithPolicy(new AutofocusPolicyDocument(true, true, false, 0.5, 400, 7)));

        using var json = JsonDocument.Parse(text);

        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        Assert.True(json.RootElement.GetProperty("version").GetInt32() >= 5);
    }

    [Theory]
    [InlineData(10)]
    public async Task ANewerVersionThanFive_IsRejectedClearly(int version)
    {
        var ex = await Rejects(Doc(version, Block("{" + Members + "}")));

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
        Assert.Equal("This sequence was created by a newer Sidera version.", ex.Message);
    }

    // Structure

    [Fact]
    public async Task AFullPolicy_IsRead()
    {
        var loaded = await Read(Doc(5, Block("{" + Members + "}")));

        Assert.Equal(
            new AutofocusPolicyDocument(true, true, false, 0.5, 400, 7),
            Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps)).Tracks[0].AutofocusPolicy);
    }

    [Fact]
    public async Task ANullPolicy_IsReadAsNoPolicy()
    {
        var loaded = await Read(Doc(5, Block("null")));

        Assert.Null(Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps)).Tracks[0].AutofocusPolicy);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"yes\"")]
    [InlineData("[]")]
    public async Task APolicyThatIsNoObject_IsRejected(string policy)
    {
        Assert.Equal("'autofocusPolicy' must be an object.", (await Rejects(Doc(5, Block(policy)))).Message);
    }

    [Theory]
    [InlineData("enabled")]
    [InlineData("atTrackStart")]
    [InlineData("afterFilterChange")]
    public async Task ASwitchThatIsMissingOrNoBoolean_IsRejected_AndNamed(string name)
    {
        var missing = Members.Replace($"\"{name}\":", "\"other\":").Replace("\"other\":true,", "").Replace("\"other\":false,", "");
        var number = Members.Replace($"\"{name}\":true", $"\"{name}\":1").Replace($"\"{name}\":false", $"\"{name}\":1");
        var expected = $"'{name}' of an 'autofocusPolicy' must be true or false.";

        Assert.Equal(expected, (await Rejects(Doc(5, Block("{" + missing + "}")))).Message);
        Assert.Equal(expected, (await Rejects(Doc(5, Block("{" + number + "}")))).Message);
    }

    [Theory]
    [InlineData("exposureSeconds")]
    [InlineData("stepSize")]
    [InlineData("samples")]
    public async Task AMissingNumber_IsRejected(string name)
    {
        var cut = Members.Split(',').Where(part => !part.StartsWith($"\"{name}\"", StringComparison.Ordinal));

        await Rejects(Doc(5, Block("{" + string.Join(",", cut) + "}")));
    }

    [Theory]
    [InlineData("\"stepSize\":400", "\"stepSize\":1.5")]
    [InlineData("\"samples\":7", "\"samples\":\"7\"")]
    [InlineData("\"samples\":7", "\"samples\":3000000000")]
    [InlineData("\"exposureSeconds\":0.5", "\"exposureSeconds\":\"0.5\"")]
    public async Task ANumberThatIsNone_IsRejected(string find, string replace)
    {
        await Rejects(Doc(5, Block("{" + Members.Replace(find, replace) + "}")));
    }

    [Fact]
    public async Task ValuesThatNoAutofocusCanRun_AreStillLoaded_ForTheEditorToReport()
    {
        var members = Members.Replace("\"stepSize\":400", "\"stepSize\":-5").Replace("\"samples\":7", "\"samples\":4");

        var loaded = await Read(Doc(5, Block("{" + members + "}")));

        var policy = Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps)).Tracks[0].AutofocusPolicy!;
        Assert.Equal((-5, 4), (policy.StepSize, policy.SampleCount));
    }

    // The mapper

    [Fact]
    public void TheMapper_CarriesThePolicyBothWays_AndWritesNothingForNoneOrTheDefault()
    {
        var policy = new RigAutofocusPolicyDraft(true, true, false, 0.5, 350, 9);
        var steps = new SequenceStepDraft[]
        {
            new MultiRigStepDraft(A,
            [
                new RigTrackDraft(B, new RigId("rig.main"), [], policy),
                new RigTrackDraft(C, new RigId("rig.wide"), [], null),
                new RigTrackDraft(D, new RigId("rig.narrow"), [], RigAutofocusPolicyDraft.Default),
            ]),
        };

        var document = SequenceDocumentMapper.ToDocument(steps);
        var back = (MultiRigStepDraft)SequenceDocumentMapper.ToDrafts(document).Single();
        var tracks = ((MultiRigDocumentStep)document.Steps[0]).Tracks;

        Assert.Equal(new AutofocusPolicyDocument(true, true, false, 0.5, 350, 9), tracks[0].AutofocusPolicy);
        Assert.Null(tracks[1].AutofocusPolicy);
        Assert.Null(tracks[2].AutofocusPolicy);
        Assert.Equal(policy, back.Tracks[0].AutofocusPolicy);
        Assert.True(back.Tracks[1].AutofocusPolicy is null or { Enabled: false });
    }

    [Fact]
    public void ADisabledPolicyWithChangedValues_IsNotTheDefault_AndIsWritten()
    {
        var policy = RigAutofocusPolicyDraft.Default with { StepSize = 250 };

        var document = SequenceDocumentMapper.ToDocument(
            [new MultiRigStepDraft(A, [new RigTrackDraft(B, null, [], policy)])]);

        var written = ((MultiRigDocumentStep)document.Steps[0]).Tracks[0].AutofocusPolicy;
        Assert.NotNull(written);
        Assert.False(written!.Enabled);
        Assert.Equal(250, written.StepSize);
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

    private static (MultiRigStepDraftViewModel Block, RigTrackDraftViewModel Main, RigTrackDraftViewModel Wide) BuildBlock(SequenceDraftViewModel draft)
    {
        draft.ReplaceSteps([]);
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        var tracks = new List<RigTrackDraftViewModel>();
        foreach (var rig in new[] { "rig.main", "rig.wide" })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            tracks.Add(track);
        }

        draft.SelectedStep = tracks[0];
        draft.AddTrackStepCommand.Execute(SequenceStepKind.ChangeFilter);
        return (block, tracks[0], tracks[1]);
    }

    [Fact]
    public async Task APolicySetInTheEditor_IsSaved_AsVersion5_AndComesBackIntoTheEditor_WithoutAnyGeneratedStep()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        var (_, main, wide) = BuildBlock(app.Draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        main.AutofocusAfterFilterChange = true;
        main.AutofocusExposureText = "0.5";
        main.AutofocusStepSizeText = "350";
        main.AutofocusSamplesText = "9";
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var rows = app.Draft.Rows.Count;
        app.Picker.SavePath = PathOf("Policy");
        await app.Document.SaveCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(PathOf("Policy.astraseq"));
        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.Contains("\"autofocusPolicy\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\": \"rigAutofocus\"", text, StringComparison.Ordinal);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Policy.astraseq"));

        Assert.False(app.Document.IsDirty);
        Assert.Equal(rows, app.Draft.Rows.Count); // no row for any generated autofocus
        var reopened = app.Draft.Rows.OfType<RigTrackDraftViewModel>().First();
        Assert.Equal((true, true, true), (reopened.AutofocusEnabled, reopened.AutofocusAtStart, reopened.AutofocusAfterFilterChange));
        Assert.Equal(("0.5", "350", "9"), (reopened.AutofocusExposureText, reopened.AutofocusStepSizeText, reopened.AutofocusSamplesText));
        var other = app.Draft.Rows.OfType<RigTrackDraftViewModel>().Last();
        Assert.False(other.AutofocusEnabled);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        _ = wide;
    }

    [Fact]
    public async Task AVersion4File_OpensWithThePolicyOff_IsNotDirty_AndSavesAsVersion5()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Old.astraseq"), Doc(4, Block(null).Replace("rig.main", "rig.main")));

        await app.Open(PathOf("Old.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        Assert.False(app.Document.IsDirty);
        var track = app.Draft.Rows.OfType<RigTrackDraftViewModel>().Single();
        Assert.False(track.AutofocusEnabled);
        Assert.False(track.AutofocusAtStart);
        Assert.False(track.AutofocusAfterFilterChange);
        app.Draft.Rows.OfType<RigExposureStepDraftViewModel>().First().ExposureText = "5";
        await app.Document.SaveCommand.ExecuteAsync(null);
        Assert.Contains("\"version\": 9", await File.ReadAllTextAsync(PathOf("Old.astraseq")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADuplicatedBlock_KeepsThePolicyOfEachTrack_AndSavesIt()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        var (block, main, _) = BuildBlock(app.Draft);
        main.AutofocusEnabled = true;
        main.AutofocusAfterFilterChange = true;
        main.AutofocusStepSizeText = "250";
        app.Draft.SelectedStep = block;

        app.Draft.DuplicateStepCommand.Execute(null);
        app.Picker.SavePath = PathOf("Twice");
        await app.Document.SaveCommand.ExecuteAsync(null);
        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Twice.astraseq"));

        var tracks = app.Draft.Rows.OfType<RigTrackDraftViewModel>().Where(t => t.AutofocusEnabled).ToList();
        Assert.Equal(2, tracks.Count);
        Assert.All(tracks, t => Assert.Equal(("250", false, true), (t.AutofocusStepSizeText, t.AutofocusAtStart, t.AutofocusAfterFilterChange)));
        Assert.Equal(2, app.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Count());
    }
}

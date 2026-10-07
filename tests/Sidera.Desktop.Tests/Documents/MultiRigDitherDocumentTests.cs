using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// The Dither Policy in a document. Format version 2 stays: the policy is an optional member of a multiRig step, and
/// a block without one does not dither, which is what every version 2 file written so far means.
/// </summary>
public sealed class MultiRigDitherDocumentTests : IDisposable
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-dither-tests-" + Guid.NewGuid().ToString("N"));

    public MultiRigDitherDocumentTests()
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
        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    private static readonly DitherPolicyDocument Policy = new(true, "rig.wide", 3, 1.5, 0.5, 1, 10);

    private static SequenceDocument Document(DitherPolicyDocument? policy) => new(
        "Dither",
        [
            new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
            new MultiRigDocumentStep(Guid.NewGuid(),
            [
                new RigTrackDocument(Guid.NewGuid(), "rig.main", [new RigExposureDocumentStep(Guid.NewGuid(), 3)]),
                new RigTrackDocument(Guid.NewGuid(), "rig.wide", [new RigExposureDocumentStep(Guid.NewGuid(), 0.6)]),
            ],
            policy),
        ],
        new SharedEquipmentDocument("mount.eq6", "guider.main"));

    // Wire contract

    [Fact]
    public async Task APolicy_SurvivesAWriteAndARead_WithEveryValue()
    {
        var text = await Write(Document(Policy));

        var loaded = await Read(text);

        var block = Assert.IsType<MultiRigDocumentStep>(loaded.Steps[1]);
        Assert.Equal(Policy, block.DitherPolicy);
        Assert.Equal(text, await Write(loaded));
    }

    [Fact]
    public async Task APolicyThatIsOff_KeepsItsValues_AndItsTrigger()
    {
        var off = new DitherPolicyDocument(false, "rig.main", 7, 2, 0.25, 4, 30);

        var loaded = await Read(await Write(Document(off)));

        Assert.Equal(off, ((MultiRigDocumentStep)loaded.Steps[1]).DitherPolicy);
    }

    [Fact]
    public async Task TheWireContract_PutsThePolicyAfterTheTracks_WithStableNames_AndNoClassNames()
    {
        var text = await Write(Document(Policy));
        using var json = JsonDocument.Parse(text);

        Assert.Equal(SequenceDocument.CurrentVersion, json.RootElement.GetProperty("version").GetInt32());
        var block = json.RootElement.GetProperty("steps")[1];
        Assert.Equal(["type", "id", "tracks", "ditherPolicy"], block.EnumerateObject().Select(p => p.Name));
        var policy = block.GetProperty("ditherPolicy");
        Assert.Equal(
            ["enabled", "triggerRigId", "everyNFrames", "amplitudePixels", "settleThresholdPixels", "settleStableSeconds", "settleTimeoutSeconds"],
            policy.EnumerateObject().Select(p => p.Name));
        Assert.True(policy.GetProperty("enabled").GetBoolean());
        Assert.Equal("rig.wide", policy.GetProperty("triggerRigId").GetString());
        Assert.Equal(3, policy.GetProperty("everyNFrames").GetInt32());
        Assert.Equal(1.5, policy.GetProperty("amplitudePixels").GetDouble());

        foreach (var forbidden in new[] { "$type", "DitherPolicyDocument", "MultiRigDither", "Wide Rig", "SafePoint", "safePoint", "FrameCounter" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoPolicy_WritesNoMember_SoAnOldReaderOfVersion2SeesTheFileItKnew()
    {
        var text = await Write(Document(null));

        Assert.DoesNotContain("ditherPolicy", text, StringComparison.Ordinal);
        Assert.Null(((MultiRigDocumentStep)(await Read(text)).Steps[1]).DitherPolicy);
    }

    [Fact]
    public async Task AGeneratedStepIsNeverWritten_NoSafePointNoDitherStep()
    {
        var text = await Write(Document(Policy));

        using var json = JsonDocument.Parse(text);
        var types = new List<string>();
        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("type", out var type))
                {
                    types.Add(type.GetString()!);
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item);
                }
            }
        }

        Walk(json.RootElement);

        Assert.Equal(["startGuiding", "multiRig", "rigExposure", "rigExposure"], types);
    }

    [Fact]
    public async Task ADocumentedExample_IsTheTextTheSerializerWrites()
    {
        var document = new SequenceDocument(
            null,
            [
                new MultiRigDocumentStep(Guid.Parse("22222222-2222-4222-8222-222222222222"),
                [
                    new RigTrackDocument(Guid.Parse("33333333-3333-4333-8333-333333333333"), "rig.main", []),
                ],
                new DitherPolicyDocument(true, "rig.main", 3, 1.5, 0.5, 1, 10)),
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
                      "steps": []
                    }
                  ],
                  "ditherPolicy": {
                    "enabled": true,
                    "triggerRigId": "rig.main",
                    "everyNFrames": 3,
                    "amplitudePixels": 1.5,
                    "settleThresholdPixels": 0.5,
                    "settleStableSeconds": 1,
                    "settleTimeoutSeconds": 10
                  }
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            text);
    }

    // Reading

    private static string Policy2(string members) => "{" + members + "}";

    private static string Block(string? policy) =>
        "{\"format\":\"astra-sequence\",\"version\":2,\"steps\":[{\"type\":\"multiRig\",\"id\":\"" + A + "\",\"tracks\":[{\"id\":\"" + B
        + "\",\"rigId\":\"rig.main\",\"steps\":[]}]" + (policy is null ? "" : ",\"ditherPolicy\":" + policy) + "}]}";

    private const string Members =
        "\"enabled\":true,\"triggerRigId\":\"rig.main\",\"everyNFrames\":3,\"amplitudePixels\":1.5,\"settleThresholdPixels\":0.5,\"settleStableSeconds\":1,\"settleTimeoutSeconds\":10";

    [Fact]
    public async Task AVersion2FileWithoutAPolicy_StillLoads_AsABlockThatDoesNotDither()
    {
        var loaded = await Read(Block(null));

        var block = Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps));
        Assert.Null(block.DitherPolicy);
        var draft = (MultiRigStepDraft)SequenceDocumentMapper.ToDrafts(loaded).Single();
        Assert.True(draft.DitherPolicy is null or { Enabled: false });
    }

    [Fact]
    public async Task ANullPolicy_IsReadAsNoPolicy()
    {
        var loaded = await Read(Block("null"));

        Assert.Null(((MultiRigDocumentStep)loaded.Steps[0]).DitherPolicy);
    }

    [Fact]
    public async Task AFullPolicy_IsRead()
    {
        var loaded = await Read(Block(Policy2(Members)));

        Assert.Equal(new DitherPolicyDocument(true, "rig.main", 3, 1.5, 0.5, 1, 10), ((MultiRigDocumentStep)loaded.Steps[0]).DitherPolicy);
    }

    [Fact]
    public async Task ATriggerThatIsNull_IsKept_ForTheEditorToSay()
    {
        var loaded = await Read(Block(Policy2(Members.Replace("\"triggerRigId\":\"rig.main\"", "\"triggerRigId\":null"))));

        Assert.Null(((MultiRigDocumentStep)loaded.Steps[0]).DitherPolicy!.TriggerRigId);
    }

    [Fact]
    public async Task ARigThatDoesNotExistHere_IsKeptAsItIs()
    {
        var loaded = await Read(Block(Policy2(Members.Replace("rig.main\",\"every", "rig.observatory\",\"every"))));

        Assert.Equal("rig.observatory", ((MultiRigDocumentStep)loaded.Steps[0]).DitherPolicy!.TriggerRigId);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("\"yes\"")]
    [InlineData("[]")]
    public async Task APolicyThatIsNoObject_IsRejected(string policy)
    {
        Assert.Equal("'ditherPolicy' must be an object.", (await Rejects(Block(policy))).Message);
    }

    [Theory]
    [InlineData("\"enabled\":true,", "")]
    [InlineData("\"enabled\":true", "\"enabled\":1")]
    [InlineData("\"enabled\":true", "\"enabled\":\"true\"")]
    [InlineData("\"enabled\":true", "\"enabled\":null")]
    public async Task EnabledMustBePresentAndABoolean(string find, string replace)
    {
        Assert.Equal(
            "'enabled' of a 'ditherPolicy' must be true or false.",
            (await Rejects(Block(Policy2(Members.Replace(find, replace))))).Message);
    }

    [Theory]
    [InlineData("\"everyNFrames\":3,")]
    [InlineData("\"amplitudePixels\":1.5,")]
    [InlineData("\"settleThresholdPixels\":0.5,")]
    [InlineData("\"settleStableSeconds\":1,")]
    [InlineData(",\"settleTimeoutSeconds\":10")]
    [InlineData("\"triggerRigId\":\"rig.main\",")]
    public async Task AMissingMember_IsRejected(string remove)
    {
        await Rejects(Block(Policy2(Members.Replace(remove, ""))));
    }

    [Theory]
    [InlineData("\"everyNFrames\":3", "\"everyNFrames\":2.5")]
    [InlineData("\"everyNFrames\":3", "\"everyNFrames\":1e20")]
    public async Task AnIntervalThatIsNoWholeNumber_IsRejected(string find, string replace)
    {
        Assert.Equal(
            "'everyNFrames' of a 'ditherPolicy' must be a whole number.",
            (await Rejects(Block(Policy2(Members.Replace(find, replace))))).Message);
    }

    [Theory]
    [InlineData("\"amplitudePixels\":1.5", "\"amplitudePixels\":\"1.5\"")]
    [InlineData("\"settleThresholdPixels\":0.5", "\"settleThresholdPixels\":true")]
    [InlineData("\"settleStableSeconds\":1", "\"settleStableSeconds\":null")]
    [InlineData("\"settleTimeoutSeconds\":10", "\"settleTimeoutSeconds\":{}")]
    [InlineData("\"everyNFrames\":3", "\"everyNFrames\":\"3\"")]
    public async Task ANumberThatIsNoNumber_IsRejected(string find, string replace)
    {
        await Rejects(Block(Policy2(Members.Replace(find, replace))));
    }

    [Theory]
    [InlineData("\"triggerRigId\":\"rig.main\"", "\"triggerRigId\":7")]
    [InlineData("\"triggerRigId\":\"rig.main\"", "\"triggerRigId\":\"\"")]
    public async Task ATriggerThatIsNoRigId_IsRejected(string find, string replace)
    {
        await Rejects(Block(Policy2(Members.Replace(find, replace))));
    }

    [Fact]
    public async Task ValuesThatAreNotUsable_AreStillLoaded_ForTheEditorToReport()
    {
        var members = Members.Replace("\"everyNFrames\":3", "\"everyNFrames\":0").Replace("\"amplitudePixels\":1.5", "\"amplitudePixels\":-1");

        var loaded = await Read(Block(Policy2(members)));

        var policy = ((MultiRigDocumentStep)loaded.Steps[0]).DitherPolicy!;
        Assert.Equal((0, -1.0), (policy.EveryNFrames, policy.AmplitudePixels));
    }

    [Fact]
    public async Task AVersion1File_CannotHaveABlock_SoCannotHaveAPolicy()
    {
        var text = Block(Policy2(Members)).Replace("\"version\":2", "\"version\":1");

        Assert.Equal("Unknown sequence step type 'multiRig'.", (await Rejects(text)).Message);
    }

    // The mapper

    [Fact]
    public void TheMapper_CarriesThePolicyBothWays_AndWritesNothingForNoneOrTheDefault()
    {
        var policy = new MultiRigDitherPolicyDraft(true, new RigId("rig.wide"), 4, 2, 0.4, 3, 20);
        var withPolicy = new MultiRigStepDraft(A, [new RigTrackDraft(B, new RigId("rig.main"), [])], policy);
        var noPolicy = new MultiRigStepDraft(C, [], null);
        var defaulted = new MultiRigStepDraft(D, [], MultiRigDitherPolicyDraft.Default);

        var document = SequenceDocumentMapper.ToDocument([withPolicy, noPolicy, defaulted], null, null);
        var back = SequenceDocumentMapper.ToDrafts(document).Cast<MultiRigStepDraft>().ToList();

        Assert.Equal(new DitherPolicyDocument(true, "rig.wide", 4, 2, 0.4, 3, 20), ((MultiRigDocumentStep)document.Steps[0]).DitherPolicy);
        Assert.Null(((MultiRigDocumentStep)document.Steps[1]).DitherPolicy);
        Assert.Null(((MultiRigDocumentStep)document.Steps[2]).DitherPolicy);
        Assert.Equal(policy, back[0].DitherPolicy);
        Assert.True(back[1].DitherPolicy is null or { Enabled: false });
    }

    [Fact]
    public void ADisabledPolicyWithAChangedValue_IsNotTheDefault_AndIsWritten()
    {
        var changed = MultiRigDitherPolicyDraft.Default with { EveryNFrames = 9 };

        var document = SequenceDocumentMapper.ToDocument([new MultiRigStepDraft(A, [], changed)], null, null);

        Assert.NotNull(((MultiRigDocumentStep)document.Steps[0]).DitherPolicy);
        Assert.Equal(9, ((MultiRigDocumentStep)document.Steps[0]).DitherPolicy!.EveryNFrames);
    }

    // The editor and files

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

    private static MultiRigStepDraftViewModel BuildBlock(SequenceDraftViewModel draft)
    {
        draft.ReplaceSteps([]);
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = (MultiRigStepDraftViewModel)draft.SelectedStep!;
        foreach (var (rig, seconds) in new[] { ("rig.main", "3"), ("rig.wide", "0.6") })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = (RigTrackDraftViewModel)draft.SelectedStep!;
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            ((RigExposureStepDraftViewModel)draft.SelectedStep!).ExposureText = seconds;
        }

        draft.SelectedStep = null;
        draft.AddStepCommand.Execute(SequenceStepKind.StopGuiding);
        return block;
    }

    [Fact]
    public async Task APolicySetInTheEditor_IsSavedInTheFile_AndComesBackIntoTheEditor()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        var block = BuildBlock(app.Draft);
        block.DitherEnabled = true;
        block.TriggerRig.Selected = block.TriggerRig.Options.Single(o => o.IdText == "rig.wide");
        block.DitherEveryText = "5";
        block.DitherAmplitudeText = "2";
        block.DitherSettleThresholdText = "0.4";
        block.DitherSettleStableText = "2";
        block.DitherSettleTimeoutText = "30";
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        app.Picker.SavePath = PathOf("Policy");
        await app.Document.SaveCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(PathOf("Policy.astraseq"));
        Assert.Contains("\"ditherPolicy\"", text, StringComparison.Ordinal);
        Assert.Contains("\"triggerRigId\": \"rig.wide\"", text, StringComparison.Ordinal);
        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Policy.astraseq"));

        Assert.False(app.Document.IsDirty);
        var loaded = app.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Single();
        Assert.True(loaded.DitherEnabled);
        Assert.Equal(new RigId("rig.wide"), loaded.TriggerRig.SelectedId);
        Assert.Equal(["5", "2", "0.4", "2", "30"],
            [loaded.DitherEveryText, loaded.DitherAmplitudeText, loaded.DitherSettleThresholdText, loaded.DitherSettleStableText, loaded.DitherSettleTimeoutText]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
    }

    [Fact]
    public async Task ADocumentWithATriggerRigThatDoesNotExistHere_LoadsAndStaysRepairable_ThenRuns()
    {
        await using var app = Create();
        var document = new SequenceDocument(
            "Observatory",
            [
                new MultiRigDocumentStep(A,
                [
                    new RigTrackDocument(B, "rig.main", [new RigExposureDocumentStep(C, 0.1)]),
                    new RigTrackDocument(D, "rig.wide", [new RigExposureDocumentStep(E, 0.1)]),
                ],
                new DitherPolicyDocument(true, "rig.observatory", 3, 1.5, 0.5, 1, 10)),
            ],
            new SharedEquipmentDocument("mount.eq6", "guider.main"));
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Obs.astraseq"), document);

        await app.Open(PathOf("Obs.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        var block = app.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Single();
        Assert.Equal(new RigId("rig.observatory"), block.TriggerRig.SelectedId); // kept, not replaced
        Assert.True(block.TriggerRig.Selected!.IsMissing);
        Assert.Contains("The imaging setup 'rig.observatory' that counts the frames is not a sequence of this block.", block.Problems);
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Vm.Sequencer.CanRun);
        Assert.False(app.Document.IsDirty);

        block.TriggerRig.Selected = block.TriggerRig.Options.Single(o => o.IdText == "rig.wide");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Document.IsDirty);
    }

    [Fact]
    public async Task AVersion2FileWrittenBeforePolicies_OpensWithThePolicyOff_AndIsNotDirty()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Old.astraseq"), Block(null));

        await app.Open(PathOf("Old.astraseq"));

        Assert.False(app.Document.IsDirty);
        var block = app.Draft.Rows.OfType<MultiRigStepDraftViewModel>().Single();
        Assert.False(block.DitherEnabled);
        Assert.Null(block.TriggerRig.SelectedId);
    }
}

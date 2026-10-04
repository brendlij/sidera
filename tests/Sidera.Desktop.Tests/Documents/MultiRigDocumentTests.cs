using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>Format version 2 (shared equipment, Multi-Rig Imaging), and version 1 documents next to it.</summary>
public sealed class MultiRigDocumentTests : IDisposable
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");
    private static readonly Guid F = Guid.Parse("00000000-0000-0000-0000-00000000000f");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-multirig-tests-" + Guid.NewGuid().ToString("N"));

    public MultiRigDocumentTests()
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

    // Three rigs on one mount and one guider, the Main rig with a Repeat.
    private static SequenceDocument ThreeRigs() => new(
        "Three Rigs",
        [
            new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
            new SlewDocumentStep(Guid.NewGuid(), "mount.eq6", 5.588, -5.39),
            new MultiRigDocumentStep(Guid.NewGuid(),
            [
                new RigTrackDocument(Guid.NewGuid(), "rig.main",
                [
                    new RepeatDocumentStep(Guid.NewGuid(), 40, [new RigExposureDocumentStep(Guid.NewGuid(), 300)]),
                ]),
                new RigTrackDocument(Guid.NewGuid(), "rig.wide",
                [
                    new RepeatDocumentStep(Guid.NewGuid(), 120, [new RigExposureDocumentStep(Guid.NewGuid(), 60), new DelayDocumentStep(Guid.NewGuid(), 1)]),
                ]),
                new RigTrackDocument(Guid.NewGuid(), "rig.narrow",
                [
                    new RigExposureDocumentStep(Guid.NewGuid(), 180),
                    new DelayDocumentStep(Guid.NewGuid(), 5),
                ]),
            ]),
            new StopGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        ],
        new SharedEquipmentDocument("mount.eq6", "guider.main"));

    // The current version: round trip and wire contract

    [Fact]
    public async Task ABlockWithItsTracks_SurvivesAWriteAndARead_WithEveryIdRigAndValue()
    {
        var document = ThreeRigs();

        var text = await Write(document);
        var loaded = await Read(text);

        Assert.Equal("Three Rigs", loaded.Name);
        Assert.Equal(new SharedEquipmentDocument("mount.eq6", "guider.main"), loaded.SharedEquipment);
        var block = Assert.IsType<MultiRigDocumentStep>(loaded.Steps[2]);
        var original = (MultiRigDocumentStep)document.Steps[2];
        Assert.Equal(original.Id, block.Id);
        Assert.Equal(original.Tracks.Select(t => t.Id), block.Tracks.Select(t => t.Id));
        Assert.Equal(["rig.main", "rig.wide", "rig.narrow"], block.Tracks.Select(t => t.RigId));
        var main = Assert.IsType<RepeatDocumentStep>(Assert.Single(block.Tracks[0].Steps));
        Assert.Equal(40, main.Count);
        Assert.Equal(300, Assert.IsType<RigExposureDocumentStep>(Assert.Single(main.Children)).ExposureSeconds);
        var wide = Assert.IsType<RepeatDocumentStep>(Assert.Single(block.Tracks[1].Steps));
        Assert.Equal(120, wide.Count);
        Assert.IsType<DelayDocumentStep>(wide.Children[1]);
        Assert.Equal(180, Assert.IsType<RigExposureDocumentStep>(block.Tracks[2].Steps[0]).ExposureSeconds);
        Assert.NotSame(original, block); // a graph of its own
        Assert.Equal(text, await Write(loaded)); // and the same once more
    }

    [Fact]
    public async Task ATrackWithoutARig_AndAnEmptyTrack_AndNoSharedEquipment_AreKeptAsTheyAre()
    {
        var document = new SequenceDocument(null,
        [
            new MultiRigDocumentStep(A, [new RigTrackDocument(B, null, []), new RigTrackDocument(C, "rig.main", [])]),
        ]);

        var loaded = await Read(await Write(document));

        Assert.Null(loaded.SharedEquipment);
        var block = Assert.IsType<MultiRigDocumentStep>(Assert.Single(loaded.Steps));
        Assert.Null(block.Tracks[0].RigId);
        Assert.Empty(block.Tracks[0].Steps);
        Assert.Equal("rig.main", block.Tracks[1].RigId);
    }

    [Fact]
    public async Task ASharedEquipmentWithNoDevices_IsKept_AsWrittenWithNulls()
    {
        var loaded = await Read(await Write(new SequenceDocument(null, [], new SharedEquipmentDocument(null, "guider.main"))));

        Assert.Equal(new SharedEquipmentDocument(null, "guider.main"), loaded.SharedEquipment);
    }

    [Fact]
    public async Task TheWireContract_NamesTheVersionTheSharedEquipmentAndTheStableDiscriminators()
    {
        using var json = JsonDocument.Parse(await Write(ThreeRigs()));
        var root = json.RootElement;

        Assert.Equal("astra-sequence", root.GetProperty("format").GetString());
        Assert.Equal(6, root.GetProperty("version").GetInt32());
        Assert.Equal(["format", "version", "name", "sharedEquipment", "steps"], root.EnumerateObject().Select(p => p.Name));
        var shared = root.GetProperty("sharedEquipment");
        Assert.Equal("mount.eq6", shared.GetProperty("mountId").GetString());
        Assert.Equal("guider.main", shared.GetProperty("guiderId").GetString());

        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(["startGuiding", "slew", "multiRig", "stopGuiding"], steps.Select(s => s.GetProperty("type").GetString()));
        var block = steps[2];
        Assert.Equal(["type", "id", "tracks"], block.EnumerateObject().Select(p => p.Name));
        var tracks = block.GetProperty("tracks").EnumerateArray().ToList();
        Assert.Equal(["id", "rigId", "steps"], tracks[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["rig.main", "rig.wide", "rig.narrow"], tracks.Select(t => t.GetProperty("rigId").GetString()));
        var firstStep = tracks[2].GetProperty("steps")[0];
        Assert.Equal("rigExposure", firstStep.GetProperty("type").GetString());
        Assert.Equal(["type", "id", "exposureSeconds"], firstStep.EnumerateObject().Select(p => p.Name));
        Assert.Equal("repeat", tracks[0].GetProperty("steps")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheDocumentContainsNoTypeMetadata_NoFriendlyNames_AndNoClassNames()
    {
        var text = await Write(ThreeRigs());

        foreach (var forbidden in new[]
                 {
                     "$type", "Sidera.Desktop", "DocumentStep", "RigTrackDocument", "ViewModel", "Version=", "Main Rig", "Wide Rig",
                     "Main Camera", "EQ6 Mount",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARigIsReferredToByIdOnly()
    {
        var text = await Write(ThreeRigs());

        Assert.Contains("\"rigId\": \"rig.wide\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cameraId", text, StringComparison.Ordinal); // a track's exposure names no camera
    }

    // Version 1 documents

    private static string V1(string steps) => "{\"format\":\"astra-sequence\",\"version\":1,\"name\":\"Old\",\"steps\":[" + steps + "]}";

    private static string V1Sample() => V1(
        $"{{\"type\":\"startGuiding\",\"id\":\"{A}\",\"guiderId\":\"guider.main\"}},"
        + $"{{\"type\":\"repeat\",\"id\":\"{B}\",\"count\":3,\"children\":["
        + $"{{\"type\":\"exposure\",\"id\":\"{C}\",\"cameraId\":\"camera.main\",\"exposureSeconds\":120}},"
        + $"{{\"type\":\"delay\",\"id\":\"{D}\",\"durationSeconds\":2}}]}},"
        + $"{{\"type\":\"stopGuiding\",\"id\":\"{E}\",\"guiderId\":\"guider.main\"}}");

    [Fact]
    public async Task AVersion1Document_StillLoads_WithItsIdsAndValues_AndNoSharedEquipment()
    {
        var loaded = await Read(V1Sample());

        Assert.Equal("Old", loaded.Name);
        Assert.Null(loaded.SharedEquipment);
        Assert.Equal([A, B, E], loaded.Steps.Select(s => s.Id));
        Assert.Equal(new StartGuidingDocumentStep(A, "guider.main"), loaded.Steps[0]);
        var repeat = Assert.IsType<RepeatDocumentStep>(loaded.Steps[1]);
        Assert.Equal(3, repeat.Count);
        Assert.Equal(new ExposureDocumentStep(C, "camera.main", 120), repeat.Children[0]);
        Assert.Equal(new DelayDocumentStep(D, 2), repeat.Children[1]);
    }

    [Fact]
    public async Task AVersion1Document_IsNotChangedByLoadingIt_AndIsWrittenAsTheCurrentVersionWithTheSameSteps()
    {
        var loaded = await Read(V1Sample());

        var rewritten = await Write(loaded);
        using var json = JsonDocument.Parse(rewritten);

        Assert.Equal(6, json.RootElement.GetProperty("version").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("sharedEquipment", out _)); // it never said anything about it
        var again = await Read(rewritten);
        Assert.Equal(loaded.Steps.Select(s => s.Id), again.Steps.Select(s => s.Id));
        Assert.Equal(loaded.Steps[0], again.Steps[0]);
        Assert.Equal(loaded.Steps[2], again.Steps[2]);
        Assert.Equal(((RepeatDocumentStep)loaded.Steps[1]).Children, ((RepeatDocumentStep)again.Steps[1]).Children);
    }

    [Fact]
    public async Task AVersion1Document_CannotUseWhatVersion2Added()
    {
        var multiRig = V1($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[]}}");
        var rigExposure = V1($"{{\"type\":\"rigExposure\",\"id\":\"{A}\",\"exposureSeconds\":1}}");

        Assert.Equal("Unknown sequence step type 'multiRig'.", (await Rejects(multiRig)).Message);
        Assert.Equal("Unknown sequence step type 'rigExposure'.", (await Rejects(rigExposure)).Message);
    }

    [Fact]
    public async Task ASharedEquipmentInAVersion1Document_IsNotPartOfThatVersion_AndIsIgnored()
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":1,\"sharedEquipment\":{\"mountId\":\"mount.eq6\",\"guiderId\":null},\"steps\":[]}";

        var loaded = await Read(text);

        Assert.Null(loaded.SharedEquipment);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("8")]
    [InlineData("100")]
    public async Task ANewerVersionThanFive_IsRejectedAsBefore(string version)
    {
        var ex = await Rejects("{\"format\":\"astra-sequence\",\"version\":" + version + ",\"steps\":[]}");

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
        Assert.Equal("This sequence was created by a newer Sidera version.", ex.Message);
    }

    // Structure of version 2

    private static string V2(string steps, string extra = "") =>
        "{\"format\":\"astra-sequence\",\"version\":2," + extra + "\"steps\":[" + steps + "]}";

    private static string Delay(Guid id) => $"{{\"type\":\"delay\",\"id\":\"{id}\",\"durationSeconds\":1}}";
    private static string RigExposure(Guid id) => $"{{\"type\":\"rigExposure\",\"id\":\"{id}\",\"exposureSeconds\":1}}";

    private static string Track(Guid id, string rig, params string[] steps) =>
        $"{{\"id\":\"{id}\",\"rigId\":{rig},\"steps\":[{string.Join(",", steps)}]}}";

    private static string Block(Guid id, params string[] tracks) =>
        $"{{\"type\":\"multiRig\",\"id\":\"{id}\",\"tracks\":[{string.Join(",", tracks)}]}}";

    private static string Repeat(Guid id, params string[] children) =>
        $"{{\"type\":\"repeat\",\"id\":\"{id}\",\"count\":2,\"children\":[{string.Join(",", children)}]}}";

    [Fact]
    public async Task ABlockCannotBeNested_NotInATrackNotInARepeat()
    {
        var inTrack = V2(Block(A, Track(B, "\"rig.main\"", Block(C)), Track(D, "\"rig.wide\"", RigExposure(E))));
        var inRepeat = V2(Repeat(A, Block(B)));

        Assert.Equal("Multi-Rig steps can only be placed at the top level of a sequence.", (await Rejects(inTrack)).Message);
        Assert.Equal("Multi-Rig steps can only be placed at the top level of a sequence.", (await Rejects(inRepeat)).Message);
    }

    [Fact]
    public async Task ARepeatInATrack_CannotContainARepeat()
    {
        var text = V2(Block(A, Track(B, "\"rig.main\"", Repeat(C, Repeat(D, RigExposure(E))))));

        Assert.Equal("Repeat steps cannot contain another Repeat.", (await Rejects(text)).Message);
    }

    [Theory]
    [InlineData("slew", "{\"type\":\"slew\",\"id\":\"ID\",\"mountId\":\"mount.eq6\",\"raHours\":1,\"decDegrees\":1}")]
    [InlineData("startGuiding", "{\"type\":\"startGuiding\",\"id\":\"ID\",\"guiderId\":\"guider.main\"}")]
    [InlineData("stopGuiding", "{\"type\":\"stopGuiding\",\"id\":\"ID\",\"guiderId\":\"guider.main\"}")]
    [InlineData("exposure", "{\"type\":\"exposure\",\"id\":\"ID\",\"cameraId\":\"camera.main\",\"exposureSeconds\":1}")]
    [InlineData("dither", "{\"type\":\"dither\",\"id\":\"ID\",\"guiderId\":null,\"mountId\":null,\"cameraId\":null,\"amplitudePixels\":1,\"settleThresholdPixels\":1,\"settleStableSeconds\":1,\"settleTimeoutSeconds\":2}")]
    public async Task WhatBelongsToTheSession_IsNotAStepOfATrack(string type, string step)
    {
        var inTrack = V2(Block(A, Track(B, "\"rig.main\"", step.Replace("ID", C.ToString()))));
        var inTrackRepeat = V2(Block(A, Track(B, "\"rig.main\"", Repeat(D, step.Replace("ID", C.ToString())))));

        Assert.Equal($"A '{type}' step cannot be used inside a rig track.", (await Rejects(inTrack)).Message);
        Assert.Equal($"A '{type}' step cannot be used inside a rig track.", (await Rejects(inTrackRepeat)).Message);
    }

    [Fact]
    public async Task ARigExposure_IsOnlyAStepOfATrack()
    {
        Assert.Equal("A 'rigExposure' step can only be used inside a rig track.", (await Rejects(V2(RigExposure(A)))).Message);
        Assert.Equal("A 'rigExposure' step can only be used inside a rig track.", (await Rejects(V2(Repeat(A, RigExposure(B))))).Message);
    }

    [Fact]
    public async Task ADelayIsAStepWhereverItIs()
    {
        var text = V2(Delay(A) + "," + Repeat(B, Delay(C)) + "," + Block(D, Track(E, "\"rig.main\"", Delay(F))));

        var loaded = await Read(text);

        Assert.Equal(3, loaded.Steps.Count);
    }

    [Fact]
    public async Task AnUnknownTrackStepType_IsRejectedByName()
    {
        var text = V2(Block(A, Track(B, "\"rig.main\"", $"{{\"type\":\"autofocus\",\"id\":\"{C}\"}}")));

        Assert.Equal("Unknown sequence step type 'autofocus'.", (await Rejects(text)).Message);
    }

    [Fact]
    public async Task TheSameIdTwice_AnywhereInTheTree_IsRejected()
    {
        var cases = new[]
        {
            V2(Block(A, Track(B, "\"rig.main\"", RigExposure(C)), Track(D, "\"rig.wide\"", RigExposure(C)))),   // step in two tracks
            V2(Block(A, Track(B, "\"rig.main\"", RigExposure(C)), Track(B, "\"rig.wide\"", RigExposure(D)))),   // two tracks
            V2(Block(A, Track(A, "\"rig.main\"", RigExposure(C)))),                                             // block and its track
            V2(Block(A, Track(B, "\"rig.main\"", RigExposure(B)))),                                             // track and its step
            V2(Delay(A) + "," + Block(B, Track(A, "\"rig.main\"", RigExposure(C)))),                            // top-level step and a track
            V2(Block(A, Track(B, "\"rig.main\"", RigExposure(C))) + "," + Block(D, Track(E, "\"rig.main\"", RigExposure(C)))), // two blocks
            V2(Block(A, Track(B, "\"rig.main\"", Repeat(C, RigExposure(D)))) + "," + Repeat(E, Delay(D))),      // a repeat's child and another
        };

        foreach (var text in cases)
        {
            Assert.Equal("Duplicate sequence step ID.", (await Rejects(text)).Message);
        }
    }

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("5")]
    [InlineData("null")]
    public async Task AMalformedTrackId_IsRejected(string id)
    {
        var text = V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":{id},\"rigId\":null,\"steps\":[]}}]}}");

        Assert.StartsWith("Invalid sequence step ID", (await Rejects(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPartOfABlockOrATrack_IsRejected_AndNamed()
    {
        Assert.Equal(
            "A 'multiRig' step is missing its list of 'tracks'.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\"}}"))).Message);
        Assert.Equal(
            "A 'rigTrack' step is missing 'id'.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"rigId\":null,\"steps\":[]}}]}}"))).Message);
        Assert.Equal(
            "A 'rigTrack' step is missing 'rigId'.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":\"{B}\",\"steps\":[]}}]}}"))).Message);
        Assert.Equal(
            "A rig track is missing its list of 'steps'.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":\"{B}\",\"rigId\":null}}]}}"))).Message);
        Assert.Equal(
            "A 'rigExposure' step is missing 'exposureSeconds'.",
            (await Rejects(V2(Block(A, Track(B, "null", $"{{\"type\":\"rigExposure\",\"id\":\"{C}\"}}"))))).Message);
    }

    [Theory]
    [InlineData("\"tracks\":{}")]
    [InlineData("\"tracks\":\"x\"")]
    public async Task TracksThatAreNotAList_AreRejected(string tracks)
    {
        var ex = await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",{tracks}}}"));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
    }

    [Fact]
    public async Task ATrackThatIsNoObject_OrWithBadStepsOrRig_IsRejected()
    {
        Assert.Equal("A rig track must be an object.", (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[5]}}"))).Message);
        Assert.Equal(
            "A rig track is missing its list of 'steps'.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":\"{B}\",\"rigId\":null,\"steps\":{{}}}}]}}"))).Message);
        Assert.Equal(
            "'rigId' of a 'rigTrack' step must be a device ID or null.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":\"{B}\",\"rigId\":7,\"steps\":[]}}]}}"))).Message);
        Assert.Equal(
            "'rigId' of a 'rigTrack' step must be a device ID or null.",
            (await Rejects(V2($"{{\"type\":\"multiRig\",\"id\":\"{A}\",\"tracks\":[{{\"id\":\"{B}\",\"rigId\":\"\",\"steps\":[]}}]}}"))).Message);
    }

    [Fact]
    public async Task ASharedEquipmentThatIsNoObject_OrIncomplete_IsRejected()
    {
        Assert.Equal(
            "'sharedEquipment' must be an object.",
            (await Rejects("{\"format\":\"astra-sequence\",\"version\":2,\"sharedEquipment\":5,\"steps\":[]}")).Message);
        Assert.Equal(
            "A 'sharedEquipment' step is missing 'guiderId'.",
            (await Rejects("{\"format\":\"astra-sequence\",\"version\":2,\"sharedEquipment\":{\"mountId\":null},\"steps\":[]}")).Message);
        Assert.Equal(
            "'mountId' of a 'sharedEquipment' step must be a device ID or null.",
            (await Rejects("{\"format\":\"astra-sequence\",\"version\":2,\"sharedEquipment\":{\"mountId\":3,\"guiderId\":null},\"steps\":[]}")).Message);
    }

    [Fact]
    public async Task AVersion2DocumentWithoutSharedEquipment_IsValid()
    {
        var loaded = await Read(V2(Delay(A)));

        Assert.Null(loaded.SharedEquipment);
    }

    [Fact]
    public async Task ABlockWithoutTracksOrWithOne_Loads_BecauseWhetherThatCanRunIsForTheEditor()
    {
        var loaded = await Read(V2(Block(A) + "," + Block(B, Track(C, "\"rig.main\"", RigExposure(D)))));

        Assert.Equal(2, loaded.Steps.Count);
        Assert.Empty(Assert.IsType<MultiRigDocumentStep>(loaded.Steps[0]).Tracks);
    }

    // The example of the format

    [Fact]
    public async Task TheDocumentedMultiRigExample_IsTheDocumentThatTheSerializerWrites()
    {
        var document = new SequenceDocument(
            "Three Telescopes",
            [
                new StartGuidingDocumentStep(Guid.Parse("11111111-1111-4111-8111-111111111111"), "guider.main"),
                new MultiRigDocumentStep(Guid.Parse("22222222-2222-4222-8222-222222222222"),
                [
                    new RigTrackDocument(Guid.Parse("33333333-3333-4333-8333-333333333333"), "rig.main",
                    [
                        new RepeatDocumentStep(Guid.Parse("44444444-4444-4444-8444-444444444444"), 40,
                            [new RigExposureDocumentStep(Guid.Parse("55555555-5555-4555-8555-555555555555"), 300)]),
                    ]),
                    new RigTrackDocument(Guid.Parse("66666666-6666-4666-8666-666666666666"), "rig.wide",
                    [
                        new RepeatDocumentStep(Guid.Parse("77777777-7777-4777-8777-777777777777"), 120,
                            [new RigExposureDocumentStep(Guid.Parse("88888888-8888-4888-8888-888888888888"), 60)]),
                    ]),
                ]),
                new StopGuidingDocumentStep(Guid.Parse("99999999-9999-4999-8999-999999999999"), "guider.main"),
            ],
            new SharedEquipmentDocument("mount.eq6", "guider.main"));

        var text = await Write(document);

        Assert.Equal(
            """
            {
              "format": "astra-sequence",
              "version": 6,
              "name": "Three Telescopes",
              "sharedEquipment": {
                "mountId": "mount.eq6",
                "guiderId": "guider.main"
              },
              "steps": [
                {
                  "type": "startGuiding",
                  "id": "11111111-1111-4111-8111-111111111111",
                  "guiderId": "guider.main"
                },
                {
                  "type": "multiRig",
                  "id": "22222222-2222-4222-8222-222222222222",
                  "tracks": [
                    {
                      "id": "33333333-3333-4333-8333-333333333333",
                      "rigId": "rig.main",
                      "steps": [
                        {
                          "type": "repeat",
                          "id": "44444444-4444-4444-8444-444444444444",
                          "count": 40,
                          "children": [
                            {
                              "type": "rigExposure",
                              "id": "55555555-5555-4555-8555-555555555555",
                              "exposureSeconds": 300
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "id": "66666666-6666-4666-8666-666666666666",
                      "rigId": "rig.wide",
                      "steps": [
                        {
                          "type": "repeat",
                          "id": "77777777-7777-4777-8777-777777777777",
                          "count": 120,
                          "children": [
                            {
                              "type": "rigExposure",
                              "id": "88888888-8888-4888-8888-888888888888",
                              "exposureSeconds": 60
                            }
                          ]
                        }
                      ]
                    }
                  ]
                },
                {
                  "type": "stopGuiding",
                  "id": "99999999-9999-4999-8999-999999999999",
                  "guiderId": "guider.main"
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            text);
        Assert.Equal(text, await Write(await Read(text)));
    }

    // The mapper

    [Fact]
    public void TheMapper_CarriesBlocksTracksRigsAndSharedEquipment_BothWays_WithoutChangingAnything()
    {
        var exposure = new RigExposureStepDraft(A, 3);
        var repeat = new RepeatStepDraft(B, 4, [exposure]);
        var track = new RigTrackDraft(C, new RigId("rig.observatory"), [repeat, new DelayStepDraft(D, 2)]);
        var block = new MultiRigStepDraft(E, [track, new RigTrackDraft(F, null, [])]);
        var shared = new SharedEquipmentDraft(new DeviceId("mount.eq6"), null);

        var document = SequenceDocumentMapper.ToDocument([block], "x", shared);
        var back = (MultiRigStepDraft)Assert.Single(SequenceDocumentMapper.ToDrafts(document));

        Assert.Equal(new SharedEquipmentDocument("mount.eq6", null), document.SharedEquipment);
        Assert.Equal(shared, SequenceDocumentMapper.ToSharedEquipment(document));
        Assert.Equal(E, back.Id);
        Assert.Equal([C, F], back.Tracks.Select(t => t.Id));
        Assert.Equal([new RigId("rig.observatory"), (RigId?)null], back.Tracks.Select(t => t.RigId));
        var backRepeat = Assert.IsType<RepeatStepDraft>(back.Tracks[0].Steps[0]);
        Assert.Equal((B, 4), (backRepeat.Id, backRepeat.Count));
        Assert.Equal(exposure, Assert.Single(backRepeat.Children));
        Assert.Equal(new DelayStepDraft(D, 2), back.Tracks[0].Steps[1]);
        Assert.Null(SequenceDocumentMapper.ToSharedEquipment(new SequenceDocument(null, [])));
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

    private static IEnumerable<Guid> Ids(IEnumerable<SequenceStepDraft> steps)
    {
        foreach (var step in steps)
        {
            yield return step.Id;
            var inner = step switch
            {
                MultiRigStepDraft m => m.Tracks.Cast<SequenceStepDraft>(),
                RigTrackDraft t => t.Steps,
                RepeatStepDraft r => r.Children,
                _ => [],
            };
            foreach (var id in Ids(inner))
            {
                yield return id;
            }
        }
    }

    // Start Guiding, Multi-Rig [Main: Repeat × 2 [Exposure 3 s], Wide: Repeat × 4 [Exposure 0.6 s], Narrow: Exposure 1.5 s], Stop Guiding.
    private static void BuildThreeRigSession(SequenceDraftViewModel draft)
    {
        draft.AddStepCommand.Execute(SequenceStepKind.StartGuiding);
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = (MultiRigStepDraftViewModel)draft.SelectedStep!;
        foreach (var (count, seconds) in new[] { (2, "3"), (4, "0.6") })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Repeat);
            ((RepeatStepDraftViewModel)draft.SelectedStep!).CountText = count.ToString();
            draft.AddChildCommand.Execute(SequenceStepKind.Exposure);
            ((RigExposureStepDraftViewModel)draft.SelectedStep!).ExposureText = seconds;
        }

        draft.SelectedStep = block;
        draft.AddTrackCommand.Execute(null);
        draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
        ((RigExposureStepDraftViewModel)draft.SelectedStep!).ExposureText = "1.5";
        draft.SelectedStep = null;
        draft.AddStepCommand.Execute(SequenceStepKind.StopGuiding);
    }

    [Fact]
    public async Task AThreeRigSession_IsSavedAsTheCurrentVersion_AndOpensAsExactlyTheSameStructure()
    {
        await using var app = Create();
        await app.Document.NewCommand.ExecuteAsync(null);
        app.Draft.ReplaceSteps([]);
        BuildThreeRigSession(app.Draft);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var ids = Ids(app.Draft.Snapshot()).ToList();
        var rigs = ((MultiRigStepDraft)app.Draft.Snapshot()[1]).Tracks.Select(t => t.RigId).ToList();
        app.Picker.SavePath = PathOf("Three");
        await app.Document.SaveCommand.ExecuteAsync(null);

        var text = await File.ReadAllTextAsync(PathOf("Three.astraseq"));
        Assert.Contains("\"version\": 6", text, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"multiRig\"", text, StringComparison.Ordinal);
        Assert.Contains("\"mountId\": \"mount.eq6\"", text, StringComparison.Ordinal);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Three.astraseq"));

        Assert.False(app.Document.IsDirty);
        Assert.Equal(ids, Ids(app.Draft.Snapshot()));
        Assert.Equal(rigs, ((MultiRigStepDraft)app.Draft.Snapshot()[1]).Tracks.Select(t => t.RigId));
        Assert.Equal(["rig.main", "rig.narrow", "rig.wide"], rigs.Select(r => r!.Value.Value).Order());
        Assert.Equal(new DeviceId("mount.eq6"), app.Draft.SharedMount.SelectedId);
        Assert.Equal(new DeviceId("guider.main"), app.Draft.SharedGuider.SelectedId);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var exposures = app.Draft.Rows.OfType<RigExposureStepDraftViewModel>().Select(e => e.ExposureText).ToList();
        Assert.Equal(["3", "0.6", "1.5"], exposures);
    }

    [Fact]
    public async Task AVersion1File_OpensInTheEditor_AsTheSequenceItWas_WithTheSharedEquipmentLeftOpen()
    {
        await using var app = Create();
        await File.WriteAllTextAsync(PathOf("Old.astraseq"), V1Sample());

        await app.Open(PathOf("Old.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        Assert.Equal("Old.astraseq", app.Document.DisplayName);
        Assert.False(app.Document.IsDirty);
        Assert.Equal([A, B, C, D, E], app.Draft.Rows.Select(r => r.Id));
        var exposure = Assert.IsType<ExposureStepDraftViewModel>(app.Draft.Rows[2]);
        Assert.Equal("120", exposure.ExposureText);
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        Assert.Equal(new SharedEquipmentDraft(null, null), app.Draft.SharedEquipment); // a v1 file says nothing about it
        Assert.Empty(app.Draft.SharedProblems);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        // Saving it writes version 2, with the same steps.
        app.Picker.SavePath = PathOf("Old2");
        app.Draft.Steps.OfType<RepeatStepDraftViewModel>().Single().CountText = "4";
        await app.Document.SaveCommand.ExecuteAsync(null);
        var saved = await File.ReadAllTextAsync(PathOf("Old.astraseq"));
        Assert.Contains("\"version\": 6", saved, StringComparison.Ordinal);
        Assert.Contains("\"count\": 4", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentWithARigThatDoesNotExistHere_LoadsAsARepairableDraft_ThatRunsOnceRepaired()
    {
        await using var app = Create();
        var document = new SequenceDocument("Observatory",
        [
            new MultiRigDocumentStep(A,
            [
                new RigTrackDocument(B, "rig.observatory", [new RigExposureDocumentStep(C, 0.1)]),
                new RigTrackDocument(D, "rig.wide", [new RigExposureDocumentStep(E, 0.1)]),
            ]),
        ]);
        await SequenceDocumentStore.CreateDefault().SaveAsync(PathOf("Obs.astraseq"), document);

        await app.Open(PathOf("Obs.astraseq"));

        Assert.Null(app.Document.ErrorMessage);
        var track = (RigTrackDraftViewModel)app.Draft.Rows[1];
        Assert.Equal(new RigId("rig.observatory"), track.Rig.SelectedId);
        Assert.True(track.Rig.Selected!.IsMissing);
        Assert.Equal(["The rig 'rig.observatory' is not available."], track.Problems);
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Vm.Sequencer.CanRun);
        Assert.False(app.Document.IsDirty);

        track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == "rig.main");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        Assert.True(app.Document.IsDirty);
        Assert.Single(app.Draft.Build().Sequence.Steps);
    }

    [Fact]
    public async Task ADuplicatedBlock_IsSavedAndOpenedAsTwoBlocks_WithDistinctIds()
    {
        await using var app = Create();
        app.Draft.ReplaceSteps([]);
        BuildThreeRigSession(app.Draft);
        app.Draft.SelectedStep = app.Draft.Steps[1];
        app.Draft.DuplicateStepCommand.Execute(null);
        var ids = Ids(app.Draft.Snapshot()).ToList();
        app.Picker.SavePath = PathOf("Twice");
        await app.Document.SaveCommand.ExecuteAsync(null);

        await app.Document.NewCommand.ExecuteAsync(null);
        await app.Open(PathOf("Twice.astraseq"));

        var blocks = app.Draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal(2, blocks.Count);
        Assert.Equal(ids, Ids(app.Draft.Snapshot()));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(blocks[0].Tracks.Select(t => t.RigId), blocks[1].Tracks.Select(t => t.RigId));
        Assert.Empty(Ids([blocks[0]]).Intersect(Ids([blocks[1]])));
    }

    [Fact]
    public async Task TheSharedEquipment_IsPartOfTheDocument_AndComesBackWithIt()
    {
        await using var app = Create();
        app.Draft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 1)]);
        app.Draft.SharedGuider.Selected = null;
        app.Picker.SavePath = PathOf("Shared");
        await app.Document.SaveCommand.ExecuteAsync(null);

        await app.Document.NewCommand.ExecuteAsync(null);
        Assert.Equal(new DeviceId("guider.main"), app.Draft.SharedGuider.SelectedId); // a new session starts with the defaults
        await app.Open(PathOf("Shared.astraseq"));

        Assert.Equal(new DeviceId("mount.eq6"), app.Draft.SharedMount.SelectedId);
        Assert.Null(app.Draft.SharedGuider.SelectedId);
        Assert.False(app.Document.IsDirty);
    }

    [Fact]
    public async Task ADocumentWithASharedDeviceThatIsNotHere_LoadsAndStaysRepairable()
    {
        await using var app = Create();
        await SequenceDocumentStore.CreateDefault().SaveAsync(
            PathOf("Obs.astraseq"),
            new SequenceDocument(null, [new DelayDocumentStep(A, 1)], new SharedEquipmentDocument("mount.observatory", "guider.main")));

        await app.Open(PathOf("Obs.astraseq"));

        Assert.Equal(new DeviceId("mount.observatory"), app.Draft.SharedMount.SelectedId);
        Assert.True(app.Draft.SharedMount.Selected!.IsMissing);
        Assert.Equal(["The shared mount 'mount.observatory' is not available."], app.Draft.SharedProblems);
        Assert.False(app.Draft.IsValid);
        Assert.False(app.Vm.Sequencer.CanRun);

        app.Draft.SharedMount.Selected = app.Draft.SharedMount.Options.Single(o => o.IdText == "mount.eq6");

        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
    }
}

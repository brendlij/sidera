using System.Globalization;
using System.Text;
using System.Text.Json;
using Sidera.Desktop.Documents;

namespace Sidera.Desktop.Tests.Documents;

public class SequenceDocumentSerializerTests
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");
    private static readonly Guid E = Guid.Parse("00000000-0000-0000-0000-00000000000e");

    private static async Task<string> Write(SequenceDocument document)
    {
        using var stream = new MemoryStream();
        await Serializer.SaveAsync(stream, document, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task<SequenceDocument> Read(string text) => Serializer.LoadAsync(
        new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private static SequenceDocument Single(DocumentStep step) => new(null, [step]);

    // Every kind of step once, a Repeat with all leaf kinds in it, and leaf steps around it.
    private static SequenceDocument MixedDocument() => new("M42 Session",
    [
        new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        new SlewDocumentStep(Guid.NewGuid(), "mount.eq6", 5.588, -5.39),
        new RepeatDocumentStep(Guid.NewGuid(), 5,
        [
            new ExposureDocumentStep(Guid.NewGuid(), "camera.main", 300),
            new DelayDocumentStep(Guid.NewGuid(), 2.5),
            new DitherDocumentStep(Guid.NewGuid(), "guider.main", "mount.eq6", "camera.main", 1.5, 0.5, 1, 10),
        ]),
        new RepeatDocumentStep(Guid.NewGuid(), 2,
        [
            new StopGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
            new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        ]),
        new StopGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
    ]);

    // Round trip

    public static TheoryData<DocumentStep> Leaves => new()
    {
        new ExposureDocumentStep(Guid.NewGuid(), "camera.observatory", 300.25),
        new DelayDocumentStep(Guid.NewGuid(), 0.001),
        new SlewDocumentStep(Guid.NewGuid(), "mount.eq6", 23.999, -89.5),
        new StartGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        new StopGuidingDocumentStep(Guid.NewGuid(), "guider.main"),
        new DitherDocumentStep(Guid.NewGuid(), "guider.main", "mount.eq6", "camera.main", 1.5, 0.25, 0.75, 12.5),
    };

    [Theory]
    [MemberData(nameof(Leaves))]
    public async Task EveryLeafStep_SurvivesAWriteAndARead_WithItsIdAndValues(DocumentStep step)
    {
        var loaded = await Read(await Write(Single(step)));

        var read = Assert.Single(loaded.Steps);
        Assert.Equal(step, read);   // records of values: the id, the devices and every number
        Assert.NotSame(step, read); // an object graph of its own
    }

    [Fact]
    public async Task ADeviceThatIsNotSelected_SurvivesAsNull()
    {
        var step = new DitherDocumentStep(Guid.NewGuid(), null, "mount.eq6", null, 1, 0.5, 1, 10);

        var loaded = await Read(await Write(Single(step)));

        Assert.Equal(step, Assert.Single(loaded.Steps));
    }

    [Fact]
    public async Task ARepeat_KeepsItsIdItsCountAndTheOrderOfItsChildren()
    {
        var children = new DocumentLeafStep[]
        {
            new DelayDocumentStep(A, 1), new ExposureDocumentStep(B, "camera.main", 2), new StartGuidingDocumentStep(C, "guider.main"),
        };
        var repeat = new RepeatDocumentStep(D, 7, children);

        var loaded = await Read(await Write(Single(repeat)));

        var read = Assert.IsType<RepeatDocumentStep>(Assert.Single(loaded.Steps));
        Assert.Equal(D, read.Id);
        Assert.Equal(7, read.Count);
        Assert.Equal(children, read.Children);
    }

    [Fact]
    public async Task AMixedDocument_KeepsNameOrderIdsAndValues_AndWritesIdenticallyAgain()
    {
        var document = MixedDocument();

        var text = await Write(document);
        var loaded = await Read(text);

        Assert.Equal("M42 Session", loaded.Name);
        Assert.Equal(document.Steps.Count, loaded.Steps.Count);
        Assert.Equal(document.Steps.Select(s => s.Id), loaded.Steps.Select(s => s.Id));
        Assert.Equal(document.Steps.Select(s => s.GetType()), loaded.Steps.Select(s => s.GetType()));
        for (var i = 0; i < document.Steps.Count; i++)
        {
            Assert.NotSame(document.Steps[i], loaded.Steps[i]);
        }

        // Everything, nested steps included, is the same if it is written out the same.
        Assert.Equal(text, await Write(loaded));
    }

    [Fact]
    public async Task AnEmptySequence_AndADocumentWithoutAName_AreValid()
    {
        var loaded = await Read(await Write(new SequenceDocument(null, [])));

        Assert.Null(loaded.Name);
        Assert.Empty(loaded.Steps);
    }

    [Fact]
    public async Task ARepeatWithoutChildren_AndOfCountZero_AreLoaded_BecauseWhetherTheyMakeSenseIsForTheEditor()
    {
        var loaded = await Read(await Write(new SequenceDocument(null, [new RepeatDocumentStep(A, 0, [])])));

        var repeat = Assert.IsType<RepeatDocumentStep>(Assert.Single(loaded.Steps));
        Assert.Equal(0, repeat.Count);
        Assert.Empty(repeat.Children);
    }

    [Fact]
    public async Task UnusualButValidNumbers_AndNames_SurviveExactly()
    {
        var step = new ExposureDocumentStep(A, "camera.main", 0.1 + 0.2);

        var loaded = await Read(await Write(new SequenceDocument("Nébuleuse d'Orion – 星雲", [step])));

        Assert.Equal("Nébuleuse d'Orion – 星雲", loaded.Name);
        Assert.Equal(0.1 + 0.2, Assert.IsType<ExposureDocumentStep>(Assert.Single(loaded.Steps)).ExposureSeconds);
    }

    // The wire contract of version 1

    [Fact]
    public async Task TheDocumentSaysWhatItIs_AndWhichVersion()
    {
        using var json = JsonDocument.Parse(await Write(MixedDocument()));
        var root = json.RootElement;

        Assert.Equal("astra-sequence", root.GetProperty("format").GetString());
        Assert.Equal(6, root.GetProperty("version").GetInt32());
        Assert.Equal("M42 Session", root.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("steps").ValueKind);
        Assert.Equal(["format", "version", "name", "steps"], root.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task EveryStepKind_HasItsExplicitStableDiscriminator()
    {
        using var json = JsonDocument.Parse(await Write(MixedDocument()));
        var steps = json.RootElement.GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal(
            ["startGuiding", "slew", "repeat", "repeat", "stopGuiding"],
            steps.Select(s => s.GetProperty("type").GetString()));
        Assert.Equal(
            ["exposure", "delay", "dither"],
            steps[2].GetProperty("children").EnumerateArray().Select(s => s.GetProperty("type").GetString()));
        Assert.Equal(
            ["stopGuiding", "startGuiding"],
            steps[3].GetProperty("children").EnumerateArray().Select(s => s.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task EveryStep_HasExactlyTheDocumentedProperties_InADeterministicOrder()
    {
        var document = new SequenceDocument(null,
        [
            new ExposureDocumentStep(A, "camera.main", 300),
            new DelayDocumentStep(B, 10),
            new SlewDocumentStep(C, "mount.eq6", 5.588, -5.39),
            new StartGuidingDocumentStep(D, "guider.main"),
            new StopGuidingDocumentStep(E, null),
            new DitherDocumentStep(Guid.NewGuid(), "guider.main", "mount.eq6", "camera.main", 1.5, 0.5, 1, 10),
            new RepeatDocumentStep(Guid.NewGuid(), 3, []),
        ]);

        using var json = JsonDocument.Parse(await Write(document));
        var names = json.RootElement.GetProperty("steps").EnumerateArray()
            .Select(step => step.EnumerateObject().Select(p => p.Name).ToArray()).ToList();

        Assert.Equal(["type", "id", "cameraId", "exposureSeconds"], names[0]);
        Assert.Equal(["type", "id", "durationSeconds"], names[1]);
        Assert.Equal(["type", "id", "mountId", "raHours", "decDegrees"], names[2]);
        Assert.Equal(["type", "id", "guiderId"], names[3]);
        Assert.Equal(["type", "id", "guiderId"], names[4]);
        Assert.Equal(
            ["type", "id", "guiderId", "mountId", "cameraId", "amplitudePixels", "settleThresholdPixels", "settleStableSeconds", "settleTimeoutSeconds"],
            names[5]);
        Assert.Equal(["type", "id", "count", "children"], names[6]);
    }

    [Fact]
    public async Task Values_AreWrittenAsPlainJsonNumbersAndStrings_IdsInTheirUsualForm_NoDeviceAsNull()
    {
        var document = new SequenceDocument(null,
        [
            new ExposureDocumentStep(A, "camera.main", 300),
            new StopGuidingDocumentStep(B, null),
            new RepeatDocumentStep(C, 5, []),
        ]);

        using var json = JsonDocument.Parse(await Write(document));
        var steps = json.RootElement.GetProperty("steps").EnumerateArray().ToList();

        Assert.Equal("00000000-0000-0000-0000-00000000000a", steps[0].GetProperty("id").GetString());
        Assert.Equal("camera.main", steps[0].GetProperty("cameraId").GetString());
        Assert.Equal(JsonValueKind.Number, steps[0].GetProperty("exposureSeconds").ValueKind);
        Assert.Equal(300, steps[0].GetProperty("exposureSeconds").GetDouble());
        Assert.Equal(JsonValueKind.Null, steps[1].GetProperty("guiderId").ValueKind);
        Assert.Equal(JsonValueKind.Number, steps[2].GetProperty("count").ValueKind);
        Assert.Equal(5, steps[2].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task TheDocumentContainsNoTypeMetadata_NoClassNamesAndNoAssemblyNames()
    {
        var text = await Write(MixedDocument());

        Assert.DoesNotContain("$type", text, StringComparison.Ordinal);
        Assert.DoesNotContain("$id", text, StringComparison.Ordinal);
        foreach (var forbidden in new[]
                 {
                     "Sidera.Desktop", "DocumentStep", "StepDraft", "ViewModel", "Version=", "PublicKeyToken", "System.", ".dll",
                     "ExposureDocument", "RepeatDocument",
                 })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheDocumentIsIndented_ReadableAndWrittenTheSameEveryTime()
    {
        var document = MixedDocument();

        var first = await Write(document);
        var second = await Write(document);

        Assert.Equal(first, second);
        Assert.Contains("\n  \"steps\": [\n    {\n      \"type\": \"startGuiding\"", first, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
        Assert.EndsWith("\n", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumbersAreWrittenAndReadTheSameWhateverTheCultureOfTheUser()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var text = await Write(Single(new ExposureDocumentStep(A, "camera.main", 1.5)));

            Assert.Contains("\"exposureSeconds\": 1.5", text, StringComparison.Ordinal);
            Assert.Equal(1.5, Assert.IsType<ExposureDocumentStep>(Assert.Single((await Read(text)).Steps)).ExposureSeconds);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Header problems: is this an Sidera document at all, and of which version?

    private static string Header(string format = "\"astra-sequence\"", string version = "1") =>
        $"\"format\":{format},\"version\":{version}";

    private static string Doc(string steps, string? header = null) => "{" + (header ?? Header()) + $",\"steps\":[{steps}]" + "}";

    private static async Task<SequenceDocumentException> Rejects(string text)
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("   at ", ex.Message, StringComparison.Ordinal); // never a stack trace
        return ex;
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("null")]
    public async Task APayloadThatIsNotAnSideraDocumentAtAll_IsInvalid(string text)
    {
        var ex = await Rejects(text);

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
        Assert.Equal("Invalid Sidera sequence document.", ex.Message);
    }

    [Fact]
    public async Task AnEmptyStream_IsInvalid()
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(
            () => Serializer.LoadAsync(new MemoryStream(), CancellationToken.None));

        Assert.Equal("Invalid Sidera sequence document.", ex.Message);
    }

    [Fact]
    public async Task BinaryContent_IsInvalid()
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(
            () => Serializer.LoadAsync(new MemoryStream([0x50, 0x4B, 0x03, 0x04, 0xFF, 0x00]), CancellationToken.None));

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
    }

    [Theory]
    [InlineData("{\"version\":1,\"steps\":[]}")]
    [InlineData("{\"format\":\"sidera-session\",\"version\":1,\"steps\":[]}")]
    [InlineData("{\"format\":5,\"version\":1,\"steps\":[]}")]
    public async Task AnotherOrMissingFormat_IsRejectedAsNotAnAstraSequence(string text)
    {
        var ex = await Rejects(text);

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
        Assert.Equal("This file is not an Sidera sequence document.", ex.Message);
    }

    [Theory]
    [InlineData("{\"format\":\"astra-sequence\",\"steps\":[]}")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":0,\"steps\":[]}")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":-3,\"steps\":[]}")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":\"1\",\"steps\":[]}")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":1.5,\"steps\":[]}")]
    [InlineData("{\"format\":\"astra-sequence\",\"version\":null,\"steps\":[]}")]
    public async Task AMissingOrUnusableVersion_IsRejectedClearly(string text)
    {
        var ex = await Rejects(text);

        Assert.Equal(SequenceDocumentErrorKind.Container, ex.Kind);
        Assert.Contains("version", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("8")]
    [InlineData("999")]
    public async Task ANewerVersion_IsRejectedWithItsOwnMessage_WhateverElseIsInIt(string version)
    {
        // The steps need not be understood: a newer document is not read at all.
        var ex = await Rejects("{" + Header(version: version) + ",\"steps\":\"whatever\",\"future\":{}}");

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
        Assert.Equal("This sequence was created by a newer Sidera version.", ex.Message);
    }

    // Structure of version 1

    [Fact]
    public async Task MissingSteps_AreRejected()
    {
        var ex = await Rejects("{" + Header() + "}");

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.Equal("The sequence has no list of steps.", ex.Message);
    }

    [Fact]
    public async Task StepsThatAreNotAList_AreRejected()
    {
        await Rejects("{" + Header() + ",\"steps\":{}}");
        await Rejects("{" + Header() + ",\"steps\":\"x\"}");
    }

    [Theory]
    [InlineData("autofocus")]
    [InlineData("group")]
    [InlineData("parallel")]
    [InlineData("safePoint")]
    [InlineData("Exposure")]
    [InlineData("")]
    public async Task AnUnknownStepType_IsRejected_AndNamed(string type)
    {
        var ex = await Rejects(Doc($"{{\"type\":\"{type}\",\"id\":\"{A}\"}}"));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.Equal($"Unknown sequence step type '{type}'.", ex.Message);
    }

    [Theory]
    [InlineData("{\"id\":\"00000000-0000-0000-0000-00000000000a\"}")]
    [InlineData("{\"type\":7,\"id\":\"00000000-0000-0000-0000-00000000000a\"}")]
    [InlineData("\"exposure\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task AStepWithoutAType_OrThatIsNoObject_IsRejected(string step)
    {
        var ex = await Rejects(Doc(step));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
    }

    [Theory]
    [InlineData("\"not-a-guid\"")]
    [InlineData("\"\"")]
    [InlineData("\"0000000000000000000000000000000a\"")]       // a Guid, but not in the written form
    [InlineData("\"{00000000-0000-0000-0000-00000000000a}\"")]
    [InlineData("12")]
    [InlineData("null")]
    public async Task AMalformedId_IsRejected_NotReplaced(string id)
    {
        var ex = await Rejects(Doc($"{{\"type\":\"delay\",\"id\":{id},\"durationSeconds\":1}}"));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.StartsWith("Invalid sequence step ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingId_IsRejected()
    {
        var ex = await Rejects(Doc("{\"type\":\"delay\",\"durationSeconds\":1}"));

        Assert.Equal("A 'delay' step is missing 'id'.", ex.Message);
    }

    private static string Delay(Guid id) => $"{{\"type\":\"delay\",\"id\":\"{id}\",\"durationSeconds\":1}}";

    private static string Repeat(Guid id, params string[] children) =>
        $"{{\"type\":\"repeat\",\"id\":\"{id}\",\"count\":2,\"children\":[{string.Join(",", children)}]}}";

    [Fact]
    public async Task TheSameIdTwiceAtTheTopLevel_IsRejected()
    {
        var ex = await Rejects(Doc(Delay(A) + "," + Delay(A)));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.Equal("Duplicate sequence step ID.", ex.Message);
    }

    [Fact]
    public async Task TheIdOfARepeatUsedByOneOfItsChildren_IsRejected()
    {
        var ex = await Rejects(Doc(Repeat(A, Delay(A))));

        Assert.Equal("Duplicate sequence step ID.", ex.Message);
    }

    [Fact]
    public async Task TheIdOfATopLevelStepUsedInsideARepeat_AndTheOtherWayRound_IsRejected()
    {
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(Delay(A) + "," + Repeat(B, Delay(A))))).Message);
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(Repeat(B, Delay(A)) + "," + Delay(A)))).Message);
    }

    [Fact]
    public async Task TheSameIdInTwoChildrenOfOneRepeat_AndInDifferentRepeats_IsRejected()
    {
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(Repeat(A, Delay(C), Delay(C))))).Message);
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(Repeat(A, Delay(C)) + "," + Repeat(B, Delay(C))))).Message);
    }

    [Fact]
    public async Task TheSameIdTwiceAsRepeats_IsRejected()
    {
        Assert.Equal("Duplicate sequence step ID.", (await Rejects(Doc(Repeat(A, Delay(C)) + "," + Repeat(A, Delay(D))))).Message);
    }

    [Fact]
    public async Task ARepeatInsideARepeat_IsRejected()
    {
        var ex = await Rejects(Doc(Repeat(A, Repeat(B, Delay(C)))));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.Equal("Repeat steps cannot contain another Repeat.", ex.Message);
    }

    [Fact]
    public async Task AnUnknownContainerInsideARepeat_IsRejected()
    {
        var ex = await Rejects(Doc(Repeat(A, $"{{\"type\":\"group\",\"id\":\"{B}\",\"children\":[]}}")));

        Assert.Equal("Unknown sequence step type 'group'.", ex.Message);
    }

    [Fact]
    public async Task ALeafStepThatHasChildren_IsRejected_BecauseThatWouldChangeWhatItIs()
    {
        var ex = await Rejects(Doc($"{{\"type\":\"delay\",\"id\":\"{A}\",\"durationSeconds\":1,\"children\":[]}}"));

        Assert.Equal("A 'delay' step cannot contain other steps.", ex.Message);
    }

    [Theory]
    [InlineData("exposure", "{\"type\":\"exposure\",\"id\":\"ID\",\"cameraId\":\"camera.main\"}", "exposureSeconds")]
    [InlineData("exposure", "{\"type\":\"exposure\",\"id\":\"ID\",\"exposureSeconds\":1}", "cameraId")]
    [InlineData("delay", "{\"type\":\"delay\",\"id\":\"ID\"}", "durationSeconds")]
    [InlineData("slew", "{\"type\":\"slew\",\"id\":\"ID\",\"mountId\":null,\"raHours\":1}", "decDegrees")]
    [InlineData("slew", "{\"type\":\"slew\",\"id\":\"ID\",\"mountId\":null,\"decDegrees\":1}", "raHours")]
    [InlineData("startGuiding", "{\"type\":\"startGuiding\",\"id\":\"ID\"}", "guiderId")]
    [InlineData("stopGuiding", "{\"type\":\"stopGuiding\",\"id\":\"ID\"}", "guiderId")]
    [InlineData("dither", "{\"type\":\"dither\",\"id\":\"ID\",\"guiderId\":null,\"mountId\":null,\"cameraId\":null,\"amplitudePixels\":1,\"settleThresholdPixels\":1,\"settleStableSeconds\":1}", "settleTimeoutSeconds")]
    [InlineData("repeat", "{\"type\":\"repeat\",\"id\":\"ID\",\"children\":[]}", "count")]
    public async Task AMissingRequiredField_IsRejected_AndNamed(string type, string step, string field)
    {
        var ex = await Rejects(Doc(step.Replace("ID", A.ToString())));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
        Assert.Equal($"A '{type}' step is missing '{field}'.", ex.Message);
    }

    [Fact]
    public async Task ARepeatWithoutItsListOfChildren_IsRejected()
    {
        var ex = await Rejects(Doc($"{{\"type\":\"repeat\",\"id\":\"{A}\",\"count\":2}}"));

        Assert.Equal("A 'repeat' step is missing its list of 'children'.", ex.Message);
    }

    [Theory]
    [InlineData("\"300\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public async Task ANumberThatIsNoNumber_IsRejected(string value)
    {
        var ex = await Rejects(Doc($"{{\"type\":\"exposure\",\"id\":\"{A}\",\"cameraId\":null,\"exposureSeconds\":{value}}}"));

        Assert.Equal("'exposureSeconds' of a 'exposure' step must be a number.", ex.Message);
    }

    [Fact]
    public async Task ANumberTooLargeForADouble_IsRejected()
    {
        var ex = await Rejects(Doc($"{{\"type\":\"delay\",\"id\":\"{A}\",\"durationSeconds\":1e999}}"));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
    }

    [Theory]
    [InlineData("2.5")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    [InlineData("1e12")]
    [InlineData("true")]
    public async Task ARepeatCountThatIsNotAWholeNumber_IsRejected(string count)
    {
        var ex = await Rejects(Doc($"{{\"type\":\"repeat\",\"id\":\"{A}\",\"count\":{count},\"children\":[]}}"));

        Assert.Equal("'count' of a 'repeat' step must be a whole number.", ex.Message);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("true")]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("{}")]
    public async Task ADeviceThatIsNeitherAnIdNorNull_IsRejected(string device)
    {
        var ex = await Rejects(Doc($"{{\"type\":\"startGuiding\",\"id\":\"{A}\",\"guiderId\":{device}}}"));

        Assert.Equal("'guiderId' of a 'startGuiding' step must be a device ID or null.", ex.Message);
    }

    [Fact]
    public async Task ANameThatIsNotText_IsRejected()
    {
        var ex = await Rejects("{" + Header() + ",\"name\":5,\"steps\":[]}");

        Assert.Equal("The sequence name must be text.", ex.Message);
    }

    [Fact]
    public async Task UnknownAdditionalProperties_AreIgnoredOnPurpose_WhenTheyDoNotChangeWhatAStepIs()
    {
        var text = "{" + Header() + ",\"name\":null,\"notes\":\"hello\",\"steps\":["
            + $"{{\"type\":\"delay\",\"id\":\"{A}\",\"durationSeconds\":4,\"comment\":\"wait for the mount\"}}"
            + $",{{\"type\":\"repeat\",\"id\":\"{B}\",\"count\":2,\"label\":\"frames\",\"children\":[{Delay(C)}]}}]}}";

        var loaded = await Read(text);

        Assert.Equal(2, loaded.Steps.Count);
        Assert.Equal(new DelayDocumentStep(A, 4), loaded.Steps[0]);
        Assert.Equal(C, Assert.IsType<RepeatDocumentStep>(loaded.Steps[1]).Children[0].Id);
    }

    [Fact]
    public async Task ADeviceThatDoesNotExistAnywhere_IsStillJustAnId()
    {
        var loaded = await Read(Doc($"{{\"type\":\"exposure\",\"id\":\"{A}\",\"cameraId\":\"camera.observatory\",\"exposureSeconds\":300}}"));

        Assert.Equal("camera.observatory", Assert.IsType<ExposureDocumentStep>(Assert.Single(loaded.Steps)).CameraId);
    }

    [Fact]
    public async Task ASerializerRefusesToWriteAValueJsonCannotHold_AndWritesNothing()
    {
        using var stream = new MemoryStream();

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Serializer.SaveAsync(
            stream, Single(new DelayDocumentStep(A, double.NaN)), CancellationToken.None));

        Assert.Equal("The sequence contains a value that cannot be saved.", ex.Message);
        Assert.Equal(0, stream.Length);
    }

    // The example of the format

    [Fact]
    public async Task TheDocumentedExample_IsTheDocumentThatTheSerializerWrites()
    {
        var document = new SequenceDocument("Demo Session", SharedEquipment: new SharedEquipmentDocument("mount.eq6", "guider.main"), Steps:
        [
            new StartGuidingDocumentStep(Guid.Parse("11111111-1111-4111-8111-111111111111"), "guider.main"),
            new RepeatDocumentStep(Guid.Parse("22222222-2222-4222-8222-222222222222"), 3,
            [
                new ExposureDocumentStep(Guid.Parse("33333333-3333-4333-8333-333333333333"), "camera.main", 120),
                new DelayDocumentStep(Guid.Parse("44444444-4444-4444-8444-444444444444"), 2),
                new DitherDocumentStep(
                    Guid.Parse("55555555-5555-4555-8555-555555555555"), "guider.main", "mount.eq6", "camera.main", 1.5, 0.5, 1, 10),
            ]),
            new StopGuidingDocumentStep(Guid.Parse("66666666-6666-4666-8666-666666666666"), "guider.main"),
        ]);

        var text = await Write(document);

        Assert.Equal(
            """
            {
              "format": "astra-sequence",
              "version": 6,
              "name": "Demo Session",
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
                  "type": "repeat",
                  "id": "22222222-2222-4222-8222-222222222222",
                  "count": 3,
                  "children": [
                    {
                      "type": "exposure",
                      "id": "33333333-3333-4333-8333-333333333333",
                      "cameraId": "camera.main",
                      "exposureSeconds": 120
                    },
                    {
                      "type": "delay",
                      "id": "44444444-4444-4444-8444-444444444444",
                      "durationSeconds": 2
                    },
                    {
                      "type": "dither",
                      "id": "55555555-5555-4555-8555-555555555555",
                      "guiderId": "guider.main",
                      "mountId": "mount.eq6",
                      "cameraId": "camera.main",
                      "amplitudePixels": 1.5,
                      "settleThresholdPixels": 0.5,
                      "settleStableSeconds": 1,
                      "settleTimeoutSeconds": 10
                    }
                  ]
                },
                {
                  "type": "stopGuiding",
                  "id": "66666666-6666-4666-8666-666666666666",
                  "guiderId": "guider.main"
                }
              ]
            }

            """.Replace("\r\n", "\n"),
            text);
        Assert.Equal(text, await Write(await Read(text)));
    }
}

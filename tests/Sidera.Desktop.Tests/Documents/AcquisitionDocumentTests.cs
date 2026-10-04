using System.Text;
using System.Text.Json;
using Sidera.Core.Devices;
using Sidera.Desktop.Documents;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>
/// Format version 6 adds the acquisition settings of an exposure: only what the exposure sets itself, in terms that stay valid
/// across cameras. Documents of older versions mean exactly what they did (everything inherited).
/// </summary>
public sealed class AcquisitionDocumentTests
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    private static async Task<string> Write(SequenceDocument document)
    {
        using var stream = new MemoryStream();
        await Serializer.SaveAsync(stream, document, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Task<SequenceDocument> Read(string text) =>
        Serializer.LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None);

    private static string Doc(int version, string steps) =>
        "{\"format\":\"astra-sequence\",\"version\":" + version + ",\"steps\":[" + steps + "]}";

    private static string Exposure(string acquisition) =>
        "{\"type\":\"exposure\",\"id\":\"" + A + "\",\"cameraId\":\"camera.main\",\"exposureSeconds\":300" + acquisition + "}";

    private static async Task<SequenceDocumentException> Rejects(string text)
    {
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        return ex;
    }

    [Fact]
    public async Task AnExposureThatSetsNothing_HasNoAcquisition_InTheFile()
    {
        var text = await Write(new SequenceDocument("x", [new ExposureDocumentStep(A, "camera.main", 300)]));

        Assert.DoesNotContain("acquisition", text, StringComparison.Ordinal);
        Assert.Equal(SequenceDocument.CurrentVersion, JsonDocument.Parse(text).RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task OnlyTheExplicitSettings_AreWritten_WithStableBackendNeutralNames()
    {
        var intent = new AcquisitionIntent
        {
            FrameType = FrameType.Dark, Gain = AcquisitionLevel.OfNumber(100), Offset = AcquisitionLevel.OfName("High"), BinX = 2, BinY = 2,
            Region = AcquisitionRegion.Of(10, 20, 300, 200), ReadoutMode = "Slow", FastReadout = true,
        };

        var text = await Write(new SequenceDocument("x", [new ExposureDocumentStep(A, "camera.main", 300, intent)]));

        using var json = JsonDocument.Parse(text);
        var acquisition = json.RootElement.GetProperty("steps")[0].GetProperty("acquisition");
        Assert.Equal("Dark", acquisition.GetProperty("frameType").GetString());
        Assert.Equal(100, acquisition.GetProperty("gain").GetInt32());
        Assert.Equal("High", acquisition.GetProperty("offset").GetString());
        Assert.Equal(2, acquisition.GetProperty("binning").GetProperty("x").GetInt32());
        Assert.Equal(300, acquisition.GetProperty("region").GetProperty("width").GetInt32());
        Assert.Equal("Slow", acquisition.GetProperty("readoutMode").GetString());
        Assert.True(acquisition.GetProperty("fastReadout").GetBoolean());
        // Nothing of ASCOM, of a driver or of a capability snapshot.
        Assert.DoesNotContain("ASCOM", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("progId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("StartX", text, StringComparison.Ordinal);
        Assert.DoesNotContain("capabilit", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASettingThatIsNotSet_IsNotWritten_SoItStaysInherited()
    {
        var text = await Write(new SequenceDocument("x", [new ExposureDocumentStep(A, "camera.main", 300, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(5) })]));

        var acquisition = JsonDocument.Parse(text).RootElement.GetProperty("steps")[0].GetProperty("acquisition");
        Assert.Equal(["gain"], acquisition.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task TheFullFrame_IsWrittenAsFullFrame_NotAsARectangle()
    {
        var text = await Write(new SequenceDocument("x", [new RigExposureDocumentStep(A, 60, new AcquisitionIntent { Region = AcquisitionRegion.Full })]));

        Assert.Equal(
            "fullFrame", JsonDocument.Parse(text).RootElement.GetProperty("steps")[0].GetProperty("acquisition").GetProperty("region").GetString());
    }

    [Fact]
    public async Task EveryAcquisitionComesBackAsItWasWritten_ForBothKindsOfExposure()
    {
        var full = new AcquisitionIntent
        {
            FrameType = FrameType.Flat, Gain = AcquisitionLevel.OfName("HDR"), Offset = AcquisitionLevel.OfNumber(3), BinX = 3, BinY = 1,
            Region = AcquisitionRegion.Full, ReadoutMode = "Normal", FastReadout = false,
        };
        var rigStep = new RigExposureDocumentStep(B, 60, new AcquisitionIntent { Region = AcquisitionRegion.Of(1, 2, 3, 4) });
        var track = new RigTrackDocument(Guid.Parse("00000000-0000-0000-0000-0000000000c1"), "rig.main", [rigStep]);
        var steps = new DocumentStep[]
        {
            new ExposureDocumentStep(A, "camera.main", 300, full),
            new MultiRigDocumentStep(Guid.Parse("00000000-0000-0000-0000-0000000000c2"), [track]),
        };

        var back = await Read(await Write(new SequenceDocument("x", steps)));

        Assert.Equal(full, ((ExposureDocumentStep)back.Steps[0]).Acquisition);
        var readTrack = ((MultiRigDocumentStep)back.Steps[1]).Tracks[0];
        Assert.Equal(AcquisitionRegion.Of(1, 2, 3, 4), ((RigExposureDocumentStep)readTrack.Steps[0]).Acquisition!.Region);
    }

    [Fact]
    public async Task AnOlderDocument_MeansExactlyWhatItDid_EverythingInherited()
    {
        var version5 = await Read(Doc(5, Exposure(string.Empty)));
        // A version 5 document does not know "acquisition": it is not read from it, so an older file means what it meant.
        var withIgnored = await Read(Doc(5, Exposure(",\"acquisition\":{\"gain\":1}")));

        Assert.Null(((ExposureDocumentStep)version5.Steps[0]).Acquisition);
        Assert.Null(((ExposureDocumentStep)withIgnored.Steps[0]).Acquisition);
    }

    [Fact]
    public async Task AVersion6Document_ReadsTheAcquisition()
    {
        var read = await Read(Doc(6, Exposure(",\"acquisition\":{\"gain\":\"High\",\"binning\":{\"x\":2},\"region\":\"fullFrame\"}")));

        var acquisition = ((ExposureDocumentStep)read.Steps[0]).Acquisition!;
        Assert.Equal("High", acquisition.Gain!.Name);
        Assert.Equal((2, (int?)null), (acquisition.BinX, acquisition.BinY));
        Assert.True(acquisition.Region!.IsFullFrame);
    }

    [Theory]
    [InlineData(",\"acquisition\":5")]
    [InlineData(",\"acquisition\":{\"frameType\":\"Sparkle\"}")]
    [InlineData(",\"acquisition\":{\"gain\":true}")]
    [InlineData(",\"acquisition\":{\"gain\":1.5}")]
    [InlineData(",\"acquisition\":{\"binning\":{\"x\":0}}")]
    [InlineData(",\"acquisition\":{\"binning\":3}")]
    [InlineData(",\"acquisition\":{\"region\":{\"x\":0,\"y\":0,\"width\":0,\"height\":5}}")]
    [InlineData(",\"acquisition\":{\"region\":{\"x\":0,\"y\":0,\"width\":5}}")]
    [InlineData(",\"acquisition\":{\"region\":\"half\"}")]
    [InlineData(",\"acquisition\":{\"readoutMode\":3}")]
    [InlineData(",\"acquisition\":{\"fastReadout\":\"yes\"}")]
    public async Task AnAcquisitionThatIsNotUnderstood_IsAnErrorOfTheFile_NeverAGuess(string acquisition)
    {
        var ex = await Rejects(Doc(6, Exposure(acquisition)));

        Assert.Equal(SequenceDocumentErrorKind.Structure, ex.Kind);
    }

    [Fact]
    public async Task ANewerVersion_IsStillRejectedAsNewer()
    {
        var ex = await Rejects(Doc(8, Exposure(string.Empty)));

        Assert.Equal(SequenceDocumentErrorKind.NewerVersion, ex.Kind);
    }

    [Fact]
    public async Task ADocumentWrittenForOneCamera_CarriesNoCameraKnowledge_SoItOpensWithAnother()
    {
        var text = await Write(new SequenceDocument("x", [new ExposureDocumentStep(A, "camera.other", 300, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(300) })]));

        var back = await Read(text);

        // Not coerced to anything: the value is what was written, and the camera that runs it decides.
        Assert.Equal(300, ((ExposureDocumentStep)back.Steps[0]).Acquisition!.Gain!.Number);
    }
}

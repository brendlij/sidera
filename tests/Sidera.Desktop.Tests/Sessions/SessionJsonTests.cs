using System.Text;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using DeviceOperation = Sidera.Runtime.Sequencing.DeviceOperation;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>The session in a version 9 file: every action, every field, the same text when it is read and written again, and nothing accepted that is not understood.</summary>
public sealed class SessionJsonTests
{
    private static readonly JsonSequenceDocumentSerializer Serializer = new();

    private static async Task<string> Write(SequenceDocument document)
    {
        using var stream = new MemoryStream();
        await Serializer.SaveAsync(stream, document, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<SequenceDocument> Read(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await Serializer.LoadAsync(stream, CancellationToken.None);
    }

    private static SessionDefinition EverythingSession()
    {
        var dawn = new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn);
        var actions = new SessionAction[]
        {
            new ExposureAction(Guid.NewGuid(), 180.5), new SetFilterAction(Guid.NewGuid(), 2), new AutofocusAction(Guid.NewGuid(), new FocusSettings(2, 300, 9)),
            new MoveFocuserAction(Guid.NewGuid(), 1234), new WaitAction(Guid.NewGuid(), 12), new WaitUntilAction(Guid.NewGuid(), [dawn, new TimeCondition(null, new TimeOnly(4, 30), "UTC")]),
            new StartGuidingAction(Guid.NewGuid()), new StopGuidingAction(Guid.NewGuid()), new DitherNowAction(Guid.NewGuid(), new DitherSettings(3, 0.4, 5, 30)),
            new SlewAction(Guid.NewGuid()), new SlewAndCenterAction(Guid.NewGuid(), 30, 4, 3), new PlateSolveAction(Guid.NewGuid(), 7),
            new CenterAndRotateAction(Guid.NewGuid(), 30, 4, 3), new SyncMountAction(Guid.NewGuid()), new CoolCameraAction(Guid.NewGuid(), -15, 6), new WarmCameraAction(Guid.NewGuid(), 12),
            new ParkAction(Guid.NewGuid()), new UnparkAction(Guid.NewGuid()), new SetTrackingAction(Guid.NewGuid(), false),
        };
        var block = new SequenceBlock(
            Guid.NewGuid(), "Ha", true, actions.Take(6).ToList(), new RepeatRule(40, [new DurationCondition(TimeSpan.FromHours(4)), dawn]),
            new BlockAutomation(new DitherAutomation(3, new DitherSettings(2, 0.5, 3, 40)), new FocusAutomation(true, 60, true, new FocusSettings(2, 300, 9))),
            [new TargetAltitudeCondition(25, ThresholdDirection.Below), dawn]);
        var plain = SequenceBlock.Imaging(null, null, 60, 5);
        var target = new SessionTarget(
            Guid.NewGuid(), "M42", 5.588, -5.39, 90, true, actions.Skip(6).Take(8).ToList(), [Lane(MainPath, block), Lane(null, plain)], [dawn]);
        var off = SessionTarget.New("M31", 0.712, 41.27) with { Enabled = false };
        return new SessionDefinition(
            new SessionAutomation(new MeridianFlipSettings { Enabled = true, AutofocusAfterFlip = true, PauseBeforeMeridianMinutes = 7 }),
            actions.Skip(14).Take(3).ToList(), [target, off], actions.Skip(6).Take(2).Select(a => a with { Id = Guid.NewGuid(), Enabled = false, Setup = WidePath }).ToList());
    }

    [Fact]
    public async Task ASession_IsWrittenInVersion9_AndReadBackAsTheSameText()
    {
        var document = new SequenceDocument("Night", [], null, null, EverythingSession());

        var text = await Write(document);
        var back = await Read(text);

        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.Contains("\"session\"", text, StringComparison.Ordinal);
        Assert.Equal(text, await Write(back));
        var session = back.Session!;
        Assert.Equal(2, session.Targets.Count);
        var block = session.Targets[0].Lanes[0].Blocks[0];
        Assert.Equal(("Ha", 40), (block.Name, block.Repeat.Count));
        Assert.Equal(2, block.Repeat.Until.Count);
        Assert.Equal(3, block.Automation.Dither!.EveryFrames);
        Assert.Equal(60, block.Automation.Focus!.EveryMinutes);
        Assert.Equal(MainPath, session.Targets[0].Lanes[0].Setup);
        Assert.Null(session.Targets[0].Lanes[1].Setup);
        Assert.False(session.Targets[1].Enabled);
        Assert.False(session.End[0].Enabled);
        Assert.Equal(WidePath, session.End[0].Setup);
        Assert.Equal(7, session.Automation.Flip!.PauseBeforeMeridianMinutes);
    }

    [Fact]
    public async Task ASessionThatFollowsTheApplicationsFlip_SaysSo_AndCopiesNothingOfIt()
    {
        var document = new SequenceDocument("Night", [], null, null, SessionDefinition.Empty);

        var text = await Write(document);

        Assert.Contains("\"useDefaults\": true", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pauseBeforeMeridianMinutes", text, StringComparison.Ordinal);
        Assert.True((await Read(text)).Session!.Automation.UsesDefaultFlip);
    }

    [Fact]
    public async Task EveryActionOfTheLibrary_HasItsOwnStableTypeInTheFile()
    {
        var types = new Dictionary<SessionActionKind, string>
        {
            [SessionActionKind.Exposure] = "exposure", [SessionActionKind.SetFilter] = "setFilter", [SessionActionKind.Autofocus] = "autofocus", [SessionActionKind.MoveFocuser] = "moveFocuser",
            [SessionActionKind.Wait] = "wait", [SessionActionKind.WaitUntil] = "waitUntil", [SessionActionKind.StartGuiding] = "startGuiding", [SessionActionKind.StopGuiding] = "stopGuiding",
            [SessionActionKind.DitherNow] = "ditherNow", [SessionActionKind.Slew] = "slew", [SessionActionKind.SlewAndCenter] = "slewAndCenter", [SessionActionKind.PlateSolve] = "plateSolve",
            [SessionActionKind.CenterAndRotate] = "centerAndRotate", [SessionActionKind.SyncMount] = "syncMount", [SessionActionKind.CoolCamera] = "coolCamera",
            [SessionActionKind.WarmCamera] = "warmCamera", [SessionActionKind.Park] = "park", [SessionActionKind.Unpark] = "unpark", [SessionActionKind.SetTracking] = "setTracking",
        };

        Assert.Equal(types.Keys.Order(), ActionCatalog.Entries.Select(e => e.Kind).Order()); // the library offers exactly the actions the file knows
        foreach (var info in ActionCatalog.Entries)
        {
            var text = await Write(new SequenceDocument("x", [], null, null, SessionDefinition.Empty with { Start = [info.Create(Guid.NewGuid())] }));
            Assert.Contains($"\"type\": \"{types[info.Kind]}\"", text, StringComparison.Ordinal);
            var back = (await Read(text)).Session!.Start.Single();
            Assert.Equal(info.Kind, back.Kind);
        }
    }

    [Fact]
    public async Task ASessionIsOnlyReadFromAVersion9File()
    {
        var text = (await Write(new SequenceDocument("x", [], null, null, SessionDefinition.Empty))).Replace("\"version\": 9", "\"version\": 8", StringComparison.Ordinal);

        Assert.Null((await Read(text)).Session);
    }

    [Fact]
    public async Task WhatIsNotUnderstood_IsRefused_NotIgnored()
    {
        var good = await Write(new SequenceDocument("x", [], null, null, SessionDefinition.Empty with { Start = [new WaitAction(Guid.NewGuid(), 5)] }));

        var unknown = good.Replace("\"type\": \"wait\"", "\"type\": \"juggle\"", StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(unknown));
        Assert.Contains("unknown action 'juggle'", ex.Message, StringComparison.Ordinal);

        var noSeconds = good.Replace("\"seconds\": 5", "\"seconds\": \"five\"", StringComparison.Ordinal);
        await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(noSeconds));
    }

    [Fact]
    public async Task TwoElementsWithOneId_AreRefused()
    {
        var id = Guid.NewGuid();
        var text = await Write(new SequenceDocument("x", [], null, null, SessionDefinition.Empty with { Start = [new WaitAction(id, 5)], End = [new WaitAction(Guid.NewGuid(), 5)] }));
        var twice = text.Replace(text[(text.LastIndexOf("\"id\": \"", StringComparison.Ordinal) + 7)..][..36], id.ToString("D"), StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(twice));

        Assert.Contains("same id", ex.Message, StringComparison.Ordinal);
    }

    // ---- the device operations as steps of a tree document

    [Fact]
    public async Task ADeviceOperationStep_SurvivesAWriteAndARead_AndIsAStepOfVersion9Only()
    {
        var steps = new SequenceStepDraft[]
        {
            new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.CoolCamera, new DeviceId("camera.a"), -12.5, 7),
            new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.WarmCamera, new DeviceId("camera.a"), 0, 9),
            new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.Park, new DeviceId("mount.1")),
            new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.TrackingOff, null),
        };

        var text = await Write(SequenceDocumentMapper.ToDocument(steps, "ops"));
        var back = await Read(text);

        Assert.Equal(steps, SequenceDocumentMapper.ToDrafts(back));
        Assert.Contains("\"type\": \"deviceOperation\"", text, StringComparison.Ordinal);

        var old = text.Replace("\"version\": 9", "\"version\": 8", StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(old));
        Assert.Contains("Unknown sequence step type 'deviceOperation'", ex.Message, StringComparison.Ordinal);

        var wrong = text.Replace("coolCamera", "boil", StringComparison.Ordinal);
        await Assert.ThrowsAsync<SequenceDocumentException>(() => Read(wrong));
    }
}

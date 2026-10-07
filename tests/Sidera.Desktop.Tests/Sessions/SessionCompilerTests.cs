using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using DeviceOperation = Sidera.Runtime.Sequencing.DeviceOperation;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>What a session compiles to: the steps of the runtime, with the devices of the setup of each lane, nothing guessed, and the same ids every time.</summary>
public sealed class SessionCompilerTests : IAsyncLifetime
{
    private readonly List<SessionFixture> _fixtures = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var fixture in _fixtures)
        {
            await fixture.DisposeAsync();
        }
    }

    private SessionFixture Equipment(bool second = false, bool sharedMount = true, bool withRotator = false, bool mainHasGuider = true, bool mainHasFocuser = true)
    {
        var fixture = Create(second, sharedMount, withRotator, mainHasGuider: mainHasGuider, mainHasFocuser: mainHasFocuser);
        _fixtures.Add(fixture);
        return fixture;
    }

    private static SessionCompilation Compile(SessionFixture equipment, SessionDefinition session, MeridianFlipSettings? applicationFlip = null) =>
        SessionCompiler.Compile(session, equipment.Catalog, applicationFlip);

    private static List<MultiRigStepDraft> Imaging(SessionCompilation compilation) => compilation.Steps.OfType<MultiRigStepDraft>().ToList();

    private static string Describe(SequenceStepDraft step) => step switch
    {
        RigChangeFilterStepDraft f => $"filter {f.SlotIndex}",
        RigExposureStepDraft e => $"exposure {e.Seconds}",
        RigAutofocusStepDraft => "autofocus",
        DelayStepDraft d => $"wait {d.Seconds}",
        WaitUntilStepDraft => "wait until",
        RepeatStepDraft r => $"repeat {r.Count} [{string.Join(", ", r.Children.Select(Describe))}]",
        _ => step.GetType().Name,
    };

    private static List<string> Track(RigTrackDraft track) => track.Steps.Select(Describe).ToList();

    // ---- blocks

    [Fact]
    public void ABlock_IsItsActionsInOrder_ThenARepeatOfTheExposure()
    {
        var equipment = Equipment();
        var compilation = Compile(equipment, Session(Target("M42", [Lane(null, Block(1, 60, 10), Block(2, 30, 5))])));

        Assert.True(compilation.IsValid, string.Join(" ", compilation.Problems.Select(p => p.Message)));
        var track = Assert.Single(Assert.Single(Imaging(compilation)).Tracks);
        Assert.Equal(new RigId("rig.main"), track.RigId);
        Assert.Equal(["filter 1", "repeat 10 [exposure 60]", "filter 2", "repeat 5 [exposure 30]"], Track(track));
    }

    [Fact]
    public void ActionsBeforeTheFirstExposureRunOnce_TheOnesFromItOnRepeat()
    {
        var equipment = Equipment();
        var block = Block(null, 60, 4, b => b.Actions(
            new SetFilterAction(Guid.NewGuid(), 3), new WaitUntilAction(Guid.NewGuid(), [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)]),
            new ExposureAction(Guid.NewGuid(), 60), new WaitAction(Guid.NewGuid(), 2)));

        var compilation = Compile(equipment, Session(Target("T", [Lane(null, block)])));

        Assert.Equal(["filter 3", "wait until", "repeat 4 [exposure 60, wait 2]"], Track(Imaging(compilation)[0].Tracks[0]));
    }

    [Fact]
    public void AFilterThatIsAlreadySet_IsNotTurnedAgain_ByTheNextBlock()
    {
        var equipment = Equipment();
        var compilation = Compile(equipment, Session(Target("T", [Lane(null, Block(1, 60, 3), Block(1, 120, 3), Block(2, 60, 3))])));

        Assert.Equal(["filter 1", "repeat 3 [exposure 60]", "repeat 3 [exposure 120]", "filter 2", "repeat 3 [exposure 60]"], Track(Imaging(compilation)[0].Tracks[0]));
    }

    [Fact]
    public void ADisabledBlock_IsLeftOut_AndNothingOfItIsCompiled()
    {
        var equipment = Equipment();
        var compilation = Compile(equipment, Session(Target("T", [Lane(null, Block(1, 60, 3), Block(2, 60, 3, b => b.Disabled()))])));

        Assert.Equal(["filter 1", "repeat 3 [exposure 60]"], Track(Imaging(compilation)[0].Tracks[0]));
    }

    [Fact]
    public void TheRepeat_IsACount_OrConditions_OrBoth_AndTheLimitsOfTheBlockEndItToo()
    {
        var equipment = Equipment();
        var dawn = new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn);
        var altitude = new TargetAltitudeCondition(25, ThresholdDirection.Below);
        var hours = new DurationCondition(TimeSpan.FromHours(4));
        var session = Session(Target("T", [Lane(null,
            Block(null, 60, 10, b => b.Limits(dawn)),
            Block(null, 60, 1, b => b.Repeat(new RepeatRule(null, [hours])).Limits(altitude)),
            Block(null, 60, 1, b => b.Repeat(new RepeatRule(20, [hours]))))]));

        var repeats = Compile(equipment, session).Steps.OfType<MultiRigStepDraft>().Single().Tracks[0].Steps.OfType<RepeatStepDraft>().ToList();

        Assert.Equal(10, repeats[0].Count);
        Assert.Equal([dawn], repeats[0].Stop!.Any);
        Assert.Equal(SessionCompiler.OpenEndedRepeat, repeats[1].Count); // only the conditions end it
        Assert.Equal([hours, altitude], repeats[1].Stop!.Any);
        Assert.Equal(20, repeats[2].Count); // a count and a condition: whichever comes first
        Assert.Equal([hours], repeats[2].Stop!.Any);
    }

    [Fact]
    public void ABlockThatCannotImage_IsToldWhy_WhereItIs()
    {
        var equipment = Equipment();
        var noExposure = Block(null, 60, 3, b => b.Actions(new SetFilterAction(Guid.NewGuid(), 1)));
        var noEnd = Block(null, 60, 3, b => b.Repeat(new RepeatRule(null, [])));
        var compilation = Compile(equipment, Session(Target("T", [Lane(null, noExposure, noEnd)])));

        Assert.Contains(compilation.Problems, p => p.ElementId == noExposure.Id && p.Message.Contains("Add an Exposure", StringComparison.Ordinal));
        Assert.Contains(compilation.Problems, p => p.ElementId == noEnd.Id && p.Message.Contains("how often", StringComparison.Ordinal));
    }

    // ---- automation

    [Fact]
    public void Dither_IsLocalAutomation_CountedOnTheLaneThatAsks_AndTheMountAndGuiderComeFromItsSetup()
    {
        var equipment = Equipment(second: true);
        var session = Session(Target("T", [Lane(MainPath, Block(1, 60, 9, b => b.Dither(3))), Lane(WidePath, Block(null, 60, 9))]));

        var compilation = Compile(equipment, session);

        Assert.True(compilation.IsValid, string.Join(" ", compilation.Problems.Select(p => p.Message)));
        var policy = Imaging(compilation).Single().DitherPolicy!;
        Assert.True(policy.Enabled);
        Assert.Equal(new RigId("rig.main"), policy.TriggerRigId);
        Assert.Equal(3, policy.EveryNFrames);
        Assert.Equal(DitherSettings.Default.AmplitudePixels, policy.AmplitudePixels);
    }

    [Fact]
    public void TwoLanesThatDither_AreToldWhichOneCounts_AndAMountlessDitherIsAProblem()
    {
        var equipment = Equipment(second: true);
        var session = Session(Target("T", [Lane(MainPath, Block(null, 60, 9, b => b.Dither(3))), Lane(WidePath, Block(null, 60, 9, b => b.Dither(5)))]));

        var compilation = Compile(equipment, session);

        Assert.Contains(compilation.Notes, n => n.Contains("every 3 exposures of Main 750", StringComparison.Ordinal));

        var without = Equipment(mainHasGuider: false);
        var problem = Compile(without, Session(Target("T", [Lane(null, Block(null, 60, 9, b => b.Dither(3)))]))).Problems;
        Assert.Contains(problem, p => p.Message.Contains("Dither needs a guider", StringComparison.Ordinal));
    }

    [Fact]
    public void Autofocus_AtTheStartAndAfterAFilterChange_AreStepsWhereTheyHappen_AndEveryNMinutesIsTheClockOfTheLane()
    {
        var equipment = Equipment();
        var session = Session(Target("T", [Lane(null,
            Block(1, 60, 5, b => b.Focus(atStart: true, afterFilter: true, everyMinutes: 60)),
            Block(2, 60, 5, b => b.Focus(afterFilter: true, everyMinutes: 30)),
            Block(2, 90, 5, b => b.Focus(atStart: true)))]));

        var compilation = Compile(equipment, session);

        var track = Imaging(compilation).Single().Tracks[0];
        // Block 1: the filter was turned, the filter focus is there (the start focus would only repeat it). Block 2: turned again and focused. Block 3: same filter, so only the start focus.
        Assert.Equal(["filter 1", "autofocus", "repeat 5 [exposure 60]", "filter 2", "autofocus", "repeat 5 [exposure 60]", "autofocus", "repeat 5 [exposure 90]"], Track(track));
        Assert.Equal(30, track.AutofocusPolicy!.IntervalMinutes); // the shortest asked
        Assert.Contains(compilation.Notes, n => n.Contains("every 30", StringComparison.Ordinal));
    }

    [Fact]
    public void AutofocusAutomation_NeedsAFocuser_AndSaysSoAtTheBlock()
    {
        var equipment = Equipment(mainHasFocuser: false);
        var block = Block(null, 60, 5, b => b.Focus(atStart: true));

        var compilation = Compile(equipment, Session(Target("T", [Lane(null, block)])));

        Assert.Contains(compilation.Problems, p => p.ElementId == block.Id && p.Message.Contains("has no focuser", StringComparison.Ordinal));
    }

    // ---- lanes, setups, preparation

    [Fact]
    public void TwoLanesOnOneMount_ShareThePreparation_ItIsDoneOnceWithTheCoordinatesOfTheTarget()
    {
        var equipment = Equipment(second: true);
        var prepare = new List<SessionAction> { new SlewAndCenterAction(Guid.NewGuid()), new StartGuidingAction(Guid.NewGuid()) };
        var session = Session(Target("M42", [Lane(MainPath, Block(1, 60, 5)), Lane(WidePath, Block(null, 30, 5))], prepare));

        var compilation = Compile(equipment, session);

        Assert.True(compilation.IsValid, string.Join(" ", compilation.Problems.Select(p => p.Message)));
        var center = Assert.Single(compilation.Steps.OfType<SlewAndCenterStepDraft>()); // two setups, one mount
        Assert.Equal((5.588, -5.39, "M42"), (center.RightAscensionHours, center.DeclinationDegrees, center.TargetName));
        Assert.Equal(new RigId("rig.main"), center.RigId); // the first lane is the one whose camera solves
        Assert.Single(compilation.Steps.OfType<StartGuidingStepDraft>()); // one guider
        Assert.Equal(2, Imaging(compilation).Single().Tracks.Count);
        Assert.IsType<SlewAndCenterStepDraft>(compilation.Steps[0]);
        Assert.IsType<StartGuidingStepDraft>(compilation.Steps[1]);
        Assert.IsType<MultiRigStepDraft>(compilation.Steps[2]);
    }

    [Fact]
    public void SetupsOnIndependentMounts_AreCenteredEachOnItsOwn()
    {
        var equipment = Equipment(second: true, sharedMount: false);
        var session = Session(Target("T", [Lane(MainPath, Block(null, 60, 5)), Lane(WidePath, Block(null, 30, 5))], [new SlewAndCenterAction(Guid.NewGuid()), new StartGuidingAction(Guid.NewGuid())]));

        var compilation = Compile(equipment, session);

        Assert.Equal(2, compilation.Steps.OfType<SlewAndCenterStepDraft>().Count());
        Assert.Equal(2, compilation.Steps.OfType<StartGuidingStepDraft>().Count());
    }

    [Fact]
    public void ARotationOnTheTarget_CentersAndRotates_WhereTheSetupHasARotator()
    {
        var withRotator = Equipment(withRotator: true);
        var rotated = Compile(withRotator, Session(Target("T", [Lane(null, Block(null, 60, 5))], [new SlewAndCenterAction(Guid.NewGuid())], rotation: 90)));
        var center = Assert.Single(rotated.Steps.OfType<CenterAndRotateStepDraft>());
        Assert.Equal(90, center.SkyRotationDegrees);

        var plain = Equipment();
        var slewed = Compile(plain, Session(Target("T", [Lane(null, Block(null, 60, 5))], [new SlewAndCenterAction(Guid.NewGuid())], rotation: 90)));
        Assert.Equal(90, Assert.Single(slewed.Steps.OfType<SlewAndCenterStepDraft>()).DesiredRotationDegrees);

        var needs = Compile(plain, Session(Target("T", [Lane(null, Block(null, 60, 5))], [new CenterAndRotateAction(Guid.NewGuid())], rotation: 90)));
        Assert.Contains(needs.Problems, p => p.Message.Contains("has no rotator", StringComparison.Ordinal));
    }

    [Fact]
    public void WithOneSetup_ALaneNeedsNoBinding_WithSeveral_ItIsNeverGuessed()
    {
        var one = Equipment();
        Assert.True(Compile(one, Session(Target("T", [Lane(null, Block(null, 60, 5))]))).IsValid);

        var two = Equipment(second: true);
        var lane = Lane(null, Block(null, 60, 5));
        var problems = Compile(two, Session(Target("T", [lane]))).Problems;
        var problem = Assert.Single(problems);
        Assert.Equal(lane.Id, problem.ElementId);
        Assert.Contains("several imaging setups", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneOfASetupThatIsNotThere_IsReported_NotGivenToAnotherSetup()
    {
        var equipment = Equipment(second: true);
        var gone = ImagingBindingId.For(new Sidera.Core.Devices.DeviceId("camera.gone"));
        var lane = Lane(gone, Block(null, 60, 5));

        var compilation = Compile(equipment, Session(Target("T", [lane, Lane(MainPath, Block(null, 60, 5))])));

        var problem = Assert.Single(compilation.Problems);
        Assert.Equal(lane.Id, problem.ElementId);
        Assert.Contains("camera 'camera.gone'", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaneThatNamesAnExplicitSetupById_StillResolves()
    {
        var equipment = Equipment();
        var compilation = Compile(equipment, Session(Target("T", [Lane(new RigId("rig.main"), Block(null, 60, 5))])));

        Assert.True(compilation.IsValid);
        Assert.Equal(new RigId("rig.main"), Imaging(compilation).Single().Tracks[0].RigId);
    }

    [Fact]
    public void TwoSequencesForOneSetup_AreAMistakeThatIsSaid()
    {
        var equipment = Equipment();
        var second = Lane(MainPath, Block(null, 60, 5));

        var compilation = Compile(equipment, Session(Target("T", [Lane(null, Block(null, 60, 5)), second])));

        Assert.Contains(compilation.Problems, p => p.ElementId == second.Id && p.Message.Contains("already has a sequence", StringComparison.Ordinal));
    }

    // ---- targets

    [Fact]
    public void Targets_RunOneAfterTheOther_EachWithItsOwnPreparationAndCoordinates_AndADisabledOneIsSkipped()
    {
        var equipment = Equipment();
        var first = Target("M31", [Lane(null, Block(null, 60, 5))], [new SlewAndCenterAction(Guid.NewGuid())]) with { RightAscensionHours = 0.7, DeclinationDegrees = 41 };
        var skipped = Target("Skipped", [Lane(null, Block(null, 60, 5))], [new SlewAndCenterAction(Guid.NewGuid())]) with { Enabled = false };
        var second = Target("M42", [Lane(null, Block(null, 30, 5))], [new SlewAndCenterAction(Guid.NewGuid())]);

        var compilation = Compile(equipment, Session(first, skipped, second));

        Assert.Equal(["SlewAndCenterStepDraft", "MultiRigStepDraft", "SlewAndCenterStepDraft", "MultiRigStepDraft"], compilation.Steps.Select(s => s.GetType().Name));
        var centers = compilation.Steps.OfType<SlewAndCenterStepDraft>().ToList();
        Assert.Equal([0.7, 5.588], centers.Select(c => c.RightAscensionHours));
        Assert.Equal(2, Imaging(compilation).Select(m => m.Id).Distinct().Count()); // the ids do not collide
    }

    [Fact]
    public void TheLimitsOfATarget_EndAllItsLanes_AtOnce()
    {
        var equipment = Equipment(second: true);
        var dawn = new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn);
        var target = Target("T", [Lane(MainPath, Block(null, 60, 5)), Lane(WidePath, Block(null, 60, 5))]) with { Limits = [dawn] };

        var step = Imaging(Compile(equipment, Session(target))).Single();

        Assert.Equal([dawn], step.TargetStop!.Any);
    }

    // ---- the start and the end of the session

    [Fact]
    public void TheStartAndTheEnd_RunOnce_ForTheSetupsThatAreImaged_AndDeviceOperationsFindTheirDevices()
    {
        var equipment = Equipment(second: true);
        var start = new SessionAction[]
        {
            new WaitUntilAction(Guid.NewGuid(), [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)]), new UnparkAction(Guid.NewGuid()), new CoolCameraAction(Guid.NewGuid(), -10, 5),
        };
        var end = new SessionAction[] { new StopGuidingAction(Guid.NewGuid()), new WarmCameraAction(Guid.NewGuid(), 10), new ParkAction(Guid.NewGuid()) };
        var session = Session(Target("T", [Lane(MainPath, Block(null, 60, 5)), Lane(WidePath, Block(null, 60, 5))])) with { Start = start, End = end };

        var compilation = Compile(equipment, session);

        Assert.True(compilation.IsValid, string.Join(" ", compilation.Problems.Select(p => p.Message)));
        var names = compilation.Steps.Select(s => s is DeviceOperationStepDraft o ? $"{o.Operation}:{o.DeviceId}" : s.GetType().Name).ToList();
        Assert.Equal(
            [
                "WaitUntilStepDraft", "Unpark:mount.1", "CoolCamera:camera.a", "CoolCamera:camera.b", "MultiRigStepDraft", "StopGuidingStepDraft", "WarmCamera:camera.a", "WarmCamera:camera.b", "Park:mount.1",
            ],
            names); // two cameras are cooled and warmed; the one mount the setups share is unparked and parked once
        Assert.Equal(-10, compilation.Steps.OfType<DeviceOperationStepDraft>().First(o => o.Operation == DeviceOperation.CoolCamera).Celsius);
    }

    [Fact]
    public void ActionsThatNeedATarget_AreRefusedAtTheStart_AndABlockOnlyTakesWhatItCanRun()
    {
        var equipment = Equipment();
        var slew = new SlewAndCenterAction(Guid.NewGuid());
        var park = new ParkAction(Guid.NewGuid());
        var block = Block(null, 60, 5, b => b.Actions(park, new ExposureAction(Guid.NewGuid(), 60)));
        var session = Session(Target("T", [Lane(null, block)])) with { Start = [slew] };

        var compilation = Compile(equipment, session);

        Assert.Contains(compilation.Problems, p => p.ElementId == slew.Id && p.Message.Contains("needs a target", StringComparison.Ordinal));
        Assert.Contains(compilation.Problems, p => p.ElementId == park.Id && p.Message.Contains("cannot be part of a block", StringComparison.Ordinal));
    }

    // ---- the meridian flip

    [Fact]
    public void TheMeridianFlip_IsOfTheSession_AndEveryTargetGetsItWithItsOwnCoordinates()
    {
        var equipment = Equipment();
        var flip = new MeridianFlipSettings { Enabled = true, AutofocusAfterFlip = true };
        var first = Target("M31", [Lane(null, Block(null, 60, 5))]) with { RightAscensionHours = 0.7, DeclinationDegrees = 41 };
        var second = Target("M42", [Lane(null, Block(null, 60, 5))]);
        var session = Session(first, second) with { Automation = new SessionAutomation(flip) };

        var steps = Imaging(Compile(equipment, session));

        Assert.All(steps, s => Assert.True(s.MeridianFlip!.IsEnabled));
        Assert.Equal([(0.7, 41.0), (5.588, -5.39)], steps.Select(s => (s.MeridianFlip!.RightAscensionHours, s.MeridianFlip.DeclinationDegrees)));
        Assert.True(steps[0].MeridianFlip!.Settings.AutofocusAfterFlip);
    }

    [Fact]
    public void WithoutItsOwnSettings_TheSessionFollowsTheApplication_AndNothingOfTheFlipIsInTheBlocks()
    {
        var equipment = Equipment();
        var session = Session(Target("T", [Lane(null, Block(null, 60, 5))]));

        Assert.Null(Imaging(Compile(equipment, session)).Single().MeridianFlip); // the application's flip is off
        var on = Imaging(Compile(equipment, session, new MeridianFlipSettings { Enabled = true })).Single();
        Assert.True(on.MeridianFlip!.IsEnabled);
    }

    // ---- determinism

    [Fact]
    public async Task TheSameSession_GivesTheSameSteps_WithTheSameIds_EveryTime()
    {
        var equipment = Equipment(second: true);
        var session = Session(
            Target("M31", [Lane(MainPath, Block(1, 60, 5, b => b.Dither(3).Focus(atStart: true, everyMinutes: 60))), Lane(WidePath, Block(null, 30, 8))], [new SlewAndCenterAction(Guid.NewGuid()), new StartGuidingAction(Guid.NewGuid())]))
            with { End = [new StopGuidingAction(Guid.NewGuid())] };

        var one = Compile(equipment, session);
        var two = Compile(equipment, session);

        var serializer = new JsonSequenceDocumentSerializer();
        async Task<string> Text(SessionCompilation c)
        {
            using var stream = new MemoryStream();
            await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument(c.Steps), CancellationToken.None);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        Assert.Equal(await Text(one), await Text(two));
        Assert.Equal(one.Origins.OrderBy(o => o.Key), two.Origins.OrderBy(o => o.Key));
    }
}

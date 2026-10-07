using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.Workflows;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>A version 8 workflow becomes a session without losing what the session can say, the same way every time, and compiles to the same imaging.</summary>
public sealed class WorkflowMigrationTests : IAsyncLifetime
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

    private SessionFixture Equipment(bool second = true)
    {
        var fixture = Create(second);
        _fixtures.Add(fixture);
        return fixture;
    }

    private static readonly RigId Main = new("rig.main");
    private static readonly RigId Wide = new("rig.wide");
    private static readonly TwilightCondition Dusk = new(Twilight.Astronomical, TwilightEvent.Dusk);
    private static readonly TwilightCondition Dawn = new(Twilight.Astronomical, TwilightEvent.Dawn);

    private static WorkflowStep Step(WorkflowStepKind kind, ImagingBindingId? setup = null, bool enabled = true) => new(Guid.NewGuid(), kind, setup, enabled);

    private static WorkflowDefinition Night() => new(
        new WorkflowTarget("M42", 5.588, -5.39, 90),
        [
            new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.Wait, WaitMode: WorkflowWaitMode.UntilCondition, Until: [Dusk]),
            new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.SlewAndCenter, ToleranceArcseconds: 30, MaxAttempts: 4, SolveExposureSeconds: 3),
            Step(WorkflowStepKind.Autofocus),
            Step(WorkflowStepKind.StartGuiding),
        ],
        [
            new ImagingBlock(Guid.NewGuid(), Main, 1, 180, 20, true, [new TargetAltitudeCondition(30, ThresholdDirection.Above)], [Dawn]),
            new ImagingBlock(Guid.NewGuid(), Wide, null, 60, 30),
            new ImagingBlock(Guid.NewGuid(), Main, 2, 120, 10, false),
        ],
        [Step(WorkflowStepKind.StopGuiding)],
        new WorkflowDither(true, 3, Main, 2, 0.4, 5, 30),
        [new SetupAutofocus(Main, true, true, 45, true, new AutofocusSettings(2, 300, 9))],
        new MeridianFlipSettings { Enabled = true, AutofocusAfterFlip = true },
        false,
        [new TargetAltitudeCondition(20, ThresholdDirection.Below)]);

    [Fact]
    public void TheWorkflow_BecomesOneTarget_WithItsPreparation_ItsLanes_ItsEndAndItsLimits()
    {
        var equipment = Equipment();
        var workflow = Night();

        var session = WorkflowMigration.ToSession(workflow, equipment.Catalog);

        var target = Assert.Single(session.Targets);
        Assert.Equal(("M42", 5.588, -5.39, 90.0), (target.Name, target.RightAscensionHours, target.DeclinationDegrees, target.RotationDegrees));
        Assert.Equal([SessionActionKind.WaitUntil], session.Start.Select(a => a.Kind)); // only the leading wait
        Assert.Equal([SessionActionKind.SlewAndCenter, SessionActionKind.Autofocus, SessionActionKind.StartGuiding], target.Preparation.Select(a => a.Kind));
        var center = Assert.IsType<SlewAndCenterAction>(target.Preparation[0]);
        Assert.Equal((30.0, 4, 3.0), (center.ToleranceArcseconds, center.MaxAttempts, center.SolveExposureSeconds));
        Assert.Equal(workflow.Prepare[1].Id, center.Id); // the ids of the steps are kept
        Assert.Equal([SessionActionKind.StopGuiding], session.End.Select(a => a.Kind));
        Assert.Equal([new TargetAltitudeCondition(20, ThresholdDirection.Below)], target.Limits);
        Assert.False(session.Automation.UsesDefaultFlip);
        Assert.True(session.Automation.Flip!.AutofocusAfterFlip);

        // Two setups, two lanes, in the order they first appear; the blocks of one setup stay in one lane in their order.
        Assert.Equal(2, target.Lanes.Count);
        Assert.Equal([MainPath, WidePath], target.Lanes.Select(l => l.Setup!.Value));
        Assert.Equal(2, target.Lanes[0].Blocks.Count);
        Assert.Equal(workflow.Imaging[0].Id, target.Lanes[0].Blocks[0].Id);
        Assert.False(target.Lanes[0].Blocks[1].Enabled); // a block that was off stays off
    }

    [Fact]
    public void ABlock_BecomesItsActions_ItsRepeat_AndItsLimits()
    {
        var equipment = Equipment();
        var block = WorkflowMigration.ToSession(Night(), equipment.Catalog).Targets[0].Lanes[0].Blocks[0];

        Assert.Equal([SessionActionKind.WaitUntil, SessionActionKind.SetFilter, SessionActionKind.Exposure], block.Actions.Select(a => a.Kind));
        Assert.Equal([new TargetAltitudeCondition(30, ThresholdDirection.Above)], Assert.IsType<WaitUntilAction>(block.Actions[0]).Conditions);
        Assert.Equal(1, Assert.IsType<SetFilterAction>(block.Actions[1]).Slot);
        Assert.Equal(180, Assert.IsType<ExposureAction>(block.Actions[2]).Seconds);
        Assert.Equal(new RepeatRule(20, []).Count, block.Repeat.Count);
        Assert.Empty(block.Repeat.Until);
        Assert.Equal([Dawn], block.Limits);
    }

    [Fact]
    public void ThePoliciesOfTheSetups_BecomeTheAutomationOfTheBlocksOfTheirLane()
    {
        var equipment = Equipment();
        var lanes = WorkflowMigration.ToSession(Night(), equipment.Catalog).Targets[0].Lanes;

        var main = lanes[0].Blocks;
        var focus = main[0].Automation.Focus!;
        Assert.Equal((true, 45.0, true), (focus.AtBlockStart, focus.EveryMinutes, focus.AfterFilterChange)); // "at the start" is the start of the first block
        Assert.Equal((2.0, 300, 9), (focus.Settings.ExposureSeconds, focus.Settings.StepSize, focus.Settings.SampleCount));
        Assert.False(main[1].Automation.Focus!.AtBlockStart);
        Assert.True(main[1].Automation.Focus!.AfterFilterChange);

        // The dither was counted on the main setup: its blocks dither, the other lane does not ask.
        Assert.All(main, b => Assert.Equal(3, b.Automation.Dither!.EveryFrames));
        Assert.Equal(2, main[0].Automation.Dither!.Settings.AmplitudePixels);
        Assert.Null(lanes[1].Blocks[0].Automation.Dither);
        Assert.Null(lanes[1].Blocks[0].Automation.Focus);
    }

    [Fact]
    public void WithOneSetup_AnAutoBlock_AndABlockThatNamesIt_AreOneLane_AndStayAuto()
    {
        var equipment = Equipment(second: false);
        var workflow = Night() with
        {
            Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 60, 5), new ImagingBlock(Guid.NewGuid(), Main, 1, 60, 5)],
            Dither = WorkflowDither.Off,
            AutofocusPolicies = [],
        };

        var lanes = WorkflowMigration.ToSession(workflow, equipment.Catalog).Targets[0].Lanes;

        var lane = Assert.Single(lanes);
        Assert.Null(lane.Setup); // one setup to image with: the lane is "the only setup", whatever its camera is later
        Assert.Equal(2, lane.Blocks.Count);
    }

    [Fact]
    public void ThePointingSetup_GoesFirst_SoItsCameraStillSolves()
    {
        var equipment = Equipment();
        var workflow = Night() with
        {
            Target = new WorkflowTarget("M42", 5.588, -5.39, null, WidePath),
            Imaging = [new ImagingBlock(Guid.NewGuid(), Main, null, 60, 5), new ImagingBlock(Guid.NewGuid(), Wide, null, 60, 5)],
        };

        var session = WorkflowMigration.ToSession(workflow, equipment.Catalog);

        Assert.Equal([WidePath, MainPath], session.Targets[0].Lanes.Select(l => l.Setup!.Value));
        var compiled = SessionCompiler.Compile(session, equipment.Catalog);
        Assert.Equal(Wide, compiled.Steps.OfType<SlewAndCenterStepDraft>().Single().RigId);
    }

    [Fact]
    public void AReferenceThatDoesNotResolve_IsKeptAsItWas_AndReportedWhenCompiled()
    {
        var equipment = Equipment();
        var workflow = Night() with { Imaging = [new ImagingBlock(Guid.NewGuid(), new RigId("rig.gone"), null, 60, 5)], Dither = WorkflowDither.Off, AutofocusPolicies = [] };

        var session = WorkflowMigration.ToSession(workflow, equipment.Catalog);

        Assert.Equal("rig.gone", session.Targets[0].Lanes[0].Setup!.Value.Value);
        var compiled = SessionCompiler.Compile(session, equipment.Catalog);
        Assert.Contains(compiled.Problems, p => p.Message.Contains("'rig.gone' does not exist any more", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyWorkflow_IsAnEmptySession_AndTheFlipFollowsTheApplicationWhenItDid()
    {
        var session = WorkflowMigration.ToSession(WorkflowDefinition.NewEmpty(), null);

        Assert.Empty(session.Targets);
        Assert.Empty(session.Start);
        Assert.True(session.Automation.UsesDefaultFlip);
    }

    [Fact]
    public void Migrating_IsDeterministic()
    {
        var equipment = Equipment();
        var workflow = Night();

        var one = WorkflowMigration.ToSession(workflow, equipment.Catalog);
        var two = WorkflowMigration.ToSession(workflow, equipment.Catalog);

        Assert.Equal(one.Targets[0].Id, two.Targets[0].Id);
        Assert.Equal(one.Targets[0].Lanes.Select(l => l.Id), two.Targets[0].Lanes.Select(l => l.Id));
        Assert.Equal(one.Targets[0].Lanes[0].Blocks[0].Actions.Select(a => a.Id), two.Targets[0].Lanes[0].Blocks[0].Actions.Select(a => a.Id));
    }

    // ---- the same imaging

    [Fact]
    public void TheMigratedSession_ImagesLikeTheWorkflowDid()
    {
        var equipment = Equipment();
        var workflow = Night() with { Imaging = Night().Imaging.Where(b => b.Enabled).ToList() };

        var old = LegacyWorkflowCompiler.Compile(workflow, equipment.Catalog);
        var migrated = SessionCompiler.Compile(WorkflowMigration.ToSession(workflow, equipment.Catalog), equipment.Catalog);

        Assert.Empty(old.Problems);
        Assert.Empty(migrated.Problems);
        var was = old.Steps.OfType<MultiRigStepDraft>().Single();
        var now = migrated.Steps.OfType<MultiRigStepDraft>().Single();
        Assert.Equal(was.Tracks.Select(t => t.RigId), now.Tracks.Select(t => t.RigId));

        // What each track images: the filters it turns to and the repeats of its exposures (the focus steps differ in where they come from, not in when).
        static List<string> Imaging(RigTrackDraft t) => t.Steps.Where(s => s is RigChangeFilterStepDraft or RepeatStepDraft or WaitUntilStepDraft)
            .Select(s => s switch
            {
                RigChangeFilterStepDraft f => $"filter {f.SlotIndex}",
                RepeatStepDraft r => $"repeat {r.Count} {string.Join("+", r.Children.OfType<RigExposureStepDraft>().Select(e => e.Seconds))} stop:{r.Stop?.Any.Count ?? 0}",
                _ => "wait until",
            }).ToList();
        Assert.Equal(was.Tracks.Select(Imaging), now.Tracks.Select(Imaging));
        Assert.Equal(was.DitherPolicy, now.DitherPolicy);
        Assert.Equal(was.MeridianFlip!.Settings, now.MeridianFlip!.Settings);
        Assert.Equal(was.TargetStop!.Any, now.TargetStop!.Any);
        Assert.Equal(was.Tracks[0].AutofocusPolicy!.IntervalMinutes, now.Tracks[0].AutofocusPolicy!.IntervalMinutes);

        // What happens around it: the same kinds of steps in the same order.
        Assert.Equal(old.Steps.Select(s => s.Kind), migrated.Steps.Select(s => s.Kind));
        Assert.Equal(
            old.Steps.OfType<SlewAndCenterStepDraft>().Select(s => (s.RigId, s.ToleranceArcseconds)), migrated.Steps.OfType<SlewAndCenterStepDraft>().Select(s => (s.RigId, s.ToleranceArcseconds)));
        Assert.Equal(old.Steps.OfType<CenterAndRotateStepDraft>().Select(s => s.SkyRotationDegrees), migrated.Steps.OfType<CenterAndRotateStepDraft>().Select(s => s.SkyRotationDegrees));
    }
}

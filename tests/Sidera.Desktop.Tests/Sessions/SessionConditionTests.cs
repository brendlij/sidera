using System.Diagnostics;
using Sidera.Core.Astrometry;
using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;
using StartGuidingAction = Sidera.Desktop.Sessions.StartGuidingAction;
using StopGuidingAction = Sidera.Desktop.Sessions.StopGuidingAction;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>
/// Conditions in a session, from the model to a run on the simulators: what the compiler makes of them, how they are saved, and what a run does when they hold: waits, starts, stops, the
/// exposure that finishes, the policies that do not start, the tracks that go on, the flip that is not made for nothing. The clock is controlled; no test waits for real minutes.
/// </summary>
public sealed class SessionConditionTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Evening = new(2026, 3, 1, 15, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId A = new("rig.a");
    private static readonly RigId B = new("rig.b");
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    // A clock that stands still until it is told, or runs at a speed (sky seconds per real second).
    private sealed class TestClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private DateTime _base;
        private double _speed;

        public TestClock(DateTime start, double speed = 0) => Set(start, speed);

        public void Set(DateTime utc, double? speed = null)
        {
            lock (_gate)
            {
                _base = utc;
                _speed = speed ?? _speed;
                _watch.Restart();
            }
        }

        public DateTime UtcNow
        {
            get { lock (_gate) { return _base + TimeSpan.FromSeconds(_watch.Elapsed.TotalSeconds * _speed); } }
        }

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }

    private sealed class Probe
    {
        private readonly object _gate = new();
        private readonly List<(string Name, string Track)> _entries = [];

        public IReadOnlyList<string> Names { get { lock (_gate) { return _entries.Select(e => e.Name).ToList(); } } }

        public int Count(string prefix) => Names.Count(n => n.StartsWith(prefix, StringComparison.Ordinal));

        // The exposures of one track: the setup is the name of the track step above the exposure.
        public int CountOn(string track, string prefix)
        {
            lock (_gate)
            {
                return _entries.Count(e => e.Track == track && e.Name.StartsWith(prefix, StringComparison.Ordinal));
            }
        }

        public void Attach(SequenceRunner runner) => runner.StepCompleted += (_, e) =>
        {
            var track = string.Empty;
            for (var p = e.Position; p is not null; p = p.Parent)
            {
                if (p.StepName is "Main" or "Wide")
                {
                    track = p.StepName;
                }
            }

            lock (_gate)
            {
                _entries.Add((e.Position.StepName, track));
            }
        };

        public int LastIndexOf(string prefix) => Names.ToList().FindLastIndex(n => n.StartsWith(prefix, StringComparison.Ordinal));
    }

    private sealed class Fixture(SideraRuntimeHost host, TestClock clock, ConditionStatusBoard board)
    {
        public SideraRuntimeHost Host => host;
        public TestClock Clock => clock;
        public ConditionStatusBoard Board => board;

        public BuiltSequence Build(IReadOnlyList<SequenceStepDraft> steps)
        {
            var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);
            var context = new SequenceDraftContext(
                host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving, null, null, Time: clock, Site: () => Site,
                MeridianPollInterval: Poll, Conditions: board, ConditionPollInterval: Poll);
            return SequenceDraftBuilder.Build(host.DeviceRegistry, steps, context);
        }

        public (SequenceRunner Runner, Probe Probe) NewRunner()
        {
            var runner = new SequenceRunner();
            var probe = new Probe();
            probe.Attach(runner);
            return (runner, probe);
        }
    }

    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<Fixture> CreateAsync(DateTime? start = null, double speed = 0)
    {
        var clock = new TestClock(start ?? Evening, speed);
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        host.AddSimulatedMount(new("mount.1"), "Mount 1", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddRig(new Rig(A, "Main", new("camera.a"), Optics, new("focuser.a"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(B, "Wide", new("camera.b"), Optics, new("focuser.b"), null, null, null, new("mount.1"), new("guider.1")));
        foreach (var rig in new[] { A, B })
        {
            host.AddSimulatedFocusModel(rig, new SimulatedFocusModel(2600));
        }

        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        clock.Set(start ?? Evening, speed); // the sky starts when the equipment is there, not when it began to connect
        return new Fixture(host, clock, new ConditionStatusBoard());
    }

    private static readonly TargetSpec Target = new("M31", 0.712, 41.27);

    private sealed record TargetSpec(string Name, double RightAscensionHours, double DeclinationDegrees);

    private sealed record Spec(RigId Setup, SequenceBlock Block);

    // One target with one sequence for each setup that the blocks name, the guiding started before and stopped after.
    private static SessionDefinition Workflow(params Spec[] blocks) => SessionDefinition.Empty with
    {
        Targets =
        [
            new SessionTarget(
                Guid.NewGuid(), Target.Name, Target.RightAscensionHours, Target.DeclinationDegrees, null, true, [new StartGuidingAction(Guid.NewGuid())],
                blocks.GroupBy(b => b.Setup).Select(g => new SetupLane(Guid.NewGuid(), g.Key, g.Select(b => b.Block).ToList())).ToList(), []),
        ],
        End = [new StopGuidingAction(Guid.NewGuid())],
    };

    // A block of frames of one exposure; what must hold before it starts is a Wait Until in front of it, what ends it early its limits.
    private static Spec Block(RigId setup, int frames, double exposure, IReadOnlyList<WorkflowCondition>? start = null, IReadOnlyList<WorkflowCondition>? stop = null)
    {
        var actions = new List<SessionAction>();
        if (start is { Count: > 0 })
        {
            actions.Add(new WaitUntilAction(Guid.NewGuid(), start));
        }

        actions.Add(new ExposureAction(Guid.NewGuid(), exposure));
        return new Spec(setup, new SequenceBlock(Guid.NewGuid(), null, true, actions, RepeatRule.Times(frames), BlockAutomation.None, stop ?? []));
    }

    private static SessionDefinition WithTargetStop(SessionDefinition session, params WorkflowCondition[] stop) =>
        session with { Targets = [session.Targets[0] with { Limits = stop }] };

    private static SessionDefinition WithAutomation(SessionDefinition session, Func<SequenceBlock, SetupLane, SequenceBlock> change) =>
        session with { Targets = [session.Targets[0] with { Lanes = session.Targets[0].Lanes.Select(l => l with { Blocks = l.Blocks.Select(b => change(b, l)).ToList() }).ToList() }] };

    private static SessionCompilation Compile(Fixture fixture, SessionDefinition session)
    {
        var compiled = SessionCompiler.Compile(session, fixture.Host.RigRegistry);
        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        return compiled;
    }

    private static async Task<Probe> RunAsync(Fixture fixture, SessionDefinition workflow)
    {
        var built = fixture.Build(Compile(fixture, workflow).Steps);
        var (runner, probe) = fixture.NewRunner();
        await runner.RunAsync(built.Sequence).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SequenceState.Completed, runner.State);
        return probe;
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(2);
        }
    }

    private static DateTime Dusk(DateTime from) => SunCrossings.NextDusk(Site, from, Twilight.Astronomical).Utc!.Value;

    private static DateTime Dawn(DateTime from) => SunCrossings.NextDawn(Site, from, Twilight.Astronomical).Utc!.Value;

    // ---- compiling

    [Fact]
    public async Task TheConditionsOfABlock_BecomeAWaitBeforeIt_AStopOnItsRepeat_AndATargetStopOnTheTarget()
    {
        var fixture = await CreateAsync();
        var start = new WorkflowCondition[] { new TargetAltitudeCondition(30, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk) };
        var stop = new WorkflowCondition[] { new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn), new DurationCondition(TimeSpan.FromHours(4)) };
        var target = new WorkflowCondition[] { TimeCondition.AtLocalTime(new TimeOnly(5, 0)) };
        var workflow = WithTargetStop(Workflow(Block(A, 40, 300, start, stop)), target);

        var compiled = Compile(fixture, workflow);

        var imaging = Assert.IsType<MultiRigStepDraft>(compiled.Steps.Single(s => s is MultiRigStepDraft));
        var steps = imaging.Tracks.Single().Steps;
        var wait = Assert.IsType<WaitUntilStepDraft>(steps[0]); // before everything the block does
        Assert.Equal(start, wait.Conditions);
        Assert.Equal(Target.RightAscensionHours, wait.Target!.RightAscensionHours);
        var waitAction = workflow.Targets[0].Lanes[0].Blocks[0].Actions[0];
        var repeat = Assert.IsType<RepeatStepDraft>(steps[^1]);
        Assert.Equal(40, repeat.Count);
        Assert.Equal(stop, repeat.Stop!.Any);
        Assert.Equal(target, imaging.TargetStop!.Any);
        Assert.Equal(waitAction.Id, compiled.Origins[wait.Id]); // the wait belongs to the action it was made from, the repeat to the block
        Assert.Equal(workflow.Targets[0].Lanes[0].Blocks[0].Id, compiled.Origins[repeat.Id]);
    }

    [Fact]
    public async Task AWorkflowWithoutConditions_CompilesToTheStepsItAlwaysDid()
    {
        var fixture = await CreateAsync();

        var compiled = Compile(fixture, Workflow(Block(A, 5, 1)));

        var imaging = (MultiRigStepDraft)compiled.Steps.Single(s => s is MultiRigStepDraft);
        Assert.Null(imaging.TargetStop);
        Assert.Null(((RepeatStepDraft)imaging.Tracks.Single().Steps.Single()).Stop);
        Assert.DoesNotContain(imaging.Tracks.Single().Steps, s => s is WaitUntilStepDraft);
    }

    [Fact]
    public async Task WaitActionsOfEveryKind_Compile_AndAnEmptyOneIsAProblem()
    {
        var fixture = await CreateAsync();
        var duration = new WaitAction(Guid.NewGuid(), 600);
        var untilTime = new WaitUntilAction(Guid.NewGuid(), [TimeCondition.AtLocalTime(new TimeOnly(22, 30))]);
        var untilCondition = new WaitUntilAction(Guid.NewGuid(), [new TargetAltitudeCondition(30, ThresholdDirection.Above)]);
        var empty = new WaitUntilAction(Guid.NewGuid(), []);
        var session = Workflow(Block(A, 1, 1));

        SessionDefinition Prepared(params SessionAction[] actions) => session with { Targets = [session.Targets[0] with { Preparation = actions }] };
        var good = SessionCompiler.Compile(Prepared(duration, untilTime, untilCondition), fixture.Host.RigRegistry);
        var bad = SessionCompiler.Compile(Prepared(empty), fixture.Host.RigRegistry);

        Assert.True(good.IsValid);
        Assert.Collection(good.Steps.Take(3), s => Assert.IsType<DelayStepDraft>(s), s => Assert.IsType<WaitUntilStepDraft>(s), s => Assert.IsType<WaitUntilStepDraft>(s));
        Assert.Contains(bad.Problems, p => p.ElementId == empty.Id && p.Message.Contains("Choose what to wait for", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Conditions_ThatMakeNoSense_AreProblemsWhereTheUserMadeThem()
    {
        var fixture = await CreateAsync();
        var block = Block(A, 5, 1, null, [new DurationCondition(TimeSpan.Zero)]);
        var compiled = SessionCompiler.Compile(Workflow(block), fixture.Host.RigRegistry);
        var shared = SharedEquipmentDraft.FromRigs(fixture.Host.RigRegistry.GetAll(), null, null, false);

        var validation = SequenceDraftBuilder.Validate(
            fixture.Host.DeviceRegistry, compiled.Steps, new SequenceDraftContext(fixture.Host.RigRegistry, shared, fixture.Host.FocusMetrics, fixture.Host.EventBus, null, fixture.Host.AcquisitionDefaults));

        Assert.Contains(validation.StepProblems.Values.SelectMany(p => p), p => p.Contains("duration must be longer than 0", StringComparison.Ordinal));
    }

    // ---- persistence

    private static async Task<SequenceDocument> RoundTrip(SequenceDocument document)
    {
        var serializer = new JsonSequenceDocumentSerializer();
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, document, CancellationToken.None);
        stream.Position = 0;
        return await serializer.LoadAsync(stream, CancellationToken.None);
    }

    private static string Text(SequenceDocument document)
    {
        using var stream = new MemoryStream();
        new JsonSequenceDocumentSerializer().SaveAsync(stream, document, CancellationToken.None).GetAwaiter().GetResult();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static readonly WorkflowCondition[] Everything =
    [
        TimeCondition.AtUtc(new DateTime(2026, 3, 2, 3, 30, 0, DateTimeKind.Utc)),
        TimeCondition.AtLocalTime(new TimeOnly(4, 30), "Europe/Berlin"),
        TimeCondition.AtLocalTime(new TimeOnly(22, 5)),
        new DurationCondition(TimeSpan.FromHours(4)),
        new TargetAltitudeCondition(25, ThresholdDirection.Below),
        new TargetAltitudeCondition(30.5, ThresholdDirection.Above),
        new SunAltitudeCondition(-15, ThresholdDirection.Below),
        new TwilightCondition(Twilight.Civil, TwilightEvent.Dusk),
        new TwilightCondition(Twilight.Nautical, TwilightEvent.Dawn),
        new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn),
        new FrameCountCondition(40),
    ];

    [Fact]
    public async Task EveryCondition_IsSaved_AndComesBack_InASession()
    {
        var session = Workflow(Block(A, 5, 1, Everything.Take(3).ToList(), Everything.Skip(3).ToList()));
        var dusk = new WaitUntilAction(Guid.NewGuid(), [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)]);
        session = WithTargetStop(session, new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn));
        session = session with { Start = [dusk] };

        var back = (await RoundTrip(new SequenceDocument("T", [], null, null, session))).Session!;

        var block = back.Targets[0].Lanes[0].Blocks[0];
        Assert.Equal(Everything.Take(3), ((WaitUntilAction)block.Actions[0]).Conditions);
        Assert.Equal(Everything.Skip(3), block.Limits);
        Assert.Equal(session.Targets[0].Limits, back.Targets[0].Limits);
        Assert.Equal(dusk.Conditions, ((WaitUntilAction)back.Start[0]).Conditions);
    }

    [Fact]
    public async Task ASessionWithoutConditions_HasNoConditionKeys_AndLoadsWithNone()
    {
        var session = Workflow(Block(A, 5, 1));

        var document = new SequenceDocument("T", [], null, null, session);
        var text = Text(document);

        Assert.DoesNotContain("\"limits\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"until\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"conditions\"", text, StringComparison.Ordinal);
        var back = (await RoundTrip(document)).Session!;
        Assert.Empty(back.Targets[0].Lanes[0].Blocks[0].Limits);
        Assert.Empty(back.Targets[0].Lanes[0].Blocks[0].Repeat.Until);
        Assert.Empty(back.Targets[0].Limits);
    }

    [Fact]
    public async Task TheCompiledSteps_KeepTheirConditions_InATree()
    {
        var fixture = await CreateAsync();
        var session = WithTargetStop(
            Workflow(Block(A, 5, 1, [new TargetAltitudeCondition(30, ThresholdDirection.Above)], [new DurationCondition(TimeSpan.FromHours(2)), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)])),
            TimeCondition.AtLocalTime(new TimeOnly(5, 0)));
        var steps = Compile(fixture, session).Steps;

        var back = SequenceDocumentMapper.ToDrafts(await RoundTrip(SequenceDocumentMapper.ToDocument(steps)));

        var imaging = (MultiRigStepDraft)back.Single(s => s is MultiRigStepDraft);
        var track = imaging.Tracks.Single().Steps;
        Assert.Equal([new TargetAltitudeCondition(30, ThresholdDirection.Above)], ((WaitUntilStepDraft)track[0]).Conditions);
        Assert.Equal(2, ((RepeatStepDraft)track[^1]).Stop!.Any.Count);
        Assert.Equal(Target.DeclinationDegrees, ((RepeatStepDraft)track[^1]).Stop!.Target!.DeclinationDegrees);
        Assert.Single(imaging.TargetStop!.Any);
    }

    // ---- Wait

    [Fact]
    public async Task AWaitUntilDarkness_AtTheStart_WaitsForTheEvening_AndSaysWhatItWaitsFor()
    {
        var fixture = await CreateAsync();
        var dusk = Dusk(Evening);
        var wait = new WaitUntilAction(Guid.NewGuid(), [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)]);
        var workflow = Workflow(Block(A, 2, 0.05)) with { Start = [wait] };
        var compiled = Compile(fixture, workflow);
        var waitId = compiled.Steps.OfType<WaitUntilStepDraft>().Single().Id;

        var built = fixture.Build(compiled.Steps);
        var (runner, probe) = fixture.NewRunner();
        var run = runner.RunAsync(built.Sequence);
        await WaitAsync(() => fixture.Board.TryGet(waitId, out var s) && s.Phase == ConditionPhase.Waiting, "the wait to start");

        Assert.Contains("astronomical darkness", fixture.Board.For(waitId).Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sun altitude", fixture.Board.For(waitId).Detail, StringComparison.Ordinal);
        await Task.Delay(40);
        Assert.Equal(0, probe.Count("Exposure")); // it is day: nothing is imaged

        fixture.Clock.Set(dusk.AddMinutes(1));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, probe.Count("Exposure"));
        Assert.Equal(ConditionPhase.Done, fixture.Board.For(waitId).Phase);
    }

    [Fact]
    public async Task AWaitUntilATime_EndsAtThatTime_AndACancelledOneEndsAtOnce()
    {
        var fixture = await CreateAsync();
        var wait = new WaitUntilAction(Guid.NewGuid(), [TimeCondition.AtUtc(Evening.AddHours(2))]);
        var built = fixture.Build(Compile(fixture, Workflow(Block(A, 1, 0.05)) with { Start = [wait] }).Steps);
        var (runner, probe) = fixture.NewRunner();
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(built.Sequence, cts.Token);
        await Task.Delay(40);
        Assert.False(run.IsCompleted);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(0, probe.Count("Exposure"));

        var (second, secondProbe) = fixture.NewRunner();
        var again = second.RunAsync(fixture.Build(Compile(fixture, Workflow(Block(A, 1, 0.05)) with { Start = [wait] }).Steps).Sequence);
        await Task.Delay(30);
        Assert.False(again.IsCompleted);
        fixture.Clock.Set(Evening.AddHours(2).AddSeconds(1));
        await again.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, secondProbe.Count("Exposure"));
    }

    // ---- start conditions

    [Fact]
    public async Task ABlock_WaitsForTheTargetToRise_AndShowsWhy_ThenImages()
    {
        // The target is well below 30° now and gets above it later: find the time by scanning the sky, not by a formula of the test.
        var fixture = await CreateAsync(new DateTime(2026, 3, 1, 2, 0, 0, DateTimeKind.Utc));
        var rising = Enumerable.Range(0, 24 * 60).Select(m => fixture.Clock.UtcNow.AddMinutes(m)).First(t => SkyAltitude.TargetDegrees(Target.RightAscensionHours, Target.DeclinationDegrees, t, Site) >= 30);
        var startAt = rising.AddMinutes(-1);
        Assert.True(SkyAltitude.TargetDegrees(Target.RightAscensionHours, Target.DeclinationDegrees, fixture.Clock.UtcNow, Site) < 30);
        var workflow = Workflow(Block(A, 2, 0.05, [new TargetAltitudeCondition(30, ThresholdDirection.Above)]));
        var compiled = Compile(fixture, workflow);
        var waitId = compiled.Steps.OfType<MultiRigStepDraft>().Single().Tracks.Single().Steps.OfType<WaitUntilStepDraft>().Single().Id;
        var (runner, probe) = fixture.NewRunner();

        var run = runner.RunAsync(fixture.Build(compiled.Steps).Sequence);
        await WaitAsync(() => fixture.Board.TryGet(waitId, out var s) && s.Phase == ConditionPhase.Waiting, "the block to wait");

        var status = fixture.Board.For(waitId);
        Assert.Contains("Target altitude", status.Detail, StringComparison.Ordinal);
        Assert.Contains("needs ≥ 30°", status.Detail, StringComparison.Ordinal);
        Assert.Equal(0, probe.Count("Exposure"));

        fixture.Clock.Set(startAt.AddMinutes(2));
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, probe.Count("Exposure"));
    }

    [Fact]
    public async Task SeveralStartConditions_AllHaveToHold()
    {
        // The target is high all night; darkness is the one that is missing, and the block waits for it.
        var fixture = await CreateAsync(Evening);
        var dusk = Dusk(Evening);
        var workflow = Workflow(Block(A, 1, 0.05, [new TargetAltitudeCondition(-90, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)]));
        var (runner, probe) = fixture.NewRunner();

        var run = runner.RunAsync(fixture.Build(Compile(fixture, workflow).Steps).Sequence);
        await Task.Delay(60);

        Assert.False(run.IsCompleted); // the altitude holds, darkness does not: waiting
        Assert.Equal(0, probe.Count("Exposure"));
        fixture.Clock.Set(dusk.AddMinutes(1));
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, probe.Count("Exposure"));
    }

    // ---- stop conditions

    [Fact]
    public async Task ABlock_StopsAtAstronomicalDawn_AfterTheExposureThatWasRunning_AndTheFinishFollows()
    {
        var dawn = Dawn(Dusk(Evening));
        var fixture = await CreateAsync(dawn.AddMinutes(-20));
        var workflow = Workflow(Block(A, 50, 0.3, null, [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)]));
        var built = fixture.Build(Compile(fixture, workflow).Steps);
        var (runner, probe) = fixture.NewRunner();
        var run = runner.RunAsync(built.Sequence);

        // During the first frame the Sun comes up through -18°.
        await WaitAsync(() => runner.ActivePositions.Any(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal)), "the first exposure");
        fixture.Clock.Set(dawn.AddMinutes(1));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(1, probe.Count("Exposure")); // the frame that was running was finished, and no other was started
        Assert.True(probe.LastIndexOf("Stop guiding") > probe.LastIndexOf("Exposure")); // the finish part followed: this was a normal end, not a failure
    }

    [Fact]
    public async Task ABlock_StopsAfterItsDuration_AndAtATime_AndAtAnAltitude()
    {
        // duration: 20 sky minutes at 3600 sky seconds per second is a third of a second
        var fixture = await CreateAsync(Evening, 3600);
        var byDuration = await RunAsync(fixture, Workflow(Block(A, 400, 0.1, null, [new DurationCondition(TimeSpan.FromMinutes(20))])));
        Assert.True(byDuration.Count("Exposure") is >= 2 and <= 8, "duration: " + byDuration.Count("Exposure"));

        // a time
        var second = await CreateAsync(Evening, 3600);
        var byTime = await RunAsync(second, Workflow(Block(A, 400, 0.1, null, [TimeCondition.AtUtc(Evening.AddMinutes(20))])));
        Assert.True(byTime.Count("Exposure") is >= 2 and <= 8, "time: " + byTime.Count("Exposure"));

        // an altitude: the target sets below 10° at some time; the block ends then
        double AltitudeAt(DateTime t) => SkyAltitude.TargetDegrees(Target.RightAscensionHours, Target.DeclinationDegrees, t, Site);
        var high = Enumerable.Range(0, 24 * 60).Select(m => Evening.AddMinutes(m)).First(t => AltitudeAt(t) >= 30);
        var low = Enumerable.Range(0, 24 * 60).Select(m => high.AddMinutes(m)).First(t => AltitudeAt(t) <= 10);
        var third = await CreateAsync(low.AddMinutes(-20), 3600);
        var byAltitude = await RunAsync(third, Workflow(Block(A, 400, 0.1, null, [new TargetAltitudeCondition(10, ThresholdDirection.Below)])));
        Assert.True(byAltitude.Count("Exposure") is >= 2 and <= 8, "altitude: " + byAltitude.Count("Exposure"));
        Assert.True(SkyAltitude.TargetDegrees(Target.RightAscensionHours, Target.DeclinationDegrees, third.Clock.UtcNow, Site) <= 10);
    }

    [Fact]
    public async Task AnyOfSeveralStopConditions_StopsTheBlock_AndTheFramesStillCount()
    {
        var fixture = await CreateAsync(Evening, 3600);

        // 3 frames come before the 4 hours (4 sky hours are 4 s at this speed): the frame count is one of the stops.
        var probe = await RunAsync(fixture, Workflow(Block(A, 3, 0.05, null, [new DurationCondition(TimeSpan.FromHours(4)), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)])));

        Assert.Equal(3, probe.Count("Exposure"));
    }

    [Fact]
    public async Task AReachedStop_DoesNotStartAnAutofocusOrADither_JustBeforeTheEnd()
    {
        var fixture = await CreateAsync(Evening); // the sky stands still until the test moves it
        var workflow = WithAutomation(
            Workflow(Block(A, 400, 0.4, null, [TimeCondition.AtUtc(Evening.AddMinutes(30))])),
            (b, _) => b with { Automation = new BlockAutomation(new DitherAutomation(1, new DitherSettings(1.5, 0.5, 0.1, 5)), new FocusAutomation(false, 1, false, new FocusSettings(0.05, 400, 5))) });
        var built = fixture.Build(Compile(fixture, workflow).Steps);
        var (runner, probe) = fixture.NewRunner();
        var run = runner.RunAsync(built.Sequence);

        // The first frame is taken (and dithered after); during the second the sky moves an hour on: the stop time has passed, and the autofocus (every minute) and the dither are due as well.
        await WaitAsync(() => probe.Count("Exposure") >= 1 && probe.Count("Dither command") >= 1 && runner.ActivePositions.Any(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal)), "the second exposure");
        var focusesBefore = probe.Count("Autofocus");
        fixture.Clock.Set(Evening.AddMinutes(60));
        await run.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(2, probe.Count("Exposure")); // the second finished, and no third started
        Assert.Equal(1, probe.Count("Dither command")); // after the first frame; none after the second, which was the last
        Assert.Equal(focusesBefore, probe.Count("Autofocus")); // the autofocus that was due did not run
        var after = probe.Names.Skip(probe.LastIndexOf("Exposure") + 1).ToList();
        Assert.DoesNotContain(after, n => n is "Dither command" or "Autofocus");
    }

    // ---- several setups, the target

    [Fact]
    public async Task WhenOneBlockHasItsFrames_TheOtherGoesOn()
    {
        var fixture = await CreateAsync();

        var probe = await RunAsync(fixture, Workflow(Block(A, 3, 0.05), Block(B, 12, 0.05)));

        Assert.Equal(3, probe.CountOn("Main", "Exposure"));
        Assert.Equal(12, probe.CountOn("Wide", "Exposure")); // Wide did not stop because Main was done
    }

    [Fact]
    public async Task TheStopOfTheTarget_EndsEveryBlock_AfterItsCurrentFrame_AndTheFinishFollows()
    {
        var fixture = await CreateAsync(Evening, 3600);
        var workflow = WithTargetStop(Workflow(Block(A, 4, 0.05), Block(B, 600, 0.1)), new DurationCondition(TimeSpan.FromMinutes(30)));

        var probe = await RunAsync(fixture, workflow);

        Assert.InRange(probe.Count("Exposure"), 6, 40); // Main's 4 and some of Wide's: nowhere near 600
        Assert.True(probe.LastIndexOf("Stop guiding") > probe.LastIndexOf("Exposure"));
    }

    [Fact]
    public async Task ABlockStop_OfOneSetup_DoesNotStopTheOther_AndNothingDeadlocks()
    {
        var fixture = await CreateAsync(Evening, 3600);
        // Wide asks for the dither (so the frames are counted on Wide); Main shares its mount and waits for it.
        var workflow = WithAutomation(
            Workflow(Block(A, 600, 0.05, null, [new DurationCondition(TimeSpan.FromMinutes(10))]), Block(B, 12, 0.1)),
            (b, lane) => lane.Setup == B ? b with { Automation = new BlockAutomation(new DitherAutomation(2, new DitherSettings(1.5, 0.5, 0.1, 5)), null) } : b);

        var probe = await RunAsync(fixture, workflow);

        Assert.Equal(12, probe.CountOn("Wide", "Exposure")); // Wide took all of its 12
        Assert.InRange(probe.CountOn("Main", "Exposure"), 1, 599); // Main took some, and stopped long before 600
    }

    // ---- the flip

    private static IEnumerable<BuiltStep> Flatten(BuiltStep step) => new[] { step }.Concat((step.Children ?? []).SelectMany(Flatten));

    // The target crosses the meridian three minutes after the start (an hour angle of -3 minutes); an exposure runs, and while it does the clock is set to six minutes after the start (+3: the flip
    // is due when the next exposure is about to start).
    private async Task<(MeridianFlipGroup Group, Probe Probe)> RunFlipScenarioAsync(IReadOnlyList<WorkflowCondition> stop)
    {
        var fixture = await CreateAsync(Evening);
        var t0 = Evening.AddHours(6);
        var ra = MeridianFlipTiming.LocalSiderealTimeHours(t0.AddMinutes(3), Site.LongitudeDegrees);
        fixture.Clock.Set(t0);
        var flip = new MeridianFlipSettings { Enabled = true, SolveExposureSeconds = 0.05, MaxCenteringAttempts = 3, VerifyRotationAfterFlip = false, RecenterAfterFlip = false, RestartGuidingAfterFlip = false };
        var workflow = Workflow(Block(A, 2, 0.4, null, stop.Select(c => c is TimeCondition ? TimeCondition.AtUtc(t0.AddMinutes(1)) : c).ToList()));
        workflow = workflow with { Targets = [workflow.Targets[0] with { RightAscensionHours = ra, DeclinationDegrees = 41.27 }], Automation = new SessionAutomation(flip) };
        var built = fixture.Build(Compile(fixture, workflow).Steps);
        var group = built.Steps.SelectMany(Flatten).Select(b => b.Step).OfType<MeridianGateStep>().Select(g => g.Group).Distinct().Single();
        var (runner, probe) = fixture.NewRunner();

        var run = runner.RunAsync(built.Sequence);
        await WaitAsync(() => runner.ActivePositions.Any(p => p.StepName.StartsWith("Exposure", StringComparison.Ordinal)), "the first exposure");
        fixture.Clock.Set(t0.AddMinutes(6));
        await run.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(SequenceState.Completed, runner.State);
        return (group, probe);
    }

    [Fact]
    public async Task AStopThatHolds_WhenTheFlipBecomesDue_MakesNoFlip()
    {
        var (group, probe) = await RunFlipScenarioAsync([TimeCondition.AtUtc(Evening)]);

        Assert.Equal(1, probe.Count("Exposure")); // the frame that was running finished; the block is over
        Assert.True(group.State is MeridianFlipState.Monitoring or MeridianFlipState.Approaching, $"the flip was {group.State}"); // no flip for imaging that is not going to happen
        Assert.True(probe.LastIndexOf("Stop guiding") > probe.LastIndexOf("Exposure"));
    }

    [Fact]
    public async Task AFlipThatIsDue_StillComesBeforeTheNextExposure_WhenNoStopHolds()
    {
        var (group, probe) = await RunFlipScenarioAsync([new DurationCondition(TimeSpan.FromHours(5))]);

        Assert.Equal(2, probe.Count("Exposure"));
        Assert.Equal(MeridianFlipState.Completed, group.State);
    }
}

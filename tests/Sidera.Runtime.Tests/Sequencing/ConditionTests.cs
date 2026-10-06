using Sidera.Core.Astronomy;
using Sidera.Core.Conditions;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>The conditions: what they decide from a controllable clock, how Wait steps and blocks use them, and what comes first when several things are due at once. Nothing waits real minutes.</summary>
public sealed class ConditionTests
{
    private static readonly ObservingSite Frankfurt = new(50.1, 8.6, 120);
    private static readonly DateTime Start = new(2026, 3, 1, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    private sealed class FakeTime(DateTime start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = start;

        public DateTime Now
        {
            get { lock (_gate) { return _now; } }
            set { lock (_gate) { _now = value; } }
        }

        public void Advance(TimeSpan span) => Now += span;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static ConditionServices Services(FakeTime time, ObservingSite? site = null) => new(time, () => site ?? Frankfurt, Poll);

    private static ConditionContext Context(DateTime now, DateTime scopeStart, CelestialCoordinates? target = null, int frames = 0) => new(now, Frankfurt, target, scopeStart, frames);

    // ---- the evaluator

    [Fact]
    public void ATimeOfDay_IsTheNextTimeTheClockReadsIt_InTheNamedZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
        var condition = TimeCondition.AtLocalTime(new TimeOnly(4, 30), zone.Id);

        // 2026-03-01 22:00 UTC is 23:00 in Berlin (CET, UTC+1): the next 04:30 there is on the 2nd, 03:30 UTC.
        var instant = condition.ResolveUtc(new DateTime(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 3, 2, 3, 30, 0, DateTimeKind.Utc), instant);

        // Before 04:30 the same day: that day's.
        Assert.Equal(new DateTime(2026, 3, 2, 3, 30, 0, DateTimeKind.Utc), condition.ResolveUtc(new DateTime(2026, 3, 2, 1, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void ATimeOfDay_FollowsTheClocksOfTheZone_AcrossTheChangeToSummerTime()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");

        // The night of 28 to 29 March 2026: the clocks go forward at 02:00 CET. 04:30 on the 29th is CEST (UTC+2), 02:30 UTC.
        var instant = TimeCondition.AtLocalTime(new TimeOnly(4, 30), zone.Id).ResolveUtc(new DateTime(2026, 3, 28, 22, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 3, 29, 2, 30, 0, DateTimeKind.Utc), instant);
    }

    [Fact]
    public void AnAbsoluteTime_IsMetAtThatInstant_AndNotBefore()
    {
        var evaluator = new ConditionEvaluator();
        var condition = TimeCondition.AtUtc(Start.AddHours(2));

        Assert.False(evaluator.Evaluate(condition, Context(Start.AddHours(1.99), Start)).Met);
        Assert.True(evaluator.Evaluate(condition, Context(Start.AddHours(2), Start)).Met);
    }

    [Fact]
    public void ADuration_CountsFromTheStartOfItsScope_NotFromTheClock()
    {
        var evaluator = new ConditionEvaluator();
        var condition = new DurationCondition(TimeSpan.FromHours(4));

        Assert.False(evaluator.Evaluate(condition, Context(Start.AddHours(10), Start.AddHours(7))).Met); // 3 h into it
        Assert.True(evaluator.Evaluate(condition, Context(Start.AddHours(11), Start.AddHours(7))).Met);
    }

    [Fact]
    public void ATargetAltitude_IsComputedFromTheCoordinates_AndOnceMet_StaysMet()
    {
        var transit = Start.AddHours(4);
        var ra = MeridianFlipTiming.LocalSiderealTimeHours(transit, Frankfurt.LongitudeDegrees);
        var target = new CelestialCoordinates(ra, 20); // 59.9° at transit
        var evaluator = new ConditionEvaluator();
        var above = new TargetAltitudeCondition(55, ThresholdDirection.Above);

        Assert.False(evaluator.Evaluate(above, Context(transit.AddHours(-5), Start, target)).Met);
        Assert.True(evaluator.Evaluate(above, Context(transit, Start, target)).Met);

        // a threshold that is crossed does not un-cross when the target moves on: one trigger
        Assert.True(evaluator.Evaluate(above, Context(transit.AddHours(5), Start, target)).Met);
    }

    [Fact]
    public void ATargetAltitudeThatWobblesAroundItsThreshold_TriggersOnce()
    {
        var evaluator = new ConditionEvaluator();
        var transit = Start.AddHours(4);
        var target = new CelestialCoordinates(MeridianFlipTiming.LocalSiderealTimeHours(transit, Frankfurt.LongitudeDegrees), 20);
        var below = new TargetAltitudeCondition(59.8, ThresholdDirection.Below); // 59.9 at transit: it is below a little after

        Assert.False(evaluator.Evaluate(below, Context(transit, Start, target)).Met);
        Assert.True(evaluator.Evaluate(below, Context(transit.AddMinutes(30), Start, target)).Met);

        // Numerically the altitude is above the threshold again (an earlier time, as a wobble would be): the condition has triggered, and stays triggered.
        Assert.True(evaluator.Evaluate(below, Context(transit, Start, target)).Met);
        Assert.True(evaluator.Evaluate(below, Context(transit.AddMinutes(-5), Start, target)).Met);
    }

    [Fact]
    public void ADusk_IsDarkness_AndHoldsAllNight_AndAnAlreadyDarkSkyNeedsNoWait()
    {
        var evaluator = new ConditionEvaluator();
        var darkness = new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk);
        var dusk = SunCrossings.NextDusk(Frankfurt, Start, Twilight.Astronomical).Utc!.Value;

        Assert.False(evaluator.Evaluate(darkness, Context(dusk.AddMinutes(-5), Start)).Met);
        Assert.True(evaluator.Evaluate(darkness, Context(dusk.AddMinutes(1), Start)).Met);
        Assert.True(new ConditionEvaluator().Evaluate(darkness, Context(dusk.AddHours(3), dusk.AddHours(3))).Met);
    }

    [Fact]
    public void ADawn_IsTheMorningCrossing_NotTheStateOfTheSky()
    {
        var evaluator = new ConditionEvaluator();
        var dawnCondition = new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn);
        var dusk = SunCrossings.NextDusk(Frankfurt, Start, Twilight.Astronomical).Utc!.Value;
        var dawn = SunCrossings.NextDawn(Frankfurt, dusk, Twilight.Astronomical).Utc!.Value;

        // Watched from the afternoon: the Sun is above -18° right now, and that must not count as dawn.
        Assert.False(evaluator.Evaluate(dawnCondition, Context(Start, Start)).Met);
        Assert.False(evaluator.Evaluate(dawnCondition, Context(dusk.AddHours(2), Start)).Met); // the middle of the night
        Assert.False(evaluator.Evaluate(dawnCondition, Context(dawn.AddSeconds(-30), Start)).Met);
        Assert.True(evaluator.Evaluate(dawnCondition, Context(dawn.AddSeconds(1), Start)).Met);
    }

    [Fact]
    public void ADawnWithNoDarkness_SaysSo_InsteadOfInventingATime()
    {
        var evaluator = new ConditionEvaluator();
        var site = new ObservingSite(55, 10, 0);
        var midsummer = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        var dusk = evaluator.Evaluate(new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk), new ConditionContext(midsummer, site, null, midsummer));
        var dawn = evaluator.Evaluate(new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn), new ConditionContext(midsummer, site, null, midsummer));

        Assert.Equal(ConditionAvailability.NotWithinReach, dusk.Availability);
        Assert.Equal(ConditionAvailability.NotWithinReach, dawn.Availability);
        Assert.False(dusk.Met || dawn.Met);
    }

    [Fact]
    public void ACondition_ThatNeedsASite_SaysThatItDoes()
    {
        var evaluator = new ConditionEvaluator();

        var result = evaluator.Evaluate(new TargetAltitudeCondition(30, ThresholdDirection.Above), new ConditionContext(Start, null, new CelestialCoordinates(1, 1), Start));

        Assert.Equal(ConditionAvailability.MissingSite, result.Availability);
    }

    [Fact]
    public void AFrameCount_IsMetWhenTheFramesAreTaken()
    {
        var evaluator = new ConditionEvaluator();

        Assert.False(evaluator.Evaluate(new FrameCountCondition(40), Context(Start, Start, frames: 39)).Met);
        Assert.True(evaluator.Evaluate(new FrameCountCondition(40), Context(Start, Start, frames: 40)).Met);
    }

    [Fact]
    public void StartConditions_AreAll_AndStopConditionsAreAny()
    {
        var evaluator = new ConditionEvaluator();
        var yes = new FrameCountCondition(1);
        var no = new FrameCountCondition(99);
        var context = Context(Start, Start, frames: 1);

        Assert.Single(evaluator.Unmet([yes, no], context)); // ALL: one is missing
        Assert.Empty(evaluator.Unmet([yes, yes with { }], context));
        Assert.NotNull(evaluator.FirstMet([no, yes], context)); // ANY: one is enough
        Assert.Null(evaluator.FirstMet([no, no with { }], context));
    }

    // ---- Wait

    private static Task<SequenceStepResult> NoOp() => Task.FromResult(new SequenceStepResult());

    private static async Task Run(ISequenceStep step, CancellationToken cancellationToken = default)
    {
        var runner = new SequenceRunner();
        await runner.RunAsync(new Sequence("s", [step]), cancellationToken);
    }

    // Advances the fake clock while a step waits, so that no test waits for real time.
    private static async Task<T> WhileAdvancing<T>(FakeTime time, TimeSpan perTick, Task<T> work)
    {
        while (!work.IsCompleted)
        {
            time.Advance(perTick);
            await Task.Delay(1);
        }

        return await work;
    }

    [Fact]
    public async Task WaitUntilATime_WaitsUntilTheClockGetsThere_WithoutSpinning()
    {
        var time = new FakeTime(Start);
        var status = new ConditionStatus();
        var step = new WaitUntilStep("Wait", [TimeCondition.AtUtc(Start.AddMinutes(30))], null, Services(time), status);

        var run = Run(step);
        await Task.Delay(40);
        Assert.False(run.IsCompleted); // the clock has not moved: nothing happens
        Assert.Equal(ConditionPhase.Waiting, status.Phase);
        Assert.Contains("Waiting", status.Text);

        await WhileAdvancing(time, TimeSpan.FromMinutes(5), run.ContinueWith(_ => 0));

        Assert.True(time.Now >= Start.AddMinutes(30));
        Assert.Equal(ConditionPhase.Done, status.Phase);
    }

    [Fact]
    public async Task ACancelledWait_EndsAtOnce()
    {
        var time = new FakeTime(Start);
        using var cts = new CancellationTokenSource();
        var run = Run(new WaitUntilStep("Wait", [TimeCondition.AtUtc(Start.AddHours(5))], null, Services(time)), cts.Token);
        await Task.Delay(30);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task WaitUntilAstronomicalDarkness_WaitsForTheEvening()
    {
        var time = new FakeTime(Start);
        var step = new WaitUntilStep("Wait", [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], null, Services(time));
        var dusk = SunCrossings.NextDusk(Frankfurt, Start, Twilight.Astronomical).Utc!.Value;

        await WhileAdvancing(time, TimeSpan.FromMinutes(1), Run(step).ContinueWith(_ => 0));

        Assert.InRange((time.Now - dusk).TotalMinutes, 0, 15); // the clock moves a minute for every few milliseconds the machine gives the poll
        Assert.True(SkyAltitude.SunDegrees(time.Now, Frankfurt) <= -18);
    }

    [Fact]
    public async Task WaitUntilTheTargetRises_AndItIsDark_NeedsBoth()
    {
        // The target is already high; darkness comes later: the wait ends with darkness (AND).
        var time = new FakeTime(Start);
        var target = new CelestialCoordinates(MeridianFlipTiming.LocalSiderealTimeHours(Start.AddHours(3), Frankfurt.LongitudeDegrees), 20);
        var step = new WaitUntilStep(
            "Wait", [new TargetAltitudeCondition(30, ThresholdDirection.Above), new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], target, Services(time));
        var dusk = SunCrossings.NextDusk(Frankfurt, Start, Twilight.Astronomical).Utc!.Value;

        await WhileAdvancing(time, TimeSpan.FromMinutes(10), Run(step).ContinueWith(_ => 0));

        Assert.True(time.Now >= dusk);
        Assert.True(SkyAltitude.TargetDegrees(target.RightAscensionHours, 20, time.Now, Frankfurt) >= 30);
    }

    [Fact]
    public async Task WaitingForADarknessThatNeverComes_FailsWithTheReason()
    {
        var time = new FakeTime(new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc));
        var step = new WaitUntilStep("Wait", [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dusk)], null, Services(time, new ObservingSite(55, 10, 0)));

        var failure = await Assert.ThrowsAsync<ConditionUnavailableException>(() => Run(step));

        Assert.Contains("astronomical", failure.Message);
        Assert.Contains("darkness", failure.Message);
    }

    [Fact]
    public async Task WaitingForTheTargetsAltitude_WithoutASite_FailsAndSaysWhy()
    {
        var time = new FakeTime(Start);
        var services = new ConditionServices(time, () => null, Poll);
        var step = new WaitUntilStep("Wait", [new TargetAltitudeCondition(30, ThresholdDirection.Above)], new CelestialCoordinates(1, 1), services);

        var failure = await Assert.ThrowsAsync<ConditionUnavailableException>(() => Run(step));

        Assert.Contains("observing site", failure.Message);
    }

    [Fact]
    public async Task AWait_EndsWhenTheTargetHasStopped()
    {
        var time = new FakeTime(Start);
        var targetScope = new ConditionScope(Services(time), null, [TimeCondition.AtUtc(Start.AddMinutes(10))]);
        var step = new WaitUntilStep("Wait", [TimeCondition.AtUtc(Start.AddDays(3))], null, Services(time), null, targetScope);

        await WhileAdvancing(time, TimeSpan.FromMinutes(5), Run(step).ContinueWith(_ => 0));

        Assert.True(time.Now < Start.AddDays(1)); // it did not wait three days for imaging that is not going to happen
    }

    // ---- a block of frames

    private sealed class FrameStep(FakeTime time, TimeSpan exposure, Action<FrameStep>? during = null) : ISequenceStep
    {
        public string Name => "Exposure";
        public int Frames { get; private set; }

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            during?.Invoke(this);
            time.Advance(exposure); // the exposure takes this long
            Frames++;
            return NoOp();
        }
    }

    private sealed class CountStep(string name) : ISequenceStep
    {
        public string Name => name;
        public int Runs { get; private set; }

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            Runs++;
            return NoOp();
        }
    }

    private sealed record Block(RepeatStep Repeat, FrameStep Exposure, CountStep Focus, CountStep Dither, ConditionScope Scope, ConditionStatus Status);

    // The body of a frame as the builder makes it: [autofocus when due, exposure, dither] under the control of the block.
    private static Block BuildBlock(
        FakeTime time, int frames, IReadOnlyList<WorkflowCondition> stop, TimeSpan exposure, ConditionScope? target = null, CelestialCoordinates? where = null, Action<FrameStep>? during = null)
    {
        var services = Services(time);
        var scope = new ConditionScope(services, where, stop, target);
        var status = new ConditionStatus();
        var frame = new FrameStep(time, exposure, during);
        var focus = new CountStep("Autofocus");
        var dither = new CountStep("Dither");
        var control = new ConditionBlockControl(scope, services, frames, new HashSet<ISequenceStep> { frame }, status);
        var body = new SequenceGroup("Frame", [focus, frame, dither], control);
        return new Block(new RepeatStep(frames, body, control), frame, focus, dither, scope, status);
    }

    [Fact]
    public async Task ABlockWithoutStopConditions_TakesItsFrames()
    {
        var time = new FakeTime(Start);
        var block = BuildBlock(time, 7, [], TimeSpan.FromMinutes(5));

        await Run(block.Repeat);

        Assert.Equal(7, block.Exposure.Frames);
        Assert.Equal(7, block.Scope.Frames);
        Assert.Contains("Completed · 7 / 7", block.Status.Text);
    }

    [Fact]
    public async Task ABlock_StopsAfterItsDuration_AndTheDurationCountsFromItsOwnStart()
    {
        var time = new FakeTime(Start);
        var block = BuildBlock(time, 100, [new DurationCondition(TimeSpan.FromHours(1))], TimeSpan.FromMinutes(5));

        await Run(block.Repeat);

        Assert.Equal(12, block.Exposure.Frames); // 12 × 5 min = 1 h: the next frame is not started
        Assert.Contains("Stopped", block.Status.Text);
        Assert.Contains("Duration 1 h", block.Status.Detail);
    }

    [Fact]
    public async Task ABlock_StopsAtATime_AndTheExposureThatCrossesItFinishes_BeforeAnythingElse()
    {
        var time = new FakeTime(Start);
        var stopAt = Start.AddMinutes(12); // in the middle of the third 5-minute exposure
        var block = BuildBlock(time, 100, [TimeCondition.AtUtc(stopAt)], TimeSpan.FromMinutes(5));

        await Run(block.Repeat);

        Assert.Equal(3, block.Exposure.Frames); // frame 3 was running when the time came: it finished, and counts
        Assert.Equal(3, block.Focus.Runs); // no autofocus before a fourth frame
        Assert.Equal(2, block.Dither.Runs); // and no dither after the last one: the block is over (the dither that came after frames 1 and 2 only)
    }

    [Fact]
    public async Task AStopThatBecomesTrueDuringAnExposure_IsTakenAtTheEnd_AndSaidSo()
    {
        var time = new FakeTime(Start);
        string? whileExposing = null;
        var services = Services(time);
        var scope = new ConditionScope(services, null, [TimeCondition.AtUtc(Start.AddMinutes(2))]);
        var status = new ConditionStatus();
        var frame = new FrameStep(time, TimeSpan.FromMinutes(5), _ =>
        {
            time.Advance(TimeSpan.FromMinutes(3)); // the stop time passes while the exposure runs
            Thread.Sleep(80); // the monitor looks at the poll interval and sees it
            whileExposing = status.Text;
        });
        var control = new ConditionBlockControl(scope, services, 10, new HashSet<ISequenceStep> { frame }, status);
        var repeat = new RepeatStep(10, new SequenceGroup("Frame", [frame], control), control);

        await Run(repeat);

        Assert.Equal(1, frame.Frames); // the exposure finished and counts; no second one starts
        Assert.Equal("Stop condition reached · finishing current exposure", whileExposing);
        Assert.Contains("Stopped", status.Text);
    }

    [Fact]
    public async Task ABlock_StopsWhenTheTargetFallsBelowTheAltitude()
    {
        var time = new FakeTime(Start);
        var transit = Start;
        var target = new CelestialCoordinates(MeridianFlipTiming.LocalSiderealTimeHours(transit, Frankfurt.LongitudeDegrees), 20);
        var block = BuildBlock(time, 100, [new TargetAltitudeCondition(45, ThresholdDirection.Below)], TimeSpan.FromMinutes(10), where: target);

        await Run(block.Repeat);

        var expected = Enumerable.Range(0, 100).First(k => SkyAltitude.TargetDegrees(target.RightAscensionHours, 20, Start.AddMinutes(10 * k), Frankfurt) <= 45);
        Assert.Equal(expected, block.Exposure.Frames);
        Assert.True(SkyAltitude.TargetDegrees(target.RightAscensionHours, 20, time.Now, Frankfurt) <= 45);
    }

    [Fact]
    public async Task ABlock_StopsAtAstronomicalDawn_NotAtDusk()
    {
        var time = new FakeTime(Start); // 18:00 UTC: daylight
        var dusk = SunCrossings.NextDusk(Frankfurt, Start, Twilight.Astronomical).Utc!.Value;
        var dawn = SunCrossings.NextDawn(Frankfurt, dusk, Twilight.Astronomical).Utc!.Value;
        time.Now = dusk.AddMinutes(1);
        var block = BuildBlock(time, 1000, [new TwilightCondition(Twilight.Astronomical, TwilightEvent.Dawn)], TimeSpan.FromMinutes(10));

        await Run(block.Repeat);

        Assert.True(time.Now >= dawn && time.Now < dawn.AddMinutes(11)); // the frame that ended after dawn was the last
        Assert.InRange(block.Exposure.Frames, 40, 60);
    }

    [Fact]
    public async Task AnyOfSeveralStopConditions_Ends_AndSaysWhichOne()
    {
        var time = new FakeTime(Start);
        var block = BuildBlock(
            time, 100, [new DurationCondition(TimeSpan.FromHours(10)), TimeCondition.AtUtc(Start.AddMinutes(20)), new FrameCountCondition(1000)], TimeSpan.FromMinutes(5));

        await Run(block.Repeat);

        Assert.Equal(4, block.Exposure.Frames);
        Assert.Contains("Time", block.Status.Detail);
    }

    [Fact]
    public async Task TheStopOfTheTarget_EndsTheBlocksOfAllItsTracks_AfterTheirCurrentFrame()
    {
        var time = new FakeTime(Start);
        var targetScope = new ConditionScope(Services(time), null, [TimeCondition.AtUtc(Start.AddMinutes(25))]);
        var main = BuildBlock(time, 100, [], TimeSpan.FromMinutes(5), targetScope);
        var wide = BuildBlock(time, 100, [new FrameCountCondition(1000)], TimeSpan.FromMinutes(5), targetScope);

        var runner = new SequenceRunner();
        await runner.RunAsync(new Sequence("s", [new ParallelStep("tracks", [main.Repeat, wide.Repeat])]));

        // The clock is shared and every frame takes 5 minutes of it: five frames in all reach the 25 minutes, and a frame that was running in the other track finishes.
        Assert.InRange(main.Exposure.Frames + wide.Exposure.Frames, 5, 7);
        Assert.Contains("Stopped", main.Status.Text);
        Assert.Contains("Stopped", wide.Status.Text);
        Assert.Contains("Time", main.Status.Detail);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task ABlockThatReachesItsFrames_DoesNotStopTheOthers()
    {
        var time = new FakeTime(Start);
        var target = new ConditionScope(Services(time), null, [new DurationCondition(TimeSpan.FromDays(1))]);
        var main = BuildBlock(time, 4, [], TimeSpan.FromMinutes(5), target);
        var wide = BuildBlock(time, 12, [], TimeSpan.FromMinutes(5), target);

        var runner = new SequenceRunner();
        await runner.RunAsync(new Sequence("s", [new ParallelStep("tracks", [main.Repeat, wide.Repeat])]));

        Assert.Equal(4, main.Exposure.Frames);
        Assert.Equal(12, wide.Exposure.Frames); // main completing did not end wide
        Assert.Equal(SequenceState.Completed, runner.State);
    }
}

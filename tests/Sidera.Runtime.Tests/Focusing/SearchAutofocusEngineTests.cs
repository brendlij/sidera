using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;
using Sidera.Runtime.Focusing;

namespace Sidera.Runtime.Tests.Focusing;

/// <summary>The autofocus that searches: it steps toward smaller stars, turns round when they get larger, does not die of a blurred sample, remembers where focus was, and falls back to the best sample.</summary>
public class SearchAutofocusEngineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(10);
    private const int Focus = 20000;

    private static AutofocusOptions Options(AutofocusStrategy? strategy = null, int step = 300, int samples = 7, bool verify = true) =>
        new(Exposure, step, samples, verify, TimeSpan.Zero, 2) { Strategy = strategy ?? new AutofocusStrategy { Mode = AutofocusMode.Search } };

    private static async Task<SimulatedFocuser> FocuserAsync(int start, int min = 0, int max = 50000)
    {
        var focuser = new SimulatedFocuser(new("focuser.main"), "EAF", new EventBus(), start, min, max, 10_000_000, TimeSpan.FromMilliseconds(1));
        await focuser.ConnectAsync();
        return focuser;
    }

    // HFR 1.8 px at focus and a hyperbola around it, the shape of a real star.
    private static double Hyperbola(int position, int best = Focus, double floor = 1.8, double slope = 0.004) =>
        Math.Sqrt(floor * floor + slope * slope * (position - best) * (position - best));

    // Measures by a function; positions the function says are blurred beyond use have no measurable star.
    private sealed class BlurMeasurer(IFocuser focuser, Func<int, double> hfr, Func<int, bool>? blurred = null) : IFocusMeasurer
    {
        public List<int> Positions { get; } = [];
        public bool Spoil { get; set; }

        public Task<FocusMeasurement> MeasureAsync(TimeSpan exposureDuration, CancellationToken cancellationToken = default)
        {
            var position = focuser.Position;
            Positions.Add(position);
            if (blurred?.Invoke(position) == true)
            {
                throw new AutofocusFailedException("Autofocus failed: no stars were detected.");
            }

            var value = Spoil ? 12.0 : hfr(position);
            Spoil = false;
            return Task.FromResult(new FocusMeasurement(position, value));
        }
    }

    private static Task<AutofocusResult> Run(SimulatedFocuser focuser, IFocusMeasurer measurer, AutofocusOptions options, RigId? rig = null, AutofocusReporter? report = null) =>
        SearchAutofocusEngine.RunAsync(focuser, measurer, options, rig, report).WaitAsync(Bound);

    [Theory]
    [InlineData(Focus)]
    [InlineData(19300)]
    [InlineData(20700)]
    [InlineData(18200)]
    [InlineData(22400)]
    public async Task FocusIsFound_WhereverTheSearchStarts_EvenFarFromIt(int start)
    {
        var focuser = await FocuserAsync(start);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p));

        var result = await Run(focuser, measurer, Options());

        Assert.InRange(result.BestPosition, Focus - 60, Focus + 60);
        Assert.Equal(result.BestPosition, focuser.Position);
        Assert.True(result.Measurements.Count(m => m.FocuserPosition < Focus) >= 2);
        Assert.True(result.Measurements.Count(m => m.FocuserPosition > Focus) >= 2);
    }

    [Fact]
    public async Task TheSearchStartsWhereTheFocuserStands_AndTakesTheStepThatMakesTheStarsSmaller()
    {
        var focuser = await FocuserAsync(19000);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p));

        await Run(focuser, measurer, Options(verify: false));

        Assert.Equal(19000, measurer.Positions[0]); // where it stood
        Assert.Equal(19300, measurer.Positions[1]); // toward focus
        Assert.Equal(19600, measurer.Positions[2]); // and on, the stars keep getting smaller
    }

    [Fact]
    public async Task WhenTheStarsGetLarger_TheSearchTurnsRound_AndDoesNotRunOnInTheWrongDirection()
    {
        var focuser = await FocuserAsync(20900); // right of focus: the first step to the right makes the stars larger
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p));

        var result = await Run(focuser, measurer, Options(verify: false));

        Assert.Equal(20900, measurer.Positions[0]);
        Assert.Equal(21200, measurer.Positions[1]); // the first try
        Assert.Equal(20600, measurer.Positions[2]); // larger: it went the other way
        Assert.True(measurer.Positions.Max() <= 21200, "it never went further right than the first try");
        Assert.InRange(result.BestPosition, Focus - 60, Focus + 60);
    }

    [Fact]
    public async Task APositionThatCannotBeMeasured_EndsTheSearchOnThatSide_AndIsNotFatal()
    {
        var focuser = await FocuserAsync(Focus);
        // Beyond 800 steps from focus the stars are blurred beyond use.
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p), p => Math.Abs(p - Focus) > 800);

        var result = await Run(focuser, measurer, Options(step: 300, samples: 7));

        Assert.InRange(result.BestPosition, Focus - 80, Focus + 80);
        Assert.Contains(measurer.Positions, p => Math.Abs(p - Focus) > 800); // it did look there
        Assert.All(result.Measurements, m => Assert.InRange(Math.Abs(m.FocuserPosition - Focus), 0, 800));
    }

    [Fact]
    public async Task WithNoStarWhereTheFocuserStands_TheLastPositionInFocusIsTried()
    {
        var history = new FocusHistory();
        var rig = new RigId("rig.main");
        history.Record(rig, Focus + 100, 1.8, DateTimeOffset.UtcNow);
        var focuser = await FocuserAsync(Focus + 9000);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p), p => Math.Abs(p - Focus) > 3000);

        var result = await Run(focuser, measurer, Options(new AutofocusStrategy { Mode = AutofocusMode.Search, History = history }), rig);

        Assert.Equal(Focus + 9000, measurer.Positions[0]);
        Assert.Equal(Focus + 100, measurer.Positions[1]); // the remembered position
        Assert.InRange(result.BestPosition, Focus - 80, Focus + 80);
    }

    [Fact]
    public async Task WithNoMemory_AFewStepsToEitherSideAreTried_BeforeItGivesUp()
    {
        var focuser = await FocuserAsync(Focus + 700);
        // Blurred at the start, usable a little nearer to focus.
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p), p => p > Focus + 600);

        var result = await Run(focuser, measurer, Options(step: 200));

        Assert.InRange(result.BestPosition, Focus - 80, Focus + 80);
    }

    [Fact]
    public async Task WhereNothingCanBeMeasured_TheRunFailsWithAClearReason_AndTheFocuserIsNotLostFar()
    {
        var focuser = await FocuserAsync(Focus);
        var measurer = new BlurMeasurer(focuser, p => 5, _ => true);

        var failure = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Contains("no star could be measured", failure.Message, StringComparison.Ordinal);
        Assert.InRange(focuser.Position, Focus - 2000, Focus + 2000);
    }

    [Fact]
    public async Task TheFocuserIsNeverCommandedOutsideItsRange()
    {
        var focuser = await FocuserAsync(500, min: 0, max: 3000);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p, best: 900));

        var result = await Run(focuser, measurer, Options(step: 300));

        Assert.All(measurer.Positions, p => Assert.InRange(p, 0, 3000));
        Assert.InRange(result.BestPosition, 800, 1000);
    }

    [Fact]
    public async Task AFocusThatChecksWorseThanTheBestSample_FallsBackToThatSample_InsteadOfFailing()
    {
        var focuser = await FocuserAsync(Focus);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p));

        var result = await Run(focuser, measurer, Options(), report: (progress, _) =>
        {
            if (progress.Phase == AutofocusPhase.Verifying)
            {
                measurer.Spoil = true; // the check is spoilt (a cloud, a wobble)
            }

            return Task.CompletedTask;
        });

        Assert.True(result.FellBackToBestSample);
        Assert.Equal(result.Measurements.MinBy(m => m.Hfr)!.FocuserPosition, result.BestPosition);
        Assert.Equal(result.BestPosition, focuser.Position);
    }

    [Fact]
    public async Task ASampleThatBreaksTheHyperbola_IsLeftOutOfTheFit_NotTheWholeRun()
    {
        var focuser = await FocuserAsync(Focus);
        // A wild outer sample: a satellite trail, a cloud.
        var measurer = new BlurMeasurer(focuser, p => p == Focus + 900 ? 30.0 : Hyperbola(p));

        var result = await Run(focuser, measurer, Options(verify: false));

        Assert.InRange(result.BestPosition, Focus - 100, Focus + 100);
    }

    [Fact]
    public async Task TheLinesOfTheTwoSides_FindFocusToo()
    {
        var focuser = await FocuserAsync(19000);
        // A V with a seeing floor: straight lines to a flat bottom.
        var measurer = new BlurMeasurer(focuser, p => Math.Max(1.6, 0.004 * Math.Abs(p - Focus) + 1.2));

        var result = await Run(focuser, measurer, Options(new AutofocusStrategy { Mode = AutofocusMode.Search, FitMethod = FocusFitMethod.TrendLines }, verify: false));

        Assert.InRange(result.BestPosition, Focus - 250, Focus + 250);
    }

    [Fact]
    public async Task AHfrThatHardlyChanges_SaysThatTheStepIsTooSmall()
    {
        var focuser = await FocuserAsync(Focus);
        var measurer = new BlurMeasurer(focuser, p => 2.0 + 0.00001 * p);

        var failure = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Contains("larger step", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProgress_IsReported_ForEverySample_AndEndsCompleted()
    {
        var focuser = await FocuserAsync(19200);
        var measurer = new BlurMeasurer(focuser, p => Hyperbola(p));
        var phases = new List<AutofocusPhase>();

        await Run(focuser, measurer, Options(), report: (progress, _) =>
        {
            phases.Add(progress.Phase);
            return Task.CompletedTask;
        });

        Assert.Equal(AutofocusPhase.Completed, phases[^1]);
        Assert.Contains(AutofocusPhase.Fitting, phases);
        Assert.True(phases.Count(p => p == AutofocusPhase.Measuring) >= 7);
    }

    [Fact]
    public async Task TheFocusHistory_KeepsTheLastPositionsOfARig_NewestLast_AndTellsWhenItChanged()
    {
        var history = new FocusHistory { Capacity = 3 };
        var rig = new RigId("rig.a");
        var changes = 0;
        history.Changed += (_, _) => changes++;
        Assert.Null(history.LastPosition(rig));

        foreach (var position in new[] { 100, 200, 300, 400 })
        {
            history.Record(rig, position, 2, DateTimeOffset.UtcNow);
        }

        Assert.Equal(400, history.LastPosition(rig));
        Assert.Equal([200, 300, 400], history.Of(rig).Select(e => e.Position));
        Assert.Null(history.LastPosition(new RigId("rig.b")));
        Assert.Equal(4, changes);
    }

    [Fact]
    public void TheSearchIsOffUntilTheApplicationChoosesIt()
    {
        Assert.Equal(AutofocusMode.Pattern, new AutofocusStrategy().Mode);
        Assert.Equal(AutofocusMode.Pattern, AutofocusStrategy.Application.Mode);
        Assert.Equal(FocusFitMethod.Hyperbola, new AutofocusStrategy().FitMethod);
    }
}

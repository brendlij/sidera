using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;
using Sidera.Runtime.Focusing;

namespace Sidera.Runtime.Tests.Focusing;

public class AutofocusEngineTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(10);

    private static AutofocusOptions Options(int step = 300, int samples = 7, bool verify = true, int attempts = 2) =>
        new(Exposure, step, samples, verify, TimeSpan.Zero, attempts);

    private static async Task<(SimulatedFocuser Focuser, EventBus Bus)> Focuser(
        int start, int min = 0, int max = 50000)
    {
        var bus = new EventBus();
        var focuser = new SimulatedFocuser(new(  "focuser.main"), "EAF", bus, start, min, max, 10_000_000, TimeSpan.FromMilliseconds(1));
        await focuser.ConnectAsync();
        return (focuser, bus);
    }

    private static Task<AutofocusResult> Run(
        SimulatedFocuser focuser, FunctionMeasurer measurer, AutofocusOptions options, AutofocusReporter? report = null,
        CancellationToken ct = default) =>
        AutofocusEngine.RunAsync(focuser, measurer, options, report, ct).WaitAsync(Bound);

    // The pattern

    [Fact]
    public void ThePattern_IsSymmetricalAroundTheCentre_LowestFirst()
    {
        var positions = AutofocusEngine.SamplePositions(20000, 300, 7, 0, 50000);

        Assert.Equal([19100, 19400, 19700, 20000, 20300, 20600, 20900], positions);
    }

    [Fact]
    public void PositionsTheFocuserCannotReach_AreLeftOut_NeverCommanded()
    {
        Assert.Equal([0, 300, 600, 900, 1200], AutofocusEngine.SamplePositions(300, 300, 7, 0, 50000));
        Assert.Equal([48800, 49100, 49400, 49700, 50000], AutofocusEngine.SamplePositions(49700, 300, 7, 0, 50000));
        Assert.Equal([100, 400, 700, 1000], AutofocusEngine.SamplePositions(100, 300, 7, 0, 1000));
    }

    [Fact]
    public async Task TheSamplesAreTakenLowestFirst_OneAtEachPosition_AsManyAsAsked()
    {
        var (focuser, bus) = await Focuser(20000);
        var trace = new FocuserTrace(bus);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options());

        // Seven samples at the pattern, then one at the best position (the verification).
        Assert.Equal([19100, 19400, 19700, 20000, 20300, 20600, 20900, 20000], measurer.Positions);
        Assert.Equal(7, result.Measurements.Count);
        Assert.Equal([19100, 19400, 19700, 20000, 20300, 20600, 20900], result.Measurements.Select(m => m.FocuserPosition));
        // The focuser went there in that order (it was at 20000 already for the middle sample, and for the end).
        Assert.Equal([19100, 19400, 19700, 20000, 20300, 20600, 20900, 20000], trace.Arrivals.Take(8));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(9)]
    public async Task TheSampleCountIsTheOneInTheOptions(int samples)
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options(samples: samples));

        Assert.Equal(samples, result.Measurements.Count);
    }

    // Finding focus

    [Theory]
    [InlineData(19500)] // left of focus
    [InlineData(20600)] // right of focus
    [InlineData(20010)] // already near
    [InlineData(20000)] // exactly there
    public async Task FocusIsFoundWithinTheTolerance_WhereverTheStartIs(int start)
    {
        var (focuser, _) = await Focuser(start);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options());

        Assert.InRange(result.BestPosition, 19950, 20050);
        Assert.Equal(start, result.InitialPosition);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(result.BestPosition, focuser.Position);
        Assert.Equal(result.BestPosition, result.FinalPosition);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task TheFocusIsFoundForAnyCurveOfThatShape_NotOnlyTheDemoOne()
    {
        var (focuser, _) = await Focuser(7000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(7400, bestHfr: 2.6, slope: 0.004));

        var result = await Run(focuser, measurer, Options(step: 200, samples: 9));

        Assert.InRange(result.BestPosition, 7350, 7450);
    }

    [Fact]
    public async Task AStartFarFromFocus_PutsThePatternAroundTheMinimumOnce_AndKeepsAllSamples()
    {
        var (focuser, _) = await Focuser(18000); // 2000 away; the pattern reaches 900
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options());

        Assert.Equal(2, result.Attempts);
        Assert.InRange(result.BestPosition, 19950, 20050);
        Assert.Equal(14, result.Measurements.Count); // seven of each pattern
    }

    [Fact]
    public async Task WithOneAttempt_AMinimumOutsideThePattern_IsNotAccepted()
    {
        var (focuser, _) = await Focuser(18000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options(attempts: 1)));

        Assert.Equal("Autofocus failed: no reliable focus minimum was found.", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task AMinimumNearTheLowestPosition_IsFoundWithAPatternThatIsCutOff()
    {
        var (focuser, _) = await Focuser(600);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(300));

        var result = await Run(focuser, measurer, Options());

        Assert.InRange(result.BestPosition, 250, 350);
        Assert.All(measurer.Positions, p => Assert.InRange(p, 0, 50000)); // nothing illegal was commanded
    }

    [Fact]
    public async Task AMinimumOutsideTheRangeOfTheFocuser_FailsClearly()
    {
        var (focuser, _) = await Focuser(49000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(53000));

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Equal(AutofocusFailedException.NoMinimum, error.Message);
        Assert.All(measurer.Positions, p => Assert.InRange(p, 0, 50000));
    }

    [Fact]
    public async Task TooLittleTravelForTheSamples_FailsBeforeMovingAnywhere()
    {
        var (focuser, _) = await Focuser(300, min: 0, max: 600); // three positions of 300 steps
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(300));

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Equal("Autofocus failed: the focuser does not have enough travel here for the samples.", error.Message);
        Assert.Empty(measurer.Positions);
        Assert.Equal(300, focuser.Position);
    }

    // No reliable minimum

    [Fact]
    public async Task AFlatCurve_FailsWithNoReliableMinimum_AndMovesNowhereAfterwards()
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, _ => 2.5);

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Equal(AutofocusFailedException.NoMinimum, error.Message);
        Assert.Equal(7, measurer.Positions.Count); // no verification, no second pattern
    }

    [Fact]
    public async Task ACurveWithAMaximumInsteadOfAMinimum_Fails()
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, p => 6 - 1.8 * Math.Sqrt(1 + Math.Pow((p - 20000) / 800.0, 2)) + 1);

        await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));
    }

    [Fact]
    public async Task ACurveThatIsJustNoise_Fails()
    {
        var (focuser, _) = await Focuser(20000);
        var values = new Queue<double>([2.0, 3.1, 1.9, 3.3, 2.2, 3.0, 2.1]);
        var measurer = new FunctionMeasurer(focuser, _ => values.Dequeue());

        await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));
    }

    [Fact]
    public async Task AVerificationThatIsNotBetterThanTheSamples_Fails_AndTheFocuserStaysAtTheBestPosition()
    {
        var (focuser, _) = await Focuser(20000);
        var calls = 0;
        var curve = Curves.Hyperbola(20000);
        // The first seven measurements are the samples; the eighth is the verification, and it is bad.
        var measurer = new FunctionMeasurer(focuser, p => ++calls > 7 ? 9.0 : curve(p));

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Run(focuser, measurer, Options()));

        Assert.Contains("not better", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.InRange(focuser.Position, 19950, 20050);
    }

    [Fact]
    public async Task WithoutVerification_ThereIsNoVerificationExposure_AndTheBestHfrIsTheFittedOne()
    {
        var (focuser, _) = await Focuser(20300);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options(verify: false));

        Assert.Null(result.Verification);
        Assert.Equal(7, measurer.Positions.Count);
        Assert.Equal(result.FittedHfr, result.BestHfr);
        Assert.Equal(1.8, result.FittedHfr, 3);
    }

    [Fact]
    public async Task WithVerification_TheBestHfrIsMeasured_AndTheFitIsKeptSeparately()
    {
        var (focuser, _) = await Focuser(20300);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var result = await Run(focuser, measurer, Options());

        Assert.NotNull(result.Verification);
        Assert.Equal(result.BestPosition, result.Verification!.FocuserPosition);
        Assert.Equal(result.Verification.Hfr, result.BestHfr);
        Assert.Equal(1.8, result.FittedHfr, 3);
        Assert.InRange(result.BestHfr, 1.8, 1.82);
        Assert.Equal(8, measurer.Positions.Count);
    }

    // Options

    [Theory]
    [InlineData(0, 300, 7)]
    [InlineData(-5, 300, 7)]
    [InlineData(10, 0, 7)]
    [InlineData(10, -300, 7)]
    [InlineData(10, 300, 6)]   // not symmetrical
    [InlineData(10, 300, 3)]   // too few for a fit
    [InlineData(10, 300, 23)]  // more than offered
    public async Task OptionsThatAreNotUsable_AreRejected_BeforeAnythingHappens(int exposureMs, int step, int samples)
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));
        var options = new AutofocusOptions(TimeSpan.FromMilliseconds(exposureMs), step, samples);

        await Assert.ThrowsAsync<ArgumentException>(() => Run(focuser, measurer, options));

        Assert.Empty(measurer.Positions);
        Assert.Equal(20000, focuser.Position);
    }

    [Fact]
    public void TheOptionsHaveSensibleDefaults()
    {
        var options = new AutofocusOptions(TimeSpan.FromSeconds(1), 300, 7);

        options.Validate();
        Assert.True(options.Verify);
        Assert.Equal(2, options.MaxAttempts);
        Assert.Equal(TimeSpan.Zero, options.SettleDelay);
    }

    // Progress

    [Fact]
    public async Task TheProgress_IsReportedInOrder_OnlyWithWhatIsKnown()
    {
        var (focuser, _) = await Focuser(20300);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));
        var reports = new List<AutofocusProgress>();

        var result = await Run(focuser, measurer, Options(), (p, _) => { reports.Add(p); return Task.CompletedTask; });

        Assert.Equal(AutofocusPhase.Measuring, reports[0].Phase);
        Assert.Equal((0, 7), (reports[0].SampleIndex, reports[0].SampleCount));
        Assert.Null(reports[0].Hfr); // nothing measured yet
        var samples = reports.Where(r => r.Phase == AutofocusPhase.Measuring && r.SampleIndex > 0).ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], samples.Select(s => s.SampleIndex));
        Assert.All(samples, s => Assert.Equal(7, s.SampleCount));
        Assert.Equal(result.Measurements.Select(m => m.FocuserPosition), samples.Select(s => s.Position!.Value));
        Assert.Equal(result.Measurements.Select(m => m.Hfr), samples.Select(s => s.Hfr!.Value));
        Assert.Equal(
            [AutofocusPhase.Measuring, AutofocusPhase.Fitting, AutofocusPhase.Moving, AutofocusPhase.Verifying, AutofocusPhase.Completed],
            reports.Select(r => r.Phase).Distinct());
        var done = reports[^1];
        Assert.Equal((result.BestPosition, result.BestHfr), (done.BestPosition, done.BestHfr));
    }

    // Cancellation and failure

    [Fact]
    public async Task CancelledDuringAMeasurement_StopsAtTheLastPositionItReached_NotMoving()
    {
        var (focuser, _) = await Focuser(20000);
        using var cts = new CancellationTokenSource();
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000))
        {
            BeforeMeasure = async (position, ct) =>
            {
                if (position == 19700)
                {
                    await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(focuser, measurer, Options(), ct: cts.Token));

        Assert.Equal(19700, focuser.Position); // where it last arrived, not where it started
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task CancelledDuringAFocuserMove_LeavesNoMovingState()
    {
        var bus = new EventBus();
        var focuser = new SimulatedFocuser(new("focuser.main"), "EAF", bus, 20000, 0, 50000, 100, TimeSpan.FromMilliseconds(1));
        await focuser.ConnectAsync();
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));
        using var cts = new CancellationTokenSource();

        var run = Run(focuser, measurer, Options(), ct: cts.Token); // 900 steps at 100 per second: 9 s
        while (focuser.MotionState != FocuserMotionState.Moving)
        {
            await Task.Delay(2);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(20000, focuser.Position);
    }

    [Fact]
    public async Task AFailingMeasurement_Propagates_AndLeavesTheFocuserIdle()
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, p => p == 19700 ? throw new InvalidOperationException("camera on fire") : 2.0);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(focuser, measurer, Options()));

        Assert.Equal("camera on fire", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task AFailingFocuserMove_Propagates()
    {
        var focuser = new ScriptedFocuser("focuser.main", 20000) { FailOnMove = 3 };
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AutofocusEngine.RunAsync(focuser, measurer, Options()).WaitAsync(Bound));

        Assert.Equal("The focuser motor stalled.", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task AReporterThatFails_FailsTheRun()
    {
        var (focuser, _) = await Focuser(20000);
        var measurer = new FunctionMeasurer(focuser, Curves.Hyperbola(20000));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run(focuser, measurer, Options(), (_, _) => throw new InvalidOperationException("listener")));
    }

    // The fit on its own

    [Fact]
    public void TheFit_OfAHyperbola_IsExact()
    {
        var curve = Curves.Hyperbola(20000, 1.8, 0.0025);
        var samples = new[] { 19100, 19400, 19700, 20000, 20300, 20600, 20900 }.Select(p => new FocusMeasurement(p, curve(p))).ToList();

        var fit = FocusCurveFit.Fit(samples);

        Assert.NotNull(fit);
        Assert.Equal(20000, fit!.Vertex, 3);
        Assert.Equal(1.8, fit.FittedHfr, 6);
        Assert.Equal(1.0, fit.RSquared, 6);
    }

    [Fact]
    public void TheFit_OfAHyperbolaSeenFromOneSide_StillPointsAtTheMinimum()
    {
        var curve = Curves.Hyperbola(20000);
        var samples = new[] { 17100, 17400, 17700, 18000, 18300, 18600, 18900 }.Select(p => new FocusMeasurement(p, curve(p))).ToList();

        var fit = FocusCurveFit.Fit(samples);

        Assert.NotNull(fit);
        Assert.Equal(20000, fit!.Vertex, 0);
    }

    [Fact]
    public void TheFit_WorksOnLargePositionsAndSmallSteps()
    {
        var curve = Curves.Hyperbola(48650, 2.2, 0.05);
        var samples = new[] { 48600, 48620, 48640, 48660, 48680 }.Select(p => new FocusMeasurement(p, curve(p))).ToList();

        var fit = FocusCurveFit.Fit(samples);

        Assert.NotNull(fit);
        Assert.Equal(48650, fit!.Vertex, 2);
    }

    [Fact]
    public void TheFit_ToleratesALittleNoise()
    {
        var curve = Curves.Hyperbola(20000);
        var error = new[] { 0.03, -0.04, 0.02, -0.01, 0.04, -0.03, 0.01 };
        var samples = new[] { 19100, 19400, 19700, 20000, 20300, 20600, 20900 }
            .Select((p, i) => new FocusMeasurement(p, curve(p) + error[i])).ToList();

        var fit = FocusCurveFit.Fit(samples);

        Assert.NotNull(fit);
        Assert.InRange(fit!.Vertex, 19900, 20100);
    }

    [Fact]
    public void TheFit_RefusesTooFewSamples_RepeatedPositions_AndFlatOrConcaveCurves()
    {
        var curve = Curves.Hyperbola(20000);
        FocusMeasurement At(int p, double? hfr = null) => new(p, hfr ?? curve(p));

        Assert.Null(FocusCurveFit.Fit([At(19700), At(20000), At(20300), At(20600)]));
        Assert.Null(FocusCurveFit.Fit([At(19700), At(19700), At(20000), At(20300), At(20600)]));
        Assert.Null(FocusCurveFit.Fit([At(19400, 2), At(19700, 2), At(20000, 2), At(20300, 2), At(20600, 2)]));
        Assert.Null(FocusCurveFit.Fit([At(19400, 2), At(19700, 3), At(20000, 4), At(20300, 3), At(20600, 2)]));
    }

    [Fact]
    public void AMeasurement_NeedsAFiniteHfrGreaterThanZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusMeasurement(100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusMeasurement(100, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusMeasurement(100, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusMeasurement(100, double.PositiveInfinity));
        Assert.Equal((100, 2.5), (new FocusMeasurement(100, 2.5).FocuserPosition, new FocusMeasurement(100, 2.5).Hfr));
    }
}

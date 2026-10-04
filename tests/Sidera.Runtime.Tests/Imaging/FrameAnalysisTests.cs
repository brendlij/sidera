using System.Diagnostics;
using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Imaging;
using Sidera.Core.Rigs;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Imaging;

namespace Sidera.Runtime.Tests.Imaging;

public class FrameAnalysisTests
{
    private static readonly FrameStatistics Stats = new(1000, 0, 100, 10, 10, 5, 10, 3);

    private static DetectedStar Star(double hfr, double flux = 1000, bool saturated = false, bool edge = false, double elongation = 1) =>
        new(10, 10, 500, flux, 10, 20, 8, hfr, elongation, saturated, edge);

    // The aggregate

    [Fact]
    public void TheMedianHfr_IsTheMedianOfTheUsableStars()
    {
        var metrics = FrameMetricsCalculator.From(Stats, [Star(2.0), Star(2.2), Star(2.1), Star(2.4), Star(2.3)]);

        Assert.Equal(5, metrics.StarCount);
        Assert.Equal(5, metrics.UsableStarCount);
        Assert.Equal(2.2, metrics.MedianHfr!.Value, 9);
        Assert.Equal(2.2, metrics.MeanHfr!.Value, 9);
    }

    [Fact]
    public void AnEvenNumberOfStars_HasTheMeanOfTheTwoInTheMiddle()
    {
        var metrics = FrameMetricsCalculator.From(Stats, [Star(2.0), Star(3.0), Star(2.5), Star(2.6)]);

        Assert.Equal(2.55, metrics.MedianHfr!.Value, 9);
    }

    [Fact]
    public void AnOutlier_DoesNotDominateTheMedian_ThoughItMovesTheMean()
    {
        var metrics = FrameMetricsCalculator.From(Stats, [Star(2.0), Star(2.1), Star(2.2), Star(2.1), Star(14.0)]);

        Assert.Equal(2.1, metrics.MedianHfr!.Value, 9);
        Assert.True(metrics.MeanHfr > 4);
    }

    [Fact]
    public void StarsThatAreNotUsable_AreCounted_ButLeftOutOfTheAggregate()
    {
        var metrics = FrameMetricsCalculator.From(Stats,
        [
            Star(2.0), Star(2.2), Star(2.4),
            Star(9.0, saturated: true), Star(9.5, saturated: true),
            Star(8.0, edge: true),
            Star(7.0, elongation: 4),
        ]);

        Assert.Equal(7, metrics.StarCount);
        Assert.Equal(3, metrics.UsableStarCount);
        Assert.Equal(2, metrics.SaturatedStarCount);
        Assert.Equal(2.2, metrics.MedianHfr!.Value, 9);
    }

    [Fact]
    public void WithoutAnyUsableStar_ThereIsNoMedian_NotAMadeUpNumber()
    {
        var metrics = FrameMetricsCalculator.From(Stats, [Star(2.0, saturated: true), Star(3.0, edge: true)]);

        Assert.Equal((2, 0, 1), (metrics.StarCount, metrics.UsableStarCount, metrics.SaturatedStarCount));
        Assert.Null(metrics.MedianHfr);
        Assert.Null(metrics.MeanHfr);
        Assert.Null(metrics.MedianFlux);
    }

    [Fact]
    public void AFrameWithNoStars_HasEmptyMetrics_AndKeepsTheBackground()
    {
        var metrics = FrameMetricsCalculator.From(Stats, []);

        Assert.Equal((0, 0, 0), (metrics.StarCount, metrics.UsableStarCount, metrics.SaturatedStarCount));
        Assert.Equal((10.0, 3.0), (metrics.Background, metrics.BackgroundSigma));
    }

    [Fact]
    public void TheMetrics_DoNotDependOnTheOrderOfTheStars()
    {
        var stars = new[] { Star(2.0, 100), Star(2.6, 300), Star(2.2, 200), Star(2.4, 400), Star(2.1, 500) };

        var forward = FrameMetricsCalculator.From(Stats, stars);
        var backward = FrameMetricsCalculator.From(Stats, stars.Reverse().ToList());

        Assert.Equal(forward, backward);
        Assert.Equal(300, forward.MedianFlux!.Value);
    }

    // The analyzer

    private static CameraFrame SomeStars(int count = 12, int seed = 3, double sigma = 2.2)
    {
        var frame = new TestFrame(600, 400, 900).Noise(8, seed);
        var random = new Random(seed);
        for (var i = 0; i < count; i++)
        {
            frame.Star(40 + random.NextDouble() * 520, 40 + random.NextDouble() * 320, 60_000 + random.NextDouble() * 200_000, sigma);
        }

        return frame.Build();
    }

    [Fact]
    public void TheAnalyzer_GivesStatisticsStarsAndTheirAggregate_InOnePass()
    {
        var analyzer = new FrameAnalyzer();

        var result = analyzer.Analyze(SomeStars(12));

        Assert.Equal(12, result.Stars.Count);
        Assert.Equal(12, result.Metrics.UsableStarCount);
        Assert.InRange(result.Metrics.MedianHfr!.Value, TestFrame.ExpectedHfr(2.2) * 0.93, TestFrame.ExpectedHfr(2.2) * 1.07);
        Assert.InRange(result.Statistics.Background, 895, 905);
        Assert.Equal(result.Statistics.Background, result.Metrics.Background);
    }

    [Fact]
    public void AFrameThatWasAnalysed_IsNotAnalysedAgain_ButAnotherFrameIs()
    {
        var counting = new CountingDetector();
        var analyzer = new FrameAnalyzer(detector: counting);
        var frame = SomeStars();

        var first = analyzer.Analyze(frame);
        var second = analyzer.Analyze(frame);
        analyzer.Analyze(SomeStars(seed: 4));

        Assert.Same(first, second);
        Assert.Equal(2, counting.Calls);
    }

    private sealed class CountingDetector : IStarDetector
    {
        private readonly StarDetector _inner = new();
        public int Calls { get; private set; }

        public IReadOnlyList<DetectedStar> Detect(CameraFrame frame, FrameStatistics statistics, FrameAnalysisOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.Detect(frame, statistics, options, cancellationToken);
        }
    }

    [Fact]
    public void ACancelledAnalysis_Throws_AndLeavesNothingInTheCache()
    {
        var counting = new CountingDetector();
        var analyzer = new FrameAnalyzer(detector: counting);
        var frame = SomeStars();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => analyzer.Analyze(frame, cts.Token));
        var result = analyzer.Analyze(frame);

        Assert.Equal(12, result.Stars.Count);
    }

    [Fact]
    public void TheAnalysis_DoesNotChangeTheFrame()
    {
        var frame = SomeStars();
        var before = frame.Pixels.ToArray();

        new FrameAnalyzer().Analyze(frame);

        Assert.True(frame.Pixels.Span.SequenceEqual(before));
    }

    [Fact]
    public void TheOptionsOfTheAnalyzer_AreValidated_AndAreTheOnesGiven()
    {
        var options = new FrameAnalysisOptions(DetectionSigma: 8, MinimumUsableStars: 3);

        Assert.Same(options, new FrameAnalyzer(options).Options);
        Assert.Throws<ArgumentException>(() => new FrameAnalyzer(options with { MaxStars = 0 }));
    }

    [Fact]
    public void TheDefaults_AreTheOnesThatWereDocumented()
    {
        var options = new FrameAnalysisOptions();

        Assert.Equal((5.0, 5, 20, 4, 65535, 5, 200), (options.DetectionSigma, options.MinStarPixels, options.MaxStarRadius, options.EdgeMargin, options.SaturationLevel, options.MinimumUsableStars, options.MaxStars));
    }

    [Fact]
    public void AFullHdFrameWithManyStars_IsAnalysedInReasonableTime_NotInQuadraticTime()
    {
        var frame = new TestFrame(1920, 1080, 900).Noise(10, seed: 2);
        var random = new Random(11);
        for (var i = 0; i < 300; i++)
        {
            frame.Star(30 + random.NextDouble() * 1860, 30 + random.NextDouble() * 1020, 30_000 + random.NextDouble() * 300_000, 1.8 + random.NextDouble());
        }

        var built = frame.Build();
        var analyzer = new FrameAnalyzer();
        var clock = Stopwatch.StartNew();

        var result = analyzer.Analyze(built);

        clock.Stop();
        Assert.True(result.Stars.Count >= 150, $"found {result.Stars.Count} stars");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}"); // a loose bound: it only catches O(N²)
    }

    [Fact]
    public void AnEmptySkyOfAnyReasonableSize_IsAnalysedWithoutAllocatingPerPixel()
    {
        var frame = new TestFrame(3000, 2000, 900).Noise(10).Build();
        var analyzer = new FrameAnalyzer();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = analyzer.Analyze(frame);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(result.Stars);
        Assert.True(allocated < 12_000_000, $"allocated {allocated} bytes"); // a mask of one byte per pixel, histograms, nothing per pixel
    }

    // The focus metric on the pixels

    private static FocusMetricInput Input(CameraFrame frame, int position = 20000) =>
        new(new RigId("rig.main"), new DeviceId("camera.main"), new DeviceId("focuser.main"), position, frame);

    [Fact]
    public async Task TheMeasurement_IsTheMedianHfrOfTheStarsOfThatFrame_AtTheFocuserPosition()
    {
        var frame = SomeStars(10, sigma: 2.5);
        var provider = new StarHfrFocusMetricProvider(new FrameAnalyzer());

        var measurement = await provider.MeasureAsync(Input(frame, 19800));

        Assert.Equal(19800, measurement.FocuserPosition);
        Assert.InRange(measurement.Hfr, TestFrame.ExpectedHfr(2.5) * 0.93, TestFrame.ExpectedHfr(2.5) * 1.07);
        Assert.Equal(new FrameAnalyzer().Analyze(frame).Metrics.MedianHfr!.Value, measurement.Hfr, 9);
    }

    [Fact]
    public async Task AFrameWithoutStars_FailsWithNoStarsDetected()
    {
        var provider = new StarHfrFocusMetricProvider(new FrameAnalyzer());

        var error = await Assert.ThrowsAsync<NoStarsDetectedException>(() => provider.MeasureAsync(Input(new TestFrame().Noise(8).Build())));

        Assert.Equal("no stars were detected", error.Reason);
        Assert.Equal("Frame analysis failed: no stars were detected.", error.Message);
    }

    [Fact]
    public async Task TooFewUsableStars_FailsWithTheCountsInTheMessage_NeverMeasuresFromThem()
    {
        var provider = new StarHfrFocusMetricProvider(new FrameAnalyzer());

        var error = await Assert.ThrowsAsync<InsufficientStarsException>(() => provider.MeasureAsync(Input(SomeStars(3))));

        Assert.Equal((3, 5), (error.UsableStars, error.RequiredStars));
        Assert.Equal("only 3 usable stars were detected; at least 5 are required", error.Reason);
    }

    [Fact]
    public async Task OneUsableStar_IsAnAnsweredWithTheSingularForm()
    {
        var provider = new StarHfrFocusMetricProvider(new FrameAnalyzer());

        var error = await Assert.ThrowsAsync<InsufficientStarsException>(() => provider.MeasureAsync(Input(SomeStars(1))));

        Assert.Equal("only 1 usable star was detected; at least 5 are required", error.Reason);
    }

    [Fact]
    public async Task TheMinimumNumberOfStars_IsConfigurable_AndOneStarIsEnoughOnlyWhenAskedFor()
    {
        var frame = SomeStars(1);

        var single = await new StarHfrFocusMetricProvider(new FrameAnalyzer(), minimumUsableStars: 1).MeasureAsync(Input(frame));
        await Assert.ThrowsAsync<InsufficientStarsException>(() => new StarHfrFocusMetricProvider(new FrameAnalyzer()).MeasureAsync(Input(frame)));

        Assert.True(single.Hfr > 0);
    }

    [Fact]
    public async Task SaturatedStars_DoNotCount_TowardsTheMinimum_NorIntoTheMedian()
    {
        var frame = new TestFrame(600, 400, 900).Noise(8, 3);
        var random = new Random(5);
        for (var i = 0; i < 6; i++)
        {
            frame.Star(60 + i * 80, 100 + (i % 2) * 150, 150_000, 2.4);
        }

        for (var i = 0; i < 4; i++)
        {
            frame.Star(80 + i * 120, 300, 6_000_000, 4); // saturated and wide: would drag the median up
        }

        var provider = new StarHfrFocusMetricProvider(new FrameAnalyzer());

        var measurement = await provider.MeasureAsync(Input(frame.Build()));

        Assert.InRange(measurement.Hfr, TestFrame.ExpectedHfr(2.4) * 0.93, TestFrame.ExpectedHfr(2.4) * 1.07);
        _ = random;
    }

    [Fact]
    public async Task AFieldOfOnlySaturatedStars_IsInsufficient_NotMeasured()
    {
        var frame = new TestFrame(600, 400, 900).Noise(8, 3);
        for (var i = 0; i < 6; i++)
        {
            frame.Star(60 + i * 80, 200, 6_000_000, 2);
        }

        var error = await Assert.ThrowsAsync<InsufficientStarsException>(() =>
            new StarHfrFocusMetricProvider(new FrameAnalyzer()).MeasureAsync(Input(frame.Build())));

        Assert.Equal(0, error.UsableStars);
    }

    [Fact]
    public async Task ACancelledMeasurement_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new StarHfrFocusMetricProvider(new FrameAnalyzer()).MeasureAsync(Input(SomeStars()), cts.Token));
    }

    [Fact]
    public void TheProvider_KnowsNothingOfASimulation()
    {
        var assemblyTypes = typeof(StarHfrFocusMetricProvider).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Select(f => f.FieldType.Name);

        Assert.DoesNotContain(assemblyTypes, name => name.Contains("Simulated", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(StarHfrFocusMetricProvider).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType.Name.Contains("Simulated", StringComparison.Ordinal));
    }
}

using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Runtime.Imaging;

namespace Sidera.Runtime.Tests.Imaging;

public class FrameStatisticsTests
{
    [Fact]
    public void AFlatFrame_HasThatLevelAsEverything_AndNoNoise()
    {
        var frame = TestFrame.From(50, 40, (_, _) => 1234);

        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.Equal(2000, stats.PixelCount);
        Assert.Equal((1234, 1234), (stats.Min, stats.Max));
        Assert.Equal(1234, stats.Mean);
        Assert.Equal(1234, stats.Median);
        Assert.Equal(0, stats.StandardDeviation);
        Assert.Equal(1234, stats.Background);
        Assert.Equal(0, stats.BackgroundSigma);
    }

    [Fact]
    public void MinMaxAndMean_AreExact()
    {
        var frame = TestFrame.From(4, 2, (x, y) => x + 4 * y * 1000); // 0 1 2 3 / 4000 4001 4002 4003

        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.Equal(0, stats.Min);
        Assert.Equal(4003, stats.Max);
        Assert.Equal((0 + 1 + 2 + 3 + 4000 + 4001 + 4002 + 4003) / 8.0, stats.Mean);
    }

    [Theory]
    [InlineData(new[] { 5 }, 5)]
    [InlineData(new[] { 1, 2, 3 }, 2)]
    [InlineData(new[] { 1, 2, 3, 4 }, 2.5)]
    [InlineData(new[] { 10, 10, 10, 90, 10 }, 10)]
    [InlineData(new[] { 0, 65535 }, 32767.5)]
    public void TheMedian_IsTheMiddleValue_OrTheMeanOfTheTwoInTheMiddle(int[] values, double expected)
    {
        var frame = TestFrame.From(values.Length, 1, (x, _) => values[x]);

        Assert.Equal(expected, FrameStatisticsCalculator.Compute(frame).Median);
    }

    [Fact]
    public void TheBackground_IsTheMedian_NotTheMean_SoStarsDoNotMoveIt()
    {
        var frame = new TestFrame(300, 200, 1000)
            .Star(60, 60, 400_000, 2)
            .Star(150, 100, 400_000, 2)
            .Star(240, 140, 400_000, 2)
            .Build();

        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.Equal(1000, stats.Background);
        Assert.True(stats.Mean > stats.Background); // the stars pull the mean, not the background
    }

    [Fact]
    public void TheNoise_IsTheMadScaledToASigma_AndMatchesTheNoiseThatWasAdded()
    {
        var frame = new TestFrame(400, 300, 1000).Noise(20, seed: 3).Build();

        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.InRange(stats.BackgroundSigma, 19, 21);
        Assert.InRange(stats.Background, 999, 1001);
        Assert.InRange(stats.StandardDeviation, 19, 21);
    }

    [Fact]
    public void ManyBrightStars_DoNotInflateTheNoise_ThoughTheyInflateTheStandardDeviation()
    {
        var plain = new TestFrame(400, 300, 1000).Noise(20, seed: 5);
        var crowded = new TestFrame(400, 300, 1000).Noise(20, seed: 5);
        var random = new Random(9);
        for (var i = 0; i < 25; i++)
        {
            crowded.Star(20 + random.NextDouble() * 360, 20 + random.NextDouble() * 260, 300_000, 2);
        }

        var a = FrameStatisticsCalculator.Compute(plain.Build());
        var b = FrameStatisticsCalculator.Compute(crowded.Build());

        Assert.InRange(b.BackgroundSigma, a.BackgroundSigma * 0.95, a.BackgroundSigma * 1.1);
        Assert.InRange(b.Background, a.Background - 2, a.Background + 2);
        Assert.True(b.StandardDeviation > a.StandardDeviation * 1.5);
    }

    [Fact]
    public void TheFullRangeOfSixteenBits_IsHandled()
    {
        var frame = TestFrame.From(2, 2, (x, y) => x + y == 0 ? 0 : 65535);

        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.Equal((0, 65535), (stats.Min, stats.Max));
        Assert.Equal(65535, stats.Median);
    }

    [Fact]
    public void ACancelledCalculation_Throws()
    {
        var frame = TestFrame.From(100, 100, (_, _) => 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => FrameStatisticsCalculator.Compute(frame, cts.Token));
    }

    [Fact]
    public void NoFrame_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => FrameStatisticsCalculator.Compute(null!));
    }

    [Fact]
    public void ABigFrame_IsDoneInOnePassOfTheHistogram_WithoutAllocatingPerPixel()
    {
        var frame = TestFrame.From(4000, 3000, (x, y) => 800 + (x * 7 + y * 13) % 50);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var stats = FrameStatisticsCalculator.Compute(frame);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(12_000_000, stats.PixelCount);
        Assert.True(allocated < 2_000_000, $"allocated {allocated} bytes for 12 million pixels"); // two histograms, nothing per pixel
    }
}

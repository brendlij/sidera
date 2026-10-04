using Sidera.Core.Devices;
using Sidera.Core.Imaging;
using Sidera.Runtime.Imaging;

namespace Sidera.Runtime.Tests.Imaging;

/// <summary>
/// The detector on frames that were made without it: Gaussian stars of a known sigma, whose half flux radius is
/// sigma · sqrt(2 ln 2) by mathematics, not by anything in the code under test.
/// </summary>
public class StarDetectorTests
{
    private static readonly FrameAnalysisOptions Options = new();

    private static IReadOnlyList<DetectedStar> Detect(CameraFrame frame, FrameAnalysisOptions? options = null, CancellationToken ct = default)
    {
        var stats = FrameStatisticsCalculator.Compute(frame, ct);
        return new StarDetector().Detect(frame, stats, options ?? Options, ct);
    }

    // Finding stars

    [Fact]
    public void AFlatBackground_HasNoStars()
    {
        Assert.Empty(Detect(new TestFrame().Build()));
    }

    [Fact]
    public void ABackgroundWithNoise_HasNoStars()
    {
        Assert.Empty(Detect(new TestFrame().Noise(25, seed: 4).Build()));
    }

    [Fact]
    public void AnIsolatedStar_IsFound_WhereItIs()
    {
        var frame = new TestFrame().Noise(10).Star(150.3, 120.7, 60_000, 2).Build();

        var star = Assert.Single(Detect(frame));

        Assert.Equal(150.3, star.X, 1);
        Assert.Equal(120.7, star.Y, 1);
        Assert.True(star.IsUsable);
        Assert.False(star.IsSaturated);
        Assert.False(star.TouchesEdge);
    }

    [Fact]
    public void SeveralStars_AreAllFound_BrightestFirst()
    {
        var frame = new TestFrame().Noise(10)
            .Star(80, 70, 30_000, 2)
            .Star(200, 150, 120_000, 2)
            .Star(320, 220, 60_000, 2)
            .Build();

        var stars = Detect(frame);

        Assert.Equal(3, stars.Count);
        Assert.Equal([(200, 150), (320, 220), (80, 70)], stars.Select(s => ((int)Math.Round(s.X), (int)Math.Round(s.Y))));
        Assert.True(stars[0].Flux > stars[1].Flux && stars[1].Flux > stars[2].Flux);
    }

    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(100.5, 100.5)]
    [InlineData(100.25, 100.75)]
    [InlineData(100.9, 100.1)]
    public void TheCentroid_HasSubpixelPrecision(double x, double y)
    {
        var frame = new TestFrame().Noise(8).Star(x, y, 80_000, 2).Build();

        var star = Assert.Single(Detect(frame));

        Assert.InRange(star.X, x - 0.05, x + 0.05);
        Assert.InRange(star.Y, y - 0.05, y + 0.05);
    }

    // The half flux radius

    // Pixels are points here: a star of a pixel or two in sigma is under-sampled, and its HFR reads a few percent low.
    [Theory]
    [InlineData(1.5, 0.08)]
    [InlineData(2.0, 0.06)]
    [InlineData(3.0, 0.06)]
    [InlineData(4.0, 0.06)]
    [InlineData(6.0, 0.06)]
    public void TheHfr_OfAGaussianStar_IsSigmaTimesRootTwoLnTwo(double sigma, double tolerance)
    {
        var frame = new TestFrame().Noise(8).Star(200.4, 150.6, 150_000, sigma).Build();

        var star = Assert.Single(Detect(frame));

        var expected = TestFrame.ExpectedHfr(sigma);
        Assert.InRange(star.Hfr, expected * (1 - tolerance), expected * (1 + tolerance));
    }

    [Theory]
    [InlineData(40_000)]
    [InlineData(100_000)]
    [InlineData(400_000)]
    public void TheHfr_DoesNotDependOnHowBrightTheStarIs(double flux)
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, flux, 2.5).Build();

        var star = Assert.Single(Detect(frame));

        var expected = TestFrame.ExpectedHfr(2.5);
        Assert.InRange(star.Hfr, expected * 0.93, expected * 1.07);
    }

    [Fact]
    public void ABroaderStar_HasALargerHfr_InSteps()
    {
        var hfrs = new[] { 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 5.0 }
            .Select(sigma => Assert.Single(Detect(new TestFrame().Noise(8).Star(200, 150, 200_000, sigma).Build())).Hfr)
            .ToList();

        for (var i = 1; i < hfrs.Count; i++)
        {
            Assert.True(hfrs[i] > hfrs[i - 1], $"{string.Join(", ", hfrs)}");
        }
    }

    [Fact]
    public void TheHfr_IsNotTheHalfWidthAtHalfMaximum_NorTheRadiusOfTheBoundingBox()
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, 150_000, 3).Build();

        var star = Assert.Single(Detect(frame));

        // HWHM of a Gaussian is sigma · sqrt(2 ln 2) as well, but the bounding radius of the region is far larger.
        Assert.True(star.Hfr < star.Radius);
        Assert.InRange(star.Hfr, 2.9, 4.1);
    }

    [Fact]
    public void TheFlux_IsTheBackgroundSubtractedFluxOfTheStar()
    {
        var frame = new TestFrame().Noise(5).Star(200, 150, 100_000, 2).Build();

        var star = Assert.Single(Detect(frame));

        Assert.InRange(star.Flux, 90_000, 115_000);
        Assert.InRange(star.Background, 995, 1005);
    }

    [Fact]
    public void TheHfr_IsTheSameForAStarWhereverItSits_IncludingBetweenPixels()
    {
        var hfrs = new[] { (100.0, 100.0), (100.5, 100.5), (100.3, 100.8), (200.5, 150.0) }
            .Select(p => Assert.Single(Detect(new TestFrame().Noise(8).Star(p.Item1, p.Item2, 120_000, 2.5).Build())).Hfr)
            .ToList();

        Assert.True(hfrs.Max() - hfrs.Min() < 0.15, string.Join(", ", hfrs));
    }

    // What is not a good star

    [Fact]
    public void AHotPixel_IsNoStar()
    {
        var frame = new TestFrame().Noise(8).Pixel(100, 100, 30_000).Pixel(250, 180, 50_000).Build();

        Assert.Empty(Detect(frame));
    }

    [Fact]
    public void AHotPixelNextToAnother_StillIsNoStar_BelowTheMinimumArea()
    {
        var frame = new TestFrame().Noise(8).Pixel(100, 100, 30_000).Pixel(101, 100, 30_000).Pixel(100, 101, 30_000).Pixel(101, 101, 30_000).Build();

        Assert.Empty(Detect(frame));
    }

    [Fact]
    public void AHotPixelDoesNotChangeAStarNextToIt_OrTheCountOfStars()
    {
        var clean = new TestFrame().Noise(8).Star(200, 150, 100_000, 2).Build();
        var hot = new TestFrame().Noise(8).Star(200, 150, 100_000, 2).Pixel(60, 60, 40_000).Build();

        Assert.Single(Detect(hot));
        Assert.Equal(Assert.Single(Detect(clean)).Hfr, Assert.Single(Detect(hot)).Hfr, 6);
    }

    [Fact]
    public void AStarAtTheEdge_IsFound_ButIsClipped_AndNotUsable()
    {
        var frame = new TestFrame().Noise(8).Star(3, 150, 100_000, 2).Star(200, 2, 100_000, 2).Star(200, 150, 100_000, 2).Build();

        var stars = Detect(frame);

        Assert.Single(stars, s => s.IsUsable);
        Assert.Equal(2, stars.Count(s => s.TouchesEdge));
        Assert.All(stars.Where(s => s.TouchesEdge), s => Assert.False(s.IsUsable));
    }

    [Fact]
    public void AStarJustInsideTheMargin_IsNotClipped()
    {
        var frame = new TestFrame().Noise(8).Star(60, 150, 100_000, 2).Build();

        Assert.False(Assert.Single(Detect(frame)).TouchesEdge);
    }

    [Fact]
    public void ASaturatedStar_IsFlagged_NotUsable_AndCounted()
    {
        var frame = new TestFrame().Noise(8).Star(100, 100, 3_000_000, 2).Star(250, 180, 100_000, 2).Build();

        var stars = Detect(frame);

        var saturated = Assert.Single(stars, s => s.IsSaturated);
        Assert.False(saturated.IsUsable);
        Assert.Single(stars, s => s.IsUsable);
    }

    [Fact]
    public void TheSaturationLevel_IsAnOption()
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, 100_000, 2).Build(); // peak about 5000

        var normal = Assert.Single(Detect(frame));
        var lowLevel = Assert.Single(Detect(frame, Options with { SaturationLevel = 3000 }));

        Assert.False(normal.IsSaturated);
        Assert.True(lowLevel.IsSaturated);
    }

    [Fact]
    public void AStreak_IsElongated_AndNotUsable()
    {
        var frame = new TestFrame().Noise(8);
        for (var i = 0; i < 20; i++)
        {
            frame.Star(150 + i, 150, 12_000, 1.5); // a trail along x
        }

        var stars = Detect(frame.Build());

        Assert.All(stars, s => Assert.False(s.IsUsable && s.Elongation < 1.5));
        Assert.DoesNotContain(stars, s => s.IsUsable);
    }

    [Fact]
    public void ARegionThatIsFarTooBig_IsNoStar()
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, 5_000_000, 22).Build();

        Assert.DoesNotContain(Detect(frame), s => s.IsUsable);
    }

    [Fact]
    public void TwoCloseStars_AreADeterministicResult_AndNeitherIsReportedAsUsableWhenTheyBlend()
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, 100_000, 2).Star(205, 150, 100_000, 2).Build();

        var first = Detect(frame);
        var second = Detect(frame);

        Assert.Equal(first, second, new StarComparer());
        Assert.DoesNotContain(first, s => s.IsUsable && s.Elongation > DetectedStar.MaxElongation);
        Assert.Single(first); // one bright region: the detector does not deblend
    }

    [Fact]
    public void TwoStarsThatAreFarApart_AreTwoStars()
    {
        var frame = new TestFrame().Noise(8).Star(100, 100, 100_000, 2).Star(300, 200, 100_000, 2).Build();

        Assert.Equal(2, Detect(frame).Count);
    }

    private sealed class StarComparer : IEqualityComparer<DetectedStar>
    {
        public bool Equals(DetectedStar? x, DetectedStar? y) => x == y;
        public int GetHashCode(DetectedStar obj) => obj.GetHashCode();
    }

    // Options and robustness

    [Fact]
    public void AFaintStarBelowTheThreshold_IsNotFound_AndAHigherSigmaFindsFewer()
    {
        var frame = new TestFrame().Noise(20).Star(100, 100, 3_000, 2).Star(200, 150, 100_000, 2).Build();

        var normal = Detect(frame);
        var strict = Detect(frame, Options with { DetectionSigma = 50 });

        Assert.Single(strict);
        Assert.True(normal.Count >= strict.Count);
    }

    [Fact]
    public void TheNumberOfStars_IsLimited_KeepingTheBrightest()
    {
        var frame = new TestFrame().Noise(8);
        for (var i = 0; i < 10; i++)
        {
            frame.Star(40 + i * 35, 100 + (i % 3) * 40, 40_000 + i * 20_000, 2);
        }

        var stars = Detect(frame.Build(), Options with { MaxStars = 4 });

        Assert.Equal(4, stars.Count);
        Assert.True(stars.All(s => s.Flux > 150_000));
    }

    [Fact]
    public void TheResultIsTheSame_EveryTime()
    {
        var frame = new TestFrame().Noise(12, seed: 6).Star(80, 70, 50_000, 2).Star(200, 150, 90_000, 2.5).Star(300, 220, 70_000, 3).Build();

        Assert.Equal(Detect(frame), Detect(frame), new StarComparer());
    }

    [Fact]
    public void ABackgroundThatIsNotLow_IsNoProblem()
    {
        var frame = new TestFrame(background: 20_000).Noise(30).Star(200, 150, 200_000, 2.5).Build();

        var star = Assert.Single(Detect(frame));

        var expected = TestFrame.ExpectedHfr(2.5);
        Assert.InRange(star.Hfr, expected * 0.93, expected * 1.07);
    }

    [Fact]
    public void ANoisyFrame_StillGivesTheHfrWithinTolerance()
    {
        var frame = new TestFrame().Noise(40, seed: 8).Star(200, 150, 400_000, 2.5).Build();

        var star = Assert.Single(Detect(frame));

        var expected = TestFrame.ExpectedHfr(2.5);
        Assert.InRange(star.Hfr, expected * 0.9, expected * 1.1);
    }

    [Fact]
    public void ACancelledDetection_Throws()
    {
        var frame = new TestFrame().Noise(8).Star(200, 150, 100_000, 2).Build();
        using var cts = new CancellationTokenSource();
        var stats = FrameStatisticsCalculator.Compute(frame);
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => new StarDetector().Detect(frame, stats, Options, cts.Token));
    }

    [Fact]
    public void BadOptions_AreRefused()
    {
        var frame = new TestFrame().Build();
        var stats = FrameStatisticsCalculator.Compute(frame);

        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { DetectionSigma = 0 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { MinStarPixels = 1 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { EdgeMargin = -1 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { SaturationLevel = 0 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { MinimumUsableStars = 0 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { MaxStars = 0 }));
        Assert.Throws<ArgumentException>(() => new StarDetector().Detect(frame, stats, Options with { MaxStarRadius = 1 }));
    }
}

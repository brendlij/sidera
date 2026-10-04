using Sidera.Core.Guiding;

namespace Sidera.Phd2.Tests;

/// <summary>What a guide step of PHD2 becomes: samples in arcseconds with the signs of PHD2, the rolling RMS, and a history that stays bounded.</summary>
public sealed class Phd2TelemetryTests
{
    private static Task<GuiderHarness> WithScale(double scale) =>
        GuiderHarness.GuidingAsync(s => s.On("get_pixel_scale", _ => FakeReply.Result(scale)));

    private static async Task<GuidingSample> NextSample(GuiderHarness h, Func<Task> send)
    {
        var before = h.Guider.History.Count;
        var version = h.Guider.History.Version;
        await send();
        await GuiderHarness.Until(() => h.Guider.History.Version > version, "a sample");
        _ = before;
        return h.Guider.History.Snapshot()[^1];
    }

    [Fact]
    public async Task AGuideStep_BecomesASample_WithThePixelsOfPhd2_AndArcsecondsFromThePixelScale()
    {
        await using var h = await WithScale(2.0);

        var sample = await NextSample(h, () => h.Step(0.5, -0.25, new
        {
            RADuration = 120, RADirection = "West", DECDuration = 40, DECDirection = "South", SNR = 28.4, StarMass = 1234.0, ErrorCode = 0,
        }));

        Assert.Equal((0.5, -0.25), (sample.RaErrorPixels, sample.DecErrorPixels));
        Assert.Equal((1.0, -0.5), (sample.RaErrorArcsec, sample.DecErrorArcsec)); // pixels times 2.0 arcseconds per pixel, signs kept
        Assert.Equal(Math.Sqrt(1.25), sample.TotalErrorArcsec!.Value, 9);
        Assert.Equal((-120.0, -40.0), (sample.RaPulseMilliseconds, sample.DecPulseMilliseconds)); // west and south are negative
        Assert.Equal(28.4, sample.StarSnr);
        var telemetry = h.Guider.Telemetry!;
        Assert.Equal((28.4, 1234.0, 2.0), (telemetry.StarSnr, telemetry.StarMass, telemetry.PixelScaleArcsecPerPixel));
    }

    [Fact]
    public async Task EastAndNorthPulses_ArePositive_AndAnErrorKeepsItsSign()
    {
        await using var h = await WithScale(1.0);

        var sample = await NextSample(h, () => h.Step(-0.7, 0.3, new { RADuration = 80, RADirection = "East", DECDuration = 10, DECDirection = "North" }));

        Assert.Equal((-0.7, 0.3), (sample.RaErrorArcsec, sample.DecErrorArcsec));
        Assert.Equal((80.0, 10.0), (sample.RaPulseMilliseconds, sample.DecPulseMilliseconds));
    }

    [Fact]
    public async Task WhatPhd2DoesNotSend_IsUnknown_NeverZero()
    {
        await using var h = await WithScale(1.0);

        var sample = await NextSample(h, () => h.Step(0.4, null)); // no declination, no pulses, no SNR

        Assert.Equal(0.4, sample.RaErrorArcsec);
        Assert.Null(sample.DecErrorPixels);
        Assert.Null(sample.DecErrorArcsec);
        Assert.Null(sample.TotalErrorArcsec);
        Assert.Null(sample.RaPulseMilliseconds);
        Assert.Null(sample.DecPulseMilliseconds);
        Assert.Null(sample.StarSnr);
        var rms = h.Guider.Telemetry!.Rms;
        Assert.Equal(0.4, rms.RaArcsec!.Value, 9);
        Assert.Null(rms.DecArcsec);
        Assert.Null(rms.TotalArcsec);
    }

    [Fact]
    public async Task AStepWithAStarFinderError_HasNoErrorValues_InsteadOfZeros()
    {
        await using var h = await WithScale(1.0);

        var sample = await NextSample(h, () => h.Step(0.0, 0.0, new { ErrorCode = 2 })); // low SNR: the offsets of such a step mean nothing

        Assert.Null(sample.RaErrorPixels);
        Assert.Null(sample.DecErrorArcsec);
        Assert.Null(h.Guider.Telemetry!.Rms.TotalArcsec);
    }

    [Fact]
    public async Task WhileThePixelScaleIsNotKnown_TheArcsecondsAreUnknown_ThePixelsAreStillThere()
    {
        await using var h = await GuiderHarness.GuidingAsync(s => s.On("get_pixel_scale", _ => FakeReply.Error(1, "no frame yet")));

        var sample = await NextSample(h, () => h.Step(0.5, 0.5));

        Assert.Equal((0.5, 0.5), (sample.RaErrorPixels, sample.DecErrorPixels));
        Assert.Null(sample.RaErrorArcsec);
        Assert.Null(sample.DecErrorArcsec);
        Assert.Null(h.Guider.Telemetry!.PixelScaleArcsecPerPixel);
        Assert.Null(h.Guider.Telemetry.Rms.TotalArcsec);
    }

    [Fact]
    public async Task TheRms_IsTheRootMeanSquareOfEachAxis_AndTheTotalIsTheirRootSumOfSquares()
    {
        await using var h = await WithScale(1.0);

        foreach (var ra in new[] { 3.0, -3.0, 3.0, -3.0 })
        {
            await h.Step(ra, 4.0);
        }

        await GuiderHarness.Until(() => h.Guider.History.Count == 4, "four samples");
        var rms = h.Guider.Telemetry!.Rms;
        Assert.Equal((3.0, 4.0, 5.0, 4), (rms.RaArcsec!.Value, rms.DecArcsec!.Value, rms.TotalArcsec!.Value, rms.Samples));
    }

    [Fact]
    public async Task TheHistory_IsBounded_UnderAHighRateOfSteps_AndNoStepIsLost()
    {
        await using var h = await WithScale(1.0);

        for (var i = 0; i < 12_000; i++)
        {
            await h.Step(0.1, 0.1);
        }

        await GuiderHarness.Until(() => h.Guider.History.Version >= 12_000, "all the steps");
        Assert.Equal(GuidingHistory.DefaultMaxSamples, h.Guider.History.Count);
        Assert.NotNull(h.Guider.Telemetry!.Rms.TotalArcsec);
    }

    [Fact]
    public async Task TheHistory_IsClearedByANewConnection_NotByAStop()
    {
        await using var h = await WithScale(1.0);
        await h.Step(0.1, 0.1);
        await GuiderHarness.Until(() => h.Guider.History.Count == 1, "a sample");
        await h.Server.Event("GuidingStopped");
        await h.UntilState(Sidera.Core.Guiding.GuidingState.Idle);
        Assert.Equal(1, h.Guider.History.Count);

        await h.Guider.DisconnectAsync();
        Assert.Equal(1, h.Guider.History.Count); // still there to look at
        await h.Guider.ConnectAsync();

        Assert.Equal(0, h.Guider.History.Count);
    }

    // ---- The history by itself

    private static GuidingSample At(double seconds, double? ra = 1.0, double? dec = 1.0) =>
        new(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds), ra, dec, ra, dec);

    [Fact]
    public void OldSamples_AreTrimmed_ByAge()
    {
        var history = new GuidingHistory(TimeSpan.FromSeconds(10));

        for (var t = 0; t <= 30; t++)
        {
            history.Add(At(t));
        }

        var times = history.Snapshot().Select(s => (s.Time - DateTimeOffset.UnixEpoch).TotalSeconds).ToArray();
        Assert.Equal(20, times[0]);
        Assert.Equal(30, times[^1]);
    }

    [Fact]
    public void TheCount_IsBounded_AndTheOldestGoFirst()
    {
        var history = new GuidingHistory(TimeSpan.FromHours(1), maxSamples: 5);

        for (var t = 0; t < 12; t++)
        {
            history.Add(At(t));
        }

        Assert.Equal([7, 8, 9, 10, 11], history.Snapshot().Select(s => (s.Time - DateTimeOffset.UnixEpoch).TotalSeconds));
    }

    [Fact]
    public void ASnapshotOfAWindow_HoldsOnlyTheLastOfIt()
    {
        var history = new GuidingHistory();
        for (var t = 0; t < 100; t++)
        {
            history.Add(At(t));
        }

        Assert.Equal(61, history.Snapshot(TimeSpan.FromSeconds(60)).Length);
        Assert.Equal(100, history.Snapshot().Length);
    }

    [Fact]
    public void TheHistory_SaysWhenItChanged_AndCanBeCleared()
    {
        var history = new GuidingHistory();
        var changes = 0;
        history.Changed += (_, _) => changes++;

        history.Add(At(0));
        history.Add(At(1));
        history.Clear();

        Assert.Equal((3, 0, 3), (changes, history.Count, (int)history.Version));
    }

    [Fact]
    public void TheRmsOverAWindow_ConsidersOnlyTheSamplesInIt_AndIgnoresWhatIsUnknown()
    {
        var history = new GuidingHistory();
        history.Add(At(0, 100, 100)); // outside the window
        history.Add(At(50, 3, null));
        history.Add(At(60, -3, 4));

        var rms = history.Rms(TimeSpan.FromSeconds(30));

        Assert.Equal(3.0, rms.RaArcsec!.Value, 9);
        Assert.Equal(4.0, rms.DecArcsec!.Value, 9); // only the one sample that had a declination
        Assert.Equal(5.0, rms.TotalArcsec!.Value, 9);
    }

    [Fact]
    public void TheRmsOfNothing_IsUnknown_NotZero()
    {
        var rms = new GuidingHistory().Rms(TimeSpan.FromSeconds(60));

        Assert.Null(rms.RaArcsec);
        Assert.Null(rms.TotalArcsec);
        Assert.Equal(0, rms.Samples);
    }
}

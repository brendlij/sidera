using Sidera.Core.Guiding;

namespace Sidera.Phd2.Tests;

/// <summary>
/// Dither and settle through PHD2: Sidera asks for a dither with its settle, PHD2 moves the lock position and settles, and the dither is
/// over when the settle is, not when the request was accepted.
/// </summary>
public sealed class Phd2DitherTests
{
    private static readonly GuidingSettleOptions Settle = GuiderHarness.Settle;

    // PHD2 accepts the dither, moves the lock position, begins to settle, and ends the settle when the test says so.
    private sealed class Script
    {
        public TaskCompletionSource Release { get; } = new();
        public object? Done { get; set; } = new { Status = 0, TotalFrames = 9, DroppedFrames = 0 };

        public void Install(FakePhd2Server server) =>
            server.On("dither", _ =>
            {
                var fired = Task.Run(async () =>
                {
                    await server.Event("GuidingDithered", new { dx = 2.1, dy = -1.3 });
                    await server.Event("SettleBegin");
                    await server.Event("Settling", new { Distance = 2.4, Time = 0.0, SettleTime = 0.3, StarLocked = true });

                    await Release.Task;
                    await server.Event("SettleDone", Done);
                });
                return FakeReply.Result();
            });
    }

    [Fact]
    public async Task ADither_SendsTheAmountTheAxesAndTheSettleOfTheRequestToPhd2()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);

        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.5, RaOnly: true, Settle)));

        var request = h.Server.Requests.Single(r => r.Method == "dither").Params!.Value;
        Assert.Equal(3.5, request.GetProperty("amount").GetDouble());
        Assert.True(request.GetProperty("raOnly").GetBoolean());
        var settle = request.GetProperty("settle");
        Assert.Equal((1.0, 0.3, 5.0), (settle.GetProperty("pixels").GetDouble(), settle.GetProperty("time").GetDouble(), settle.GetProperty("timeout").GetDouble()));
        script.Release.SetResult();
    }

    [Fact]
    public async Task TheDither_IsOverOnlyWhenPhd2HasSettled_NotWhenTheCommandIsAccepted()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);

        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        var settle = h.Guider.SettleAsync(Settle);
        await h.UntilState(GuidingState.Settling);
        await Task.Delay(150);

        Assert.False(settle.IsCompleted); // the lock position moved, the settle is not over
        script.Release.SetResult();
        await GuiderHarness.Soon(settle);

        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
        Assert.Equal([GuidingState.Dithering, GuidingState.Settling, GuidingState.Guiding], h.Events.States.Skip(1)); // the first change is the connect, to a PHD2 that guides
        var status = h.Guider.Telemetry!.Settle;
        Assert.Equal((GuidingSettleOutcome.Settled, false), (status.LastOutcome, status.IsActive));
        Assert.NotNull(status.LastDuration);
    }

    [Fact]
    public async Task WhileSettling_TheTelemetrySaysHowFarItIs()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        await GuiderHarness.Until(() => h.Guider.Telemetry!.Settle.DistancePixels == 2.4, "the first settling progress");
        await h.Server.Event("Settling", new { Distance = 0.8, Time = 0.2, SettleTime = 0.3, StarLocked = true });
        await GuiderHarness.Until(() => h.Guider.Telemetry!.Settle.DistancePixels == 0.8, "the settling progress");

        var status = h.Guider.Telemetry!.Settle;

        Assert.True(status.IsActive);
        Assert.Equal((0.8, 1.0), (status.DistancePixels, status.TolerancePixels));
        Assert.Equal((TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.3), TimeSpan.FromSeconds(5)), (status.StableFor, status.RequiredStable, status.Timeout));
        Assert.True(status.Elapsed >= TimeSpan.Zero);
        script.Release.SetResult();
    }

    [Fact]
    public async Task ASettleThatTimesOut_IsATimeout_AndGuidingGoesOn()
    {
        var script = new Script { Done = new { Status = 1, Error = "Settling timed out", TotalFrames = 30, DroppedFrames = 2 } };
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        var settle = h.Guider.SettleAsync(Settle);

        script.Release.SetResult();

        await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => GuiderHarness.Soon(settle));
        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
        var status = h.Guider.Telemetry!.Settle;
        Assert.Equal(GuidingSettleOutcome.TimedOut, status.LastOutcome);
        Assert.Contains("timed out", status.FailureReason);
    }

    [Fact]
    public async Task ALostStarDuringTheSettle_IsAFailureWithItsReason_NotASilentContinue()
    {
        var script = new Script { Done = new { Status = 1, Error = "Star lost while settling", TotalFrames = 6, DroppedFrames = 6 } };
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        var settle = h.Guider.SettleAsync(Settle);
        await h.Server.Event("StarLost", new { Frame = 4 });

        script.Release.SetResult();

        var error = await Assert.ThrowsAsync<Phd2SettleFailedException>(() => GuiderHarness.Soon(settle));
        Assert.Contains("Star lost while settling", error.Message);
        Assert.Equal(GuidingSettleOutcome.Failed, h.Guider.Telemetry!.Settle.LastOutcome);
    }

    [Fact]
    public async Task AFailedSettle_ThatFailsTheDitherWithoutASettleOfItsOwn_FailsTheDither()
    {
        var script = new Script { Done = new { Status = 1, Error = "Guiding stopped", TotalFrames = 0, DroppedFrames = 0 } };
        await using var h = await GuiderHarness.GuidingAsync(script.Install);

        var dither = h.Guider.DitherAsync(new DitherRequest(3.0)); // no settle of the caller: the dither is over when the default settle is
        await GuiderHarness.Until(() => h.Guider.GuidingState == GuidingState.Settling, "the settle");
        Assert.False(dither.IsCompleted);
        script.Release.SetResult();

        await Assert.ThrowsAsync<Phd2SettleFailedException>(() => GuiderHarness.Soon(dither));
    }

    [Fact]
    public async Task ADitherWithoutASettleOfTheCaller_UsesTheDefaultSettle_AndWaitsForItsEnd()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);

        var dither = h.Guider.DitherAsync(3.0);
        await GuiderHarness.Until(() => h.Server.Methods.Contains("dither"), "the request");
        await Task.Delay(100);
        Assert.False(dither.IsCompleted);
        script.Release.SetResult();
        await GuiderHarness.Soon(dither);

        var settle = h.Server.Requests.Single(r => r.Method == "dither").Params!.Value.GetProperty("settle");
        Assert.Equal((1.5, 0.5, 10.0), (settle.GetProperty("pixels").GetDouble(), settle.GetProperty("time").GetDouble(), settle.GetProperty("timeout").GetDouble()));
    }

    [Fact]
    public async Task ConnectionLostDuringTheSettle_FailsTheWait_AndTheGuiderIsFaulted()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        var settle = h.Guider.SettleAsync(Settle);
        await h.UntilState(GuidingState.Settling);

        h.Server.Drop();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => GuiderHarness.Soon(settle));
        await GuiderHarness.Until(() => h.Guider.ConnectionState == Sidera.Core.Devices.DeviceConnectionState.Faulted, "the loss");
        await h.UntilState(GuidingState.Idle);
        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
        Assert.Null(h.Guider.Telemetry);
    }

    [Fact]
    public async Task ConnectionLostBeforeTheDitherIsReported_FailsTheDither()
    {
        await using var h = await GuiderHarness.GuidingAsync(s => s.On("dither", _ => FakeReply.Result())); // accepted, and then nothing

        var dither = h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle));
        await GuiderHarness.Until(() => h.Server.Methods.Contains("dither"), "the request");
        h.Server.Drop();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => GuiderHarness.Soon(dither));
    }

    [Fact]
    public async Task CancellingTheSettleWait_CancelsTheWait_AndGuidingGoesOn()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        using var cts = new CancellationTokenSource();
        var settle = h.Guider.SettleAsync(Settle, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settle);
        Assert.DoesNotContain("stop_capture", h.Server.Methods); // guiding itself is not touched
        script.Release.SetResult();
        await h.UntilState(GuidingState.Guiding);
    }

    [Fact]
    public async Task CancellingTheDither_BeforePhd2ReportsIt_ThrowsCancellation_AndTheStateFollowsPhd2Afterwards()
    {
        await using var h = await GuiderHarness.GuidingAsync(s => s.On("dither", _ => FakeReply.Result()));
        using var cts = new CancellationTokenSource();
        var dither = h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle), cts.Token);
        await GuiderHarness.Until(() => h.Server.Methods.Contains("dither"), "the request");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dither);
    }

    [Fact]
    public async Task ADitherWhileNotGuiding_IsRefused_WithoutARequest()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));

        Assert.DoesNotContain("dither", h.Server.Methods);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task ADitherAmountThatIsNotAPositiveNumber_IsRefused(double amount)
    {
        await using var h = await GuiderHarness.GuidingAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Guider.DitherAsync(amount));

        Assert.DoesNotContain("dither", h.Server.Methods);
    }

    [Fact]
    public async Task ADitherThatPhd2Refuses_ForExampleBecauseItIsStillSettling_FailsAndTheStateGoesBack()
    {
        await using var h = await GuiderHarness.GuidingAsync(s => s.On("dither", _ => FakeReply.Error(1, "operation not allowed while settling (re-entrancy)")));

        var error = await Assert.ThrowsAsync<Phd2RpcException>(() => h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));

        Assert.Contains("re-entrancy", error.Reason);
        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
    }

    [Fact]
    public async Task ASecondDither_IsRefusedWhileTheFirstSettles()
    {
        var script = new Script();
        await using var h = await GuiderHarness.GuidingAsync(script.Install);
        await GuiderHarness.Soon(h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));
        await h.UntilState(GuidingState.Settling);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.DitherAsync(new DitherRequest(3.0, false, Settle)));

        Assert.Single(h.Server.Requests, r => r.Method == "dither");
        script.Release.SetResult();
    }

    // ---- A settle without one of PHD2 to wait for: the criterion applied to the guide steps

    [Fact]
    public async Task ASettleOnRequest_IsMetWhenTheGuideErrorStaysWithinTheToleranceForTheStableTime()
    {
        await using var h = await GuiderHarness.GuidingAsync();
        var settle = h.Guider.SettleAsync(Settle);

        for (var i = 0; i < 8; i++)
        {
            await h.Step(0.3, 0.2);
            await Task.Delay(60);
        }

        await GuiderHarness.Soon(settle);
    }

    [Fact]
    public async Task ASettleOnRequest_StartsAgainAtAStepAboveTheTolerance_AndTimesOut()
    {
        await using var h = await GuiderHarness.GuidingAsync();
        var settle = h.Guider.SettleAsync(new GuidingSettleOptions(1.0, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(900)));

        for (var i = 0; i < 12; i++)
        {
            await h.Step(i % 2 == 0 ? 0.3 : 2.5, 0.0); // every other step is too far
            await Task.Delay(60);
        }

        await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => GuiderHarness.Soon(settle));
    }

    [Fact]
    public async Task ASettleOnRequest_FailsWhenGuidingStops()
    {
        await using var h = await GuiderHarness.GuidingAsync();
        var settle = h.Guider.SettleAsync(Settle);

        await h.Server.Event("GuidingStopped");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GuiderHarness.Soon(settle));
    }

    [Fact]
    public async Task ASettleWhileNotGuiding_IsRefused()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.SettleAsync(Settle));
    }
}

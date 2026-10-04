using System.Net;
using System.Net.Sockets;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;

namespace Sidera.Phd2.Tests;

/// <summary>The PHD2 guider against the fake server: the connection, the state that follows what PHD2 reports, and starting, stopping and pausing.</summary>
public sealed class Phd2GuiderTests
{
    private static readonly string[] ReadOnlyMethods =
        ["get_app_state", "get_profile", "get_current_equipment", "get_calibrated", "get_connected", "get_exposure", "get_pixel_scale"];

    // ---- Connection

    [Fact]
    public async Task Connecting_ReadsWhereIPhd2Stands_AndAsksNothingOfItBeyondThat()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        Assert.Equal(DeviceConnectionState.Connected, h.Guider.ConnectionState);
        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
        var info = h.Guider.Info!;
        Assert.Equal(("Main Rig", "ASI120MM Mini", "AM3", true, true), (info.Profile, info.GuideCamera, info.Mount, info.IsCalibrated, info.EquipmentConnected));
        Assert.Equal(2.0, h.Guider.Telemetry!.ExposureSeconds);
        Assert.Equal(2.0, h.Guider.Telemetry.PixelScaleArcsecPerPixel);
        Assert.All(h.Server.Methods, method => Assert.Contains(method, ReadOnlyMethods)); // no guide, loop, stop, connect of equipment, dither
        var capabilities = h.Guider.Capabilities.Value!;
        Assert.True(capabilities.CanGuide && capabilities.CanDither && capabilities.CanSettle && capabilities.CanPause && capabilities.ProvidesGuideTelemetry);
        Assert.Equal(
            [(DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting), (DeviceConnectionState.Connecting, DeviceConnectionState.Connected)],
            h.Events.Events.OfType<DeviceConnectionStateChanged>().Select(e => (e.PreviousState, e.NewState)));
    }

    [Fact]
    public async Task Connecting_ToAPhd2ThatIsGuiding_ShowsGuiding_AndDoesNotStartAnything()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
        Assert.DoesNotContain("guide", h.Server.Methods);
    }

    [Fact]
    public async Task TheStateAtTheConnect_IsWhatPhd2SaysItIs()
    {
        await using var h = GuiderHarness.Create(s => s.On("get_app_state", _ => FakeReply.Result("Looping")));

        await h.Guider.ConnectAsync();

        Assert.Equal(GuidingState.Looping, h.Guider.GuidingState);
    }

    [Fact]
    public async Task ConnectingToNothing_FailsWithASentenceAboutWhere_AndTheGuiderStaysDisconnected()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        await using var guider = new Phd2Guider(new DeviceId("guider.main"), "Main Guider", new Phd2Endpoint("127.0.0.1", port));

        var error = await Assert.ThrowsAsync<Phd2ConnectionException>(() => guider.ConnectAsync());

        Assert.StartsWith($"Could not connect to PHD2 at 127.0.0.1:{port}", error.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, guider.ConnectionState);
        Assert.Null(guider.Telemetry);
    }

    [Theory]
    [InlineData("", 4400)]
    [InlineData("  ", 4400)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    public void AnEndpointThatIsNotValid_IsRefused(string host, int port)
    {
        Assert.NotNull(new Phd2Endpoint(host, port).Problem());
        Assert.Throws<ArgumentException>(() => new Phd2Guider(new DeviceId("g"), "G", new Phd2Endpoint(host, port)));
    }

    [Fact]
    public void TheEndpoint_IsReadFromTheSettings_WithTheDefaultsForWhatIsMissing()
    {
        Assert.Equal(new Phd2Endpoint("192.168.1.20", 4401), Phd2Endpoint.FromSettings(new Dictionary<string, string> { ["host"] = "192.168.1.20", ["port"] = "4401" }));
        Assert.Equal(Phd2Endpoint.Default, Phd2Endpoint.FromSettings(new Dictionary<string, string>()));
        Assert.Throws<FormatException>(() => Phd2Endpoint.FromSettings(new Dictionary<string, string> { ["port"] = "abc" }));
        Assert.Throws<FormatException>(() => Phd2Endpoint.FromSettings(new Dictionary<string, string> { ["port"] = "70000" }));
        Assert.Equal("127.0.0.1:4400", Phd2Endpoint.Default.ToString());
    }

    [Fact]
    public async Task Disconnecting_EndsOnlyTheConnection_Phd2KeepsDoingWhatItDoes()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        await h.Guider.DisconnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, h.Guider.ConnectionState);
        Assert.Null(h.Guider.Telemetry);
        Assert.Null(h.Guider.Info);
        Assert.DoesNotContain("stop_capture", h.Server.Methods);
    }

    [Fact]
    public async Task WhenPhd2GoesAway_TheGuiderIsFaulted_NothingIsGuidingAsFarAsSideraKnows_AndTheStateIsTold()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        h.Server.Drop();

        await GuiderHarness.Until(() => h.Guider.ConnectionState == DeviceConnectionState.Faulted, "the guider to notice that PHD2 is gone");
        await h.UntilState(GuidingState.Idle);
        Assert.Null(h.Guider.Telemetry);
        Assert.Contains(h.Events.Events.OfType<DeviceConnectionStateChanged>(), e => e.NewState == DeviceConnectionState.Faulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.StartGuidingAsync());
    }

    [Fact]
    public async Task AfterALoss_AManualReconnect_ReadsTheStateAgain_ClearsTheHistory_AndDoesNotResumeGuiding()
    {
        await using var h = await GuiderHarness.GuidingAsync();
        await h.Step(0.5, 0.2);
        await GuiderHarness.Until(() => h.Guider.History.Count == 1, "a sample");
        h.Server.Drop();
        await GuiderHarness.Until(() => h.Guider.ConnectionState == DeviceConnectionState.Faulted, "the loss");
        h.Server.On("get_app_state", _ => FakeReply.Result("Looping"));

        await h.Guider.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, h.Guider.ConnectionState);
        Assert.Equal(GuidingState.Looping, h.Guider.GuidingState); // what PHD2 says now, not what was before
        Assert.Equal(0, h.Guider.History.Count);
        Assert.Equal(2, h.Server.Sessions);
        Assert.DoesNotContain("guide", h.Server.Methods);
    }

    // ---- The state follows the notifications of PHD2

    [Fact]
    public async Task TheState_FollowsTheNotificationsOfPhd2()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        await h.Server.Event("StarSelected", new { X = 100.0, Y = 80.0 });
        await h.UntilState(GuidingState.StarSelected);
        await h.Server.Event("LoopingExposures", new { Frame = 1 });
        await h.UntilState(GuidingState.Looping);
        await h.Server.Event("StartCalibration", new { Mount = "AM3" });
        await h.UntilState(GuidingState.Calibrating);
        await h.Server.Event("CalibrationComplete", new { Mount = "AM3" });
        await h.Server.Event("StartGuiding");
        await h.UntilState(GuidingState.Guiding);
        await h.Server.Event("Paused");
        await h.UntilState(GuidingState.Paused);
        await h.Server.Event("Resumed");
        await h.UntilState(GuidingState.Guiding);
        await h.Server.Event("StarLost", new { Frame = 5, SNR = 1.2 });
        await h.UntilState(GuidingState.StarLost);
        await h.Step(0.2, 0.1);
        await h.UntilState(GuidingState.Guiding);
        await h.Server.Event("GuidingStopped");
        await h.UntilState(GuidingState.Idle);
        await h.Server.Event("LoopingExposures", new { Frame = 1 });
        await h.UntilState(GuidingState.Looping);
        await h.Server.Event("LoopingExposuresStopped");
        await h.UntilState(GuidingState.Idle);
    }

    [Fact]
    public async Task ASuccessfulRequest_IsNotTakenForGuiding_OnlyWhatPhd2ReportsIs()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => s.On("guide", _ => FakeReply.Result())); // answers, and says nothing more
        using var cts = new CancellationTokenSource();

        var start = h.Guider.StartGuidingAsync(cts.Token);
        await GuiderHarness.Until(() => h.Server.Methods.Contains("guide"), "the request");
        await Task.Delay(100);

        Assert.False(start.IsCompleted);
        Assert.Equal(GuidingState.Starting, h.Guider.GuidingState); // not Guiding: PHD2 did not say so
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
    }

    // ---- Start

    private static void StartScript(FakePhd2Server server, bool calibrate = true, int settleStatus = 0, string? settleError = null) =>
        server.On("guide", _ =>
        {
            var fired = Task.Run(async () =>
            {
                if (calibrate)
                {
                    await server.Event("StartCalibration", new { Mount = "AM3" });
                    await server.Event("CalibrationComplete", new { Mount = "AM3" });
                }

                await server.Event("StartGuiding");
                await server.Event("SettleBegin");
                await server.Event("Settling", new { Distance = 0.8, Time = 2.0, SettleTime = 0.5, StarLocked = true });
                await server.Event("SettleDone", new { Status = settleStatus, Error = settleError, TotalFrames = 12, DroppedFrames = 0 });
            });
            return FakeReply.Result();
        });

    [Fact]
    public async Task StartingGuiding_LetsPhd2Calibrate_AndCompletesWhenGuidingHasSettled()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => StartScript(s));

        await GuiderHarness.Soon(h.Guider.StartGuidingAsync());

        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
        Assert.Equal(
            [GuidingState.Starting, GuidingState.Calibrating, GuidingState.Guiding, GuidingState.Settling, GuidingState.Guiding],
            h.Events.States);
        var guide = h.Server.Requests.Single(r => r.Method == "guide").Params!.Value.GetProperty("settle");
        Assert.Equal((1.5, 0.5, 10.0), (guide.GetProperty("pixels").GetDouble(), guide.GetProperty("time").GetDouble(), guide.GetProperty("timeout").GetDouble()));
        Assert.DoesNotContain("clear_calibration", h.Server.Methods);
        Assert.Equal(GuidingSettleOutcome.Settled, h.Guider.Telemetry!.Settle.LastOutcome);
    }

    [Fact]
    public async Task StartingGuiding_WithPhd2EquipmentNotConnected_IsRefused_AndPhd2IsNotAskedToGuide()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => s.On("get_connected", _ => FakeReply.Result(false)));
        h.Server.On("get_connected", _ => FakeReply.Result(false));

        var error = await Assert.ThrowsAsync<Phd2Exception>(() => h.Guider.StartGuidingAsync());

        Assert.Contains("equipment of PHD2", error.Message);
        Assert.DoesNotContain("guide", h.Server.Methods);
        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
    }

    [Fact]
    public async Task AFailedCalibration_FailsTheStart_WithTheReasonOfPhd2_AndTheStateIsIdle()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => s.On("guide", _ =>
        {
            var fired = Task.Run(async () =>
            {
                await s.Event("StartCalibration", new { Mount = "AM3" });
                await s.Event("CalibrationFailed", new { Reason = "RA guide output is not responding" });
            });
            return FakeReply.Result();
        }));

        var error = await Assert.ThrowsAsync<Phd2SettleFailedException>(() => GuiderHarness.Soon(h.Guider.StartGuidingAsync()));

        Assert.Contains("RA guide output is not responding", error.Message);
        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
    }

    [Fact]
    public async Task AStartThatPhd2CannotSettle_IsAFailure_WithTheTimeoutDistinguished()
    {
        await using var timedOut = await GuiderHarness.ConnectedAsync(s => StartScript(s, calibrate: false, settleStatus: 1, settleError: "Settling timed out after 120 s"));
        await using var failed = await GuiderHarness.ConnectedAsync(s => StartScript(s, calibrate: false, settleStatus: 1, settleError: "Star lost during settling"));

        await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => GuiderHarness.Soon(timedOut.Guider.StartGuidingAsync()));
        var error = await Assert.ThrowsAsync<Phd2SettleFailedException>(() => GuiderHarness.Soon(failed.Guider.StartGuidingAsync()));

        Assert.Contains("Star lost during settling", error.Message);
    }

    [Fact]
    public async Task ARequestThatPhd2Refuses_FailsTheStart_AndTheStateGoesBack()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => s.On("guide", _ => FakeReply.Error(1, "equipment not connected")));

        var error = await Assert.ThrowsAsync<Phd2RpcException>(() => h.Guider.StartGuidingAsync());

        Assert.Equal("equipment not connected", error.Reason);
        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
    }

    [Fact]
    public async Task CancellingTheStart_TellsPhd2ToStop_AndThrowsCancellation()
    {
        await using var h = await GuiderHarness.ConnectedAsync(s => s.On("guide", _ => FakeReply.Result()));
        using var cts = new CancellationTokenSource();
        var start = h.Guider.StartGuidingAsync(cts.Token);
        await GuiderHarness.Until(() => h.Server.Methods.Contains("guide"), "the request");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Contains("stop_capture", h.Server.Methods);
    }

    [Fact]
    public async Task StartingWhileAlreadyGuiding_AsksPhd2Nothing()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        await h.Guider.StartGuidingAsync();

        Assert.DoesNotContain("guide", h.Server.Methods);
    }

    // ---- Stop, pause, resume

    [Fact]
    public async Task Stopping_IsConfirmedByPhd2_NotAssumed()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        var stop = h.Guider.StopGuidingAsync();
        await GuiderHarness.Until(() => h.Server.Methods.Contains("stop_capture"), "the request");
        await h.UntilState(GuidingState.Stopping);
        await Task.Delay(100);
        Assert.False(stop.IsCompleted);
        Assert.Equal(GuidingState.Stopping, h.Guider.GuidingState);

        await h.Server.Event("GuidingStopped");
        await h.Server.Event("LoopingExposuresStopped");
        await GuiderHarness.Soon(stop);

        Assert.Equal(GuidingState.Idle, h.Guider.GuidingState);
    }

    [Fact]
    public async Task Stopping_AGuiderThatIsNotCapturing_AsksPhd2Nothing()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        await h.Guider.StopGuidingAsync();

        Assert.DoesNotContain("stop_capture", h.Server.Methods);
    }

    [Fact]
    public async Task AStopThatPhd2DoesNotConfirm_IsAFailure_AndTheStateGoesBackToWhatPhd2Does()
    {
        await using var h = await GuiderHarness.GuidingAsync(s => s.On("get_app_state", _ => FakeReply.Result("Guiding")));

        var error = await Assert.ThrowsAsync<Phd2TimeoutException>(() => GuiderHarness.Soon(h.Guider.StopGuidingAsync()));

        Assert.Contains("did not confirm", error.Message);
        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
    }

    [Fact]
    public async Task Pausing_AndResuming_AreConfirmedByPhd2()
    {
        await using var h = await GuiderHarness.GuidingAsync();

        var pause = h.Guider.PauseGuidingAsync();
        await GuiderHarness.Until(() => h.Server.Methods.Contains("set_paused"), "the request");
        Assert.False(pause.IsCompleted);
        await h.Server.Event("Paused");
        await GuiderHarness.Soon(pause);
        Assert.Equal(GuidingState.Paused, h.Guider.GuidingState);
        Assert.True(h.Server.Requests.Single(r => r.Method == "set_paused").Params!.Value[0].GetBoolean());

        var resume = h.Guider.ResumeGuidingAsync();
        await GuiderHarness.Until(() => h.Server.Methods.Count(m => m == "set_paused") == 2, "the second request");
        await h.Server.Event("Resumed");
        await GuiderHarness.Soon(resume);

        Assert.Equal(GuidingState.Guiding, h.Guider.GuidingState);
        Assert.False(h.Server.Requests.Last(r => r.Method == "set_paused").Params!.Value[0].GetBoolean());
    }

    [Fact]
    public async Task Pausing_WhenNotGuiding_IsRefused_WithoutARequest()
    {
        await using var h = await GuiderHarness.ConnectedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.PauseGuidingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Guider.ResumeGuidingAsync());

        Assert.DoesNotContain("set_paused", h.Server.Methods);
    }
}

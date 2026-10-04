using System.Diagnostics;
using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Guiding;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Phd2.Tests;

/// <summary>
/// The dither of a Multi-Rig block with PHD2 as the guider, run by the real runner: Sidera decides when the dither may happen (every camera
/// of the block at a safe point), PHD2 does it and settles, and the block goes on only when the settle has ended well. The scheduler knows
/// nothing of PHD2: it calls the guider through the same contract as for the simulator.
/// </summary>
public sealed class Phd2SequenceTests : IAsyncLifetime
{
    private static readonly DeviceId MainId = new("camera.main");
    private static readonly DeviceId WideId = new("camera.wide");
    private static readonly DeviceId MountId = new("mount.main");
    private static readonly DeviceId GuiderId = new("guider.main");
    private static readonly GuidingSettleOptions Settle = new(1.0, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5));

    private readonly SideraRuntimeHost _host = new();
    private readonly FakePhd2Server _server = new();
    private readonly TaskCompletionSource _release = new();
    private Phd2Guider _guider = null!;
    private SimulatedCamera _main = null!;
    private SimulatedCamera _wide = null!;

    // What the cameras were doing when PHD2 was asked to dither.
    private string? _mainStateAtDither;
    private string? _wideStateAtDither;
    private long _ditherAskedAt;
    private object _settleDone = new { Status = 0, TotalFrames = 8, DroppedFrames = 0 };

    public async Task InitializeAsync()
    {
        _main = _host.AddSimulatedCamera(MainId, "Main Camera");
        _wide = _host.AddSimulatedCamera(WideId, "Wide Camera");
        _host.AddSimulatedMount(MountId, "Mount", TimeSpan.FromMilliseconds(20));
        _server.On("get_app_state", _ => FakeReply.Result("Guiding"));
        _server.On("dither", _ =>
        {
            _mainStateAtDither = _main.ExposureState.ToString();
            _wideStateAtDither = _wide.ExposureState.ToString();
            _ditherAskedAt = Stopwatch.GetTimestamp();
            var fired = Task.Run(async () =>
            {
                await _server.Event("GuidingDithered", new { dx = 1.0, dy = -1.0 });
                await _server.Event("SettleBegin");
                await _release.Task;
                await _server.Event("SettleDone", _settleDone);
            });
            return FakeReply.Result();
        });
        _guider = new Phd2Guider(GuiderId, "Guider", Phd2Endpoint.Default, _host.EventBus, options: GuiderHarness.FastOptions, connector: _server.Connector);
        _host.AddDevice(_guider);
        foreach (var device in _host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Guiding, "the guider to be guiding");
    }

    public async Task DisposeAsync()
    {
        _release.TrySetResult();
        await _guider.DisposeAsync();
        await _server.DisposeAsync();
        await _host.DisposeAsync();
    }

    private ISequenceStep Exposure(DeviceId camera, int milliseconds) =>
        new CameraExposureAction(_host.DeviceRegistry, camera, TimeSpan.FromMilliseconds(milliseconds));

    private static SafePointStep SafePoint() => new();

    private Sequence Block(int mainExposureMs)
    {
        var dither = new DitherAction(_host.DeviceRegistry, GuiderId, MountId, [MainId, WideId], 3.0, Settle);
        var main = new SequenceGroup("Main", [Exposure(MainId, mainExposureMs), SafePoint(), Exposure(MainId, 80), SafePoint(), Exposure(MainId, 80), SafePoint()]);
        var wide = new SequenceGroup("Wide",
        [
            Exposure(WideId, 60), SafePoint(), Exposure(WideId, 60), SafePoint(), Exposure(WideId, 60), SafePoint(),
            dither,
            Exposure(WideId, 60), SafePoint(), Exposure(WideId, 60), SafePoint(),
        ]);
        return new Sequence("PHD2 dither", [new ParallelStep("Multi-Rig", [main, wide], new CoordinationGroupId("multirig.phd2"))]);
    }

    private SequenceRunner NewRunner() => new(_host.ResourceManager, _host.SafePointCoordinator);

    private async Task AssertNothingIsLeftBehind()
    {
        var everything = _host.DeviceRegistry.GetAll().Select(d => ResourceId.ForDevice(d.Id)).ToList();
        using var lease = await _host.ResourceManager.AcquireAsync(everything, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        var status = _host.SafePointCoordinator.GetStatus(new CoordinationGroupId("multirig.phd2"));
        Assert.Empty(status.Participants);
        Assert.False(status.RequestPending);
    }

    private async Task UntilDitherAsked() =>
        await GuiderHarness.Until(() => _server.Methods.Contains("dither"), "PHD2 to be asked to dither");

    [Fact]
    public async Task TheDither_WaitsForTheCameraThatIsStillExposing_ThenPhd2DithersAndSettles_AndBothCamerasGoOn()
    {
        var runner = NewRunner();
        var started = Stopwatch.GetTimestamp();
        var run = runner.RunAsync(Block(mainExposureMs: 900));

        await UntilDitherAsked();

        // The wide rig asked after about 180 ms; the main camera was exposing until about 900 ms: PHD2 was asked only when both were at a safe point.
        Assert.True(Stopwatch.GetElapsedTime(started).TotalMilliseconds >= 800);
        Assert.Equal(("Idle", "Idle"), (_mainStateAtDither, _wideStateAtDither));

        // While PHD2 settles, nothing exposes.
        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Settling, "the settle");
        for (var i = 0; i < 10; i++)
        {
            await Task.Delay(30);
            Assert.Equal(CameraExposureState.Idle, _main.ExposureState);
            Assert.Equal(CameraExposureState.Idle, _wide.ExposureState);
        }

        Assert.False(run.IsCompleted);
        _release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(GuidingState.Guiding, _guider.GuidingState);
        Assert.Single(_server.Requests, r => r.Method == "dither");
        var settle = _server.Requests.Single(r => r.Method == "dither").Params!.Value;
        Assert.Equal((3.0, false), (settle.GetProperty("amount").GetDouble(), settle.GetProperty("raOnly").GetBoolean()));
        Assert.Equal(GuidingSettleOutcome.Settled, _guider.Telemetry!.Settle.LastOutcome);
        await AssertNothingIsLeftBehind();
    }

    [Fact]
    public async Task AFailedSettle_FailsTheBlock_AndNoCameraExposesAfterIt()
    {
        _settleDone = new { Status = 1, Error = "Star lost while settling", TotalFrames = 5, DroppedFrames = 5 };
        var runner = NewRunner();
        var run = runner.RunAsync(Block(mainExposureMs: 200));
        await UntilDitherAsked();
        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Settling, "the settle");

        _release.SetResult();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("Star lost while settling", error.ToString());
        Assert.Equal(SequenceState.Failed, runner.State);
        await Task.Delay(300);
        Assert.Equal(CameraExposureState.Idle, _main.ExposureState); // the block did not go on exposing
        Assert.Equal(CameraExposureState.Idle, _wide.ExposureState);
        await AssertNothingIsLeftBehind();
    }

    [Fact]
    public async Task ASettleThatTimesOut_FailsTheBlockAsATimeout()
    {
        _settleDone = new { Status = 1, Error = "Settling timed out", TotalFrames = 40, DroppedFrames = 0 };
        var runner = NewRunner();
        var run = runner.RunAsync(Block(mainExposureMs: 200));
        await UntilDitherAsked();
        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Settling, "the settle");

        _release.SetResult();

        await Assert.ThrowsAsync<GuidingSettleTimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(SequenceState.Failed, runner.State);
        await AssertNothingIsLeftBehind();
    }

    [Fact]
    public async Task Cancelling_WhileTheGuiderSettles_EndsTheRun_WithoutADeadlock_AndReleasesEverything()
    {
        var runner = NewRunner();
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(Block(mainExposureMs: 200), cts.Token);
        await UntilDitherAsked();
        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Settling, "the settle");

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(SequenceState.Cancelled, runner.State);
        await AssertNothingIsLeftBehind();
        _release.SetResult();
    }

    [Fact]
    public async Task WhenPhd2IsLostWhileSettling_TheBlockFails_AndEverythingIsReleased()
    {
        var runner = NewRunner();
        var run = runner.RunAsync(Block(mainExposureMs: 200));
        await UntilDitherAsked();
        await GuiderHarness.Until(() => _guider.GuidingState == GuidingState.Settling, "the settle");

        _server.Drop();

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(DeviceConnectionState.Faulted, _guider.ConnectionState);
        await AssertNothingIsLeftBehind();
    }

    [Fact]
    public async Task ADitherWithTheGuiderNotGuiding_FailsTheBlock_AndPhd2IsNeverAsked()
    {
        await _guider.DisconnectAsync();
        var runner = NewRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Block(mainExposureMs: 100)).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.DoesNotContain("dither", _server.Methods);
        Assert.Equal(SequenceState.Failed, runner.State);
    }
}

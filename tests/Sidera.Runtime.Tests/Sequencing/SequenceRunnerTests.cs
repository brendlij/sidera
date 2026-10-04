using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

public class SequenceRunnerTests
{
    private sealed class LambdaStep(string name, Func<CancellationToken, Task> body) : ISequenceStep
    {
        public string Name { get; } = name;
        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            await body(cancellationToken);
            return new SequenceStepResult();
        }
    }

    private sealed class FakeDevice : IDevice
    {
        public DeviceId Id { get; } = new("focuser.1");
        public string Name => "Fake";
        public DeviceType Type => DeviceType.Focuser;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Connected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly DeviceId CameraId = new("camera.main");

    private static ISequenceStep Step(string name, List<string> log) =>
        new LambdaStep(name, _ =>
        {
            log.Add(name);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Steps_ExecuteInOrder()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("s", [Step("a", log), Step("b", log), Step("c", log)]));

        Assert.Equal(new[] { "a", "b", "c" }, log);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.False(runner.IsRunning);
        Assert.Equal(2, runner.CurrentStepIndex);
        Assert.Equal("c", runner.CurrentStepName);
    }

    [Fact]
    public async Task InitialState_IsIdle()
    {
        var runner = new SequenceRunner();

        Assert.Equal(SequenceState.Idle, runner.State);
        Assert.Equal(-1, runner.CurrentStepIndex);
        Assert.Null(runner.CurrentStepName);
    }

    [Fact]
    public async Task Failure_StopsLaterStepsAndRethrows()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var failing = new LambdaStep("boom", _ => throw new InvalidOperationException("boom"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Step("a", log), failing, Step("c", log)])));

        Assert.Equal("boom", error.Message);
        Assert.Equal(new[] { "a" }, log);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Same(error, runner.Failure);
        Assert.Equal(1, runner.CurrentStepIndex);
        Assert.Equal("boom", runner.CurrentStepName);
    }

    [Fact]
    public async Task Cancellation_StopsExecution()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        var runner = new SequenceRunner();
        var cancelling = new LambdaStep("cancel", async ct =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [cancelling, Step("after", log)]), cts.Token));

        Assert.Empty(log);
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Null(runner.Failure);
    }

    [Fact]
    public async Task AlreadyCancelledToken_RunsNoStep()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = new SequenceRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [Step("a", log)]), cts.Token));

        Assert.Empty(log);
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task SecondRun_WhileRunning_IsRejected()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var runner = new SequenceRunner();
        var blocking = new LambdaStep("block", async _ =>
        {
            started.SetResult();
            await release.Task;
        });

        var first = runner.RunAsync(new Sequence("first", [blocking]));
        await started.Task;

        Assert.True(runner.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("second", [])));

        release.SetResult();
        await first;
        Assert.Equal(SequenceState.Completed, runner.State);

        await runner.RunAsync(new Sequence("third", [])); // reusable afterwards
    }

    [Fact]
    public async Task ConnectExposeDisconnect_WorksWithSimulatedCamera()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        var exposure = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(30));
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await runner.RunAsync(new Sequence("night", [
            new ConnectDeviceAction(host.DeviceRegistry, CameraId),
            exposure,
            new DisconnectDeviceAction(host.DeviceRegistry, CameraId),
        ]));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        var frame = Assert.IsType<CameraFrame>(Assert.Single(completed, c => c.StepIndex == 1).Result.Payload);
        Assert.Equal(800, frame.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(30), frame.ExposureDuration);
    }

    [Fact]
    public async Task ThreeExposures_ProduceThreeSeparateCompletedFramesInOrder()
    {
        await using var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        await host.DeviceRegistry.GetAll().Single().ConnectAsync();
        var exposures = Enumerable.Range(0, 3)
            .Select(_ => new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20)))
            .ToArray();
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        // If exposures overlapped, the camera would reject the second one and the run would fail.
        await runner.RunAsync(new Sequence("three", exposures));

        Assert.Equal(new[] { 0, 1, 2 }, completed.Select(c => c.StepIndex));
        var frames = completed.Select(c => Assert.IsType<CameraFrame>(c.Result.Payload)).ToArray();
        Assert.Equal(3, frames.Distinct().Count());
        Assert.Equal(3, completed.Select(c => c.Result).Distinct().Count());
    }

    [Fact]
    public async Task StepCompleted_ReportsIndexNameAndResult_InExecutionOrder_BeforeNextStepStarts()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        runner.StepCompleted += (_, e) => log.Add($"completed {e.StepIndex}:{e.StepName}");
        var steps = new ISequenceStep[]
        {
            new LambdaStep("a", _ => { log.Add("run a"); return Task.CompletedTask; }),
            new LambdaStep("b", _ => { log.Add("run b"); return Task.CompletedTask; }),
        };

        await runner.RunAsync(new Sequence("s", steps));

        Assert.Equal(new[] { "run a", "completed 0:a", "run b", "completed 1:b" }, log);
    }

    [Fact]
    public async Task ExposureAction_ExecutedTwice_ProducesSeparateResultsAndFrames()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        await camera.ConnectAsync();
        var action = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20));

        var first = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);
        var second = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.NotSame(first, second);
        var frame1 = Assert.IsType<CameraFrame>(first.Payload);
        var frame2 = Assert.IsType<CameraFrame>(second.Payload);
        Assert.NotSame(frame1, frame2);
        // The definition holds no result state.
        Assert.Null(typeof(CameraExposureAction).GetProperty("Frame"));
    }

    [Fact]
    public async Task ConnectAndDisconnectActions_ReturnFreshResultsWithoutPayload()
    {
        await using var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(CameraId, "Main Camera");
        var connect = new ConnectDeviceAction(host.DeviceRegistry, CameraId);
        var disconnect = new DisconnectDeviceAction(host.DeviceRegistry, CameraId);

        var connected = await connect.ExecuteAsync(NoContext.Instance, CancellationToken.None);
        var disconnected = await disconnect.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Null(connected.Payload);
        Assert.Null(disconnected.Payload);
        Assert.NotSame(connected, disconnected);
    }

    [Fact]
    public async Task FailedStep_DoesNotEmitCompletion_AndLaterStepsDoNotRun()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var failing = new LambdaStep("boom", _ => throw new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [Step("a", log), failing, Step("c", log)])));

        Assert.Equal(new[] { 0 }, completed.Select(c => c.StepIndex));
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task CancelledExposure_DoesNotEmitCompletedFrame()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera");
        await camera.ConnectAsync();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                new Sequence("s", [new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromSeconds(10))]),
                cts.Token));

        Assert.Empty(completed);
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task ThrowingStepCompletedObserver_DoesNotBreakTheSequence()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        runner.StepCompleted += (_, _) => throw new InvalidOperationException("observer");

        await runner.RunAsync(new Sequence("s", [Step("a", log), Step("b", log)]));

        Assert.Equal(new[] { "a", "b" }, log);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    private static List<SequenceStepCompletedEventArgs> Observe(SequenceRunner runner)
    {
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) => completed.Add(e);
        return completed;
    }

    [Fact]
    public async Task UnknownDeviceId_FailsClearly()
    {
        var registry = new DeviceRegistry();
        var runner = new SequenceRunner();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [new ConnectDeviceAction(registry, new DeviceId("nope"))])));

        Assert.Contains("'nope' is not registered", error.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task ExposureAction_RejectsNonCameraDevice()
    {
        var registry = new DeviceRegistry();
        registry.Register(new FakeDevice());
        var action = new CameraExposureAction(registry, new DeviceId("focuser.1"), TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            action.ExecuteAsync(NoContext.Instance, CancellationToken.None));

        Assert.Contains("'focuser.1' is not a camera", error.Message);
    }

    [Fact]
    public async Task ExposureAction_DoesNotConnectTheCamera()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera");
        var action = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ExecuteAsync(NoContext.Instance, CancellationToken.None));

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }
}

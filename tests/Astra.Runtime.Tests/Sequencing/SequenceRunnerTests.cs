using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;

namespace Astra.Runtime.Tests.Sequencing;

public class SequenceRunnerTests
{
    private sealed class LambdaStep(string name, Func<CancellationToken, Task> body) : ISequenceStep
    {
        public string Name { get; } = name;
        public Task ExecuteAsync(CancellationToken cancellationToken) => body(cancellationToken);
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
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        var exposure = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(30));
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("night", [
            new ConnectDeviceAction(host.DeviceRegistry, CameraId),
            exposure,
            new DisconnectDeviceAction(host.DeviceRegistry, CameraId),
        ]));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        Assert.NotNull(exposure.Frame);
        Assert.Equal(800, exposure.Frame!.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(30), exposure.Frame.ExposureDuration);
    }

    [Fact]
    public async Task ThreeExposures_RunSequentiallyAndEachProducesAFrame()
    {
        await using var host = new AstraRuntimeHost();
        host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        var exposures = Enumerable.Range(0, 3)
            .Select(_ => new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20)))
            .ToArray();
        var runner = new SequenceRunner();

        // If exposures overlapped, the camera would reject the second one and the run would fail.
        await runner.RunAsync(new Sequence("three", [
            new ConnectDeviceAction(host.DeviceRegistry, CameraId),
            .. exposures,
            new DisconnectDeviceAction(host.DeviceRegistry, CameraId),
        ]));

        Assert.All(exposures, e => Assert.NotNull(e.Frame));
        Assert.Equal(3, exposures.Select(e => e.Frame).Distinct().Count());
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
            action.ExecuteAsync(CancellationToken.None));

        Assert.Contains("'focuser.1' is not a camera", error.Message);
        Assert.Null(action.Frame);
    }

    [Fact]
    public async Task ExposureAction_DoesNotConnectTheCamera()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera");
        var action = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAsync<InvalidOperationException>(() => action.ExecuteAsync(CancellationToken.None));

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }
}

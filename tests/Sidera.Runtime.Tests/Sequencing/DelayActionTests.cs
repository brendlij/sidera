using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

public class DelayActionTests
{
    private static readonly DeviceId CameraId = new("camera.main");

    private static List<SequenceStepCompletedEventArgs> Observe(SequenceRunner runner)
    {
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) => completed.Add(e);
        return completed;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Constructor_RejectsZeroOrNegativeDuration(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DelayAction(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void Name_FormatsDuration()
    {
        Assert.Equal("Wait 1s", new DelayAction(TimeSpan.FromSeconds(1)).Name);
        Assert.Equal("Wait 0.5s", new DelayAction(TimeSpan.FromMilliseconds(500)).Name);
        Assert.Equal(TimeSpan.FromSeconds(1), new DelayAction(TimeSpan.FromSeconds(1)).Duration);
    }

    [Fact]
    public async Task Execute_CompletesWithResultWithoutPayload()
    {
        var delay = new DelayAction(TimeSpan.FromMilliseconds(20));

        var result = await delay.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task Execute_SupportsCancellation()
    {
        var delay = new DelayAction(TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            delay.ExecuteAsync(NoContext.Instance, cts.Token));
    }

    [Fact]
    public async Task SameDefinition_CanBeReused_WithSeparateResults()
    {
        var delay = new DelayAction(TimeSpan.FromMilliseconds(10));

        var first = await delay.ExecuteAsync(NoContext.Instance, CancellationToken.None);
        var second = await delay.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task Group_ExecutesExposureThenDelay_AndReportsCompletionsInOrder()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        await camera.ConnectAsync();
        var group = new SequenceGroup("Imaging Block", [
            new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20)),
            new DelayAction(TimeSpan.FromMilliseconds(20)),
        ]);
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(2, group)]));

        Assert.Equal(
            new[]
            {
                "Exposure 0.02s", "Wait 0.02s", "Imaging Block",
                "Exposure 0.02s", "Wait 0.02s", "Imaging Block",
                "Repeat × 2",
            },
            completed.Select(c => c.StepName));
        Assert.IsType<CameraFrame>(completed[0].Result.Payload);
        Assert.Null(completed[1].Result.Payload);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task CancellationDuringDelay_StopsLaterIterations_AndKeepsEarlierFrameValid()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        await camera.ConnectAsync();
        using var cts = new CancellationTokenSource();
        var group = new SequenceGroup("Imaging Block", [
            new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20)),
            new DelayAction(TimeSpan.FromSeconds(30)),
        ]);
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        // Cancel as soon as the exposure has completed, i.e. while the delay runs.
        runner.StepCompleted += (_, e) =>
        {
            if (e.Result.Payload is CameraFrame)
            {
                cts.CancelAfter(TimeSpan.FromMilliseconds(30));
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [new RepeatStep(3, group)]), cts.Token));

        // Only the first exposure completed: no delay, no group, no repeat, no later iteration.
        var only = Assert.Single(completed);
        var frame = Assert.IsType<CameraFrame>(only.Result.Payload);
        Assert.Equal(800, frame.Width);
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }
}

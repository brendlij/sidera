using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

public class RepeatStepTests
{
    private static readonly DeviceId CameraId = new("camera.main");

    /// <summary>A child that counts its executions and can be told to throw or block.</summary>
    private sealed class ProbeStep(Func<int, CancellationToken, Task>? body = null) : ISequenceStep
    {
        public int Executions { get; private set; }
        public string Name => "Probe";

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            var number = ++Executions;
            if (body is not null)
            {
                await body(number, cancellationToken);
            }

            return new SequenceStepResult(number);
        }
    }

    private static List<SequenceStepCompletedEventArgs> Observe(SequenceRunner runner)
    {
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) => completed.Add(e);
        return completed;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Repeat_RejectsCountOfZeroOrLess(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepeatStep(count, new ProbeStep()));
    }

    [Fact]
    public void Repeat_RejectsNullChild()
    {
        Assert.Throws<ArgumentNullException>(() => new RepeatStep(2, null!));
    }

    [Fact]
    public void Repeat_HasReadableName()
    {
        Assert.Equal("Repeat × 3", new RepeatStep(3, new ProbeStep()).Name);
    }

    [Fact]
    public async Task Repeat_ExecutesChildExactlyNTimes_AndCompletes()
    {
        var child = new ProbeStep();
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("s", [new RepeatStep(4, child)]));

        Assert.Equal(4, child.Executions);
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task EveryIteration_ProducesDistinctResult_InOrder_WithIterationPositions()
    {
        var child = new ProbeStep();
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, child)]));

        // Three child completions, then the repeat's own completion.
        Assert.Equal(4, completed.Count);
        var children = completed.Take(3).ToArray();
        Assert.Equal(new object?[] { 1, 2, 3 }, children.Select(c => c.Result.Payload));
        Assert.Equal(3, children.Select(c => c.Result).Distinct().Count());
        Assert.Equal(new[] { 0, 1, 2 }, children.Select(c => c.Position.Index));
        Assert.All(children, c =>
        {
            Assert.Equal(3, c.Position.Count);
            Assert.Equal("Probe", c.StepName);
            Assert.Equal("Repeat × 3", c.Position.Parent!.StepName);
        });

        var repeat = completed[3];
        Assert.Equal("Repeat × 3", repeat.StepName);
        Assert.Null(repeat.Position.Parent);
        var repeatPayload = Assert.IsAssignableFrom<IReadOnlyList<SequenceStepResult>>(repeat.Result.Payload);
        Assert.Equal(children.Select(c => c.Result), repeatPayload);
    }

    [Fact]
    public async Task ChildCompletion_IsReportedBeforeNextIterationStarts()
    {
        var log = new List<string>();
        var child = new ProbeStep((n, _) =>
        {
            log.Add($"run {n}");
            return Task.CompletedTask;
        });
        var runner = new SequenceRunner();
        runner.StepCompleted += (_, e) =>
        {
            if (e.Position.Parent is not null)
            {
                log.Add($"completed {e.Position.Index + 1}");
            }
        };

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, child)]));

        Assert.Equal(new[] { "run 1", "completed 1", "run 2", "completed 2", "run 3", "completed 3" }, log);
    }

    [Fact]
    public async Task CurrentPosition_TracksIterationDuringExecution()
    {
        var seen = new List<string?>();
        var runner = new SequenceRunner();
        var child = new ProbeStep((_, _) =>
        {
            var p = runner.CurrentPosition!;
            seen.Add($"{p.Parent!.StepName}|{p.StepName}|{p.Index + 1}/{p.Count}|top={runner.CurrentStepIndex}:{runner.CurrentStepName}");
            return Task.CompletedTask;
        });

        await runner.RunAsync(new Sequence("s", [new RepeatStep(2, child)]));

        Assert.Equal(new[]
        {
            "Repeat × 2|Probe|1/2|top=0:Repeat × 2",
            "Repeat × 2|Probe|2/2|top=0:Repeat × 2",
        }, seen);
    }

    [Fact]
    public async Task ReusedExposureDefinition_ProducesSeparateFrames_AndKeepsNoState()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera", seed: 1);
        await camera.ConnectAsync();
        var exposure = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20));
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, exposure)]));

        var frames = completed
            .Where(c => c.Position.Parent is not null)
            .Select(c => Assert.IsType<CameraFrame>(c.Result.Payload))
            .ToArray();
        Assert.Equal(3, frames.Length);
        Assert.Equal(3, frames.Distinct().Count());
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }

    [Fact]
    public async Task Cancellation_DuringIteration_StopsFutureIterations_AndReportsNothingForCancelledChild()
    {
        using var cts = new CancellationTokenSource();
        var child = new ProbeStep(async (n, ct) =>
        {
            if (n == 3)
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }
        });
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [new RepeatStep(20, child)]), cts.Token));

        Assert.Equal(3, child.Executions); // iterations 4..20 never ran
        Assert.Equal(new object?[] { 1, 2 }, completed.Select(c => c.Result.Payload)); // no result for 3, none for the repeat
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task Cancellation_BetweenIterations_StopsBeforeNextIteration()
    {
        using var cts = new CancellationTokenSource();
        var child = new ProbeStep((n, _) =>
        {
            if (n == 2)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        });
        var runner = new SequenceRunner();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [new RepeatStep(5, child)]), cts.Token));

        Assert.Equal(2, child.Executions);
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task CancelledExposure_InRepeat_ProducesNoFrame()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Main Camera");
        await camera.ConnectAsync();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var exposure = new CameraExposureAction(host.DeviceRegistry, CameraId, TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [new RepeatStep(20, exposure)]), cts.Token));

        Assert.Empty(completed);
        Assert.Equal(SequenceState.Cancelled, runner.State);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }

    [Fact]
    public async Task Failure_InIteration_StopsRemainingIterations_KeepsEarlierResults_AndSetsFailure()
    {
        var child = new ProbeStep((n, _) =>
            n == 3 ? throw new InvalidOperationException("iteration 3 failed") : Task.CompletedTask);
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [new RepeatStep(5, child)])));

        Assert.Equal("iteration 3 failed", error.Message);
        Assert.Same(error, runner.Failure);
        Assert.Equal(3, child.Executions);
        Assert.Equal(new object?[] { 1, 2 }, completed.Select(c => c.Result.Payload));
        Assert.Equal(SequenceState.Failed, runner.State);
    }
}

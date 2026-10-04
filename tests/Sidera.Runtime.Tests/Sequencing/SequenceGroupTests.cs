using Sidera.Core.Devices;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

public class SequenceGroupTests
{
    /// <summary>A step that logs its executions, returns its name as payload, and can throw or block.</summary>
    private sealed class NamedStep(string name, List<string> log, Func<CancellationToken, Task>? body = null) : ISequenceStep
    {
        public string Name { get; } = name;

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            log.Add($"run {Name}");
            if (body is not null)
            {
                await body(cancellationToken);
            }

            return new SequenceStepResult(Name);
        }
    }

    private static List<SequenceStepCompletedEventArgs> Observe(SequenceRunner runner)
    {
        var completed = new List<SequenceStepCompletedEventArgs>();
        runner.StepCompleted += (_, e) => completed.Add(e);
        return completed;
    }

    private static string PathOf(SequenceExecutionPosition p) =>
        string.Join(" > ", Chain(p).Select(x => $"{x.StepName} {x.Index + 1}/{x.Count}"));

    private static IEnumerable<SequenceExecutionPosition> Chain(SequenceExecutionPosition p) =>
        p.Parent is null ? [p] : Chain(p.Parent).Append(p);

    [Fact]
    public void Constructor_ValidatesArguments()
    {
        var log = new List<string>();
        var step = new NamedStep("a", log);

        Assert.Throws<ArgumentException>(() => new SequenceGroup("", [step]));
        Assert.Throws<ArgumentException>(() => new SequenceGroup("  ", [step]));
        Assert.Throws<ArgumentNullException>(() => new SequenceGroup("g", null!));
        Assert.Throws<ArgumentException>(() => new SequenceGroup("g", []));
        Assert.Throws<ArgumentException>(() => new SequenceGroup("g", [step, null!]));
    }

    [Fact]
    public async Task Children_ExecuteInOrder_AndResultsAreReturnedInOrder()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [new NamedStep("A", log), new NamedStep("B", log), new NamedStep("C", log)]);

        await runner.RunAsync(new Sequence("s", [group]));

        Assert.Equal(new[] { "run A", "run B", "run C" }, log);
        Assert.Equal(SequenceState.Completed, runner.State);

        // Three child completions, then the group's own.
        Assert.Equal(new object?[] { "A", "B", "C", null }, completed.Select(c => c.Result.Payload is string s ? s : null));
        var children = completed.Take(3).Select(c => c.Result).ToArray();
        Assert.Equal(3, children.Distinct().Count());
        var groupPayload = Assert.IsAssignableFrom<IReadOnlyList<SequenceStepResult>>(completed[3].Result.Payload);
        Assert.Equal(children, groupPayload);
    }

    [Fact]
    public async Task ChildCompletion_IsReportedImmediatelyAfterEachChild()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        runner.StepCompleted += (_, e) =>
        {
            if (e.Position.Parent is not null)
            {
                log.Add($"done {e.StepName}");
            }
        };
        var group = new SequenceGroup("block", [new NamedStep("A", log), new NamedStep("B", log)]);

        await runner.RunAsync(new Sequence("s", [group]));

        Assert.Equal(new[] { "run A", "done A", "run B", "done B" }, log);
    }

    [Fact]
    public async Task Failure_InChild_StopsLaterChildren_AndGroupDoesNotComplete()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [
            new NamedStep("A", log),
            new NamedStep("B", log, _ => throw new InvalidOperationException("B failed")),
            new NamedStep("C", log),
        ]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("s", [group])));

        Assert.Equal("B failed", error.Message);
        Assert.Same(error, runner.Failure);
        Assert.Equal(new[] { "run A", "run B" }, log);
        Assert.Equal(new[] { "A" }, completed.Select(c => c.StepName)); // no B, no group
        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task Cancellation_InChild_StopsLaterChildren_AndGroupDoesNotComplete()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [
            new NamedStep("A", log),
            new NamedStep("B", log, async ct =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }),
            new NamedStep("C", log),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [group]), cts.Token));

        Assert.Equal(new[] { "run A", "run B" }, log);
        Assert.Equal(new[] { "A" }, completed.Select(c => c.StepName));
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task Cancellation_BetweenChildren_DoesNotStartNextChild()
    {
        var log = new List<string>();
        using var cts = new CancellationTokenSource();
        var runner = new SequenceRunner();
        var group = new SequenceGroup("block", [
            new NamedStep("A", log, _ =>
            {
                cts.Cancel();
                return Task.CompletedTask;
            }),
            new NamedStep("B", log),
        ]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new Sequence("s", [group]), cts.Token));

        Assert.Equal(new[] { "run A" }, log);
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public async Task RepeatContainingGroup_RunsGroupEveryIteration_WithNestedPositions()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [new NamedStep("A", log), new NamedStep("B", log)]);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, group)]));

        Assert.Equal(
            new[] { "run A", "run B", "run A", "run B", "run A", "run B" },
            log);
        Assert.Equal(SequenceState.Completed, runner.State);

        // Repeat iteration 2 → block → A
        var secondIterationA = completed.First(c => c.StepName == "A" && c.Position.Parent!.Index == 1);
        Assert.Equal("Repeat × 3 1/1 > block 2/3 > A 1/2", PathOf(secondIterationA.Position));
        Assert.Equal("Repeat × 3", secondIterationA.Position.Root.StepName);
    }

    [Fact]
    public async Task GroupContainingRepeat_RunsRepeatInOrder_WithNestedPositions()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [
            new NamedStep("A", log),
            new RepeatStep(3, new NamedStep("B", log)),
        ]);

        await runner.RunAsync(new Sequence("s", [group]));

        Assert.Equal(new[] { "run A", "run B", "run B", "run B" }, log);
        var thirdB = completed.Last(c => c.StepName == "B");
        Assert.Equal("block 1/1 > Repeat × 3 2/2 > B 3/3", PathOf(thirdB.Position));
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task ReusingTheSameGroupDefinition_KeepsNoStateBetweenRuns()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();
        var completed = Observe(runner);
        var group = new SequenceGroup("block", [new NamedStep("A", log), new NamedStep("B", log)]);
        var sequence = new Sequence("s", [group]);

        await runner.RunAsync(sequence);
        var firstRun = completed.ToArray();
        completed.Clear();
        await runner.RunAsync(sequence);

        Assert.Equal(4, log.Count);
        Assert.Equal(firstRun.Length, completed.Count);
        // Same positions, but brand-new result objects for every execution.
        Assert.Equal(firstRun.Select(c => c.Position), completed.Select(c => c.Position));
        Assert.Empty(firstRun.Select(c => c.Result).Intersect(completed.Select(c => c.Result)));
        Assert.Equal(SequenceState.Completed, runner.State);
    }

    [Fact]
    public async Task ExposuresInGroupInRepeat_ProduceFramesAfterEachIteration()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera", seed: 1);
        await camera.ConnectAsync();
        var exposure = new CameraExposureAction(host.DeviceRegistry, camera.Id, TimeSpan.FromMilliseconds(20));
        var runner = new SequenceRunner();
        var completed = Observe(runner);

        await runner.RunAsync(new Sequence("s", [new RepeatStep(3, new SequenceGroup("Imaging Block", [exposure]))]));

        var frames = completed.Select(c => c.Result.Payload).OfType<CameraFrame>().ToArray();
        Assert.Equal(3, frames.Length);
        Assert.Equal(3, frames.Distinct().Count());
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }
}

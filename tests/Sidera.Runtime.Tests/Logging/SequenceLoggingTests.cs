using Sidera.Core.Coordination;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Diagnostics;
using Sidera.Runtime.Sequencing;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

public class SequenceLoggingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    private sealed class OkStep(string name) : ISequenceStep
    {
        public string Name { get; } = name;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SequenceStepResult());
    }

    private sealed class FailStep(string name, Exception failure) : ISequenceStep
    {
        public string Name { get; } = name;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken) =>
            Task.FromException<SequenceStepResult>(failure);
    }

    /// <summary>Runs until released, or until the run is cancelled.</summary>
    private sealed class GateStep(string name) : ISequenceStep
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name { get; } = name;
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();

        public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new SequenceStepResult();
        }
    }

    private static (SideraRuntimeHost Host, SequenceRunner Runner, LogCapture Log) Create()
    {
        var log = new LogCapture();
        var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        var runner = new SequenceRunner(
            host.ResourceManager, host.SafePointCoordinator, host.LoggerFactory.CreateLogger<SequenceRunner>());
        return (host, runner, log);
    }

    [Fact]
    public async Task ACompletedRun_IsLoggedAsStartedAndCompleted_WithItsExecutionIdAndTheSession()
    {
        var (host, runner, log) = Create();
        await using var _ = host;

        await runner.RunAsync(new Sequence("Night 1", [new OkStep("A"), new OkStep("B")]));

        var started = log.Single(LogLevel.Information, "Sequence Night 1 started");
        var completed = log.Single(LogLevel.Information, "Sequence Night 1 completed");
        Assert.NotEqual(Guid.Empty, runner.ExecutionId);
        Assert.Equal(runner.ExecutionTag, started.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Equal(runner.ExecutionTag, completed.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Equal(host.SessionId, started.ScopeValue(LogContext.SessionId));
        Assert.Equal(2, started.Properties["StepCount"]);
        Assert.IsType<double>(completed.Properties["DurationSeconds"]);
    }

    [Fact]
    public async Task EveryRunGetsANewExecutionId_AndTheScopeEndsWithTheRun()
    {
        var (host, runner, log) = Create();
        await using var _ = host;
        var sequence = new Sequence("s", [new OkStep("A")]);

        await runner.RunAsync(sequence);
        var first = runner.ExecutionId;
        await runner.RunAsync(sequence);
        host.LoggerFactory.CreateLogger("after").LogInformation("after the runs");

        Assert.NotEqual(first, runner.ExecutionId);
        Assert.Equal(2, log.Entries.Select(e => e.ScopeValue(LogContext.SequenceExecutionId)).OfType<string>().Distinct().Count());
        var after = log.Single(LogLevel.Information, "after the runs");
        Assert.Null(after.ScopeValue(LogContext.SequenceExecutionId)); // the run's scope did not leak
        Assert.Equal(host.SessionId, after.ScopeValue(LogContext.SessionId)); // the session's did not need one
    }

    [Fact]
    public async Task Steps_AreLoggedAtDebug_WithTheirPathAndNotAtInformation()
    {
        var (host, runner, log) = Create();
        await using var _ = host;

        await runner.RunAsync(new Sequence("s", [new RepeatStep(2, new OkStep("Inner"))]));

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message == "Step Repeat × 2 [1/1] started");
        var inner = log.Entries.Where(e => e.Level == LogLevel.Debug && e.Message.Contains("Inner") && e.Message.Contains("completed")).ToList();
        Assert.Equal(2, inner.Count);
        Assert.All(inner, e => Assert.Contains(" > ", e.Message)); // the path leads from the top-level step to the child
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Information && e.Message.Contains("Inner"));
    }

    [Fact]
    public async Task AFailedRun_IsAnErrorWithTheException_AndTheFailedStepIsDebug()
    {
        var (host, runner, log) = Create();
        await using var _ = host;
        var failure = new InvalidOperationException("the camera is not connected");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync(new Sequence("Night 2", [new OkStep("A"), new FailStep("Expose", failure)])));

        var error = log.Single(LogLevel.Error, "Sequence Night 2 failed");
        Assert.Same(failure, error.Exception);
        Assert.Equal(runner.ExecutionTag, error.ScopeValue(LogContext.SequenceExecutionId));
        var step = log.Entries.Single(e => e.Level == LogLevel.Debug && e.Message.Contains("Expose") && e.Message.Contains("failed"));
        Assert.Contains("the camera is not connected", step.Message);
        Assert.Null(step.Exception); // the exception is logged once, with its stack trace, by the run
    }

    [Fact]
    public async Task ACancelledRun_IsInformation_NeverAnError()
    {
        var (host, runner, log) = Create();
        await using var _ = host;
        var gate = new GateStep("Wait");
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("Night 3", [gate]), cts.Token);
        await gate.Started.WaitAsync(Bound);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Single(log.Where(LogLevel.Information, "Sequence Night 3 cancelled"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("Wait") && e.Message.Contains("cancelled"));
    }

    [Fact]
    public async Task PauseAndResume_AreInformation_WithTheExecutionId()
    {
        var (host, runner, log) = Create();
        await using var _ = host;
        var first = new GateStep("First");
        var run = runner.RunAsync(new Sequence("s", [first, new OkStep("Second")]));
        await first.Started.WaitAsync(Bound);

        var paused = runner.PauseAsync();
        first.Release();
        Assert.True(await paused.WaitAsync(Bound));

        var requested = log.Single(LogLevel.Information, "Pause requested");
        var pausedEntry = await log.WaitForAsync(e => e.Message.StartsWith("Sequence paused", StringComparison.Ordinal));
        Assert.Equal(runner.ExecutionTag, requested.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Equal(runner.ExecutionTag, pausedEntry.ScopeValue(LogContext.SequenceExecutionId));

        Assert.True(runner.Resume());
        await run.WaitAsync(Bound);

        var resumed = log.Single(LogLevel.Information, "Resume requested");
        Assert.Equal(runner.ExecutionTag, resumed.ScopeValue(LogContext.SequenceExecutionId));
        Assert.Contains(log.Entries, e => e.Message.Contains("waits at the pause boundary"));
    }

    [Fact]
    public async Task AParallelBlock_IsInformation_AndAFailingBranchIsAWarningWithItsReason()
    {
        var (host, runner, log) = Create();
        await using var _ = host;

        await runner.RunAsync(new Sequence("s", [new ParallelStep("Multi-Rig Imaging", [new OkStep("A"), new OkStep("B")])]));
        var started = log.Single(LogLevel.Information, "Parallel block Multi-Rig Imaging started");
        Assert.Equal(2, started.Properties["BranchCount"]);
        Assert.Single(log.Where(LogLevel.Information, "Parallel block Multi-Rig Imaging completed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new Sequence("t", [
            new ParallelStep("Multi-Rig Imaging", [new OkStep("A"), new FailStep("B", new InvalidOperationException("boom"))])])));
        var warning = log.Single(LogLevel.Warning, "Parallel block Multi-Rig Imaging failed");
        Assert.Contains("boom", warning.Message);
    }

    [Fact]
    public async Task TheBranchesOfACoordinatedBlock_LogInTheirOwnGroupAndParticipantContext()
    {
        var (host, runner, log) = Create();
        await using var _ = host;
        var group = new CoordinationGroupId("session.test");

        await runner.RunAsync(new Sequence("s", [new ParallelStep("Block", [new OkStep("Left"), new OkStep("Right")], group)]));

        var left = log.Entries.Single(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Step Block", StringComparison.Ordinal)
            && e.Message.Contains("Left") && e.Message.Contains("started"));
        var right = log.Entries.Single(e => e.Level == LogLevel.Debug && e.Message.StartsWith("Step Block", StringComparison.Ordinal)
            && e.Message.Contains("Right") && e.Message.Contains("started"));
        Assert.Equal("session.test", left.ScopeValue(LogContext.CoordinationGroupId));
        Assert.Equal("session.test", right.ScopeValue(LogContext.CoordinationGroupId));
        Assert.NotNull(left.ScopeValue(LogContext.ParticipantId));
        Assert.NotEqual(left.ScopeValue(LogContext.ParticipantId), right.ScopeValue(LogContext.ParticipantId));
        Assert.Equal(runner.ExecutionTag, left.ScopeValue(LogContext.SequenceExecutionId));
    }

    [Fact]
    public async Task ConcurrentRuns_KeepTheirContextsApart()
    {
        var log = new LogCapture();
        await using var host = new SideraRuntimeHost(loggerFactory: log.Factory);
        SequenceRunner NewRunner() => new(host.ResourceManager, host.SafePointCoordinator, host.LoggerFactory.CreateLogger<SequenceRunner>());
        var one = NewRunner();
        var two = NewRunner();
        var gateOne = new GateStep("OneStep");
        var gateTwo = new GateStep("TwoStep");

        var runOne = one.RunAsync(new Sequence("One", [gateOne, new OkStep("OneAfter")]));
        var runTwo = two.RunAsync(new Sequence("Two", [gateTwo, new OkStep("TwoAfter")]));
        await Task.WhenAll(gateOne.Started, gateTwo.Started).WaitAsync(Bound);
        gateTwo.Release();
        gateOne.Release();
        await Task.WhenAll(runOne, runTwo).WaitAsync(Bound);

        Assert.NotEqual(one.ExecutionTag, two.ExecutionTag);
        Assert.All(log.Entries.Where(e => e.Message.Contains("One")),
            e => Assert.Equal(one.ExecutionTag, e.ScopeValue(LogContext.SequenceExecutionId)));
        Assert.All(log.Entries.Where(e => e.Message.Contains("Two")),
            e => Assert.Equal(two.ExecutionTag, e.ScopeValue(LogContext.SequenceExecutionId)));
    }

    [Fact]
    public async Task ARunnerWithoutALogger_StillRuns()
    {
        var runner = new SequenceRunner();

        await runner.RunAsync(new Sequence("s", [new OkStep("A")]));

        Assert.Equal(SequenceState.Completed, runner.State);
    }
}

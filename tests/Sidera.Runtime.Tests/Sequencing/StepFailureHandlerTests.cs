using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Runtime.Tests.Sequencing;

/// <summary>A failure handler can hold a run at the step that failed and have the step run again; what it lets go goes up as it was, once.</summary>
public class StepFailureHandlerTests
{
    private sealed class FailingStep(string name, List<string> log, int failures) : ISequenceStep
    {
        private int _runs;

        public string Name { get; } = name;

        public Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
        {
            _runs++;
            log.Add($"run {Name} #{_runs}");
            return _runs <= failures ? throw new InvalidOperationException($"{Name} failed #{_runs}") : Task.FromResult(new SequenceStepResult(Name));
        }
    }

    private sealed class Handler(Func<ISequenceStep, Exception, CancellationToken, Task<bool>> decide) : IStepFailureHandler
    {
        public List<string> Asked { get; } = [];

        public Task<bool> HandleAsync(ISequenceStep step, Exception exception, CancellationToken cancellationToken)
        {
            Asked.Add($"{step.Name}: {exception.Message}");
            return decide(step, exception, cancellationToken);
        }
    }

    [Fact]
    public async Task ABrokenStep_IsRunAgain_WhenTheHandlerSaysSo_AndTheRunCompletes()
    {
        var log = new List<string>();
        var handler = new Handler((_, _, _) => Task.FromResult(true));
        var runner = new SequenceRunner { FailureHandler = handler };

        await runner.RunAsync(new Sequence("s", [new FailingStep("A", log, failures: 2), new FailingStep("B", log, failures: 0)]));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(["run A #1", "run A #2", "run A #3", "run B #1"], log);
        Assert.Equal(2, handler.Asked.Count);
    }

    [Fact]
    public async Task WhatTheHandlerLetsGo_FailsTheRun_AndIsAskedOnce_EvenInsideGroups()
    {
        var log = new List<string>();
        var handler = new Handler((_, _, _) => Task.FromResult(false));
        var runner = new SequenceRunner { FailureHandler = handler };
        var inner = new SequenceGroup("inner", [new FailingStep("A", log, failures: 1)]);
        var outer = new SequenceGroup("outer", [inner]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new Sequence("s", [outer])));

        Assert.Equal("A failed #1", ex.Message);
        Assert.Equal(SequenceState.Failed, runner.State);
        Assert.Equal(["A: A failed #1"], handler.Asked); // the groups above the step are not asked about the same failure again
        Assert.Equal(["run A #1"], log);
    }

    [Fact]
    public async Task WithoutAHandler_AFailureGoesUpAsItAlwaysDid()
    {
        var log = new List<string>();
        var runner = new SequenceRunner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new Sequence("s", [new FailingStep("A", log, failures: 1)])));

        Assert.Equal(SequenceState.Failed, runner.State);
    }

    [Fact]
    public async Task TheRunWaitsInTheHandler_AndCancelling_EndsIt()
    {
        var log = new List<string>();
        var waiting = new TaskCompletionSource();
        var handler = new Handler(async (_, _, ct) =>
        {
            waiting.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        });
        var runner = new SequenceRunner { FailureHandler = handler };
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(new Sequence("s", [new FailingStep("A", log, failures: 1)]), cts.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SequenceState.Running, runner.State);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(SequenceState.Cancelled, runner.State);
    }

    [Fact]
    public void AMarkedFailure_IsRecognisedThroughWrappingExceptions()
    {
        var inner = new InvalidOperationException("inner");
        var wrapped = new InvalidOperationException("outer", inner);

        Assert.False(StepFailureHandling.IsHandled(wrapped));
        StepFailureHandling.MarkHandled(inner);
        Assert.True(StepFailureHandling.IsHandled(wrapped));
        Assert.True(StepFailureHandling.IsHandled(new AggregateException(wrapped)));
    }
}

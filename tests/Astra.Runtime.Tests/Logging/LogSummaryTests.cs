using Astra.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Astra.Runtime.Tests.Logging;

public class LogSummaryTests
{
    [Fact]
    public void WarningsAndErrorsAreCounted_AndCriticalCountsAsAnError()
    {
        var summary = new LogSummary();
        var logger = summary.CreateLogger("Astra.Runtime.X");

        logger.LogInformation("fine");
        logger.LogWarning("w1");
        logger.LogError("e1");
        logger.LogCritical("c1");

        Assert.Equal(1, summary.WarningCount);
        Assert.Equal(2, summary.ErrorCount);
        Assert.Equal(["w1", "e1", "c1"], summary.Recent.Select(p => p.Message));
        Assert.All(summary.Recent, p => Assert.Equal("X", p.Category));
    }

    [Fact]
    public void OnlyTheLatestProblemsAreKept()
    {
        var summary = new LogSummary();
        var logger = summary.CreateLogger("X");

        for (var i = 0; i < LogSummary.Capacity + 3; i++)
        {
            logger.LogWarning("w{Number}", i);
        }

        Assert.Equal(LogSummary.Capacity + 3, summary.WarningCount);
        Assert.Equal(LogSummary.Capacity, summary.Recent.Count);
        Assert.Equal("w3", summary.Recent[0].Message);
    }

    [Fact]
    public void ChangedIsRaisedForEachProblem_AndAnObserverThatThrowsDoesNotBreakLogging()
    {
        var summary = new LogSummary();
        var raised = 0;
        summary.Changed += (_, _) =>
        {
            raised++;
            throw new InvalidOperationException("observer");
        };

        summary.CreateLogger("X").LogError("boom");
        summary.CreateLogger("X").LogDebug("quiet");

        Assert.Equal(1, raised);
        Assert.Equal(1, summary.ErrorCount);
    }

    [Fact]
    public async Task ConcurrentProblems_AreAllCounted()
    {
        var summary = new LogSummary();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var logger = summary.CreateLogger("X");
            for (var i = 0; i < 500; i++)
            {
                logger.LogWarning("w");
            }
        })));

        Assert.Equal(4000, summary.WarningCount);
        Assert.Equal(LogSummary.Capacity, summary.Recent.Count);
    }
}

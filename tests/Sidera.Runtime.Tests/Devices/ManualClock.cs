using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>
/// A clock that only moves when the test advances it. Every delay requested from it is announced, so a test can
/// wait for the code under test to reach its next wait instead of sleeping.
/// </summary>
internal sealed class ManualClock : ISimulatedClock
{
    private readonly object _gate = new();
    private readonly List<(TimeSpan Due, TaskCompletionSource Done)> _delays = new();
    private readonly SemaphoreSlim _requested = new(0);
    private TimeSpan _now = TimeSpan.FromMinutes(1);

    public TimeSpan Now
    {
        get { lock (_gate) { return _now; } }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _delays.Add((_now + delay, done));
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                _delays.RemoveAll(d => d.Done == done);
            }

            done.TrySetCanceled(cancellationToken);
        });
        _requested.Release();
        return done.Task;
    }

    /// <summary>Completes once the code under test has requested its next delay (one per call).</summary>
    public async Task NextDelay() =>
        Assert.True(await _requested.WaitAsync(TimeSpan.FromSeconds(5)), "Timed out waiting for the next delay.");

    /// <summary>Moves time forward and completes every delay that is due.</summary>
    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _now += by;
            due = _delays.Where(d => d.Due <= _now).Select(d => d.Done).ToList();
            _delays.RemoveAll(d => d.Due <= _now);
        }

        foreach (var done in due)
        {
            done.TrySetResult();
        }
    }

    /// <summary>Waits for the next delay, then advances by <paramref name="by"/>.</summary>
    public async Task StepAsync(TimeSpan by)
    {
        await NextDelay();
        Advance(by);
    }
}

using System.Collections.Concurrent;

namespace Astra.Ascom.Infrastructure;

/// <summary>
/// One single-threaded apartment (STA) thread that every COM call of one ASCOM device goes through, one at a time.
/// The COM object is created, used and released on this thread and nowhere else. Classic ASCOM drivers may be
/// apartment-threaded; the simulators being free-threaded proves nothing about real drivers.
/// <para>
/// Cancelling a call only has an effect while it still waits in the queue. A call that already runs cannot be
/// interrupted: a driver that blocks, blocks the dispatcher, and everything queued behind it waits. Callers that must
/// stay responsive wait with their own token (<c>WaitAsync</c>) and treat the work as possibly still running.
/// </para>
/// <para>
/// There is no message loop on the thread: a driver that needs window messages for callbacks outside of its own calls
/// is not served. Dialogs that a driver shows run their own loop.
/// </para>
/// <para>
/// Disposal is idempotent. Work that has not started is completed with <see cref="ObjectDisposedException"/>, never
/// run; work that is running is waited for, for a bounded time; the thread is a background thread, so a driver that
/// never returns cannot keep the process alive.
/// </para>
/// </summary>
public sealed class AscomDispatcher : IDisposable
{
    /// <summary>How long disposal waits for a call that is running before it gives up on the thread.</summary>
    public static readonly TimeSpan DefaultShutdownWait = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly BlockingCollection<IWork> _queue = [];
    private readonly Thread _thread;
    private readonly TimeSpan _shutdownWait;
    private bool _disposed;

    public AscomDispatcher(string name, TimeSpan? shutdownWait = null)
    {
        Name = name;
        _shutdownWait = shutdownWait ?? DefaultShutdownWait;
        _thread = new Thread(Run) { IsBackground = true, Name = $"ASCOM STA {name}" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public string Name { get; }

    /// <summary>The managed id of the dispatcher thread, for tests and diagnostics.</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public bool IsOnDispatcherThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>The thread has ended. After disposal it is false for as long as a call that was running has not returned.</summary>
    public bool HasExited => !_thread.IsAlive;

    public Task InvokeAsync(Action work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync<object?>(
            () =>
            {
                work();
                return null;
            },
            cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dispatcher thread after everything queued before it. Exceptions of the work
    /// are carried by the returned task unchanged.
    /// </summary>
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new Work<T>(work, cancellationToken);

        lock (_gate)
        {
            if (_disposed)
            {
                item.Fail(new ObjectDisposedException(nameof(AscomDispatcher), $"The ASCOM dispatcher {Name} was disposed."));
                return item.Task;
            }

            _queue.Add(item);
        }

        return item.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    public void Dispose()
    {
        List<IWork> pending = [];
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_queue.TryTake(out var item))
            {
                pending.Add(item);
            }

            _queue.CompleteAdding();
        }

        foreach (var item in pending)
        {
            item.Fail(new ObjectDisposedException(nameof(AscomDispatcher), $"The ASCOM dispatcher {Name} was disposed."));
        }

        if (!IsOnDispatcherThread)
        {
            _thread.Join(_shutdownWait);
        }
    }

    private interface IWork
    {
        void Execute();

        void Fail(Exception exception);
    }

    private sealed class Work<T>(Func<T> work, CancellationToken cancellationToken) : IWork
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _completion.TrySetResult(work());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }

        public void Fail(Exception exception) => _completion.TrySetException(exception);
    }
}

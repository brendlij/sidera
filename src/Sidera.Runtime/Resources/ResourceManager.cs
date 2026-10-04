using Sidera.Core.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Resources;

/// <summary>
/// Hands out exclusive access to resources. A request names all resources it needs and is granted
/// all of them at once or none (no partial holding), so requests can never deadlock each other
/// however their resources are ordered. Waiting requests are served first-come-first-served among
/// those that conflict; requests for unrelated resources never wait for each other.
/// <para>
/// Diagnostics: requests, grants (with how long they waited) and releases (with how long the lease was held) are logged
/// at Debug, in the scopes of whoever asks, so a log shows which step held what. A request that waits longer than
/// <c>waitWarningThreshold</c> is reported once as a Warning, naming what it is waiting for; that is the first thing to
/// look at when a sequence seems stuck. The wait is watched by one timer per waiting request, not by polling.
/// </para>
/// </summary>
public sealed class ResourceManager
{
    /// <summary>How long a request waits before it is reported as a warning.</summary>
    public static readonly TimeSpan DefaultWaitWarningThreshold = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly HashSet<ResourceId> _held = new();
    private readonly List<Waiter> _waiters = new();
    private readonly ILogger _logger;
    private readonly TimeSpan _waitWarningThreshold;
    private readonly TimeProvider _time;

    /// <param name="logger">Where requests, grants, releases and long waits are reported.</param>
    /// <param name="waitWarningThreshold">How long a request may wait before a warning; <see cref="DefaultWaitWarningThreshold"/> by default.</param>
    /// <param name="time">The clock of the waits; the system clock by default.</param>
    public ResourceManager(
        ILogger<ResourceManager>? logger = null, TimeSpan? waitWarningThreshold = null, TimeProvider? time = null)
    {
        _logger = logger ?? NullLogger<ResourceManager>.Instance;
        _waitWarningThreshold = waitWarningThreshold ?? DefaultWaitWarningThreshold;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_waitWarningThreshold, TimeSpan.Zero);
        _time = time ?? TimeProvider.System;
    }

    private sealed class Waiter(ResourceId[] resources)
    {
        public ResourceId[] Resources { get; } = resources;

        public TaskCompletionSource<ResourceLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Waits until every requested resource is free and takes them all. Duplicates are ignored and the
    /// caller's order does not matter. An empty request succeeds immediately. Cancelling while waiting
    /// throws <see cref="OperationCanceledException"/> and leaves nothing held.
    /// </summary>
    public async Task<ResourceLease> AcquireAsync(
        IEnumerable<ResourceId> resources,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resources);
        cancellationToken.ThrowIfCancellationRequested();

        // Distinct and sorted: a stable order for everything below, independent of the caller.
        var requested = resources.Distinct().OrderBy(r => r.Value, StringComparer.Ordinal).ToArray();
        if (requested.Length == 0)
        {
            return new ResourceLease(this, requested, 0);
        }

        var waiter = new Waiter(requested);
        var names = Describe(requested);
        var requestedAt = _time.GetTimestamp();
        _logger.LogDebug("Resources {Resources} requested", names);

        lock (_gate)
        {
            _waiters.Add(waiter);
            GrantReadyWaiters();
        }

        using var watch = waiter.Completion.Task.IsCompleted ? null : WatchWait(waiter, names, requestedAt);
        using (cancellationToken.Register(() => CancelWaiter(waiter, cancellationToken)))
        {
            try
            {
                var lease = await waiter.Completion.Task;
                lease.Granted(_time.GetTimestamp());
                _logger.LogDebug(
                    "Resources {Resources} granted after {WaitMs:0} ms", names, _time.GetElapsedTime(requestedAt).TotalMilliseconds);
                return lease;
            }
            catch (OperationCanceledException)
            {
                // Expected when a run is cancelled: not a failure.
                _logger.LogDebug(
                    "Request for resources {Resources} cancelled after {WaitMs:0} ms",
                    names, _time.GetElapsedTime(requestedAt).TotalMilliseconds);
                throw;
            }
        }
    }

    private static string Describe(IEnumerable<ResourceId> resources) => string.Join(", ", resources.Select(r => r.Value));

    // One timer for a request that has to wait: when it fires and the request is still waiting, it is reported once.
    private ITimer? WatchWait(Waiter waiter, string names, long requestedAt)
    {
        if (!_logger.IsEnabled(LogLevel.Warning))
        {
            return null;
        }

        return _time.CreateTimer(
            _ =>
            {
                if (waiter.Completion.Task.IsCompleted)
                {
                    return;
                }

                string heldBy;
                lock (_gate)
                {
                    heldBy = Describe(waiter.Resources.Where(_held.Contains));
                }

                _logger.LogWarning(
                    "Resource wait exceeded {WaitMs:0} ms: still waiting for {Resources}; held by someone else: {HeldResources}",
                    _time.GetElapsedTime(requestedAt).TotalMilliseconds, names,
                    heldBy.Length == 0 ? "none (queued behind an earlier request)" : heldBy);
            },
            null, _waitWarningThreshold, Timeout.InfiniteTimeSpan);
    }

    public bool IsHeld(ResourceId resource)
    {
        lock (_gate)
        {
            return _held.Contains(resource);
        }
    }

    /// <summary>Number of requests currently waiting for resources; for tests and diagnostics.</summary>
    internal int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    internal void Release(IReadOnlyList<ResourceId> resources, long grantedAt)
    {
        lock (_gate)
        {
            foreach (var resource in resources)
            {
                _held.Remove(resource);
            }

            GrantReadyWaiters();
        }

        if (_logger.IsEnabled(LogLevel.Debug) && resources.Count > 0)
        {
            _logger.LogDebug(
                "Resources {Resources} released after {HeldMs:0} ms",
                Describe(resources), _time.GetElapsedTime(grantedAt).TotalMilliseconds);
        }
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        bool removed;
        lock (_gate)
        {
            removed = _waiters.Remove(waiter);
            if (removed)
            {
                // The cancelled request may have been the only thing blocking later ones.
                GrantReadyWaiters();
            }
        }

        // If it was not in the list it has just been granted; the caller then owns (and releases) the lease.
        if (removed)
        {
            waiter.Completion.TrySetCanceled(cancellationToken);
        }
    }

    // Must be called with the lock held. Walks the queue in arrival order; a request is granted when none
    // of its resources is held or wanted by an earlier, still waiting request.
    private void GrantReadyWaiters()
    {
        var blocked = new HashSet<ResourceId>(_held);

        for (var i = 0; i < _waiters.Count;)
        {
            var waiter = _waiters[i];

            if (waiter.Resources.Any(blocked.Contains))
            {
                blocked.UnionWith(waiter.Resources);
                i++;
                continue;
            }

            _waiters.RemoveAt(i);
            _held.UnionWith(waiter.Resources);
            blocked.UnionWith(waiter.Resources);
            waiter.Completion.TrySetResult(new ResourceLease(this, waiter.Resources, 0));
        }
    }
}

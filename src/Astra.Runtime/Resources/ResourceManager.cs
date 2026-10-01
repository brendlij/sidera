using Astra.Core.Resources;

namespace Astra.Runtime.Resources;

/// <summary>
/// Hands out exclusive access to resources. A request names all resources it needs and is granted
/// all of them at once or none (no partial holding), so requests can never deadlock each other
/// however their resources are ordered. Waiting requests are served first-come-first-served among
/// those that conflict; requests for unrelated resources never wait for each other.
/// </summary>
public sealed class ResourceManager
{
    private readonly object _gate = new();
    private readonly HashSet<ResourceId> _held = new();
    private readonly List<Waiter> _waiters = new();

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
            return new ResourceLease(this, requested);
        }

        var waiter = new Waiter(requested);
        lock (_gate)
        {
            _waiters.Add(waiter);
            GrantReadyWaiters();
        }

        using (cancellationToken.Register(() => CancelWaiter(waiter, cancellationToken)))
        {
            return await waiter.Completion.Task;
        }
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

    internal void Release(IReadOnlyList<ResourceId> resources)
    {
        lock (_gate)
        {
            foreach (var resource in resources)
            {
                _held.Remove(resource);
            }

            GrantReadyWaiters();
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
            waiter.Completion.TrySetResult(new ResourceLease(this, waiter.Resources));
        }
    }
}

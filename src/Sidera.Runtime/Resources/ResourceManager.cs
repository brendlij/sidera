using Sidera.Core.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Resources;

/// <summary>
/// Hands out access to resources, shared or exclusive. A request names all the claims it needs and is granted
/// all of them at once or none (no partial holding), so requests can never deadlock each other
/// however their resources are ordered. Shared claims of one resource are granted together; an exclusive claim
/// is granted alone. Waiting requests are served first-come-first-served among those that conflict (a waiting
/// exclusive claim holds back the shared claims that come after it, so it cannot starve); requests for unrelated
/// resources never wait for each other.
/// <para>
/// Diagnostics: requests, grants (with how long they waited) and releases (with how long the lease was held) are logged
/// at Debug, in the scopes of whoever asks, so a log shows which step held what. A request that waits longer than
/// <c>waitWarningThreshold</c> is reported once as a Warning, naming what it is waiting for; that is the first thing to
/// look at when a sequence seems stuck. The wait is watched by one timer per waiting request, not by polling.
/// </para>
/// </summary>
public sealed class ResourceManager
{
    /// <summary>
    /// Raised, on the thread that changed it and in the order of the changes, when claims were granted or released. Meant for diagnostics and tests that want to know exactly what was held when:
    /// it is raised while the manager is locked, so an observer must be quick and must not call back into the manager.
    /// </summary>
    public event EventHandler<ResourceChange>? Changed;

    private void RaiseChanged(IReadOnlyList<ResourceClaim> claims, bool granted)
    {
        try
        {
            Changed?.Invoke(this, new ResourceChange(claims, granted));
        }
        catch
        {
            // An observer must not break the hand-out of resources.
        }
    }

    /// <summary>How long a request waits before it is reported as a warning.</summary>
    public static readonly TimeSpan DefaultWaitWarningThreshold = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly HashSet<ResourceId> _heldExclusive = new();
    private readonly Dictionary<ResourceId, int> _heldShared = new();
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

    private sealed class Waiter(ResourceClaim[] claims)
    {
        public ResourceClaim[] Claims { get; } = claims;
        public IEnumerable<ResourceId> Resources => Claims.Select(c => c.Resource);

        public TaskCompletionSource<ResourceLease> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Waits until every requested resource is free and takes them all. Duplicates are ignored and the
    /// caller's order does not matter. An empty request succeeds immediately. Cancelling while waiting
    /// throws <see cref="OperationCanceledException"/> and leaves nothing held.
    /// </summary>
    public Task<ResourceLease> AcquireAsync(IEnumerable<ResourceId> resources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return AcquireClaimsAsync(ResourceClaim.AllExclusive(resources), cancellationToken);
    }

    /// <summary>
    /// Waits until every claim can be granted and takes them all: shared claims next to other shared claims of the same resource, exclusive claims alone. A resource named twice is held exclusively
    /// when either claim is. Cancelling while waiting throws <see cref="OperationCanceledException"/> and leaves nothing held.
    /// </summary>
    public async Task<ResourceLease> AcquireClaimsAsync(IEnumerable<ResourceClaim> claims, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        cancellationToken.ThrowIfCancellationRequested();

        // One claim for each resource, sorted: a stable order for everything below, independent of the caller.
        var requested = ResourceClaim.Normalize(claims).ToArray();
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

    private static string Describe(IEnumerable<ResourceClaim> claims) => string.Join(", ", claims.Select(c => c.ToString()));

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
                    heldBy = Describe(waiter.Claims.Where(c => IsHeldInConflict(c)).Select(c => c.Resource));
                }

                _logger.LogWarning(
                    "Resource wait exceeded {WaitMs:0} ms: still waiting for {Resources}; held by someone else: {HeldResources}",
                    _time.GetElapsedTime(requestedAt).TotalMilliseconds, names,
                    heldBy.Length == 0 ? "none (queued behind an earlier request)" : heldBy);
            },
            null, _waitWarningThreshold, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Whether somebody holds the resource, in any mode.</summary>
    public bool IsHeld(ResourceId resource)
    {
        lock (_gate)
        {
            return _heldExclusive.Contains(resource) || _heldShared.ContainsKey(resource);
        }
    }

    /// <summary>Whether somebody holds the resource alone.</summary>
    public bool IsHeldExclusively(ResourceId resource)
    {
        lock (_gate)
        {
            return _heldExclusive.Contains(resource);
        }
    }

    /// <summary>How many holders have the resource shared right now.</summary>
    public int SharedHolders(ResourceId resource)
    {
        lock (_gate)
        {
            return _heldShared.TryGetValue(resource, out var count) ? count : 0;
        }
    }

    // Must be called with the lock held: whether what is held now stands in the way of the claim.
    private bool IsHeldInConflict(ResourceClaim claim) =>
        _heldExclusive.Contains(claim.Resource) || (claim.Mode == ClaimMode.Exclusive && _heldShared.ContainsKey(claim.Resource));

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

    internal void Release(IReadOnlyList<ResourceClaim> claims, long grantedAt)
    {
        lock (_gate)
        {
            foreach (var claim in claims)
            {
                if (claim.Mode == ClaimMode.Exclusive)
                {
                    _heldExclusive.Remove(claim.Resource);
                }
                else if (_heldShared.TryGetValue(claim.Resource, out var count))
                {
                    if (count <= 1)
                    {
                        _heldShared.Remove(claim.Resource);
                    }
                    else
                    {
                        _heldShared[claim.Resource] = count - 1;
                    }
                }
            }

            if (claims.Count > 0)
            {
                RaiseChanged(claims, false);
            }

            GrantReadyWaiters();
        }

        if (_logger.IsEnabled(LogLevel.Debug) && claims.Count > 0)
        {
            _logger.LogDebug(
                "Resources {Resources} released after {HeldMs:0} ms",
                Describe(claims), _time.GetElapsedTime(grantedAt).TotalMilliseconds);
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

    // Must be called with the lock held. Walks the queue in arrival order. "anyBlocked" are the resources that are held, or wanted by an earlier request that still waits, in any mode;
    // "exclusiveBlocked" those that are held exclusively or wanted exclusively. An exclusive claim is granted when its resource is not in the first set, a shared claim when it is not in the
    // second: so shared claims go together, and a waiting exclusive claim holds back the shared ones that come after it.
    private void GrantReadyWaiters()
    {
        var granted = GrantReadyWaitersCore();
        foreach (var claims in granted)
        {
            RaiseChanged(claims, true);
        }
    }

    private List<ResourceClaim[]> GrantReadyWaitersCore()
    {
        var granted = new List<ResourceClaim[]>();
        var anyBlocked = new HashSet<ResourceId>(_heldExclusive);
        anyBlocked.UnionWith(_heldShared.Keys);
        var exclusiveBlocked = new HashSet<ResourceId>(_heldExclusive);

        for (var i = 0; i < _waiters.Count;)
        {
            var waiter = _waiters[i];
            var blocked = waiter.Claims.Any(c => c.Mode == ClaimMode.Exclusive ? anyBlocked.Contains(c.Resource) : exclusiveBlocked.Contains(c.Resource));

            foreach (var claim in waiter.Claims)
            {
                anyBlocked.Add(claim.Resource);
                if (claim.Mode == ClaimMode.Exclusive)
                {
                    exclusiveBlocked.Add(claim.Resource);
                }
            }

            if (blocked)
            {
                i++;
                continue;
            }

            _waiters.RemoveAt(i);
            foreach (var claim in waiter.Claims)
            {
                if (claim.Mode == ClaimMode.Exclusive)
                {
                    _heldExclusive.Add(claim.Resource);
                }
                else
                {
                    _heldShared[claim.Resource] = (_heldShared.TryGetValue(claim.Resource, out var count) ? count : 0) + 1;
                }
            }

            granted.Add(waiter.Claims);
            waiter.Completion.TrySetResult(new ResourceLease(this, waiter.Claims, 0));
        }

        return granted;
    }
}

/// <summary>Claims that were granted (<see cref="Granted"/>) or released.</summary>
public sealed record ResourceChange(IReadOnlyList<ResourceClaim> Claims, bool Granted);

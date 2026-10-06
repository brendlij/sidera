using Sidera.Core.Resources;

namespace Sidera.Runtime.Resources;

/// <summary>Proof of holding a set of resources. Disposing releases them; disposing twice is harmless.</summary>
public sealed class ResourceLease : IDisposable
{
    private readonly ResourceManager _manager;
    private long _grantedAt;
    private int _released;

    internal ResourceLease(ResourceManager manager, IReadOnlyList<ResourceClaim> claims, long grantedAt)
    {
        _manager = manager;
        Claims = claims;
        Resources = [.. claims.Select(c => c.Resource)];
        _grantedAt = grantedAt;
    }

    // The moment the caller received the lease, for the duration it is reported to have been held.
    internal void Granted(long timestamp) => _grantedAt = timestamp;

    /// <summary>The distinct resources held by this lease, in the manager's acquisition order.</summary>
    public IReadOnlyList<ResourceId> Resources { get; }

    /// <summary>What is held, and how.</summary>
    public IReadOnlyList<ResourceClaim> Claims { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _manager.Release(Claims, _grantedAt);
        }
    }
}

using Astra.Core.Resources;

namespace Astra.Runtime.Resources;

/// <summary>Proof of holding a set of resources. Disposing releases them; disposing twice is harmless.</summary>
public sealed class ResourceLease : IDisposable
{
    private readonly ResourceManager _manager;
    private int _released;

    internal ResourceLease(ResourceManager manager, IReadOnlyList<ResourceId> resources)
    {
        _manager = manager;
        Resources = resources;
    }

    /// <summary>The distinct resources held by this lease, in the manager's acquisition order.</summary>
    public IReadOnlyList<ResourceId> Resources { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _manager.Release(Resources);
        }
    }
}

using Sidera.Core.Devices;

namespace Sidera.Core.Resources;

/// <summary>How an action holds a resource: next to others that only look at it, or alone.</summary>
public enum ClaimMode
{
    /// <summary>Many may hold it at once, none may hold it exclusively meanwhile (an exposure keeps the mount still; it does not mind another exposure doing the same).</summary>
    Shared,

    /// <summary>Held alone: nobody else holds it in any mode meanwhile (a slew, a dither, a camera that exposes).</summary>
    Exclusive
}

/// <summary>
/// What an action needs of one resource, and how. Every action that does real work on equipment declares its claims; what may run at the same time follows from them and from nothing else:
/// two actions run together when none of their claims conflicts (an exclusive claim conflicts with every other claim of the resource, a shared one only with exclusive ones). There is no rule that
/// names a rig, a track or a number of cameras.
/// </summary>
public readonly record struct ResourceClaim(ResourceId Resource, ClaimMode Mode)
{
    public static ResourceClaim Exclusive(ResourceId resource) => new(resource, ClaimMode.Exclusive);

    public static ResourceClaim Shared(ResourceId resource) => new(resource, ClaimMode.Shared);

    /// <summary>Whether holding this together with <paramref name="other"/> is not allowed: same resource, and at least one of them exclusive.</summary>
    public bool ConflictsWith(ResourceClaim other) =>
        Resource == other.Resource && (Mode == ClaimMode.Exclusive || other.Mode == ClaimMode.Exclusive);

    public override string ToString() => Mode == ClaimMode.Shared ? $"{Resource} (shared)" : Resource.ToString();

    /// <summary>The claims as a list in which a resource appears once; an exclusive claim wins over a shared one of the same resource.</summary>
    public static IReadOnlyList<ResourceClaim> Normalize(IEnumerable<ResourceClaim> claims) =>
    [
        .. claims
            .GroupBy(c => c.Resource)
            .Select(g => g.Any(c => c.Mode == ClaimMode.Exclusive) ? Exclusive(g.Key) : Shared(g.Key))
            .OrderBy(c => c.Resource.Value, StringComparer.Ordinal),
    ];

    /// <summary>Exclusive claims for each of the resources.</summary>
    public static IReadOnlyList<ResourceClaim> AllExclusive(IEnumerable<ResourceId> resources) => [.. resources.Distinct().Select(Exclusive)];
}

using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Core.Resources;

/// <summary>What one execution track holds over its whole run, as claims: its devices alone, and everything it relies on shared. Named for display.</summary>
public sealed record ClaimProfile(string Name, IReadOnlyList<ResourceClaim> Claims);

/// <summary>
/// Who conflicts with whom, worked out from claims alone: two tracks or an operation conflict when a claim of one conflicts with a claim of the other (an exclusive claim with any claim of the same
/// resource, a shared one only with an exclusive one). Nothing here names a rig or counts cameras: a mount is shared when two tracks claim the stability of the same mount device, a guider when they
/// claim the same guider, and so on. The sequence builder asks it which tracks a dither, a flip or a focus run affects, and the user interface asks it which resources are shared.
/// </summary>
public static class ResourceClaimMatrix
{
    public static bool Conflict(IEnumerable<ResourceClaim> a, IReadOnlyCollection<ResourceClaim> b) => a.Any(x => b.Any(x.ConflictsWith));

    /// <summary>The indexes of the profiles that an operation with these claims has to wait for or hold at a safe point: the ones whose claims conflict with its own.</summary>
    public static IReadOnlyList<int> Affected(IReadOnlyList<ClaimProfile> profiles, IReadOnlyCollection<ResourceClaim> operation) =>
        [.. Enumerable.Range(0, profiles.Count).Where(i => Conflict(profiles[i].Claims, operation))];

    /// <summary>
    /// The sets of profiles that are tied together by a resource they both claim (in any mode), for the resources <paramref name="isLink"/> accepts: the profiles on one mount are one group, the
    /// ones on another mount another; a profile that is tied to nobody is a group of its own. Each group is in the order of the profiles; the groups too.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Groups(IReadOnlyList<ClaimProfile> profiles, Func<ResourceId, bool> isLink)
    {
        var parent = Enumerable.Range(0, profiles.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        var owner = new Dictionary<ResourceId, int>();
        for (var i = 0; i < profiles.Count; i++)
        {
            foreach (var claim in profiles[i].Claims.Where(c => isLink(c.Resource)))
            {
                if (owner.TryGetValue(claim.Resource, out var first))
                {
                    parent[Find(i)] = Find(first);
                }
                else
                {
                    owner[claim.Resource] = i;
                }
            }
        }

        return [.. Enumerable.Range(0, profiles.Count).GroupBy(Find).OrderBy(g => g.Min()).Select(g => (IReadOnlyList<int>)[.. g.OrderBy(i => i)])];
    }
}

/// <summary>What a track holds over its run, and what the operations that matter ask for: the single place where "mount" and "guider" become claims.</summary>
public static class OperationClaims
{
    /// <summary>
    /// What an imaging setup holds while it images: its camera, focuser, filter wheel and rotator alone; its mount and the stability of the mount, and its guider, shared (it relies on them and does
    /// not mind others doing the same).
    /// </summary>
    public static IReadOnlyList<ResourceClaim> ImagingSetup(Rig rig, DeviceId? mount, DeviceId? guider)
    {
        var claims = new List<ResourceClaim> { ResourceClaim.Exclusive(ResourceId.ForDevice(rig.CameraId)) };
        foreach (var device in new[] { rig.FocuserId, rig.FilterWheelId, rig.RotatorId })
        {
            if (device is { } id)
            {
                claims.Add(ResourceClaim.Exclusive(ResourceId.ForDevice(id)));
            }
        }

        if (mount is { } m)
        {
            claims.Add(ResourceClaim.Shared(ResourceId.ForDevice(m)));
            claims.Add(ResourceClaim.Shared(ResourceId.ForMountStability(m)));
        }

        if (guider is { } g)
        {
            claims.Add(ResourceClaim.Shared(ResourceId.ForDevice(g)));
        }

        return claims;
    }

    /// <summary>A dither: the guider and the stability of the mount, alone.</summary>
    public static IReadOnlyList<ResourceClaim> Dither(DeviceId mount, DeviceId? guider) =>
        guider is { } g
            ? [ResourceClaim.Exclusive(ResourceId.ForDevice(g)), ResourceClaim.Exclusive(ResourceId.ForMountStability(mount))]
            : [ResourceClaim.Exclusive(ResourceId.ForMountStability(mount))];

    /// <summary>A meridian flip or a slew: the mount, its stability and its guider, alone.</summary>
    public static IReadOnlyList<ResourceClaim> MountMove(DeviceId mount, DeviceId? guider) =>
    [
        ResourceClaim.Exclusive(ResourceId.ForDevice(mount)),
        ResourceClaim.Exclusive(ResourceId.ForMountStability(mount)),
        .. guider is { } g ? [ResourceClaim.Exclusive(ResourceId.ForDevice(g))] : Array.Empty<ResourceClaim>(),
    ];

    /// <summary>An autofocus: the camera and the focuser of its setup, and, where focusing must not overlap other exposures on the mount, the stability of the mount.</summary>
    public static IReadOnlyList<ResourceClaim> Autofocus(Rig rig, DeviceId? stableMount) =>
    [
        ResourceClaim.Exclusive(ResourceId.ForDevice(rig.CameraId)),
        .. rig.FocuserId is { } f ? [ResourceClaim.Exclusive(ResourceId.ForDevice(f))] : Array.Empty<ResourceClaim>(),
        .. stableMount is { } m ? [ResourceClaim.Exclusive(ResourceId.ForMountStability(m))] : Array.Empty<ResourceClaim>(),
    ];
}

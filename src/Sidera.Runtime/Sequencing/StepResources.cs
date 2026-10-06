using Sidera.Core.Resources;
using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Sequencing;

/// <summary>What a step holds while it runs, whoever takes it: the runner (<see cref="IResourceAwareSequenceStep"/>) or the operation the step calls (<see cref="IServiceLeasedStep"/>).</summary>
public static class StepResources
{
    public static IReadOnlyCollection<ResourceId> Of(ISequenceStep step) => step switch
    {
        IClaimingSequenceStep claiming => [.. claiming.Claims.Select(c => c.Resource)],
        IResourceAwareSequenceStep aware => aware.RequiredResources,
        IServiceLeasedStep leased => leased.ServiceResources,
        _ => [],
    };

    /// <summary>What the runner takes before the step runs, and how: the declared claims, or the required resources exclusively. Steps that lease through a service take nothing here.</summary>
    public static IReadOnlyCollection<ResourceClaim> ClaimsOf(ISequenceStep step) => step switch
    {
        IClaimingSequenceStep claiming => claiming.Claims,
        IResourceAwareSequenceStep aware => ResourceClaim.AllExclusive(aware.RequiredResources),
        _ => [],
    };
}

using Sidera.Core.Resources;
using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Sequencing;

/// <summary>What a step holds while it runs, whoever takes it: the runner (<see cref="IResourceAwareSequenceStep"/>) or the operation the step calls (<see cref="IServiceLeasedStep"/>).</summary>
public static class StepResources
{
    public static IReadOnlyCollection<ResourceId> Of(ISequenceStep step) => step switch
    {
        IResourceAwareSequenceStep aware => aware.RequiredResources,
        IServiceLeasedStep leased => leased.ServiceResources,
        _ => [],
    };
}

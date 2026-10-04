using Sidera.Core.Resources;

namespace Sidera.Core.Sequencing;

/// <summary>
/// A step that needs exclusive use of some resources while it executes. Only implemented by steps that do
/// real work on equipment; steps without requirements, and container steps, leave it out. A container
/// never holds the resources of its children: each child acquires its own when it actually runs.
/// </summary>
public interface IResourceAwareSequenceStep : ISequenceStep
{
    IReadOnlyCollection<ResourceId> RequiredResources { get; }
}

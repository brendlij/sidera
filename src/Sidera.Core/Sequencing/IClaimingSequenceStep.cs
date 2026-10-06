using Sidera.Core.Resources;

namespace Sidera.Core.Sequencing;

/// <summary>
/// A step that declares what it needs of its resources and how (shared or exclusive), instead of only which ones it holds alone. The runner takes exactly these claims for as long as the step runs.
/// A step that is only <see cref="IResourceAwareSequenceStep"/> holds its resources exclusively, as it always did.
/// </summary>
public interface IClaimingSequenceStep : ISequenceStep
{
    IReadOnlyCollection<ResourceClaim> Claims { get; }
}

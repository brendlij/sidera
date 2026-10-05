using Sidera.Core.Resources;

namespace Sidera.Core.Sequencing;

/// <summary>
/// A step whose equipment is taken by the operation it calls, not by the sequence runner: a centering, a rotation or a sync takes everything it needs once, for its whole duration, so a lease that the
/// runner took first would be waited for by the step itself. It says what the operation holds, so that what a step needs is known (and shown, and tested) without running it. Steps that are
/// <see cref="IResourceAwareSequenceStep"/> are leased by the runner and do not implement this.
/// </summary>
public interface IServiceLeasedStep : ISequenceStep
{
    /// <summary>The resources the operation holds while the step runs.</summary>
    IReadOnlyCollection<ResourceId> ServiceResources { get; }
}

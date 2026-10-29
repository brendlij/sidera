using Sidera.Core.Sequencing;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// The failure handler of a run with meridian flips: a failure near the meridian, before the flip, is not the end of the session. The flip group of the mount stops guiding and tracking, holds every setup
/// of the mount, and waits for the user to retry or abort (see <see cref="MeridianFlipGroup.HoldAfterErrorAsync"/>). Far from the meridian the failure goes up as it always did.
/// </summary>
public sealed class MeridianFailureHandler(IReadOnlyList<MeridianFlipGroup> groups) : IStepFailureHandler
{
    public async Task<bool> HandleAsync(ISequenceStep step, Exception exception, CancellationToken cancellationToken)
    {
        foreach (var group in groups)
        {
            if (await group.HoldAfterErrorAsync(exception, cancellationToken) is { } retry)
            {
                return retry;
            }
        }

        return false;
    }
}

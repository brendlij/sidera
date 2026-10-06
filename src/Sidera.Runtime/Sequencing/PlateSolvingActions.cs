using System.Globalization;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Astrometry;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Slews to a target and centers it with the plate solver: the existing centering operation of <see cref="PlateSolveService"/> (slew, solve, measure the
/// spherical error, correct the slew, solve again), run as a sequence step. It never synchronizes the mount, asks nothing and retries no more than
/// <c>maxAttempts</c> times; a target that is not reached fails the step, and cancelling the sequence cancels the operation. The service takes the leases of
/// the mount and the camera itself and gives them back, so the step declares none (it would wait for its own).
/// </summary>
public sealed class SlewAndCenterAction(
    PlateSolveService service, Rig rig, DeviceId mountId, CelestialCoordinates target, double toleranceArcseconds, int maxAttempts,
    TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null) : ISequenceStep, IServiceLeasedStep
{
    public IReadOnlyCollection<Sidera.Core.Resources.ResourceId> ServiceResources =>
        [Sidera.Core.Resources.ResourceId.ForDevice(mountId), Sidera.Core.Resources.ResourceId.ForMountStability(mountId), Sidera.Core.Resources.ResourceId.ForDevice(rig.CameraId)];

    public CelestialCoordinates Target => target;

    public DeviceId MountId => mountId;

    public Rig Rig => rig;

    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"Slew & Center RA {target.RightAscensionHours:0.###}h Dec {target.DeclinationDegrees:+0.###;-0.###;0}°");

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var result = await service.CenterTargetAsync(target, rig, mountId, toleranceArcseconds, maxAttempts, exposure, defaults, intent, null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message ?? "The target could not be centered.");
        }

        return new SequenceStepResult(result);
    }
}

/// <summary>
/// An explicit, advanced step: synchronizes the mount to the position of the most recent plate solve of this run of Sidera. It does not solve
/// (put a Plate Solve before it), and it is never added by another step. It fails when there is no successful solve, when the latest solve failed, when the
/// mount cannot sync, or when the sync fails.
/// </summary>
public sealed class SyncMountToSolvedPositionAction(PlateSolveService service, DeviceId mountId) : ISequenceStep, IServiceLeasedStep
{
    public IReadOnlyCollection<Sidera.Core.Resources.ResourceId> ServiceResources => [Sidera.Core.Resources.ResourceId.ForDevice(mountId)];

    public DeviceId MountId => mountId;

    public string Name => "Sync Mount to Solved Position";

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        await service.SyncMountToSolvedPositionAsync(mountId, cancellationToken);
        return new SequenceStepResult();
    }
}

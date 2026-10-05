using System.Globalization;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Astrometry;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// Turns the camera so that the sky has the given rotation in the image, by the calibration of the rig, and does not solve. A rig without a rotator or without a calibration fails the
/// step with a sentence that says so. The service takes the rotator and the cameras on it itself and gives them back, so the step declares no resources (it would wait for its own).
/// </summary>
public sealed class RotateToAngleAction(RotationService service, Rig rig, double skyRotationDegrees) : ISequenceStep
{
    public Rig Rig => rig;

    public double SkyRotationDegrees => skyRotationDegrees;

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Rotate to {skyRotationDegrees:0.##}°");

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var result = await service.RotateToAngleAsync(rig, skyRotationDegrees, null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message ?? "The rotator could not be moved.");
        }

        return new SequenceStepResult(result);
    }
}

/// <summary>
/// Rotates to a sky angle and checks it with a plate solve, correcting until it is within the tolerance or the attempts are used up; the solve is the authority. Never
/// synchronizes the mount.
/// </summary>
public sealed class RotateAndVerifyAction(
    RotationService service, Rig rig, DeviceId? mountId, double skyRotationDegrees, double toleranceDegrees, int maxAttempts,
    TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null) : ISequenceStep
{
    public Rig Rig => rig;

    public double SkyRotationDegrees => skyRotationDegrees;

    public string Name => string.Create(CultureInfo.InvariantCulture, $"Rotate & Verify {skyRotationDegrees:0.##}° (±{toleranceDegrees:0.##}°)");

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var result = await service.RotateAndVerifyAsync(rig, mountId, skyRotationDegrees, toleranceDegrees, maxAttempts, exposure, defaults, intent, null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message ?? "The rotation could not be verified.");
        }

        return new SequenceStepResult(result);
    }
}

/// <summary>
/// Centers the target and rotates the sky to the angle, verified by plate solves, and centers again when the turn moved the field; it succeeds only when the position and
/// the rotation are both within their tolerances. Never synchronizes the mount.
/// </summary>
public sealed class CenterAndRotateAction(
    RotationService service, Rig rig, DeviceId mountId, CelestialCoordinates target, double skyRotationDegrees, double toleranceArcseconds,
    double rotationToleranceDegrees, int maxCenteringAttempts, int maxRotationAttempts, int maxRounds, TimeSpan exposure, PlateSolveDefaults defaults,
    AcquisitionIntent? intent = null) : ISequenceStep
{
    public Rig Rig => rig;

    public DeviceId MountId => mountId;

    public CelestialCoordinates Target => target;

    public double SkyRotationDegrees => skyRotationDegrees;

    public string Name => string.Create(
        CultureInfo.InvariantCulture,
        $"Center & Rotate RA {target.RightAscensionHours:0.###}h Dec {target.DeclinationDegrees:+0.###;-0.###;0}° at {skyRotationDegrees:0.##}°");

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var result = await service.CenterAndRotateAsync(
            target, skyRotationDegrees, rig, mountId, toleranceArcseconds, rotationToleranceDegrees, maxCenteringAttempts, maxRotationAttempts, maxRounds,
            exposure, defaults, intent, null, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message ?? "The target could not be centered and rotated.");
        }

        return new SequenceStepResult(result);
    }
}

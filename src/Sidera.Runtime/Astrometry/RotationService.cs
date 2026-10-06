using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Rigs;

namespace Sidera.Runtime.Astrometry;

/// <summary>Where a rotation operation is: a stage ("Move", "Solve", "Correction", "Centering", ...), the attempt, and the error that was last measured.</summary>
public sealed record RotationProgress(string Stage, int Attempt, double? RotationErrorDegrees = null, double? PointingErrorArcseconds = null);

/// <summary>
/// What a rotation came to. <see cref="SolvedRotationDegrees"/> and <see cref="ErrorDegrees"/> (target minus solved, the shortest way round, signed) are <c>null</c> when nothing
/// was solved (a plain move). <see cref="Center"/> is where the last solve said the camera points.
/// </summary>
public sealed record RotationResult(
    bool Success, int Attempts, double? PositionDegrees, double? SolvedRotationDegrees, double? ErrorDegrees, string? Message, CelestialCoordinates? Center = null);

/// <summary>What centering and rotating came to: both tolerances held at the end, or the first thing that did not work.</summary>
public sealed record CenterAndRotateResult(
    bool Success, int Rounds, CenteringResult? Centering, RotationResult? Rotation, double? PointingErrorArcseconds, double? RotationErrorDegrees, string? Message);

/// <summary>A calibration measured from one solve; the caller decides whether to keep it. Nothing is stored by the service.</summary>
public sealed record RotatorCalibrationResult(bool Success, RotatorSkyModel? Model, double? PositionDegrees, double? SolvedRotationDegrees, string? Message);

/// <summary>
/// Rotating the camera to a sky angle: the mechanical position of a rotator and the rotation of the sky in the image are different angles, related by the
/// calibration of the rig (<see cref="RotatorSkyModel"/>). A move to a sky angle needs that calibration (otherwise it fails and says so; it never guesses and never solves
/// by itself); verifying a rotation measures it with a plate solve, which is the authority, and corrects it until it is within the tolerance. None of the operations
/// here synchronizes the mount.
/// <para>
/// An operation takes the rotator and every camera that sits on it (and the mount, when it centers) once, together, and gives them back when it ends,
/// whatever way: a rotator never moves during an exposure of its camera, two rotations never overlap, and the operations of this service never wait for a lease that they hold themselves
/// (the solve and centering primitives they call are the ones that expect the caller to hold the leases).
/// </para>
/// </summary>
public sealed class RotationService
{
    /// <summary>The tolerance of a rotation when none is chosen: half a degree.</summary>
    public const double DefaultToleranceDegrees = 0.5;

    /// <summary>How often a rotation is corrected when nothing else is chosen.</summary>
    public const int DefaultMaxAttempts = 4;

    /// <summary>How many times centering and rotating may go round when nothing else is chosen.</summary>
    public const int DefaultMaxRounds = 3;

    private readonly PlateSolveService _solver;
    private readonly DeviceRegistry _devices;
    private readonly RigRegistry _rigs;
    private readonly ResourceManager _resources;
    private readonly ILogger _logger;
    // The rotators (and mounts, for centering and rotating) that running operations use, with how many. It only says what is going on and never refuses an operation: the resource leases decide
    // whether two overlap (two rigs with their own rotator, mount and camera run side by side, two on the same one queue).
    private readonly Dictionary<string, int> _claims = [];
    private readonly object _claimGate = new();

    public RotationService(PlateSolveService solver, DeviceRegistry devices, RigRegistry rigs, ResourceManager resources, ILogger<RotationService>? logger = null)
    {
        _solver = solver;
        _devices = devices;
        _rigs = rigs;
        _resources = resources;
        _logger = logger ?? NullLogger<RotationService>.Instance;
    }

    public bool IsBusy { get { lock (_claimGate) { return _claims.Count > 0; } } }

    public string? LastFailure { get; private set; }

    public event EventHandler? Changed;

    /// <summary>
    /// Moves the rotator so that the sky has <paramref name="targetSkyRotationDegrees"/> in the image, by the calibration of the rig. It does not solve: there is nothing
    /// to say about where it ended up but the position it reached. Use <see cref="RotateAndVerifyAsync"/> for that.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rig has no rotator or no calibration, the rotator is not connected, or an operation is running.</exception>
    public Task<RotationResult> RotateToAngleAsync(Rig rig, double targetSkyRotationDegrees, IProgress<RotationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireFinite(targetSkyRotationDegrees, nameof(targetSkyRotationDegrees));
        var (rotator, model) = Prepare(rig, requireModel: true);
        return RunAsync(async () =>
        {
            using var lease = await _resources.AcquireAsync(RotatorResources(rig, rotator.Id), cancellationToken);
            var position = model!.PositionFor(SkyMath.NormalizeRotationDegrees(targetSkyRotationDegrees));
            progress?.Report(new("Move", 1));
            try
            {
                await rotator.MoveToAsync(position, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failure(1, rotator.Position, null, null, $"Rotator move failed: {ex.Message}");
            }

            _logger.LogInformation(new EventId(5400, "RotateToAngle"), "RotateToAngle sky {Target} at position {Position}", targetSkyRotationDegrees, rotator.Position);
            return new RotationResult(true, 1, rotator.Position, null, null, null);
        }, RotatorClaim(rotator.Id));
    }

    /// <summary>
    /// Moves to the sky angle, solves, compares by the shortest signed angle and corrects, until the error is within <paramref name="toleranceDegrees"/> or
    /// <paramref name="maxAttempts"/> moves were made. The solve decides, not the calibration: a rotation that the calibration says is done but the sky does not
    /// show is corrected. A correction that makes the error larger fails the operation (the direction of the rotator is then probably the other one), and it does not go on turning.
    /// </summary>
    public Task<RotationResult> RotateAndVerifyAsync(Rig rig, DeviceId? mountId, double targetSkyRotationDegrees, double toleranceDegrees, int maxAttempts,
        TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null, IProgress<RotationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireFinite(targetSkyRotationDegrees, nameof(targetSkyRotationDegrees));
        ValidateTolerance(toleranceDegrees, maxAttempts);
        var (rotator, model) = Prepare(rig, requireModel: true);
        return RunAsync(async () =>
        {
            using var lease = await _resources.AcquireAsync(RotatorResources(rig, rotator.Id), cancellationToken);
            return await VerifyCoreAsync(rig, rotator, model!, mountId, SkyMath.NormalizeRotationDegrees(targetSkyRotationDegrees), toleranceDegrees, maxAttempts, exposure, defaults, intent, progress, cancellationToken);
        }, RotatorClaim(rotator.Id));
    }

    /// <summary>
    /// Centers the target, rotates the sky to the angle and verifies it, and centers again when turning moved the field; it ends when both the pointing error and the rotation error are
    /// within their tolerances, or after <paramref name="maxRounds"/> rounds. The mount, the camera and the rotator are held for the whole operation.
    /// </summary>
    public Task<CenterAndRotateResult> CenterAndRotateAsync(CelestialCoordinates target, double targetSkyRotationDegrees, Rig rig, DeviceId mountId,
        double toleranceArcseconds, double rotationToleranceDegrees, int maxCenteringAttempts, int maxRotationAttempts, int maxRounds,
        TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null, IProgress<RotationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireFinite(targetSkyRotationDegrees, nameof(targetSkyRotationDegrees));
        ValidateTolerance(rotationToleranceDegrees, maxRotationAttempts);
        if (!double.IsFinite(toleranceArcseconds) || toleranceArcseconds <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceArcseconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCenteringAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRounds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRounds, 20);
        var (rotator, model) = Prepare(rig, requireModel: true);
        var skyTarget = SkyMath.NormalizeRotationDegrees(targetSkyRotationDegrees);
        return RunAsync(async () =>
        {
            using var lease = await _resources.AcquireClaimsAsync(
                ResourceClaim.AllExclusive([ResourceId.ForDevice(mountId), ResourceId.ForMountStability(mountId), .. RotatorResources(rig, rotator.Id)]), cancellationToken);
            CenteringResult? centering = null;
            RotationResult? rotation = null;
            double? pointing = null;
            for (var round = 1; round <= maxRounds; round++)
            {
                var centeringProgress = progress is null ? null : new Progress<CenteringProgress>(p => progress.Report(new("Centering: " + p.Stage, p.Attempt, null, p.PointingErrorArcseconds)));
                progress?.Report(new(round == 1 ? "Centering" : "Recentering", round));
                centering = await _solver.CenterTargetInLeaseAsync(target, rig, mountId, toleranceArcseconds, maxCenteringAttempts, exposure, defaults, intent, centeringProgress, cancellationToken);
                if (!centering.Success)
                {
                    return CenterAndRotateFailure(round, centering, rotation, pointing, rotation?.ErrorDegrees, "Centering failed: " + centering.Message);
                }

                progress?.Report(new("Rotating", round));
                rotation = await VerifyCoreAsync(
                    rig, rotator, model!, mountId, skyTarget, rotationToleranceDegrees, maxRotationAttempts, exposure, defaults, intent, progress, cancellationToken, measureFirst: round > 1);
                if (!rotation.Success)
                {
                    return CenterAndRotateFailure(round, centering, rotation, centering.PointingErrorArcseconds, rotation.ErrorDegrees, "Rotation failed: " + rotation.Message);
                }

                // The solve that confirmed the rotation also says where the camera points now: turning the camera may have moved the field.
                pointing = rotation.Center is { } solved ? SkyMath.DegreesToArcseconds(SkyMath.AngularSeparationDegrees(target, solved)) : null;
                progress?.Report(new("Checked", round, rotation.ErrorDegrees, pointing));
                if (pointing is { } error && error <= toleranceArcseconds)
                {
                    _logger.LogInformation(new EventId(5420, "CenterAndRotateCompleted"), "CenterAndRotateCompleted after {Rounds} rounds, pointing {Pointing} arcsec, rotation {Rotation}", round, error, rotation.ErrorDegrees);
                    return new CenterAndRotateResult(true, round, centering, rotation, error, rotation.ErrorDegrees, null);
                }

                if (pointing is null)
                {
                    return CenterAndRotateFailure(round, centering, rotation, null, rotation.ErrorDegrees, "The last solve did not say where the camera points.");
                }
            }

            return CenterAndRotateFailure(maxRounds, centering, rotation, pointing, rotation?.ErrorDegrees,
                "Turning the camera kept moving the field out of the centering tolerance; the maximum number of rounds was reached.");
        }, RotatorClaim(rotator.Id), "mount:" + mountId.Value);
    }

    /// <summary>
    /// Measures the sky rotation at the position the rotator is at now and returns the calibration that fits: the offset is what is measured, the direction stays the one the rig has
    /// (a rotator that is not reversed unless it was set), and the time is now. It moves nothing and keeps nothing: the caller stores the model, and a direction that is wrong shows
    /// at the first verified rotation.
    /// </summary>
    public Task<RotatorCalibrationResult> CalibrateAsync(Rig rig, DeviceId? mountId, TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent = null,
        CancellationToken cancellationToken = default)
    {
        var (rotator, existing) = Prepare(rig, requireModel: false);
        return RunAsync(async () =>
        {
            using var lease = await _resources.AcquireAsync(RotatorResources(rig, rotator.Id), cancellationToken);
            var position = rotator.Position;
            var result = await _solver.CaptureAndSolveInLeaseAsync(rig, mountId, exposure, defaults, intent, null, cancellationToken);
            if (!result.Success || result.RotationDegrees is not { } solved)
            {
                return new RotatorCalibrationResult(false, null, position, null, result.Success ? "The solve did not say the rotation of the image." : result.Message ?? "Plate solving failed.");
            }

            var model = (existing ?? new RotatorSkyModel(0)).Calibrated(position, solved, DateTimeOffset.UtcNow);
            _logger.LogInformation(new EventId(5430, "RotatorCalibrated"), "RotatorCalibrated at position {Position}: sky {Solved}, offset {Offset}, reversed {Reversed}", position, solved, model.OffsetDegrees, model.Reversed);
            return new RotatorCalibrationResult(true, model, position, solved, null);
        }, RotatorClaim(rotator.Id));
    }

    private async Task<RotationResult> VerifyCoreAsync(Rig rig, IRotator rotator, RotatorSkyModel model, DeviceId? mountId, double target, double tolerance, int maxAttempts,
        TimeSpan exposure, PlateSolveDefaults defaults, AcquisitionIntent? intent, IProgress<RotationProgress>? progress, CancellationToken token,
        bool measureFirst = false)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["RigId"] = rig.Id.Value, ["RotatorId"] = rotator.Id.Value, ["TargetRotation"] = target });
        _logger.LogInformation(new EventId(5410, "RotateAndVerifyStarted"), "RotateAndVerifyStarted target {Target} tolerance {Tolerance}", target, tolerance);
        var commanded = model.PositionFor(target);
        double? previous = null;
        double? error = null;
        double? solved = null;
        CelestialCoordinates? center = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            // After a centering that follows a rotation that was verified, the rotator stays where it is: the first thing to do is to look at what the rotation is now.
            if (!(measureFirst && attempt == 1))
            {
                progress?.Report(new(attempt == 1 ? "Move" : "Correction", attempt, error));
                try
                {
                    await rotator.MoveToAsync(commanded, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return Failure(attempt, rotator.Position, solved, error, $"Rotator move failed: {ex.Message}", center);
                }
            }

            progress?.Report(new("Solve", attempt, error));
            var result = await _solver.CaptureAndSolveInLeaseAsync(rig, mountId, exposure, defaults, intent, null, token);
            if (!result.Success)
            {
                return Failure(attempt, rotator.Position, solved, error, result.Message ?? "Plate solving failed.", center);
            }

            if (result.RotationDegrees is not { } measured)
            {
                return Failure(attempt, rotator.Position, solved, error, "The solve did not say the rotation of the image.", center);
            }

            solved = measured;
            center = result.Center;
            error = SkyMath.RotationDifferenceDegrees(measured, target);
            progress?.Report(new("Rotation error", attempt, error));
            _logger.LogInformation(new EventId(5411, "RotationMeasured"), "RotationMeasured attempt {Attempt}: position {Position}, solved {Solved}, error {Error}", attempt, rotator.Position, measured, error);
            if (Math.Abs(error.Value) <= tolerance)
            {
                return new RotationResult(true, attempt, rotator.Position, solved, error, null, center);
            }

            if (previous is { } before && Math.Abs(error.Value) > before + tolerance / 2)
            {
                return Failure(attempt, rotator.Position, solved, error,
                    $"The rotation error grew from {before:0.##}° to {Math.Abs(error.Value):0.##}° after a correction. The direction of the rotator is probably the other one: check the calibration (reversed) before trying again.", center);
            }

            if (attempt == maxAttempts)
            {
                break;
            }

            previous = Math.Abs(error.Value);
            // The measured error is the angle still to turn the sky by; by the model one degree of position is one degree of sky, the other way when reversed.
            commanded = RotatorSkyModel.Normalize360(rotator.Position + (model.Reversed ? -error.Value : error.Value));
        }

        return Failure(maxAttempts, rotator.Position, solved, error,
            $"The rotation is {Math.Abs(error ?? 0):0.##}° off after {maxAttempts} attempts, more than the tolerance of {tolerance:0.##}°.", center);
    }

    private RotationResult Failure(int attempts, double? position, double? solved, double? error, string message, CelestialCoordinates? center = null)
    {
        LastFailure = message;
        _logger.LogWarning(new EventId(5412, "RotationFailed"), "RotationFailed after {Attempts} attempts: {Message}", attempts, message);
        return new RotationResult(false, attempts, position, solved, error, message, center);
    }

    private CenterAndRotateResult CenterAndRotateFailure(int rounds, CenteringResult? centering, RotationResult? rotation, double? pointing, double? rotationError, string message)
    {
        LastFailure = message;
        _logger.LogWarning(new EventId(5421, "CenterAndRotateFailed"), "CenterAndRotateFailed in round {Round}: {Message}", rounds, message);
        return new CenterAndRotateResult(false, rounds, centering, rotation, pointing, rotationError, message);
    }

    // The rig's rotator and, when it is needed, its calibration; a rig without them is a precondition that is named, not an operation that fails halfway.
    private (IRotator Rotator, RotatorSkyModel? Model) Prepare(Rig rig, bool requireModel)
    {
        if (rig.RotatorId is not { } rotatorId)
        {
            throw new InvalidOperationException($"The rig '{rig.Name}' has no rotator.");
        }

        if (!_devices.TryGet(rotatorId, out var device) || device is not IRotator rotator)
        {
            throw new InvalidOperationException($"The rotator '{rotatorId}' of the rig '{rig.Name}' is not available.");
        }

        if (rotator.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"The rotator '{rotator.Name}' is not connected.");
        }

        if (requireModel && rig.RotatorModel is null)
        {
            throw new InvalidOperationException($"The rotator of the rig '{rig.Name}' is not calibrated: Sidera does not know how its position relates to the sky in the image. Calibrate it with a plate solve first.");
        }

        return (rotator, rig.RotatorModel);
    }

    private static string RotatorClaim(DeviceId id) => "rotator:" + id.Value;

    /// <summary>What a rotation of this rig holds while it runs: the rotator and every camera that sits on it (none of them may expose while it turns).</summary>
    public IReadOnlyCollection<ResourceId> ResourcesOfRotation(Rig rig) =>
        rig.RotatorId is { } rotatorId ? RotatorResources(rig, rotatorId) : [];

    /// <summary>What centering and rotating of this rig holds while it runs: the mount, the rotator and the cameras on it.</summary>
    public IReadOnlyCollection<ResourceId> ResourcesOfCenterAndRotate(Rig rig, DeviceId mountId) =>
        [ResourceId.ForDevice(mountId), ResourceId.ForMountStability(mountId), .. ResourcesOfRotation(rig)];

    // The rotator and every camera that sits on it: none of them may expose while it turns.
    private ResourceId[] RotatorResources(Rig rig, DeviceId rotatorId) =>
    [
        ResourceId.ForDevice(rotatorId),
        .. _rigs.GetAll().Where(r => r.RotatorId == rotatorId).Select(r => r.CameraId).Append(rig.CameraId).Distinct().Select(ResourceId.ForDevice),
    ];

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, "The angle must be a finite number of degrees.");
        }
    }

    private static void ValidateTolerance(double toleranceDegrees, int maxAttempts)
    {
        if (!double.IsFinite(toleranceDegrees) || toleranceDegrees <= 0 || toleranceDegrees > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(toleranceDegrees), "The tolerance must be greater than 0 and at most 90 degrees.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 20);
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> body, params string[] claims)
    {
        lock (_claimGate)
        {
            foreach (var claim in claims)
            {
                _claims[claim] = _claims.GetValueOrDefault(claim) + 1;
            }
        }

        LastFailure = null;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            return await body();
        }
        catch (OperationCanceledException)
        {
            LastFailure = "Operation cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            LastFailure = ex.Message;
            throw;
        }
        finally
        {
            lock (_claimGate)
            {
                foreach (var claim in claims)
                {
                    if (--_claims[claim] == 0)
                    {
                        _claims.Remove(claim);
                    }
                }
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

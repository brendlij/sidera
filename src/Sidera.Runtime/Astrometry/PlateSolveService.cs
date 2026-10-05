using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Resources;

namespace Sidera.Runtime.Astrometry;

public sealed record CenteringProgress(string Stage, int Attempt, double? PointingErrorArcseconds = null);
public sealed record CenteringResult(bool Success, int Attempts, double? PointingErrorArcseconds, string? Message);

/// <summary>Backend-neutral solve and centering operations. Direct capture/centering owns device leases for the whole operation.</summary>
public sealed class PlateSolveService
{
    private readonly DeviceRegistry _devices;
    private readonly ResourceManager _resources;
    private readonly IAcquisitionDefaultsSource _acquisition;
    private readonly ILogger _logger;
    // What the running operations use, by device (a camera exposing for a solve, a mount slewing for centering), with how many. It only says what is going on: it never refuses an operation.
    // Whether two operations may overlap is decided by the resource leases they take: two rigs on different cameras and mounts run side by side, two on the same one queue.
    private readonly Dictionary<string, int> _claims = [];
    private readonly object _claimGate = new();
    public PlateSolveService(IPlateSolver solver, DeviceRegistry devices, ResourceManager resources,
        IAcquisitionDefaultsSource acquisition, ILogger<PlateSolveService>? logger = null)
    {
        Solver = solver;
        _devices = devices;
        _resources = resources;
        _acquisition = acquisition;
        _logger = logger ?? NullLogger<PlateSolveService>.Instance;
    }

    public IPlateSolver Solver { get; }
    public bool IsSolving { get { lock (_claimGate) { return _claims.Count > 0; } } }

    /// <summary>The mount that the latest result was made for (the one of its rig); <c>null</c> when the solve had no mount.</summary>
    public DeviceId? LastResultMountId { get; private set; }

    private static string CameraClaim(DeviceId id) => "camera:" + id.Value;
    private static string MountClaim(DeviceId id) => "mount:" + id.Value;
    public PlateSolveResult? LastResult { get; private set; }
    public string? LastFailure { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    public PlateSolveRequest? LastRequest { get; private set; }
    public CameraFrame? LastFrame { get; private set; }
    public event EventHandler? Changed;

    public PlateSolveRequest Resolve(CameraFrame frame, Rig rig, DeviceId? mountId, PlateSolveDefaults defaults,
        PlateSolveOverrides? overrides = null)
    {
        var camera = Camera(rig.CameraId);
        var sensor = SensorGeometry.For(camera);
        CelestialCoordinates? center = null;
        if (mountId is { } id && _devices.TryGet(id, out var device) && device is IMount mount
            && mount.ConnectionState == DeviceConnectionState.Connected)
        {
            try { center = mount.Coordinates; }
            catch (Exception ex) { _logger.LogDebug(ex, "Mount coordinates unavailable for {MountId}", id); }
        }
        return PlateSolveHintResolver.Resolve(frame, rig, sensor, center, defaults, overrides);
    }

    public Task<PlateSolveResult> SolveAsync(CameraFrame frame, Rig rig, DeviceId? mountId,
        PlateSolveDefaults defaults, PlateSolveOverrides? overrides = null, CancellationToken cancellationToken = default) =>
        RunAsync(() => SolveCoreAsync(Resolve(frame, rig, mountId, defaults, overrides), rig, cancellationToken), mountId, CameraClaim(rig.CameraId));

    public Task<PlateSolveResult> CaptureAndSolveAsync(Rig rig, DeviceId? mountId, TimeSpan exposure,
        PlateSolveDefaults defaults, AcquisitionIntent? intent = null, PlateSolveOverrides? overrides = null,
        CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        using var lease = await _resources.AcquireAsync([ResourceId.ForDevice(rig.CameraId)], cancellationToken);
        return await CaptureAndSolveCoreAsync(rig, mountId, exposure, defaults, intent, overrides, cancellationToken);
    }, mountId, CameraClaim(rig.CameraId));

    /// <summary>For a sequence whose runner already holds the camera lease; never acquires it twice.</summary>
    public Task<PlateSolveResult> CaptureAndSolveWithLeaseAsync(Rig rig, DeviceId? mountId, TimeSpan exposure,
        PlateSolveDefaults defaults, AcquisitionIntent? intent = null, PlateSolveOverrides? overrides = null,
        CancellationToken cancellationToken = default) => RunAsync(() =>
            CaptureAndSolveCoreAsync(rig, mountId, exposure, defaults, intent, overrides, cancellationToken), mountId, CameraClaim(rig.CameraId));

    /// <summary>
    /// A capture and a solve inside an operation that already holds the camera lease and the busy state of its own (a rotation, a centering and rotating): it neither
    /// acquires the camera nor claims the service as busy, so it never waits for what the caller holds. The result is recorded as the last one.
    /// </summary>
    public async Task<PlateSolveResult> CaptureAndSolveInLeaseAsync(Rig rig, DeviceId? mountId, TimeSpan exposure,
        PlateSolveDefaults defaults, AcquisitionIntent? intent = null, PlateSolveOverrides? overrides = null,
        CancellationToken cancellationToken = default)
    {
        var result = await CaptureAndSolveCoreAsync(rig, mountId, exposure, defaults, intent, overrides, cancellationToken);
        Record(result, mountId);
        return result;
    }

    private void Record(PlateSolveResult result, DeviceId? mountId)
    {
        LastResult = result;
        LastResultMountId = mountId;
        LastFailure = result.Message;
        LastDuration = result.Duration;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<PlateSolveResult> CaptureAndSolveCoreAsync(Rig rig, DeviceId? mountId, TimeSpan exposure,
        PlateSolveDefaults defaults, AcquisitionIntent? intent, PlateSolveOverrides? overrides, CancellationToken token)
    {
        LastFrame = null;
        LastRequest = null;
        CameraFrame frame;
        var camera = Camera(rig.CameraId);
        if (camera is not SimulatedCamera && Sidera.Core.SideraEnvironment.Get("SIDERA_ASTAP_CAMERA_OK") != "1")
            return PlateSolveResult.Failed(Solver.Name, PlateSolveFailure.ImageError, "Real camera solve exposure requires SIDERA_ASTAP_CAMERA_OK=1.", TimeSpan.Zero);
        try { frame = await AcquisitionExposer.ExposeAsync(camera, exposure, intent, _acquisition, _logger, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return PlateSolveResult.Failed(Solver.Name, PlateSolveFailure.ImageError, $"Camera exposure failed: {ex.Message}", TimeSpan.Zero); }
        return await SolveCoreAsync(Resolve(frame, rig, mountId, defaults, overrides), rig, token);
    }

    private async Task<PlateSolveResult> SolveCoreAsync(PlateSolveRequest request, Rig rig, CancellationToken token)
    {
        LastRequest = request;
        LastFrame = request.Image.Frame;
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["RigId"] = rig.Id.Value, ["CameraId"] = rig.CameraId.Value, ["Backend"] = Solver.Name });
        _logger.LogInformation(new EventId(5100, "PlateSolveStarted"), "PlateSolveStarted with hint {Center}", request.ApproximateCenter);
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(request.Timeout ?? TimeSpan.FromSeconds(90));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        PlateSolveResult result;
        try { result = await Solver.SolveAsync(request, linked.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        { result = PlateSolveResult.Failed(Solver.Name, PlateSolveFailure.Timeout, "Plate solving timed out.", clock.Elapsed); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { result = PlateSolveResult.Failed(Solver.Name, PlateSolveFailure.Error, $"Plate solver failed: {ex.Message}", clock.Elapsed); }
        for (var i = 0; i < result.Attempts.Count; i++)
        {
            var attempt = result.Attempts[i];
            _logger.LogInformation(new EventId(5101, "PlateSolveAttempt"), "PlateSolveAttempt {Attempt} {Strategy} {Succeeded} {Duration}", i + 1, attempt.Strategy, attempt.Succeeded, attempt.Duration);
        }
        _logger.LogInformation(new EventId(result.Success ? 5102 : 5103, result.Success ? "PlateSolveCompleted" : "PlateSolveFailed"),
            "Plate solve outcome {Success} in {Duration}, center {Center}, scale {Scale}, failure {Message}", result.Success, clock.Elapsed, result.Center, result.PixelScaleArcsecPerPixel, result.Message);
        return result;
    }

    public Task<CenteringResult> CenterTargetAsync(CelestialCoordinates target, Rig rig, DeviceId mountId,
        double toleranceArcseconds, int maxAttempts, TimeSpan exposure, PlateSolveDefaults defaults,
        AcquisitionIntent? intent = null, IProgress<CenteringProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateCentering(toleranceArcseconds, maxAttempts);
        return RunAsync(async () =>
        {
            using var lease = await _resources.AcquireAsync([ResourceId.ForDevice(mountId), ResourceId.ForDevice(rig.CameraId)], cancellationToken);
            return await CenterTargetInLeaseAsync(target, rig, mountId, toleranceArcseconds, maxAttempts, exposure, defaults, intent, progress, cancellationToken);
        }, mountId, MountClaim(mountId), CameraClaim(rig.CameraId));
    }

    private static void ValidateCentering(double toleranceArcseconds, int maxAttempts)
    {
        if (!double.IsFinite(toleranceArcseconds) || toleranceArcseconds <= 0) throw new ArgumentOutOfRangeException(nameof(toleranceArcseconds));
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxAttempts, 100);
    }

    /// <summary>
    /// Centering inside an operation that already holds the mount and the camera and has the busy state of its own: the same slew, solve and correct loop, without
    /// acquiring anything. Never synchronizes the mount.
    /// </summary>
    public async Task<CenteringResult> CenterTargetInLeaseAsync(CelestialCoordinates target, Rig rig, DeviceId mountId,
        double toleranceArcseconds, int maxAttempts, TimeSpan exposure, PlateSolveDefaults defaults,
        AcquisitionIntent? intent = null, IProgress<CenteringProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateCentering(toleranceArcseconds, maxAttempts);
        if (!_devices.TryGet(mountId, out var device) || device is not IMount mount) throw new InvalidOperationException("The selected mount is unavailable.");
        if (mount is not SimulatedMount && Sidera.Core.SideraEnvironment.Get("SIDERA_ASTROMETRY_CENTERING_OK") != "1")
            return CenterFailure(0, null, "Real mount centering requires SIDERA_ASTROMETRY_CENTERING_OK=1.");
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["RigId"] = rig.Id.Value, ["CameraId"] = rig.CameraId.Value, ["MountId"] = mountId.Value, ["Target"] = target, ["Backend"] = Solver.Name });
        _logger.LogInformation(new EventId(5200, "CenteringStarted"), "CenteringStarted {Target}", target);
        var commanded = target;
        double? error = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Slew", attempt));
            try { await mount.SlewToAsync(commanded, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return CenterFailure(attempt, error, $"Mount slew failed: {ex.Message}"); }
            progress?.Report(new("Solve", attempt));
            var result = await CaptureAndSolveCoreAsync(rig, mountId, exposure, defaults, intent, null, cancellationToken);
            Record(result, mountId);
            if (!result.Success || result.Center is null) return CenterFailure(attempt, error, result.Message ?? "Plate solving failed.");
            error = SkyMath.DegreesToArcseconds(SkyMath.AngularSeparationDegrees(target, result.Center));
            progress?.Report(new("Pointing error", attempt, error));
            if (error <= toleranceArcseconds)
            {
                _logger.LogInformation(new EventId(5202, "CenteringCompleted"), "CenteringCompleted after {Attempt} attempts, error {ErrorArcseconds}", attempt, error);
                progress?.Report(new("Centered", attempt, error));
                return new CenteringResult(true, attempt, error, null);
            }
            if (attempt == maxAttempts) break;
            commanded = SkyMath.CorrectedTarget(commanded, result.Center, target);
            _logger.LogInformation(new EventId(5201, "CenteringCorrection"), "CenteringCorrection {Attempt} to {Correction}, solved {Center}, error {ErrorArcseconds}", attempt, commanded, result.Center, error);
            progress?.Report(new("Correction", attempt, error));
        }
        return CenterFailure(maxAttempts, error, "Maximum centering attempts reached.");
    }

    /// <summary>
    /// Tells the mount that it points at the position of the last successful solve. Only ever called by a person's explicit action: a solve, a
    /// sequence step and centering never synchronize the mount by themselves.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no successful solve, the mount is unavailable, or it cannot sync.</exception>
    public Task SyncMountToSolvedPositionAsync(DeviceId mountId, CancellationToken cancellationToken = default) => RunAsync(async () =>
    {
        if (LastResult is not { Success: true, Center: { } solved })
        {
            throw new InvalidOperationException("There is no successful plate solve to synchronize the mount to.");
        }

        if (!_devices.TryGet(mountId, out var device) || device is not IMountControl mount)
        {
            throw new InvalidOperationException("The selected mount cannot be synchronized.");
        }

        if (mount.Capabilities.Value is not { CanSync: true })
        {
            throw new InvalidOperationException($"{mount.Name} does not support sync.");
        }

        // With several rigs the latest solve may be one of a rig on another mount: that position says nothing about this mount.
        if (LastResultMountId is { } solvedFor && solvedFor != mountId)
        {
            throw new InvalidOperationException($"The latest plate solve was made for the mount '{solvedFor}', not for '{mountId}'. Solve with a rig of this mount first.");
        }

        using var lease = await _resources.AcquireAsync([ResourceId.ForDevice(mountId)], cancellationToken);
        _logger.LogInformation(new EventId(5300, "MountSyncStarted"), "MountSyncStarted {MountId} to {Solved}", mountId.Value, solved);
        try
        {
            await mount.SyncAsync(solved, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(new EventId(5302, "MountSyncFailed"), ex, "MountSyncFailed {MountId}", mountId.Value);
            throw;
        }

        _logger.LogInformation(new EventId(5301, "MountSyncCompleted"), "MountSyncCompleted {MountId}", mountId.Value);
        return true;
    }, null, MountClaim(mountId));

    private CenteringResult CenterFailure(int attempt, double? error, string message)
    {
        LastFailure = message;
        _logger.LogWarning(new EventId(5203, "CenteringFailed"), "CenteringFailed after {Attempt} attempts, error {ErrorArcseconds}: {Message}", attempt, error, message);
        return new(false, attempt, error, message);
    }

    private ICamera Camera(DeviceId id) => _devices.TryGet(id, out var device) && device is ICamera camera
        ? camera : throw new InvalidOperationException($"Camera '{id}' is unavailable.");

    private async Task<T> RunAsync<T>(Func<Task<T>> body, DeviceId? mountForResult = null, params string[] claims)
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
            var result = await body();
            if (result is PlateSolveResult solve) { LastResult = solve; LastResultMountId = mountForResult; LastFailure = solve.Message; LastDuration = solve.Duration; }
            return result;
        }
        catch (OperationCanceledException) { LastFailure = "Operation cancelled."; throw; }
        catch (Exception ex) { LastFailure = ex.Message; throw; }
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

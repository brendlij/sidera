using System.Diagnostics;
using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Guiding;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Rotators;
using Sidera.Runtime.Resources;
using Sidera.Runtime.Sequencing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Runtime.Devices;

/// <summary>
/// Direct (manual) device operations. Each call takes the device's resource from the shared
/// <see cref="ResourceManager"/> for the duration of the operation, so it coordinates with sequences and
/// other direct calls on the same device even if the UI does not stop them.
/// <para>
/// Only for the direct path. A sequence step must not call this service: the <see cref="SequenceRunner"/>
/// already holds the step's resources while it runs, and acquiring them a second time would block forever.
/// </para>
/// <para>
/// Every operation is logged with its parameters (a focuser target, a slot, coordinates, an exposure time): the request
/// at Information, the completion with its duration at Debug, a cancellation at Information (the user stopped it), a
/// failure at Error with the exception. The entries carry the device id as scope. Frames are never logged.
/// </para>
/// </summary>
public sealed class DeviceOperationService
{
    private readonly DeviceRegistry _registry;
    private readonly ResourceManager _resources;
    private readonly ILogger _logger;

    private readonly IAcquisitionDefaultsSource? _acquisitionDefaults;

    public DeviceOperationService(
        DeviceRegistry registry,
        ResourceManager resources,
        ILogger<DeviceOperationService>? logger = null,
        IAcquisitionDefaultsSource? acquisitionDefaults = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resources);

        _registry = registry;
        _resources = resources;
        _logger = logger ?? NullLogger<DeviceOperationService>.Instance;
        _acquisitionDefaults = acquisitionDefaults;
    }

    /// <exception cref="InvalidOperationException">The device is not registered.</exception>
    public async Task ConnectAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var device = DeviceLookup.Resolve<IDevice>(_registry, deviceId, "device");

        // The device reports connecting and connected itself (see DeviceLifecycleLogger); here only the request.
        _logger.LogDebug("Connect requested for {DeviceType} {DeviceId}", device.Type, deviceId);
        await Run("Connect", deviceId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(deviceId)], cancellationToken))
            {
                await device.ConnectAsync(cancellationToken);
            }
        });
    }

    /// <exception cref="InvalidOperationException">The device is not registered.</exception>
    public async Task DisconnectAsync(DeviceId deviceId, CancellationToken cancellationToken = default)
    {
        var device = DeviceLookup.Resolve<IDevice>(_registry, deviceId, "device");

        _logger.LogDebug("Disconnect requested for {DeviceType} {DeviceId}", device.Type, deviceId);
        await Run("Disconnect", deviceId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(deviceId)], cancellationToken))
            {
                await device.DisconnectAsync(cancellationToken);
            }
        });
    }

    /// <exception cref="InvalidOperationException">The device is not registered or is not a mount.</exception>
    public async Task SlewToAsync(
        DeviceId mountId,
        CelestialCoordinates target,
        CancellationToken cancellationToken = default
    )
    {
        var mount = DeviceLookup.Resolve<IMount>(_registry, mountId, "mount");

        _logger.LogInformation(
            "Slewing mount {DeviceId} to RA {RightAscensionHours:0.####} h, Dec {DeclinationDegrees:0.####} deg",
            mountId, target.RightAscensionHours, target.DeclinationDegrees);
        await Run("Slew", mountId, async () =>
        {
            using (await _resources.AcquireClaimsAsync(ResourceClaim.AllExclusive([ResourceId.ForDevice(mountId), ResourceId.ForMountStability(mountId)]), cancellationToken))
            {
                await mount.SlewToAsync(target, cancellationToken);
            }
        });
    }

    /// <summary>
    /// Moves a focuser to an absolute position. Takes the focuser resource only: a move needs no camera.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a focuser.</exception>
    public async Task MoveFocuserToAsync(DeviceId focuserId, int target, CancellationToken cancellationToken = default)
    {
        var focuser = DeviceLookup.ResolveAbsoluteFocuser(_registry, focuserId);

        _logger.LogInformation(
            "Moving focuser {DeviceId} from position {FromPosition} to {TargetPosition}", focuserId, focuser.Position, target);
        await Run("Focuser move", focuserId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(focuserId)], cancellationToken))
            {
                await focuser.MoveToAsync(target, cancellationToken);
            }
        });
    }

    /// <summary>
    /// The cameras of the rigs that a rotator belongs to: a rotator turns them, so it is never moved while one of them exposes. Set by the host, which knows the rigs.
    /// </summary>
    public Func<DeviceId, IEnumerable<DeviceId>>? CamerasOfRotator { get; set; }

    private ResourceId[] RotatorResources(DeviceId rotatorId) =>
        [ResourceId.ForDevice(rotatorId), .. (CamerasOfRotator?.Invoke(rotatorId) ?? []).Distinct().Select(ResourceId.ForDevice)];

    /// <summary>
    /// Moves a rotator to an absolute position. Takes the rotator and the cameras of its rigs, so it waits for an exposure that is running and an exposure waits for it. Explicit
    /// only: nothing in Sidera moves a rotator by itself.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a rotator.</exception>
    public async Task MoveRotatorToAsync(DeviceId rotatorId, double positionDegrees, CancellationToken cancellationToken = default)
    {
        var rotator = DeviceLookup.Resolve<IRotator>(_registry, rotatorId, "rotator");
        _logger.LogInformation("Moving rotator {DeviceId} from {FromPosition:0.##}° to {TargetPosition:0.##}°", rotatorId, rotator.Position, positionDegrees);
        await Run("Rotator move", rotatorId, async () =>
        {
            using (await _resources.AcquireAsync(RotatorResources(rotatorId), cancellationToken))
            {
                await rotator.MoveToAsync(positionDegrees, cancellationToken);
            }
        });
    }

    /// <summary>Moves a rotator by an angle (negative is the other way); see <see cref="MoveRotatorToAsync"/>.</summary>
    public async Task MoveRotatorByAsync(DeviceId rotatorId, double degrees, CancellationToken cancellationToken = default)
    {
        var rotator = DeviceLookup.Resolve<IRotatorControl>(_registry, rotatorId, "rotator");
        _logger.LogInformation("Moving rotator {DeviceId} by {Degrees:+0.##;-0.##}°", rotatorId, degrees);
        await Run("Rotator move", rotatorId, async () =>
        {
            using (await _resources.AcquireAsync(RotatorResources(rotatorId), cancellationToken))
            {
                await rotator.MoveByAsync(degrees, cancellationToken);
            }
        });
    }

    /// <summary>Asks a rotator to stop. Takes no resource: a halt must work while a move holds the rotator.</summary>
    public async Task HaltRotatorAsync(DeviceId rotatorId, CancellationToken cancellationToken = default)
    {
        var rotator = DeviceLookup.Resolve<IRotatorControl>(_registry, rotatorId, "rotator");
        _logger.LogInformation("Halting rotator {DeviceId}", rotatorId);
        await Run("Rotator halt", rotatorId, () => rotator.HaltAsync(cancellationToken));
    }

    /// <summary>
    /// Turns a filter wheel to the slot with the given index. Takes the wheel resource only: it needs no camera.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a filter wheel.</exception>
    public async Task MoveFilterWheelToAsync(DeviceId filterWheelId, int slotIndex, CancellationToken cancellationToken = default)
    {
        var wheel = DeviceLookup.Resolve<IFilterWheel>(_registry, filterWheelId, "filter wheel");

        _logger.LogInformation("Turning filter wheel {DeviceId} to slot {SlotIndex}", filterWheelId, slotIndex);
        await Run("Filter change", filterWheelId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(filterWheelId)], cancellationToken))
            {
                await wheel.MoveToSlotAsync(slotIndex, cancellationToken);
            }
        });
    }

    /// <exception cref="InvalidOperationException">The device is not registered or is not a camera.</exception>
    public Task<CameraFrame> ExposeAsync(
        DeviceId cameraId,
        TimeSpan duration,
        CancellationToken cancellationToken = default
    ) => ExposeAsync(cameraId, duration, null, cancellationToken);

    /// <summary>
    /// A manual exposure through the same pipeline as a sequence exposure: the acquisition settings are resolved against the
    /// camera (the intent, else the camera defaults), checked, applied and the exposure started as one operation, all under the
    /// camera's resource.
    /// </summary>
    /// <exception cref="AcquisitionException">The camera does not support what the exposure asks for.</exception>
    public async Task<CameraFrame> ExposeAsync(
        DeviceId cameraId,
        TimeSpan duration,
        AcquisitionIntent? intent,
        CancellationToken cancellationToken = default
    )
    {
        var camera = DeviceLookup.Resolve<ICamera>(_registry, cameraId, "camera");

        _logger.LogInformation(
            "Exposing camera {DeviceId} for {ExposureSeconds} s", cameraId, duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        return await Run("Exposure", cameraId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(cameraId)], cancellationToken))
            {
                return await AcquisitionExposer.ExposeAsync(camera, duration, intent, _acquisitionDefaults, _logger, cancellationToken);
            }
        });
    }

    /// <summary>
    /// Starts guiding. The guider's resource is held only while the command runs: once guiding has started,
    /// the call completes and releases it while guiding stays active.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device is not registered or is not a guider.</exception>
    public async Task StartGuidingAsync(DeviceId guiderId, CancellationToken cancellationToken = default)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, guiderId, "guider");

        _logger.LogInformation("Starting guiding on {DeviceId}", guiderId);
        await Run("Start guiding", guiderId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(guiderId)], cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await guider.StartGuidingAsync(cancellationToken);
            }
        });
    }

    /// <exception cref="InvalidOperationException">The device is not registered or is not a guider.</exception>
    public async Task StopGuidingAsync(DeviceId guiderId, CancellationToken cancellationToken = default)
    {
        var guider = DeviceLookup.Resolve<IGuider>(_registry, guiderId, "guider");

        _logger.LogInformation("Stopping guiding on {DeviceId}", guiderId);
        await Run("Stop guiding", guiderId, async () =>
        {
            using (await _resources.AcquireAsync([ResourceId.ForDevice(guiderId)], cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await guider.StopGuidingAsync(cancellationToken);
            }
        });
    }

    private async Task Run(string operation, DeviceId deviceId, Func<Task> body) =>
        await Run<object?>(operation, deviceId, async () =>
        {
            await body();
            return null;
        });

    // The outcome of an operation: completed (Debug, with the time it took), cancelled (Information: the user stopped it,
    // which is not an error) or failed (Error, with the exception). The device is the scope of all of it.
    private async Task<T> Run<T>(string operation, DeviceId deviceId, Func<Task<T>> body)
    {
        using var scope = _logger.BeginScope(new KeyValuePair<string, object?>[] { new("DeviceId", deviceId.Value) });
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await body();
            _logger.LogDebug(
                "{Operation} on {DeviceId} completed in {DurationMs:0} ms",
                operation, deviceId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "{Operation} on {DeviceId} was cancelled after {DurationMs:0} ms",
                operation, deviceId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} on {DeviceId} failed", operation, deviceId);
            throw;
        }
    }
}

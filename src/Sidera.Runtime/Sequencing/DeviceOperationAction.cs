using System.Globalization;
using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Core.Mounts;
using Sidera.Core.Resources;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>What a <see cref="DeviceOperationAction"/> does to its device.</summary>
public enum DeviceOperation
{
    /// <summary>Cools a camera to a temperature, in steps.</summary>
    CoolCamera,

    /// <summary>Warms a camera up in steps and switches its cooler off.</summary>
    WarmCamera,

    Park,

    Unpark,

    TrackingOn,

    TrackingOff,

    /// <summary>Opens the shutter of a dome (or the roof of a roll-off roof) and waits until it reports open.</summary>
    OpenDome,

    /// <summary>Closes the shutter and waits until it reports closed.</summary>
    CloseDome,

    /// <summary>Turns a dome to an azimuth.</summary>
    TurnDome,

    /// <summary>Parks a dome.</summary>
    ParkDome,

    /// <summary>Sends a dome to its home position.</summary>
    FindDomeHome,
}

/// <summary>
/// An operation on one camera, one mount or one dome that is not a move across the sky: cool or warm the camera, park or unpark the mount, switch tracking, open or close the shutter of a dome, turn, park or
/// home it. Each checks the capability of the device first and refuses what
/// the device cannot do; none is retried. The camera is cooled and warmed <b>gently</b>: the set point moves in steps over the ramp so that the sensor is never shocked, and a cooling action waits (for a
/// while) until the sensor has got there. Parking, unparking and tracking hold the mount and its stability, so exposures on that mount do not run through them.
/// </summary>
public sealed class DeviceOperationAction : IResourceAwareSequenceStep, IClaimingSequenceStep
{
    /// <summary>The temperature a camera is warmed to before its cooler is switched off; a sensor that is already warmer is only switched off.</summary>
    public const double WarmTargetCelsius = 10;

    /// <summary>How close the sensor has to be to the cooling target for the action to be done.</summary>
    public const double ToleranceCelsius = 1;

    private readonly DeviceRegistry _registry;
    private readonly DeviceId _deviceId;
    private readonly double _celsius;
    private readonly double _rampMinutes;
    private readonly double _azimuthDegrees;
    private readonly TimeSpan _stepInterval;

    public DeviceOperationAction(
        DeviceRegistry registry, DeviceOperation operation, DeviceId deviceId, double celsius = 0, double rampMinutes = 0, TimeSpan? stepInterval = null, double azimuthDegrees = 0)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        Operation = operation;
        _deviceId = deviceId;
        _celsius = celsius;
        _rampMinutes = Math.Max(0, rampMinutes);
        _azimuthDegrees = azimuthDegrees;
        _stepInterval = stepInterval ?? TimeSpan.FromSeconds(15);
    }

    public DeviceOperation Operation { get; }

    public string Name => Operation switch
    {
        DeviceOperation.CoolCamera => string.Create(CultureInfo.InvariantCulture, $"Cool camera to {_celsius:0.#} °C"),
        DeviceOperation.WarmCamera => "Warm camera",
        DeviceOperation.Park => "Park mount",
        DeviceOperation.Unpark => "Unpark mount",
        DeviceOperation.TrackingOn => "Tracking on",
        DeviceOperation.TrackingOff => "Tracking off",
        DeviceOperation.OpenDome => "Open dome",
        DeviceOperation.CloseDome => "Close dome",
        DeviceOperation.TurnDome => string.Create(CultureInfo.InvariantCulture, $"Turn dome to {_azimuthDegrees:0.#}°"),
        DeviceOperation.ParkDome => "Park dome",
        _ => "Find dome home",
    };

    private bool IsDome => Operation is DeviceOperation.OpenDome or DeviceOperation.CloseDome or DeviceOperation.TurnDome or DeviceOperation.ParkDome or DeviceOperation.FindDomeHome;

    public IReadOnlyCollection<ResourceId> RequiredResources => [ResourceId.ForDevice(_deviceId)];

    public IReadOnlyCollection<ResourceClaim> Claims => Operation is DeviceOperation.CoolCamera or DeviceOperation.WarmCamera || IsDome
        ? [ResourceClaim.Exclusive(ResourceId.ForDevice(_deviceId))]
        : [ResourceClaim.Exclusive(ResourceId.ForDevice(_deviceId)), ResourceClaim.Exclusive(ResourceId.ForMountStability(_deviceId))];

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        switch (Operation)
        {
            case DeviceOperation.CoolCamera:
                await CoolAsync(cancellationToken);
                break;
            case DeviceOperation.WarmCamera:
                await WarmAsync(cancellationToken);
                break;
            case var _ when IsDome:
                await DomeAsync(cancellationToken);
                break;
            default:
                await MountAsync(cancellationToken);
                break;
        }

        return new SequenceStepResult();
    }

    // ---- camera

    private ICameraControl Camera()
    {
        var camera = DeviceLookup.Resolve<ICameraControl>(_registry, _deviceId, "camera");
        if (camera.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Camera '{_deviceId}' is not connected.");
        }

        return camera;
    }

    private async Task CoolAsync(CancellationToken cancellationToken)
    {
        var camera = Camera();
        if (camera.Capabilities.Value is not { CanSetCcdTemperature: true, HasCooler: true })
        {
            throw new InvalidOperationException($"Camera '{_deviceId}' cannot be cooled: it has no cooler with a target temperature.");
        }

        var start = camera.Telemetry?.CcdTemperature ?? _celsius;
        await RampAsync(camera, start, _celsius, coolerOn: true, cancellationToken);

        // Wait for the sensor, for as long as the ramp took and a few minutes more; a sensor that is slow does not stop the night.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(_rampMinutes + 5);
        while (camera.Telemetry?.CcdTemperature is { } now && Math.Abs(now - _celsius) > ToleranceCelsius && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    private async Task WarmAsync(CancellationToken cancellationToken)
    {
        var camera = Camera();
        if (camera.Capabilities.Value is not { HasCooler: true })
        {
            throw new InvalidOperationException($"Camera '{_deviceId}' has no cooler to switch off.");
        }

        var start = camera.Telemetry?.CcdTemperature ?? WarmTargetCelsius;
        if (start < WarmTargetCelsius && camera.Telemetry?.CoolerOn != false)
        {
            await RampAsync(camera, start, WarmTargetCelsius, coolerOn: true, cancellationToken);
        }

        await camera.ApplyAsync(new CameraSettings { CoolerOn = false }, cancellationToken);
    }

    // The set point moves from where the sensor is to where it is going, in steps of the step interval over the ramp.
    private async Task RampAsync(ICameraControl camera, double from, double to, bool coolerOn, CancellationToken cancellationToken)
    {
        var steps = _rampMinutes <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(TimeSpan.FromMinutes(_rampMinutes) / _stepInterval));
        for (var i = 1; i <= steps; i++)
        {
            var setPoint = from + ((to - from) * i / steps);
            await camera.ApplyAsync(new CameraSettings { TargetTemperature = setPoint, CoolerOn = coolerOn }, cancellationToken);
            if (i < steps)
            {
                await Task.Delay(_stepInterval, cancellationToken);
            }
        }
    }

    // ---- dome

    private async Task DomeAsync(CancellationToken cancellationToken)
    {
        var dome = DeviceLookup.Resolve<IDomeControl>(_registry, _deviceId, "dome");
        if (dome.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Dome '{_deviceId}' is not connected.");
        }

        var capabilities = dome.Capabilities.Value;
        switch (Operation)
        {
            case DeviceOperation.OpenDome:
            case DeviceOperation.CloseDome:
                if (capabilities is not { CanSetShutter: true })
                {
                    throw new InvalidOperationException($"Dome '{_deviceId}' cannot open or close its shutter.");
                }

                await (Operation == DeviceOperation.OpenDome ? dome.OpenShutterAsync(cancellationToken) : dome.CloseShutterAsync(cancellationToken));
                break;
            case DeviceOperation.TurnDome:
                if (capabilities is not { CanSetAzimuth: true })
                {
                    throw new InvalidOperationException($"Dome '{_deviceId}' cannot be turned: it has no azimuth.");
                }

                await dome.SlewToAzimuthAsync(_azimuthDegrees, cancellationToken);
                break;
            case DeviceOperation.ParkDome:
                if (capabilities is not { CanPark: true })
                {
                    throw new InvalidOperationException($"Dome '{_deviceId}' cannot park.");
                }

                await dome.ParkAsync(cancellationToken);
                break;
            default:
                if (capabilities is not { CanFindHome: true })
                {
                    throw new InvalidOperationException($"Dome '{_deviceId}' cannot find its home position.");
                }

                await dome.FindHomeAsync(cancellationToken);
                break;
        }
    }

    // ---- mount

    private async Task MountAsync(CancellationToken cancellationToken)
    {
        var mount = DeviceLookup.Resolve<IMountControl>(_registry, _deviceId, "mount");
        if (mount.ConnectionState != DeviceConnectionState.Connected)
        {
            throw new InvalidOperationException($"Mount '{_deviceId}' is not connected.");
        }

        var capabilities = mount.Capabilities.Value;
        switch (Operation)
        {
            case DeviceOperation.Park:
                if (capabilities is not { CanPark: true })
                {
                    throw new InvalidOperationException($"Mount '{_deviceId}' cannot park.");
                }

                await mount.ParkAsync(cancellationToken);
                break;
            case DeviceOperation.Unpark:
                if (capabilities is not { CanPark: true })
                {
                    throw new InvalidOperationException($"Mount '{_deviceId}' cannot be unparked: it cannot park.");
                }

                await mount.UnparkAsync(cancellationToken);
                break;
            default:
                if (capabilities is not { CanSetTracking: true })
                {
                    throw new InvalidOperationException($"Mount '{_deviceId}' cannot switch tracking.");
                }

                await mount.SetTrackingAsync(Operation == DeviceOperation.TrackingOn, cancellationToken);
                break;
        }
    }
}

using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Runtime.Devices;

namespace Astra.Desktop;

/// <summary>
/// The parameters of the demo sequence that a user can change, in the units a user thinks in. A plain value
/// description: it holds no runtime objects and is turned into a fresh sequence by <see cref="DemoSequenceBuilder"/>
/// every time the sequence runs. How the steps are wired together (coordination groups, safe points, branches) is
/// not part of it.
/// </summary>
public sealed record DemoSequenceConfiguration
{
    public DeviceId? CameraId { get; init; }
    public DeviceId? MountId { get; init; }
    public DeviceId? GuiderId { get; init; }

    /// <summary>Right ascension of the slew target, in hours.</summary>
    public double RightAscensionHours { get; init; } = DemoDefaults.Target.RightAscensionHours;

    /// <summary>Declination of the slew target, in degrees.</summary>
    public double DeclinationDegrees { get; init; } = DemoDefaults.Target.DeclinationDegrees;

    public double ExposureSeconds { get; init; } = 2;
    public int ImagingRepeatCount { get; init; } = 3;

    public int DitherRepeatCount { get; init; } = 2;

    /// <summary>The wait before each dither, in seconds.</summary>
    public double DitherDelaySeconds { get; init; } = 2;

    /// <summary>Dither amplitude in guide camera pixels.</summary>
    public double DitherAmplitudePixels { get; init; } = 1.5;

    /// <summary>Guiding counts as settled at or below this guide error, in guide camera pixels...</summary>
    public double SettleThresholdPixels { get; init; } = 0.5;

    /// <summary>...when it stays there for this many seconds without interruption...</summary>
    public double SettleStableSeconds { get; init; } = 1;

    /// <summary>...and the wait fails after this many seconds in total.</summary>
    public double SettleTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// The configuration that reproduces the demo as it always ran, with the demo timings of <paramref name="options"/>
    /// and the demo equipment preselected where it is registered (otherwise the first device of the kind).
    /// </summary>
    public static DemoSequenceConfiguration Default(DemoOptions options, Astra.Runtime.Devices.DeviceRegistry registry)
    {
        var devices = registry.GetAll();

        DeviceId? Pick<T>(DeviceId preferred) where T : class, IDevice =>
            devices.OfType<T>().FirstOrDefault(d => d.Id == preferred)?.Id
            ?? devices.OfType<T>().OrderBy(d => d.Id.Value, System.StringComparer.Ordinal).FirstOrDefault()?.Id;

        return new DemoSequenceConfiguration
        {
            CameraId = Pick<ICamera>(DemoSetup.MainCameraId),
            MountId = Pick<IMount>(DemoSetup.MountId),
            GuiderId = Pick<IGuider>(DemoSetup.GuiderId),
            ExposureSeconds = options.SequenceExposure.TotalSeconds,
            DitherDelaySeconds = options.SequenceWait.TotalSeconds,
            DitherAmplitudePixels = options.DitherAmplitudePixels,
            SettleThresholdPixels = options.SettleThresholdPixels,
            SettleStableSeconds = options.SettleStableDuration.TotalSeconds,
            SettleTimeoutSeconds = options.SettleTimeout.TotalSeconds,
        };
    }
}

internal static class DemoDefaults
{
    /// <summary>Where the demo slews to (the Orion Nebula).</summary>
    public static readonly CelestialCoordinates Target = new(5.588, -5.39);
}

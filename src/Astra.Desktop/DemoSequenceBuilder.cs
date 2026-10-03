using System;
using System.Collections.Generic;
using System.Linq;
using Astra.Core.Devices;
using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Sequencing;
using Astra.Desktop.ViewModels;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop;

/// <summary>The configuration cannot make a sequence; <see cref="Problems"/> say why, one short sentence each.</summary>
public sealed class SequenceConfigurationException(IReadOnlyList<string> problems)
    : InvalidOperationException(string.Join(" ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Builds the demo sequence from a <see cref="DemoSequenceConfiguration"/>. Every call makes a fresh tree out of the
/// existing steps, so a run is never affected by a later edit. The structure is fixed:
/// <code>
/// Start guiding
/// Slew
/// Parallel (coordinated)
///   Main camera:  Repeat → Imaging Block: Exposure, Safe Point
///   Dither:       Repeat → Dither Block: Wait, Dither (then settle)
/// Stop guiding
/// </code>
/// The coordination group, the safe point and the branches are part of this template, not of the configuration.
/// The dither disturbs the imaging camera, so that is the camera it is told about.
/// </summary>
public static class DemoSequenceBuilder
{
    /// <summary>Checks everything that can be checked without building; empty when the configuration is fine.</summary>
    public static IReadOnlyList<string> Validate(DeviceRegistry registry, DemoSequenceConfiguration c)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(c);

        var problems = new List<string>();

        CheckDevice<ICamera>(registry, c.CameraId, "camera", problems);
        CheckDevice<IMount>(registry, c.MountId, "mount", problems);
        CheckDevice<IGuider>(registry, c.GuiderId, "guider", problems);

        Check(() => _ = new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees), problems);

        CheckPositive(c.ExposureSeconds, "Exposure", "s", problems);
        CheckCount(c.ImagingRepeatCount, "Imaging repeat count", problems);
        CheckCount(c.DitherRepeatCount, "Dither repeat count", problems);
        CheckPositive(c.DitherDelaySeconds, "Wait before dither", "s", problems);
        CheckPositive(c.DitherAmplitudePixels, "Dither amplitude", "px", problems);

        var settleFieldsUsable =
            IsPositive(c.SettleThresholdPixels) && IsPositive(c.SettleStableSeconds) && IsPositive(c.SettleTimeoutSeconds);
        CheckPositive(c.SettleThresholdPixels, "Settle threshold", "px", problems);
        CheckPositive(c.SettleStableSeconds, "Settle stable time", "s", problems);
        CheckPositive(c.SettleTimeoutSeconds, "Settle timeout", "s", problems);
        if (settleFieldsUsable && c.SettleTimeoutSeconds <= c.SettleStableSeconds)
        {
            problems.Add("Settle timeout must be longer than the stable time.");
        }

        return problems;
    }

    /// <exception cref="SequenceConfigurationException">The configuration is not valid.</exception>
    public static Sequence Build(DeviceRegistry registry, DemoSequenceConfiguration c)
    {
        var problems = Validate(registry, c);
        if (problems.Count > 0)
        {
            throw new SequenceConfigurationException(problems);
        }

        try
        {
            var camera = c.CameraId!.Value;
            var mount = c.MountId!.Value;
            var guider = c.GuiderId!.Value;

            var exposure = new CameraExposureAction(registry, camera, TimeSpan.FromSeconds(c.ExposureSeconds));
            var settle = new GuidingSettleOptions(
                c.SettleThresholdPixels,
                TimeSpan.FromSeconds(c.SettleStableSeconds),
                TimeSpan.FromSeconds(c.SettleTimeoutSeconds));
            var dither = new DitherAction(registry, guider, mount, [camera], c.DitherAmplitudePixels, settle);

            var imaging = new SequenceGroup("Main camera", [
                new RepeatStep(c.ImagingRepeatCount, new SequenceGroup("Imaging Block", [exposure, new SafePointStep()])),
            ]);
            var dithering = new SequenceGroup("Dither", [
                new RepeatStep(
                    c.DitherRepeatCount,
                    new SequenceGroup("Dither Block", [
                        new DelayAction(TimeSpan.FromSeconds(c.DitherDelaySeconds)),
                        dither,
                    ])),
            ]);

            return new Sequence("Demo", [
                new StartGuidingAction(registry, guider),
                new SlewAction(registry, mount, new CelestialCoordinates(c.RightAscensionHours, c.DeclinationDegrees)),
                new ParallelStep("Imaging and dither", [imaging, dithering], DemoSetup.CoordinationGroup),
                new StopGuidingAction(registry, guider),
            ]);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            // The steps validate their own arguments too; anything the checks above missed ends up here.
            throw new SequenceConfigurationException([UserFacingError.Describe(ex)]);
        }
    }

    private static void CheckDevice<T>(DeviceRegistry registry, DeviceId? id, string kind, List<string> problems)
        where T : class, IDevice
    {
        if (id is not { } deviceId)
        {
            problems.Add($"Select a {kind}.");
        }
        else if (!registry.TryGet(deviceId, out var device) || device is null)
        {
            problems.Add($"The {kind} '{deviceId}' is not available.");
        }
        else if (device is not T)
        {
            problems.Add($"'{deviceId}' is not a {kind}.");
        }
    }

    private static void Check(Action domainCheck, List<string> problems)
    {
        try
        {
            domainCheck();
        }
        catch (ArgumentException ex)
        {
            problems.Add(UserFacingError.Describe(ex));
        }
    }

    private static bool IsPositive(double value) => double.IsFinite(value) && value > 0;

    private static void CheckPositive(double value, string label, string unit, List<string> problems)
    {
        if (!IsPositive(value))
        {
            problems.Add($"{label} must be greater than 0 {unit}.");
        }
    }

    private static void CheckCount(int value, string label, List<string> problems)
    {
        if (value < 1)
        {
            problems.Add($"{label} must be at least 1.");
        }
    }
}

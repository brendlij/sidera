using Astra.Core.Guiding;
using Astra.Core.Mounts;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop;

/// <summary>
/// The read-only demo sequence. It only combines steps that already exist and expects the equipment to be
/// connected by the user beforehand:
/// <code>
/// Start guiding
/// Slew
/// Parallel (coordination group)
///   Main camera:  Repeat × 3 → Imaging Block: Exposure, Safe Point
///   Dither:       Repeat × 2 → Dither Block: Wait, Dither (waits for the camera's safe point, then settles)
/// Stop guiding
/// </code>
/// </summary>
public static class DemoSequenceFactory
{
    /// <summary>Where the demo slews to (the Orion Nebula).</summary>
    public static readonly CelestialCoordinates Target = new(5.588, -5.39);

    public static Sequence Create(DeviceRegistry registry, DemoOptions options)
    {
        var exposure = new CameraExposureAction(registry, DemoSetup.MainCameraId, options.SequenceExposure);
        var settle = new GuidingSettleOptions(
            options.SettleThresholdPixels, options.SettleStableDuration, options.SettleTimeout);
        var dither = new DitherAction(
            registry, DemoSetup.GuiderId, DemoSetup.MountId, [DemoSetup.MainCameraId],
            options.DitherAmplitudePixels, settle);

        var imaging = new SequenceGroup("Main camera", [
            new RepeatStep(3, new SequenceGroup("Imaging Block", [exposure, new SafePointStep()])),
        ]);
        var dithering = new SequenceGroup("Dither", [
            new RepeatStep(2, new SequenceGroup("Dither Block", [new DelayAction(options.SequenceWait), dither])),
        ]);

        return new Sequence("Demo", [
            new StartGuidingAction(registry, DemoSetup.GuiderId),
            new SlewAction(registry, DemoSetup.MountId, Target),
            new ParallelStep("Imaging and dither", [imaging, dithering], DemoSetup.CoordinationGroup),
            new StopGuidingAction(registry, DemoSetup.GuiderId),
        ]);
    }
}

using System;
using Astra.Core.Coordination;
using Astra.Core.Devices;
using Astra.Core.Rigs;
using Astra.Runtime;
using Astra.Runtime.Devices;

namespace Astra.Desktop;

/// <summary>The simulated equipment of the demo, all registered with the host and none of it connected.</summary>
public sealed record DemoEquipment(
    SimulatedCamera Camera,
    SimulatedMount Mount,
    SimulatedGuider Guider,
    Rig Rig
);

/// <summary>
/// Composes the local demo: a camera, a mount and a guider, and the logical rig around the camera. The mount and
/// the guider are shared equipment, deliberately not part of the rig.
/// </summary>
public static class DemoSetup
{
    public static readonly RigId MainRigId = new("rig.main");
    public static readonly DeviceId MainCameraId = new("camera.main");
    public static readonly DeviceId MountId = new("mount.eq6");
    public static readonly DeviceId GuiderId = new("guider.main");

    public static readonly RigId WideRigId = new("rig.wide");
    public static readonly DeviceId WideCameraId = new("camera.wide");
    public static readonly RigId NarrowRigId = new("rig.narrow");
    public static readonly DeviceId NarrowCameraId = new("camera.narrow");

    /// <summary>The coordination group of the demo sequence's parallel branches.</summary>
    public static readonly CoordinationGroupId CoordinationGroup = new("session.demo");

    /// <summary>
    /// Adds a wide and a narrow field rig to the demo, each with a simulated camera of its own: with the main rig a
    /// session of three telescopes on one mount and one guider, which is what Multi-Rig Imaging is for. The mount and
    /// the guider of <see cref="AddDemoEquipment"/> stay the shared equipment. Nothing is connected.
    /// </summary>
    public static void AddDemoRigs(AstraRuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var wide = host.AddSimulatedCamera(WideCameraId, "Wide Camera");
        host.AddRig(new Rig(
            WideRigId, "Wide Rig", wide.Id,
            new OpticalTrain(250, 60, 3.76, 23.5, 15.7, 6248, 4176)));

        var narrow = host.AddSimulatedCamera(NarrowCameraId, "Narrow Camera");
        host.AddRig(new Rig(
            NarrowRigId, "Narrow Rig", narrow.Id,
            new OpticalTrain(1200, 200, 3.76, 17.6, 13.2, 4656, 3520)));
    }

    public static DemoEquipment AddDemoEquipment(AstraRuntimeHost host, DemoOptions? options = null)
    {
        options ??= new DemoOptions();

        var camera = host.AddSimulatedCamera(MainCameraId, "Main Camera");
        var mount = host.AddSimulatedMount(MountId, "EQ6 Mount", options.SlewDuration);
        var guider = host.AddSimulatedGuider(
            GuiderId, "Main Guider",
            options.GuiderStartDuration, options.GuiderStopDuration, options.GuiderDitherDuration);

        var rig = new Rig(
            MainRigId,
            "Main Rig",
            camera.Id,
            new OpticalTrain(
                focalLengthMm: 750,
                apertureMm: 150,
                pixelSizeMicrons: 3.76,
                sensorWidthMm: 23.5,
                sensorHeightMm: 15.7,
                resolutionWidth: 6248,
                resolutionHeight: 4176));
        host.AddRig(rig);

        return new DemoEquipment(camera, mount, guider, rig);
    }
}

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

    /// <summary>The coordination group of the demo sequence's parallel branches.</summary>
    public static readonly CoordinationGroupId CoordinationGroup = new("session.demo");

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

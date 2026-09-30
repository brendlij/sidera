using Astra.Core.Devices;
using Astra.Core.Rigs;
using Astra.Runtime;
using Astra.Runtime.Devices;

namespace Astra.Desktop;

/// <summary>Composes the local demo equipment: one simulated camera and the logical rig built around it.</summary>
public static class DemoSetup
{
    public static readonly RigId MainRigId = new("rig.main");
    public static readonly DeviceId MainCameraId = new("camera.main");

    /// <summary>Adds the "Main Camera" and the "Main Rig" to the host and returns the camera.</summary>
    public static SimulatedCamera AddMainRig(AstraRuntimeHost host)
    {
        var camera = host.AddSimulatedCamera(MainCameraId, "Main Camera");

        host.AddRig(new Rig(
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
                resolutionHeight: 4176)));

        return camera;
    }
}

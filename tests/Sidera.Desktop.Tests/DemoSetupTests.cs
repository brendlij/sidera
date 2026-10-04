using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests;

public class DemoSetupTests
{
    [Fact]
    public async Task DemoSetup_RegistersMainRigAsRigMain()
    {
        await using var host = new SideraRuntimeHost();

        DemoSetup.AddDemoEquipment(host);

        Assert.True(host.RigRegistry.TryGet(new RigId("rig.main"), out var rig));
        Assert.Equal("Main Rig", rig!.Name);
        Assert.Equal(750, rig.Optics!.FocalLengthMm);
        Assert.Equal(6248, rig.Optics.SensorWidthPixels);
        Assert.Equal(new DeviceId("focuser.main"), rig.FocuserId);
        Assert.Equal(new DeviceId("filterwheel.main"), rig.FilterWheelId);
    }

    [Fact]
    public async Task DemoRig_ReferencesTheRegisteredMainCamera()
    {
        await using var host = new SideraRuntimeHost();

        var camera = DemoSetup.AddDemoEquipment(host).Camera;

        host.RigRegistry.TryGet(new RigId("rig.main"), out var rig);
        Assert.Equal(new DeviceId("camera.main"), rig!.CameraId);
        Assert.Equal(camera.Id, rig.CameraId);
        Assert.True(host.DeviceRegistry.TryGet(rig.CameraId, out var device));
        Assert.Same(camera, device);
    }
}

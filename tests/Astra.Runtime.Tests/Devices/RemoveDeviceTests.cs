using Astra.Core.Devices;
using Astra.Core.Rigs;

namespace Astra.Runtime.Tests.Devices;

/// <summary>A device can leave the host again: the registry and the state store let go of it.</summary>
public class RemoveDeviceTests
{
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly DeviceId FocuserId = new("focuser.main");

    [Fact]
    public async Task ADisconnectedDevice_LeavesTheRegistry_AndTheStateStoreForgetsIt()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera");
        await camera.ConnectAsync();
        await camera.DisconnectAsync();
        Assert.True(host.StateStore.TryGet(CameraId, out _));

        var removed = host.RemoveDevice(CameraId);

        Assert.True(removed);
        Assert.False(host.DeviceRegistry.TryGet(CameraId, out _));
        Assert.False(host.StateStore.TryGet(CameraId, out _));
    }

    [Fact]
    public async Task AnUnknownDevice_IsNotAnError()
    {
        await using var host = new AstraRuntimeHost();

        Assert.False(host.RemoveDevice(CameraId));
    }

    [Fact]
    public async Task AConnectedDevice_CannotBeRemoved()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera");
        await camera.ConnectAsync();

        var failure = Assert.Throws<InvalidOperationException>(() => host.RemoveDevice(CameraId));

        Assert.Contains("disconnect it before removing", failure.Message);
        Assert.True(host.DeviceRegistry.TryGet(CameraId, out _));
    }

    [Fact]
    public async Task ADevicePartOfARig_CannotBeRemoved()
    {
        await using var host = new AstraRuntimeHost();
        host.AddSimulatedCamera(CameraId, "Camera");
        host.AddSimulatedFocuser(FocuserId, "Focuser");
        host.AddRig(new Rig(new RigId("rig.main"), "Main Rig", CameraId, new OpticalTrain(750, 150, 3.76, 23.5, 15.7, 6248, 4176), FocuserId));

        var failure = Assert.Throws<InvalidOperationException>(() => host.RemoveDevice(FocuserId));

        Assert.Contains("part of the rig 'rig.main'", failure.Message);
        Assert.True(host.DeviceRegistry.TryGet(FocuserId, out _));
    }

    [Fact]
    public async Task AnIdThatWasFreed_CanBeUsedAgain()
    {
        await using var host = new AstraRuntimeHost();
        host.AddSimulatedCamera(CameraId, "First");
        host.RemoveDevice(CameraId);

        host.AddSimulatedCamera(CameraId, "Second");

        Assert.True(host.DeviceRegistry.TryGet(CameraId, out var device));
        Assert.Equal("Second", device!.Name);
    }
}

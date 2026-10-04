using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>Rigs with optional focuser and filter wheel: the camera stays the only required device.</summary>
public class RigHardwareTests
{
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.main"), "Camera");
        host.AddSimulatedCamera(new DeviceId("camera.wide"), "Wide Camera");
        host.AddSimulatedFocuser(new DeviceId("focuser.main"), "EAF");
        host.AddSimulatedFilterWheel(new DeviceId("filterwheel.main"), "EFW", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha")]);
        return host;
    }

    private static Rig MakeRig(string id, string camera, string? focuser = null, string? wheel = null) => new(
        new RigId(id), id, new DeviceId(camera), Optics,
        focuser is null ? null : new DeviceId(focuser),
        wheel is null ? null : new DeviceId(wheel));

    [Fact]
    public async Task ACameraOnlyRig_HasNoFocuserAndNoFilterWheel()
    {
        await using var host = CreateHost();
        var rig = MakeRig("rig.a", "camera.main");

        host.AddRig(rig);

        Assert.Null(rig.FocuserId);
        Assert.Null(rig.FilterWheelId);
    }

    [Fact]
    public async Task ARigWithAFocuserOnly_ARigWithAWheelOnly_AndARigWithBoth_AreAllValid()
    {
        await using var host = CreateHost();
        var focuserOnly = MakeRig("rig.f", "camera.main", focuser: "focuser.main");
        var wheelOnly = MakeRig("rig.w", "camera.wide", wheel: "filterwheel.main");
        var both = MakeRig("rig.b", "camera.main", "focuser.main", "filterwheel.main");

        host.AddRig(focuserOnly);
        host.AddRig(wheelOnly);
        host.AddRig(both);

        Assert.Equal((new DeviceId("focuser.main"), (DeviceId?)null), (focuserOnly.FocuserId!.Value, focuserOnly.FilterWheelId));
        Assert.Equal(((DeviceId?)null, new DeviceId("filterwheel.main")), (wheelOnly.FocuserId, wheelOnly.FilterWheelId!.Value));
        Assert.Equal((new DeviceId("focuser.main"), new DeviceId("filterwheel.main")), (both.FocuserId!.Value, both.FilterWheelId!.Value));
        Assert.Equal(3, host.RigRegistry.GetAll().Count);
    }

    [Fact]
    public async Task TwoRigsMayReferToTheSameFocuserAndWheel_TheResourceManagerKeepsUseApart()
    {
        await using var host = CreateHost();

        host.AddRig(MakeRig("rig.a", "camera.main", "focuser.main", "filterwheel.main"));
        host.AddRig(MakeRig("rig.b", "camera.wide", "focuser.main", "filterwheel.main"));

        Assert.Equal(2, host.RigRegistry.GetAll().Count);
    }

    [Fact]
    public async Task RegisteringARig_ConnectsNothing_AndRefersToDevicesByIdOnly()
    {
        await using var host = CreateHost();

        host.AddRig(MakeRig("rig.b", "camera.main", "focuser.main", "filterwheel.main"));

        Assert.All(host.DeviceRegistry.GetAll(), d => Assert.Equal(DeviceConnectionState.Disconnected, d.ConnectionState));
    }

    [Fact]
    public async Task AFocuserOrWheelThatIsNotRegistered_IsRejected()
    {
        await using var host = CreateHost();

        var focuser = Assert.Throws<InvalidOperationException>(() => host.AddRig(MakeRig("rig.a", "camera.main", focuser: "focuser.none")));
        var wheel = Assert.Throws<InvalidOperationException>(() => host.AddRig(MakeRig("rig.a", "camera.main", wheel: "filterwheel.none")));

        Assert.Equal("Focuser device 'focuser.none' is not registered.", focuser.Message);
        Assert.Equal("Filter wheel device 'filterwheel.none' is not registered.", wheel.Message);
    }

    [Fact]
    public void TheOpticalTrain_HoldsNoDeviceIdentity()
    {
        var properties = typeof(OpticalTrain).GetProperties().Select(p => p.PropertyType);

        Assert.DoesNotContain(typeof(DeviceId), properties);
        Assert.DoesNotContain(typeof(DeviceId?), properties);
    }
}

using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

public class RigRegistryTests
{
    private sealed class FakeDevice(string id, DeviceType type) : IDevice
    {
        public DeviceId Id { get; } = new(id);
        public string Name => "Fake";
        public DeviceType Type { get; } = type;
        public DeviceConnectionState ConnectionState => DeviceConnectionState.Disconnected;
        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 23.5, 15.7, 6248, 4176);

    private static Rig MakeRig(
        string rigId,
        string cameraId,
        string? focuserId = null,
        string? filterWheelId = null
    ) => new(
        new RigId(rigId),
        rigId,
        new DeviceId(cameraId),
        Optics,
        focuserId is null ? null : new DeviceId(focuserId),
        filterWheelId is null ? null : new DeviceId(filterWheelId));

    private static (DeviceRegistry Devices, RigRegistry Rigs) Create(params string[] cameraIds)
    {
        var devices = new DeviceRegistry();
        foreach (var id in cameraIds)
        {
            devices.Register(new SimulatedCamera(new DeviceId(id)));
        }

        return (devices, new RigRegistry(devices));
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsRig()
    {
        var (_, rigs) = Create("camera.main");
        var rig = MakeRig("rig.main", "camera.main");

        rigs.Register(rig);

        Assert.True(rigs.TryGet(new RigId("rig.main"), out var found));
        Assert.Same(rig, found);
    }

    [Fact]
    public void Register_RejectsDuplicateRigId()
    {
        var (_, rigs) = Create("camera.main", "camera.wide");
        rigs.Register(MakeRig("rig.main", "camera.main"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            rigs.Register(MakeRig("rig.main", "camera.wide")));

        Assert.Contains("'rig.main' is already registered", error.Message);
        Assert.Single(rigs.GetAll());
    }

    [Fact]
    public void Unregister_RemovesRig()
    {
        var (_, rigs) = Create("camera.main");
        rigs.Register(MakeRig("rig.main", "camera.main"));

        Assert.True(rigs.Unregister(new RigId("rig.main")));
        Assert.False(rigs.TryGet(new RigId("rig.main"), out _));
        Assert.False(rigs.Unregister(new RigId("rig.main")));
    }

    [Fact]
    public void TryGet_ReturnsFalseForUnknownRig()
    {
        var (_, rigs) = Create();

        Assert.False(rigs.TryGet(new RigId("nope"), out var rig));
        Assert.Null(rig);
    }

    [Fact]
    public void GetAll_ReturnsSnapshot()
    {
        var (_, rigs) = Create("camera.main", "camera.wide");
        rigs.Register(MakeRig("rig.main", "camera.main"));

        var snapshot = rigs.GetAll();
        rigs.Register(MakeRig("rig.wide", "camera.wide"));

        Assert.Single(snapshot);
        Assert.Equal(2, rigs.GetAll().Count);
    }

    [Fact]
    public void MultipleRigs_CanCoexist()
    {
        var (_, rigs) = Create("camera.main", "camera.wide");

        rigs.Register(MakeRig("rig.main", "camera.main"));
        rigs.Register(MakeRig("rig.wide", "camera.wide"));

        Assert.Equal(
            new[] { "camera.main", "camera.wide" },
            rigs.GetAll().Select(r => r.CameraId.Value).OrderBy(v => v));
    }

    [Fact]
    public void SameDevice_CanBeReferencedByMoreThanOneRig()
    {
        var (_, rigs) = Create("camera.main");

        rigs.Register(MakeRig("rig.a", "camera.main"));
        rigs.Register(MakeRig("rig.b", "camera.main"));

        Assert.Equal(2, rigs.GetAll().Count);
    }

    [Fact]
    public void Register_RejectsMissingCamera()
    {
        var (_, rigs) = Create();

        var error = Assert.Throws<InvalidOperationException>(() =>
            rigs.Register(MakeRig("rig.main", "camera.main")));

        Assert.Equal("Camera device 'camera.main' is not registered.", error.Message);
        Assert.Empty(rigs.GetAll());
    }

    [Fact]
    public void Register_RejectsNonCameraAsCamera()
    {
        var (devices, rigs) = Create();
        devices.Register(new FakeDevice("foo", DeviceType.Focuser));

        var error = Assert.Throws<InvalidOperationException>(() =>
            rigs.Register(MakeRig("rig.main", "foo")));

        Assert.Equal("Device 'foo' assigned as camera does not implement ICamera.", error.Message);
    }

    [Fact]
    public void Register_RejectsMissingFocuser()
    {
        var (_, rigs) = Create("camera.main");

        var error = Assert.Throws<InvalidOperationException>(() =>
            rigs.Register(MakeRig("rig.main", "camera.main", focuserId: "focuser.main")));

        Assert.Equal("Focuser device 'focuser.main' is not registered.", error.Message);
    }

    [Fact]
    public void Register_RejectsMissingFilterWheel()
    {
        var (_, rigs) = Create("camera.main");

        var error = Assert.Throws<InvalidOperationException>(() =>
            rigs.Register(MakeRig("rig.main", "camera.main", filterWheelId: "wheel.main")));

        Assert.Equal("Filter wheel device 'wheel.main' is not registered.", error.Message);
    }

    [Fact]
    public void Register_AcceptsRigWithAllDevicesPresent()
    {
        var (devices, rigs) = Create("camera.main");
        devices.Register(new SimulatedFocuser(new DeviceId("focuser.main")));
        devices.Register(new SimulatedFilterWheel(new DeviceId("wheel.main"), [new FilterSlot(0, "L")]));

        rigs.Register(MakeRig("rig.main", "camera.main", "focuser.main", "wheel.main"));

        Assert.Single(rigs.GetAll());
    }

    [Fact]
    public void Register_RejectsADeviceAssignedAsFocuserThatIsNoFocuser()
    {
        var (devices, rigs) = Create("camera.main");
        devices.Register(new FakeDevice("focuser.main", DeviceType.Focuser));

        var error = Assert.Throws<InvalidOperationException>(() => rigs.Register(MakeRig("rig.main", "camera.main", "focuser.main")));

        Assert.Contains("does not implement IFocuser", error.Message);
        Assert.Empty(rigs.GetAll());
    }

    [Fact]
    public void Register_RejectsADeviceAssignedAsFilterWheelThatIsNoFilterWheel()
    {
        var (devices, rigs) = Create("camera.main");
        devices.Register(new SimulatedFocuser(new DeviceId("focuser.main")));

        var error = Assert.Throws<InvalidOperationException>(
            () => rigs.Register(MakeRig("rig.main", "camera.main", filterWheelId: "focuser.main")));

        Assert.Contains("does not implement IFilterWheel", error.Message);
    }

    [Fact]
    public void Register_DoesNotConnectAnyDevice()
    {
        var (devices, rigs) = Create("camera.main");

        rigs.Register(MakeRig("rig.main", "camera.main"));

        Assert.True(devices.TryGet(new DeviceId("camera.main"), out var camera));
        Assert.Equal(DeviceConnectionState.Disconnected, camera!.ConnectionState);
    }
}

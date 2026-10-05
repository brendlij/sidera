using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Runtime.Tests.Rigs;

/// <summary>A rig is an imaging train with an optional mount and guider of its own; sharing one is naming the same device.</summary>
public sealed class RigOwnershipTests
{
    private static readonly DeviceId Cam = new("camera.a");

    [Fact]
    public void ARigNeedsOnlyACamera_AndMountAndGuiderAreOptional()
    {
        var rig = new Rig(new("r"), "R", Cam);

        Assert.Null(rig.MountId);
        Assert.Null(rig.GuiderId);
        Assert.Equal([(RigRole.Camera, Cam)], rig.Devices());
    }

    [Fact]
    public void TheDevicesOfARig_AreListedInTheOrderTheyAreShown()
    {
        var rig = new Rig(new("r"), "R", Cam, null, new("f"), new("w"), new("rot"), null, new("m"), new("g"));

        Assert.Equal(
            [RigRole.Camera, RigRole.Focuser, RigRole.FilterWheel, RigRole.Rotator, RigRole.Mount, RigRole.Guider],
            rig.Devices().Select(d => d.Role));
        Assert.Equal(new DeviceId("m"), rig.DeviceFor(RigRole.Mount));
        Assert.Equal(new DeviceId("g"), rig.DeviceFor(RigRole.Guider));
    }

    [Fact]
    public void EveryWithMethod_KeepsEverythingElse()
    {
        var model = new Sidera.Core.Rotators.RotatorSkyModel(12);
        var rig = new Rig(new("r"), "R", Cam, new OpticalTrain(500), new("f"), new("w"), new("rot"), model, new("m"), new("g"));

        foreach (var changed in new[]
                 {
                     rig.WithName("Other"), rig.WithOptics(new OpticalTrain(900)), rig.WithFocuser(new("f2")), rig.WithFilterWheel(null), rig.WithRotatorModel(null),
                     rig.WithMount(new("m2")), rig.WithGuider(null), rig.WithCamera(new("camera.b")),
                 })
        {
            Assert.Equal(rig.Id, changed.Id);
        }

        var onNewMount = rig.WithMount(new("m2"));
        Assert.Equal(new DeviceId("m2"), onNewMount.MountId);
        Assert.Equal(new DeviceId("g"), onNewMount.GuiderId);
        Assert.Equal(model, onNewMount.RotatorModel);
        Assert.Equal(500, onNewMount.Optics!.FocalLengthMm);
        Assert.Null(rig.WithGuider(null).GuiderId);
        Assert.Equal(new DeviceId("m"), rig.WithGuider(null).MountId);
        Assert.Equal("Other", rig.WithName("Other").Name);
        Assert.Equal(new DeviceId("f"), rig.WithName("Other").FocuserId);
    }

    [Fact]
    public void ANewRotator_DoesNotKeepTheCalibrationOfTheOldOne()
    {
        var rig = new Rig(new("r"), "R", Cam, null, null, null, new("rot"), new Sidera.Core.Rotators.RotatorSkyModel(5));

        Assert.Null(rig.WithDevice(RigRole.Rotator, new DeviceId("rot2")).RotatorModel);
        Assert.NotNull(rig.WithDevice(RigRole.Rotator, new DeviceId("rot")).RotatorModel);
    }

    [Fact]
    public void TheCameraOfARig_CannotBeTakenAway()
    {
        var rig = new Rig(new("r"), "R", Cam);

        Assert.Throws<ArgumentException>(() => rig.WithDevice(RigRole.Camera, null));
    }

    // ---- The registry

    private static async Task<SideraRuntimeHost> HostWithDevicesAsync()
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new("camera.a"), "A");
        host.AddSimulatedCamera(new("camera.b"), "B");
        host.AddSimulatedMount(new("mount.1"), "Mount 1", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedMount(new("mount.2"), "Mount 2", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.2"), "Guider 2", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        await Task.CompletedTask;
        return host;
    }

    [Fact]
    public async Task TwoRigs_MayShareAMountAndAGuider_BecauseTheyNameTheSameDevices()
    {
        await using var host = await HostWithDevicesAsync();

        host.AddRig(new Rig(new("rig.a"), "A", new("camera.a"), mountId: new("mount.1"), guiderId: new("guider.1")));
        host.AddRig(new Rig(new("rig.b"), "B", new("camera.b"), mountId: new("mount.1"), guiderId: new("guider.1")));

        var rigs = host.RigRegistry.GetAll();
        Assert.Equal(2, rigs.Count(r => r.MountId == new DeviceId("mount.1")));
        Assert.Equal(2, rigs.Count(r => r.GuiderId == new DeviceId("guider.1")));
    }

    [Fact]
    public async Task TwoRigs_MayHaveTheirOwnMountsAndGuiders()
    {
        await using var host = await HostWithDevicesAsync();

        host.AddRig(new Rig(new("rig.a"), "A", new("camera.a"), mountId: new("mount.1"), guiderId: new("guider.1")));
        host.AddRig(new Rig(new("rig.b"), "B", new("camera.b"), mountId: new("mount.2"), guiderId: new("guider.2")));

        Assert.Equal(2, host.RigRegistry.GetAll().Select(r => r.MountId).Distinct().Count());
        Assert.Equal(2, host.RigRegistry.GetAll().Select(r => r.GuiderId).Distinct().Count());
    }

    [Fact]
    public async Task ARigWithoutAMountOrGuider_IsStillValid()
    {
        await using var host = await HostWithDevicesAsync();

        host.AddRig(new Rig(new("rig.a"), "A", new("camera.a")));

        Assert.Null(host.RigRegistry.GetAll().Single().MountId);
    }

    [Fact]
    public async Task AMountOrGuiderThatIsNotThere_OrOfTheWrongKind_IsRefused()
    {
        await using var host = await HostWithDevicesAsync();

        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new("a"), "A", new("camera.a"), mountId: new("mount.none"))));
        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new("a"), "A", new("camera.a"), guiderId: new("guider.none"))));
        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new("a"), "A", new("camera.a"), mountId: new("guider.1"))));
        Assert.Throws<InvalidOperationException>(() => host.AddRig(new Rig(new("a"), "A", new("camera.a"), guiderId: new("mount.1"))));
        Assert.Empty(host.RigRegistry.GetAll());
    }

    [Fact]
    public async Task AMountThatARigUses_CannotBeRemovedFromTheHost()
    {
        await using var host = await HostWithDevicesAsync();
        host.AddRig(new Rig(new("rig.a"), "A", new("camera.a"), mountId: new("mount.1"), guiderId: new("guider.1")));

        Assert.Throws<InvalidOperationException>(() => host.RemoveDevice(new("mount.1")));
        Assert.Throws<InvalidOperationException>(() => host.RemoveDevice(new("guider.1")));
        Assert.True(host.RemoveDevice(new("mount.2"))); // a mount no rig has
    }
}

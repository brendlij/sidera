using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Runtime.Resources;

namespace Sidera.Runtime.Tests.Resources;

/// <summary>Shared and exclusive claims: what is granted together, what waits, in which order, and what a claim profile of tracks says about who is affected.</summary>
public sealed class ResourceClaimTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly ResourceId Stability = ResourceId.ForMountStability(new DeviceId("mount.1"));
    private static readonly ResourceId CameraA = ResourceId.ForDevice(new DeviceId("camera.a"));
    private static readonly ResourceId CameraB = ResourceId.ForDevice(new DeviceId("camera.b"));

    private static async Task AssertWaitingAsync(Task task) => Assert.False(await Task.WhenAny(task, Task.Delay(60)) == task, "it was granted although it conflicts");

    [Fact]
    public async Task SharedClaims_OfOneResource_AreGrantedTogether()
    {
        var manager = new ResourceManager();

        using var a = await manager.AcquireClaimsAsync([ResourceClaim.Exclusive(CameraA), ResourceClaim.Shared(Stability)]).WaitAsync(Bound);
        using var b = await manager.AcquireClaimsAsync([ResourceClaim.Exclusive(CameraB), ResourceClaim.Shared(Stability)]).WaitAsync(Bound);

        Assert.Equal(2, manager.SharedHolders(Stability));
        Assert.True(manager.IsHeld(Stability));
        Assert.False(manager.IsHeldExclusively(Stability));
    }

    [Fact]
    public async Task AnExclusiveClaim_WaitsForTheSharedOnes_AndThenRunsAlone()
    {
        var manager = new ResourceManager();
        var exposure = await manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);
        var second = await manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);

        var move = manager.AcquireClaimsAsync([ResourceClaim.Exclusive(Stability)]);
        await AssertWaitingAsync(move);
        exposure.Dispose();
        await AssertWaitingAsync(move); // one is still exposing
        second.Dispose();

        using var lease = await move.WaitAsync(Bound);
        Assert.True(manager.IsHeldExclusively(Stability));
        Assert.Equal(0, manager.SharedHolders(Stability));
    }

    [Fact]
    public async Task AWaitingExclusiveClaim_KeepsNewSharedClaimsBack_SoItCannotStarve()
    {
        var manager = new ResourceManager();
        var running = await manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);
        var move = manager.AcquireClaimsAsync([ResourceClaim.Exclusive(Stability)]);
        await AssertWaitingAsync(move);

        var newExposure = manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);
        await AssertWaitingAsync(newExposure); // an exposure that wants to start waits behind the move that waits for the running one

        running.Dispose();
        using (await move.WaitAsync(Bound))
        {
            await AssertWaitingAsync(newExposure);
        }

        using var after = await newExposure.WaitAsync(Bound);
        Assert.Equal(1, manager.SharedHolders(Stability));
    }

    [Fact]
    public async Task ExclusiveClaims_OfUnrelatedResources_NeverWaitForEachOther()
    {
        var manager = new ResourceManager();
        using var a = await manager.AcquireClaimsAsync([ResourceClaim.Exclusive(CameraA)]);

        using var b = await manager.AcquireClaimsAsync([ResourceClaim.Exclusive(CameraB)]).WaitAsync(Bound);

        Assert.True(manager.IsHeld(CameraA) && manager.IsHeld(CameraB));
    }

    [Fact]
    public async Task ARequestIsAllOrNothing_AndAResourceNamedTwice_IsExclusive()
    {
        var manager = new ResourceManager();
        using var cameraHeld = await manager.AcquireClaimsAsync([ResourceClaim.Exclusive(CameraA)]);

        var both = manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability), ResourceClaim.Exclusive(CameraA)]);
        await AssertWaitingAsync(both);
        Assert.False(manager.IsHeld(Stability)); // nothing of it was taken while it waits

        using var twice = await manager.AcquireClaimsAsync([ResourceClaim.Shared(CameraB), ResourceClaim.Exclusive(CameraB)]).WaitAsync(Bound);
        Assert.True(manager.IsHeldExclusively(CameraB));
        Assert.Equal([ResourceClaim.Exclusive(CameraB)], twice.Claims);
    }

    [Fact]
    public async Task ACancelledRequest_LeavesNothingHeld_AndDoesNotHoldBackOthers()
    {
        var manager = new ResourceManager();
        var running = await manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);
        using var cts = new CancellationTokenSource();
        var move = manager.AcquireClaimsAsync([ResourceClaim.Exclusive(Stability)], cts.Token);
        var behind = manager.AcquireClaimsAsync([ResourceClaim.Shared(Stability)]);
        await AssertWaitingAsync(behind);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        using var granted = await behind.WaitAsync(Bound); // the cancelled move no longer blocks it
        running.Dispose();
        Assert.Equal(1, manager.SharedHolders(Stability));
    }

    [Fact]
    public async Task TheOldExclusiveOverload_StillHoldsEverythingAlone()
    {
        var manager = new ResourceManager();
        using var lease = await manager.AcquireAsync([CameraA, CameraB]);

        Assert.True(manager.IsHeldExclusively(CameraA) && manager.IsHeldExclusively(CameraB));
        var other = manager.AcquireClaimsAsync([ResourceClaim.Shared(CameraA)]);
        await AssertWaitingAsync(other);
    }

    // ---- who is affected

    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);

    private static Rig Setup(string name, string camera, string? focuser = null, string? mount = null, string? guider = null) =>
        new(new RigId("rig." + name), name, new DeviceId(camera), Optics, focuser is null ? null : new DeviceId(focuser), null, null, null, mount is null ? null : new DeviceId(mount), guider is null ? null : new DeviceId(guider));

    private static ClaimProfile Profile(Rig rig) => new(rig.Name, OperationClaims.ImagingSetup(rig, rig.MountId, rig.GuiderId));

    [Fact]
    public void TwoSetupsOnOneMount_AreBothAffectedByItsDither_AndAMoveOfThatMount()
    {
        var profiles = new[] { Profile(Setup("main", "camera.a", "focuser.a", "mount.1", "guider.1")), Profile(Setup("wide", "camera.b", "focuser.b", "mount.1", "guider.1")) };

        Assert.Equal([0, 1], ResourceClaimMatrix.Affected(profiles, OperationClaims.Dither(new DeviceId("mount.1"), new DeviceId("guider.1"))));
        Assert.Equal([0, 1], ResourceClaimMatrix.Affected(profiles, OperationClaims.MountMove(new DeviceId("mount.1"), null)));
    }

    [Fact]
    public void SetupsOnIndependentMounts_AreNotAffectedByEachOthersOperations()
    {
        var profiles = new[] { Profile(Setup("main", "camera.a", null, "mount.1", "guider.1")), Profile(Setup("wide", "camera.b", null, "mount.2", "guider.2")) };

        Assert.Equal([0], ResourceClaimMatrix.Affected(profiles, OperationClaims.Dither(new DeviceId("mount.1"), new DeviceId("guider.1"))));
        Assert.Equal([1], ResourceClaimMatrix.Affected(profiles, OperationClaims.MountMove(new DeviceId("mount.2"), null)));
        Assert.Equal([[0], [1]], ResourceClaimMatrix.Groups(profiles, r => r.Value.StartsWith("mountstability:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASharedGuider_AffectsEverySetupItGuides()
    {
        var profiles = new[] { Profile(Setup("main", "camera.a", null, "mount.1", "guider.1")), Profile(Setup("wide", "camera.b", null, "mount.2", "guider.1")), Profile(Setup("third", "camera.c", null, "mount.3", "guider.3")) };

        Assert.Equal([0, 1], ResourceClaimMatrix.Affected(profiles, OperationClaims.Dither(new DeviceId("mount.1"), new DeviceId("guider.1"))));
    }

    [Fact]
    public void AnAutofocus_AffectsNobodyElse_UnlessItHoldsTheStabilityOfTheMount()
    {
        var main = Setup("main", "camera.a", "focuser.a", "mount.1");
        var profiles = new[] { Profile(main), Profile(Setup("wide", "camera.b", "focuser.b", "mount.1")) };

        var independent = ResourceClaimMatrix.Affected(profiles, OperationClaims.Autofocus(main, null));
        var holding = ResourceClaimMatrix.Affected(profiles, OperationClaims.Autofocus(main, new DeviceId("mount.1")));

        Assert.Equal([0], independent); // only the setup that focuses: wide keeps exposing
        Assert.Equal([0, 1], holding);
    }

    [Fact]
    public void GroupsFollowTheMounts_AndASetupWithoutOneIsAGroupOfItsOwn()
    {
        var profiles = new[]
        {
            Profile(Setup("a", "camera.a", null, "mount.1")), Profile(Setup("b", "camera.b", null, "mount.2")), Profile(Setup("c", "camera.c", null, "mount.1")), Profile(Setup("d", "camera.d")),
        };

        var groups = ResourceClaimMatrix.Groups(profiles, r => r.Value.StartsWith("mountstability:", StringComparison.Ordinal));

        Assert.Equal([[0, 2], [1], [3]], groups);
    }
}

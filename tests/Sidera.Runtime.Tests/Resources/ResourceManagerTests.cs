using Sidera.Core.Devices;
using Sidera.Core.Resources;
using Sidera.Runtime.Resources;

namespace Sidera.Runtime.Tests.Resources;

public class ResourceManagerTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    private static readonly ResourceId CameraMain = new("device:camera.main");
    private static readonly ResourceId CameraWide = new("device:camera.wide");
    private static readonly ResourceId Mount = new("device:mount.eq6");

    // ResourceId

    [Fact]
    public void ResourceId_AcceptsValue_TrimsAndPrints()
    {
        var id = new ResourceId("  guiding ");

        Assert.Equal("guiding", id.Value);
        Assert.Equal("guiding", id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void ResourceId_RejectsEmptyOrWhitespace(string? value)
    {
        Assert.Throws<ArgumentException>(() => new ResourceId(value!));
    }

    [Fact]
    public void ResourceId_HasValueEquality()
    {
        Assert.Equal(new ResourceId("guiding"), new ResourceId(" guiding"));
        Assert.NotEqual(new ResourceId("guiding"), new ResourceId("dome"));
    }

    [Fact]
    public void ResourceId_ForDevice_ProducesStableId()
    {
        Assert.Equal(new ResourceId("device:camera.main"), ResourceId.ForDevice(new DeviceId("camera.main")));
        Assert.Equal("device:mount.eq6", ResourceId.ForDevice(new DeviceId(" mount.eq6 ")).Value);
    }

    // ResourceManager

    [Fact]
    public async Task Acquire_TakesResource_AndReleaseFreesIt()
    {
        var manager = new ResourceManager();

        var lease = await manager.AcquireAsync([CameraMain]);
        Assert.True(manager.IsHeld(CameraMain));

        lease.Dispose();
        Assert.False(manager.IsHeld(CameraMain));

        using var again = await manager.AcquireAsync([CameraMain]).WaitAsync(Bound);
        Assert.True(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task EmptyRequest_SucceedsImmediately()
    {
        var manager = new ResourceManager();

        var task = manager.AcquireAsync([]);

        Assert.True(task.IsCompletedSuccessfully);
        using var lease = await task;
        Assert.Empty(lease.Resources);
    }

    [Fact]
    public async Task DifferentResources_CanBeHeldAtTheSameTime()
    {
        var manager = new ResourceManager();

        using var main = await manager.AcquireAsync([CameraMain]);
        var wide = manager.AcquireAsync([CameraWide]);

        using var wideLease = await wide.WaitAsync(Bound);
        Assert.True(manager.IsHeld(CameraMain));
        Assert.True(manager.IsHeld(CameraWide));
    }

    [Fact]
    public async Task SameResource_SecondAcquisitionWaits_ThenProceedsAfterRelease()
    {
        var manager = new ResourceManager();
        var first = await manager.AcquireAsync([CameraMain]);

        var second = manager.AcquireAsync([CameraMain]);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, manager.WaitingCount);

        first.Dispose();
        using var lease = await second.WaitAsync(Bound);

        Assert.Equal(0, manager.WaitingCount);
        Assert.True(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task WaitingAcquisition_CanBeCancelled_AndTheHolderKeepsTheResource()
    {
        var manager = new ResourceManager();
        using var holder = await manager.AcquireAsync([CameraMain]);
        using var cts = new CancellationTokenSource();

        var waiting = manager.AcquireAsync([CameraMain], cts.Token);
        Assert.Equal(1, manager.WaitingCount);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(0, manager.WaitingCount);
        Assert.True(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task AlreadyCancelledToken_ThrowsWithoutWaiting()
    {
        var manager = new ResourceManager();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.AcquireAsync([CameraMain], cts.Token));

        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task DuplicateIdsInOneRequest_DoNotDeadlock()
    {
        var manager = new ResourceManager();

        using var lease = await manager.AcquireAsync([CameraMain, CameraMain, CameraMain]).WaitAsync(Bound);

        Assert.Equal(new[] { CameraMain }, lease.Resources);
    }

    [Fact]
    public async Task MultipleResources_AreAcquiredTogether_AndReleasedTogether()
    {
        var manager = new ResourceManager();

        var lease = await manager.AcquireAsync([Mount, CameraMain]).WaitAsync(Bound);

        Assert.True(manager.IsHeld(Mount));
        Assert.True(manager.IsHeld(CameraMain));
        lease.Dispose();
        Assert.False(manager.IsHeld(Mount));
        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task ReversedRequestOrder_DoesNotDeadlock()
    {
        var manager = new ResourceManager();
        var completed = 0;

        async Task Worker(ResourceId[] order)
        {
            for (var i = 0; i < 200; i++)
            {
                using var lease = await manager.AcquireAsync(order);
                Interlocked.Increment(ref completed);
                await Task.Yield();
            }
        }

        await Task.WhenAll(
            Task.Run(() => Worker([CameraMain, Mount])),
            Task.Run(() => Worker([Mount, CameraMain])),
            Task.Run(() => Worker([Mount, CameraMain])),
            Task.Run(() => Worker([CameraMain, Mount]))).WaitAsync(Bound);

        Assert.Equal(800, completed);
        Assert.False(manager.IsHeld(Mount));
        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task ResourcesAreReleased_WhenCallerFailsWhileHoldingTheLease()
    {
        var manager = new ResourceManager();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = await manager.AcquireAsync([CameraMain]);
            throw new InvalidOperationException("boom");
        });

        Assert.False(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndDoesNotReleaseSomeoneElsesHold()
    {
        var manager = new ResourceManager();
        var first = await manager.AcquireAsync([CameraMain]);
        first.Dispose();
        using var second = await manager.AcquireAsync([CameraMain]);

        first.Dispose(); // second disposal of the old lease must not free the new holder's resource

        Assert.True(manager.IsHeld(CameraMain));
    }

    [Fact]
    public async Task WaitingRequest_IsNotOvertakenByLaterRequestForTheSameResource()
    {
        var manager = new ResourceManager();
        var holder = await manager.AcquireAsync([Mount]);

        var bigWaiter = manager.AcquireAsync([Mount, CameraMain]);
        var laterWaiter = manager.AcquireAsync([CameraMain]); // free, but wanted by the earlier waiter

        Assert.False(bigWaiter.IsCompleted);
        Assert.False(laterWaiter.IsCompleted);

        holder.Dispose();
        var big = await bigWaiter.WaitAsync(Bound);
        Assert.False(laterWaiter.IsCompleted);

        big.Dispose();
        using var later = await laterWaiter.WaitAsync(Bound);
    }

    [Fact]
    public async Task UnrelatedRequest_IsNotBlockedByWaitingOnes()
    {
        var manager = new ResourceManager();
        using var holder = await manager.AcquireAsync([Mount]);
        var blocked = manager.AcquireAsync([Mount, CameraMain]);

        using var unrelated = await manager.AcquireAsync([CameraWide]).WaitAsync(Bound);

        Assert.False(blocked.IsCompleted);
    }

    [Fact]
    public async Task CancellingTheWaiterThatBlockedOthers_LetsThemProceed()
    {
        var manager = new ResourceManager();
        var holder = await manager.AcquireAsync([Mount]);
        using var cts = new CancellationTokenSource();
        var blocker = manager.AcquireAsync([Mount, CameraMain], cts.Token);
        var behind = manager.AcquireAsync([CameraMain]);
        Assert.False(behind.IsCompleted);

        await cts.CancelAsync();

        using var lease = await behind.WaitAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocker);
        holder.Dispose();
    }
}

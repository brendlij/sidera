using Sidera.Core.Coordination;
using Sidera.Core.Resources;
using Sidera.Runtime.Coordination;
using Sidera.Runtime.Resources;
using Microsoft.Extensions.Logging;

namespace Sidera.Runtime.Tests.Logging;

public class ResourceAndCoordinationLoggingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly ResourceId Camera = ResourceId.ForDevice(new Sidera.Core.Devices.DeviceId("camera.main"));
    private static readonly ResourceId Focuser = ResourceId.ForDevice(new Sidera.Core.Devices.DeviceId("focuser.main"));
    private static readonly CoordinationGroupId Group = new("session.mount.eq6");

    private static (ResourceManager Manager, LogCapture Log) CreateManager(TimeSpan? threshold = null)
    {
        var log = new LogCapture();
        return (new ResourceManager(log.Factory.CreateLogger<ResourceManager>(), threshold), log);
    }

    // ResourceManager

    [Fact]
    public async Task ARequest_IsLoggedAsRequestedGrantedAndReleased_WithTheResources()
    {
        var (manager, log) = CreateManager();

        using (await manager.AcquireAsync([Focuser, Camera]))
        {
        }

        var requested = log.Single(LogLevel.Debug, "requested");
        Assert.Equal("device:camera.main, device:focuser.main", requested.Properties["Resources"]); // sorted, as the manager takes them
        var granted = log.Single(LogLevel.Debug, "granted after");
        Assert.IsType<double>(granted.Properties["WaitMs"]);
        var released = log.Single(LogLevel.Debug, "released after");
        Assert.IsType<double>(released.Properties["HeldMs"]);
        Assert.DoesNotContain(log.Entries, e => e.Level > LogLevel.Debug);
    }

    [Fact]
    public async Task AnEmptyRequest_LogsNothing()
    {
        var (manager, log) = CreateManager();

        using (await manager.AcquireAsync([]))
        {
        }

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task ARequestThatWaitsTooLong_IsAWarningNamingWhatItWaitsFor_OnceOnly()
    {
        var (manager, log) = CreateManager(TimeSpan.FromMilliseconds(40));
        var holder = await manager.AcquireAsync([Camera]);

        var waiting = manager.AcquireAsync([Camera, Focuser]);
        var warning = await log.WaitForAsync(e => e.Level == LogLevel.Warning);
        holder.Dispose();
        using (await waiting.WaitAsync(Bound))
        {
        }

        Assert.StartsWith("Resource wait exceeded", warning.Message);
        Assert.Equal("device:camera.main, device:focuser.main", warning.Properties["Resources"]);
        Assert.Equal("device:camera.main", warning.Properties["HeldResources"]);
        Assert.True((double)warning.Properties["WaitMs"]! >= 40);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ARequestThatIsGrantedInTime_NeverWarns()
    {
        var (manager, log) = CreateManager(TimeSpan.FromMilliseconds(50));
        var holder = await manager.AcquireAsync([Camera]);

        var waiting = manager.AcquireAsync([Camera]);
        holder.Dispose();
        using (await waiting.WaitAsync(Bound))
        {
        }

        await Task.Delay(120); // longer than the threshold: the timer of a granted request must be gone
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ACancelledWait_IsDebug_NotAnErrorAndNotAWarning()
    {
        var (manager, log) = CreateManager();
        using var holder = await manager.AcquireAsync([Camera]);
        using var cts = new CancellationTokenSource();

        var waiting = manager.AcquireAsync([Camera], cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        log.Single(LogLevel.Debug, "cancelled after");
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public void TheWarningThreshold_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceManager(null, TimeSpan.Zero));
    }

    // SafePointCoordinator

    private static (SafePointCoordinator Coordinator, LogCapture Log) CreateCoordinator()
    {
        var log = new LogCapture();
        return (new SafePointCoordinator(log.Factory.CreateLogger<SafePointCoordinator>()), log);
    }

    [Fact]
    public async Task ACoordinatedOperation_IsLoggedFromRequestThroughArrivalToCompletion()
    {
        var (coordinator, log) = CreateCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 2);
        var operationRan = new TaskCompletionSource();

        var request = coordinator.ExecuteWhenSafeAsync(Group, ids[0], _ =>
        {
            operationRan.SetResult();
            return Task.CompletedTask;
        });
        var requested = await log.WaitForAsync(e => e.Message.Contains("coordinated operation requested"));
        Assert.Equal(1, requested.Properties["Required"]);
        Assert.Equal(0, requested.Properties["Arrived"]);
        Assert.Equal("session.mount.eq6", requested.Properties["CoordinationGroupId"]?.ToString());

        await coordinator.ReachSafePointAsync(Group, ids[1]).WaitAsync(Bound);
        await request.WaitAsync(Bound);

        var arrival = log.Single(LogLevel.Debug, "reached a safe point");
        Assert.Equal(ids[1].ToString(), arrival.Properties["ParticipantId"]?.ToString());
        Assert.Equal(1, arrival.Properties["Arrived"]);
        Assert.Equal(1, arrival.Properties["Required"]);
        var started = log.Single(LogLevel.Information, "the coordinated operation starts");
        var completed = log.Single(LogLevel.Information, "coordinated operation completed");
        Assert.IsType<double>(completed.Properties["DurationMs"]);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("participant") && e.Message.Contains("released"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("waiting participants released"));

        // The order of the trail: requested, arrived, started, completed.
        var order = log.Entries.Select(e => e.Message).ToList();
        Assert.True(order.IndexOf(requested.Message) < order.IndexOf(arrival.Message));
        Assert.True(order.IndexOf(arrival.Message) < order.IndexOf(started.Message));
        Assert.True(order.IndexOf(started.Message) < order.IndexOf(completed.Message));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ARoundCalledOffBecauseAParticipantFailed_IsAWarning()
    {
        var (coordinator, log) = CreateCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 2);

        var request = coordinator.ExecuteWhenSafeAsync(Group, ids[0], _ => Task.CompletedTask);
        await log.WaitForAsync(e => e.Message.Contains("coordinated operation requested"));
        coordinator.Unregister(Group, ids[1], failed: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
        var warning = log.Single(LogLevel.Warning, "is called off");
        Assert.Equal(ids[1].ToString(), warning.Properties["ParticipantId"]?.ToString());
    }

    [Fact]
    public async Task ACancelledRound_IsInformation_AndAFailingOperationAWarning()
    {
        var (coordinator, log) = CreateCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 2);
        using var cts = new CancellationTokenSource();

        var cancelled = coordinator.ExecuteWhenSafeAsync(Group, ids[0], _ => Task.CompletedTask, cts.Token);
        await log.WaitForAsync(e => e.Message.Contains("coordinated operation requested"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        log.Single(LogLevel.Information, "coordinated operation cancelled (while waiting for safe points)");

        var alone = coordinator.RegisterParticipants(new CoordinationGroupId("other"), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ExecuteWhenSafeAsync(new CoordinationGroupId("other"), alone[0], _ => throw new InvalidOperationException("dither failed")));
        var warning = log.Single(LogLevel.Warning, "coordinated operation failed");
        Assert.Equal("dither failed", warning.Properties["Reason"]);
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Error);
    }
}

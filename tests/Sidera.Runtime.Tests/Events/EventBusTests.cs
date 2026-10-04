using Sidera.Core.Devices;
using Sidera.Runtime.Events;

namespace Sidera.Runtime.Tests.Events;

public class EventBusTests
{
    private static readonly DeviceConnectionStateChanged SampleEvent = new(
        new DeviceId("cam-1"),
        DeviceConnectionState.Disconnected,
        DeviceConnectionState.Connected
    );

    [Fact]
    public async Task Subscriber_ReceivesPublishedEvent()
    {
        var bus = new EventBus();
        DeviceConnectionStateChanged? received = null;
        bus.Subscribe<DeviceConnectionStateChanged>((e, _) =>
        {
            received = e;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(SampleEvent);

        Assert.Same(SampleEvent, received);
    }

    [Fact]
    public async Task AllSubscribers_ReceiveEvent()
    {
        var bus = new EventBus();
        var count = 0;
        Task Handler(DeviceConnectionStateChanged _, CancellationToken __)
        {
            count++;
            return Task.CompletedTask;
        }
        bus.Subscribe<DeviceConnectionStateChanged>(Handler);
        bus.Subscribe<DeviceConnectionStateChanged>(Handler);

        await bus.PublishAsync(SampleEvent);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UnsubscribedHandler_NoLongerReceivesEvents()
    {
        var bus = new EventBus();
        var count = 0;
        var subscription = bus.Subscribe<DeviceConnectionStateChanged>((_, _) =>
        {
            count++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(SampleEvent);
        subscription.Dispose();
        await bus.PublishAsync(SampleEvent);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PublishAsync_ThrowsWhenCancelled()
    {
        var bus = new EventBus();
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => Task.CompletedTask);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bus.PublishAsync(SampleEvent, cts.Token));
    }

    [Fact]
    public async Task ThrowingHandler_DoesNotBreakPublishOrOtherHandlers()
    {
        var failures = new List<EventHandlerFailure>();
        var bus = new EventBus(failures.Add);
        var delivered = 0;
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => throw new InvalidOperationException("sync"));
        bus.Subscribe<DeviceConnectionStateChanged>(async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("async");
        });
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) =>
        {
            delivered++;
            return Task.CompletedTask;
        });

        await bus.PublishAsync(SampleEvent);

        Assert.Equal(1, delivered);
        Assert.Equal(new[] { "sync", "async" }, failures.Select(f => f.Exception.Message));
        Assert.All(failures, f =>
        {
            Assert.Equal(typeof(DeviceConnectionStateChanged), f.EventType);
            Assert.Same(SampleEvent, f.Event);
        });
    }

    [Fact]
    public async Task ThrowingFailureObserver_DoesNotBreakPublish()
    {
        var bus = new EventBus(_ => throw new InvalidOperationException("observer"));
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => throw new InvalidOperationException("handler"));

        await bus.PublishAsync(SampleEvent);
    }
}

using Sidera.Core.Devices;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Events;

namespace Sidera.Runtime.Tests.Devices;

public class SimulatedCameraTests
{
    private static (SimulatedCamera Camera, List<DeviceConnectionStateChanged> Events) Create()
    {
        var bus = new EventBus();
        var events = new List<DeviceConnectionStateChanged>();
        bus.Subscribe<DeviceConnectionStateChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });

        return (new SimulatedCamera(new DeviceId("cam-1"), events: bus), events);
    }

    [Fact]
    public async Task ConnectAndDisconnect_PublishStateChanges()
    {
        var (camera, events) = Create();

        await camera.ConnectAsync();
        await camera.DisconnectAsync();

        Assert.Equal(
            new[]
            {
                (DeviceConnectionState.Disconnected, DeviceConnectionState.Connecting),
                (DeviceConnectionState.Connecting, DeviceConnectionState.Connected),
                (DeviceConnectionState.Connected, DeviceConnectionState.Disconnecting),
                (DeviceConnectionState.Disconnecting, DeviceConnectionState.Disconnected),
            },
            events.Select(e => (e.PreviousState, e.NewState)));
        Assert.All(events, e => Assert.Equal(camera.Id, e.DeviceId));
    }

    [Fact]
    public async Task CancelledConnect_PublishesReturnToDisconnected()
    {
        var (camera, events) = Create();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => camera.ConnectAsync(cts.Token));

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        var last = events[^1];
        Assert.Equal(DeviceConnectionState.Connecting, last.PreviousState);
        Assert.Equal(DeviceConnectionState.Disconnected, last.NewState);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotAffectConnectOrDisconnect()
    {
        var failures = new List<EventHandlerFailure>();
        var bus = new EventBus(failures.Add);
        bus.Subscribe<DeviceConnectionStateChanged>((_, _) => throw new InvalidOperationException("boom"));
        var camera = new SimulatedCamera(new DeviceId("cam-1"), events: bus);

        await camera.ConnectAsync();
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);

        await camera.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        Assert.Equal(4, failures.Count);
    }

    private static async Task<(SimulatedCamera Camera, List<CameraExposureStateChanged> Events)> CreateConnected()
    {
        var bus = new EventBus();
        var events = new List<CameraExposureStateChanged>();
        bus.Subscribe<CameraExposureStateChanged>((e, _) =>
        {
            events.Add(e);
            return Task.CompletedTask;
        });
        var camera = new SimulatedCamera(new DeviceId("cam-1"), events: bus);
        await camera.ConnectAsync();
        return (camera, events);
    }

    [Fact]
    public async Task Exposure_PublishesEventWhenStarting()
    {
        var (camera, events) = await CreateConnected();

        var exposure = camera.ExposeAsync(TimeSpan.FromMilliseconds(200));

        var started = Assert.Single(events);
        Assert.Equal(CameraExposureState.Idle, started.PreviousState);
        Assert.Equal(CameraExposureState.Exposing, started.NewState);
        Assert.Equal(camera.Id, started.DeviceId);
        await exposure;
    }

    [Fact]
    public async Task Exposure_PublishesEventWhenFinishing()
    {
        var (camera, events) = await CreateConnected();

        await camera.ExposeAsync(TimeSpan.FromMilliseconds(20));

        Assert.Equal(
            new[]
            {
                (CameraExposureState.Idle, CameraExposureState.Exposing),
                (CameraExposureState.Exposing, CameraExposureState.Idle),
            },
            events.Select(e => (e.PreviousState, e.NewState)));
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }

    [Fact]
    public async Task CancelledExposure_ReturnsToIdleAndPublishes()
    {
        var (camera, events) = await CreateConnected();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            camera.ExposeAsync(TimeSpan.FromSeconds(10), cts.Token));

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureState.Exposing, events[^2].NewState);
        Assert.Equal(CameraExposureState.Idle, events[^1].NewState);
        Assert.Equal(CameraExposureState.Exposing, events[^1].PreviousState);
    }

    [Fact]
    public async Task Disconnect_WhileExposing_IsRejected()
    {
        var (camera, _) = await CreateConnected();
        var exposure = camera.ExposeAsync(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        await exposure;
        await camera.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task Exposure_ReturnsFrameWithExpectedShape()
    {
        var (camera, _) = await CreateConnected();
        var duration = TimeSpan.FromMilliseconds(50);

        var frame = await camera.ExposeAsync(duration);

        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);
        Assert.Equal(800 * 600, frame.Pixels.Length);
        Assert.Equal(duration, frame.ExposureDuration);
        Assert.Contains(frame.Pixels.ToArray(), p => p != 0);
        Assert.Contains(frame.Pixels.ToArray(), p => p > 10_000); // at least one star
    }

    [Fact]
    public async Task SameSeed_ProducesIdenticalFrames()
    {
        var first = new SimulatedCamera(new DeviceId("a"), seed: 7);
        var second = new SimulatedCamera(new DeviceId("b"), seed: 7);
        await first.ConnectAsync();
        await second.ConnectAsync();

        var frameA = await first.ExposeAsync(TimeSpan.FromMilliseconds(20));
        var frameB = await second.ExposeAsync(TimeSpan.FromMilliseconds(20));

        Assert.True(frameA.Pixels.Span.SequenceEqual(frameB.Pixels.Span));
    }

    [Fact]
    public async Task CancelledExposure_ProducesNoFrame()
    {
        var (camera, _) = await CreateConnected();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(10), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.True(exposure.IsCanceled);
    }

    [Fact]
    public async Task Progress_StartsAtZero()
    {
        var (camera, _) = await CreateConnected();

        Assert.Equal(0.0, camera.ExposureProgress);
        Assert.Equal(TimeSpan.Zero, camera.ExposureElapsed);
    }

    [Fact]
    public async Task Progress_IncreasesDuringExposureAndReachesOne()
    {
        var (camera, _) = await CreateConnected();
        var samples = new List<double>();
        camera.ExposureProgressChanged += (_, _) => samples.Add(camera.ExposureProgress);

        await camera.ExposeAsync(TimeSpan.FromMilliseconds(700));

        Assert.True(samples.Count >= 3, $"expected several updates, got {samples.Count}");
        Assert.Equal(samples.OrderBy(s => s), samples);
        Assert.Contains(samples, s => s > 0.0 && s < 1.0);
        Assert.Equal(1.0, camera.ExposureProgress);
        Assert.Equal(TimeSpan.FromMilliseconds(700), camera.ExposureElapsed);
    }

    [Fact]
    public async Task Progress_ResetsWhenNewExposureStarts()
    {
        var (camera, _) = await CreateConnected();
        await camera.ExposeAsync(TimeSpan.FromMilliseconds(50));

        var second = camera.ExposeAsync(TimeSpan.FromMilliseconds(300));

        Assert.True(camera.ExposureProgress < 0.5);
        Assert.True(camera.ExposureElapsed < TimeSpan.FromMilliseconds(150));
        await second;
    }

    [Fact]
    public async Task CancelledExposure_ReportsActualProgress()
    {
        var (camera, _) = await CreateConnected();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            camera.ExposeAsync(TimeSpan.FromSeconds(10), cts.Token));

        Assert.InRange(camera.ExposureElapsed, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(3));
        Assert.True(camera.ExposureProgress < 0.5);
    }

    [Fact]
    public async Task ThrowingProgressObserver_DoesNotBreakExposure()
    {
        var (camera, _) = await CreateConnected();
        camera.ExposureProgressChanged += (_, _) => throw new InvalidOperationException("boom");

        await camera.ExposeAsync(TimeSpan.FromMilliseconds(250));

        Assert.Equal(1.0, camera.ExposureProgress);
    }

    [Fact]
    public async Task WithoutPublisher_ConnectStillWorks()
    {
        var camera = new SimulatedCamera(new DeviceId("cam-1"));

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
    }
}

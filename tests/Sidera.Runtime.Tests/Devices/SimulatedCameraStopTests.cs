using Sidera.Core.Devices;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Tests.Devices;

/// <summary>The simulated camera keeps the same contract as the ASCOM one: stop keeps the image, abort and cancel throw it away.</summary>
public class SimulatedCameraStopTests
{
    private static async Task<SimulatedCamera> Connected()
    {
        var camera = new SimulatedCamera(new DeviceId("cam-1"));
        await camera.ConnectAsync();
        return camera;
    }

    private static async Task WaitUntilExposing(ICamera camera)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (camera.ExposureState != CameraExposureState.Exposing)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(2);
        }
    }

    [Fact]
    public async Task TheSimulator_SaysItCanStopAndAbort()
    {
        var camera = await Connected();

        Assert.True(camera.Capabilities.Value!.CanStopExposure);
        Assert.True(camera.Capabilities.Value.CanAbortExposure);
    }

    [Fact]
    public async Task Stop_ReturnsTheFrameOfTheShortenedExposure_MarkedStopped_AndTheCameraIsIdleAndUsable()
    {
        var camera = await Connected();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(30));
        await WaitUntilExposing(camera);
        await Task.Delay(50);

        await camera.StopExposureAsync();
        var frame = await exposure;

        Assert.True(frame.Acquisition!.Stopped);
        Assert.InRange(frame.Acquisition.StoppedAfter!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(30), frame.ExposureDuration);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Stopped, camera.LastOutcome);

        var next = await camera.ExposeAsync(TimeSpan.FromMilliseconds(30));
        Assert.False(next.Acquisition!.Stopped);
        Assert.Equal(CameraExposureOutcome.Completed, camera.LastOutcome);
    }

    [Fact]
    public async Task Abort_EndsTheExposureWithNoFrame_AndTheCameraIsUsable()
    {
        var camera = await Connected();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(30));
        await WaitUntilExposing(camera);

        await camera.AbortExposureAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Aborted, camera.LastOutcome);
        Assert.NotNull(await camera.ExposeAsync(TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task Cancelling_IsAnAbort()
    {
        var camera = await Connected();
        using var cts = new CancellationTokenSource();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(30), cts.Token);
        await WaitUntilExposing(camera);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(CameraExposureOutcome.Aborted, camera.LastOutcome);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }

    [Fact]
    public async Task StopAndAbort_WithoutAnExposure_AreRefused()
    {
        var camera = await Connected();

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.StopExposureAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.AbortExposureAsync());
    }

    [Fact]
    public async Task ADisconnectDuringAnExposure_IsRefused()
    {
        var camera = await Connected();
        using var cts = new CancellationTokenSource();
        var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(30), cts.Token);
        await WaitUntilExposing(camera);

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
    }
}

using Sidera.Ascom.Cameras;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;
using Sidera.Runtime.Tests.Logging;
using Microsoft.Extensions.Logging;

namespace Sidera.Ascom.Tests;

/// <summary>The ASCOM camera adapter against a fake driver: light frames, orientation, rejection of what is not understood.</summary>
public class CameraTests
{
    private sealed record Rig(AscomCamera Camera, FakeDriverFactory Drivers, CallLog Log, EventSink Events, LogCapture Logs);

    private static async Task<Rig> Connected(Action<FakeCameraDriver>? configure = null, AscomTimings? timings = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureCamera = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var camera = new AscomCamera(
            new DeviceId("camera.test"), "Test Camera", "ASCOM.Test.Camera", drivers, events,
            logs.Factory.CreateLogger("camera"), timings ?? FastTimings.Create());
        await camera.ConnectAsync();
        return new Rig(camera, drivers, log, events, logs);
    }

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Connecting_ReadsTheSensorOnTheStaThread()
    {
        var rig = await Connected();

        Assert.Equal(DeviceConnectionState.Connected, rig.Camera.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        rig.Log.AssertOneStaThread();
        Assert.Equal(DeviceType.Camera, rig.Camera.Type);
        Assert.Equal("ASCOM.Test.Camera", ((IBackendDescribed)rig.Camera).DriverId);
    }

    [Fact]
    public async Task ACameraWithoutASensorSize_IsRefusedAtConnect_AndReleased()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureCamera = d => d.CameraXSize = 0 };
        var camera = new AscomCamera(new DeviceId("camera.test"), "Test Camera", "ASCOM.Test.Camera", drivers);

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => camera.ConnectAsync());

        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
        Assert.True(Assert.Single(drivers.Cameras).Disposed);
    }

    [Fact]
    public async Task AnExposure_IsOneLightExposure_ThenPolling_ThenOneReadOfTheImage()
    {
        var rig = await Connected();

        var frame = await rig.Camera.ExposeAsync(TimeSpan.FromSeconds(2.5));

        var driver = rig.Drivers.Cameras[0];
        var start = Assert.Single(driver.Starts);
        Assert.Equal((2.5, true), start); // true: a light frame, not a dark
        Assert.Equal(1, rig.Log.Count("get ImageArray"));
        Assert.True(rig.Log.Count("get ImageReady") >= 2);
        Assert.Equal(TimeSpan.FromSeconds(2.5), frame.ExposureDuration);
        Assert.Equal((4, 3), (frame.Width, frame.Height));
        rig.Log.AssertOneStaThread();
    }

    [Fact]
    public async Task TheFrame_HasRowsOfTheImage_WithTheFirstArrayIndexAsX()
    {
        var rig = await Connected();

        var frame = await rig.Camera.ExposeAsync(Second);

        var pixels = frame.Pixels.ToArray();
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                Assert.Equal((ushort)(10 * x + y), pixels[y * 4 + x]);
            }
        }
    }

    [Fact]
    public async Task AFrameOfAnotherSize_KeepsItsOrientation()
    {
        var image = new int[6, 2];
        image[5, 1] = 1234;
        var rig = await Connected(d => (d.Image, d.NumX, d.NumY, d.CameraXSize, d.CameraYSize) = (image, 6, 2, 6, 2));

        var frame = await rig.Camera.ExposeAsync(Second);

        Assert.Equal((6, 2), (frame.Width, frame.Height));
        Assert.Equal(1234, frame.Pixels.Span[1 * 6 + 5]);
    }

    [Fact]
    public async Task TheStateEventsAndTheProgress_FollowTheExposure()
    {
        var rig = await Connected(d => d.ExposePolls = 5);
        var progress = new List<double>();
        rig.Camera.ExposureProgressChanged += (_, _) => progress.Add(rig.Camera.ExposureProgress);

        await rig.Camera.ExposeAsync(TimeSpan.FromMilliseconds(100));

        Assert.Equal(
            [(CameraExposureState.Idle, CameraExposureState.Exposing), (CameraExposureState.Exposing, CameraExposureState.Idle)],
            rig.Events.Of<CameraExposureStateChanged>().Select(e => (e.PreviousState, e.NewState)));
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.True(progress.Count >= 3);
        Assert.Equal(progress.Order(), progress); // never goes backwards
        Assert.Equal(1.0, rig.Camera.ExposureProgress, 3);
    }

    [Fact]
    public async Task ADurationThatIsNotPositive_IsRefused()
    {
        var rig = await Connected();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Camera.ExposeAsync(TimeSpan.Zero));
        Assert.Empty(rig.Drivers.Cameras[0].Starts);
    }

    [Fact]
    public async Task ACameraThatIsNotConnected_RefusesToExpose()
    {
        var camera = new AscomCamera(new DeviceId("camera.x"), "X", "ASCOM.X", new FakeDriverFactory(new CallLog()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.ExposeAsync(Second));
    }

    [Fact]
    public async Task ASecondExposureWhileOneRuns_IsRefused()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        var first = rig.Camera.ExposeAsync(Second);
        await WaitUntil(() => rig.Drivers.Cameras[0].Starts.Count == 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Single(rig.Drivers.Cameras[0].Starts);
        rig.Drivers.Cameras[0].HoldExposure = false;
        await first;
    }

    [Fact]
    public async Task DisconnectingDuringAnExposure_IsRefused()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        var exposure = rig.Camera.ExposeAsync(Second);
        await WaitUntil(() => rig.Drivers.Cameras[0].Starts.Count == 1);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Camera.DisconnectAsync());

        Assert.Contains("while an exposure is running", failure.Message);
        rig.Drivers.Cameras[0].HoldExposure = false;
        await exposure;
    }

    // Stopping

    [Fact]
    public async Task Cancelling_CallsAbortExposure_AndSaysItWasConfirmed()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        using var cts = new CancellationTokenSource();
        var exposure = rig.Camera.ExposeAsync(Second, cts.Token);
        await WaitUntil(() => rig.Drivers.Cameras[0].Starts.Count == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(1, rig.Log.Count("AbortExposure"));
        Assert.Equal(0, rig.Log.Count("get ImageArray")); // no image of a cancelled exposure
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(CameraExposureState.Idle, rig.Events.Of<CameraExposureStateChanged>().Last().NewState);
        Assert.NotEmpty(rig.Logs.Containing("Test Camera confirmed idle after the cancelled exposure"));
    }

    [Fact]
    public async Task ACameraThatCannotAbort_IsNeverCalledToAbort_NorClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanAbort) = (true, false));
        using var cts = new CancellationTokenSource();
        var exposure = rig.Camera.ExposeAsync(Second, cts.Token);
        await WaitUntil(() => rig.Drivers.Cameras[0].Starts.Count == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(0, rig.Log.Count("AbortExposure"));
        Assert.NotEmpty(rig.Logs.Containing("cannot abort an exposure"));
        Assert.NotEmpty(rig.Logs.Containing("could not be confirmed stopped"));
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
    }

    [Fact]
    public async Task AnAbortThatFails_IsNotClaimedToHaveStopped()
    {
        var rig = await Connected(d => (d.HoldExposure, d.AbortThrows) = (true, new ASCOM.InvalidOperationException("busy")));
        using var cts = new CancellationTokenSource();
        var exposure = rig.Camera.ExposeAsync(Second, cts.Token);
        await WaitUntil(() => rig.Drivers.Cameras[0].Starts.Count == 1);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.NotEmpty(rig.Logs.Containing("could not be confirmed stopped"));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("AbortExposure did not work"));
    }

    [Fact]
    public async Task AnExposureThatNeverDeliversAnImage_TimesOut_AndIsAborted()
    {
        var rig = await Connected(d => d.HoldExposure = true, FastTimings.Create(downloadMargin: TimeSpan.FromMilliseconds(60)));

        var failure = await Assert.ThrowsAsync<AscomTimeoutException>(() => rig.Camera.ExposeAsync(TimeSpan.FromMilliseconds(50)));

        Assert.Contains("did not deliver an image", failure.Message);
        Assert.Contains("accepted AbortExposure", failure.Message);
        Assert.Single(rig.Drivers.Cameras[0].Starts); // started once, never again
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
    }

    [Fact]
    public async Task ACameraInItsErrorState_FailsTheExposure_NotAfterTheTimeout()
    {
        var rig = await Connected(d => (d.HoldExposure, d.ErrorState) = (true, true));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Contains("error state", failure.Message);
        Assert.Single(rig.Drivers.Cameras[0].Starts);
    }

    [Fact]
    public async Task AFailingStart_IsTranslated_AndNotRetried()
    {
        var rig = await Connected(d => d.StartThrows = new ASCOM.InvalidValueException("StartExposure", "100000", "0 to 3600"));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Contains("Could not start the exposure of Test Camera", failure.Message);
        Assert.Equal(1, rig.Log.Calls.Count(c => c.Name.StartsWith("StartExposure")));
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
    }

    // What the driver returns

    [Theory]
    [MemberData(nameof(RejectedImages))]
    public async Task AnImageThatIsNotUnderstood_FailsTheExposure_WithAShortMessage_AndADetailedLogEntry(object image, string shortMessage)
    {
        var rig = await Connected(d => d.Image = image);

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Contains(shortMessage, failure.Message);
        Assert.IsType<ImageConversionException>(failure.InnerException);
        var entry = Assert.Single(rig.Logs.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("the image was rejected"));
        Assert.Contains(image.GetType().ToString(), entry.Message);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(0, rig.Log.Count("AbortExposure")); // the exposure had finished; nothing to abort
    }

    public static TheoryData<object, string> RejectedImages() => new()
    {
        { new short[4, 3], "Int16" },
        { new double[4, 3], "Double" },
        { new int[4, 3, 3], "rank 3" },
        { new int[3, 4], "announced 4 x 3" },
    };

    [Fact]
    public async Task ADriverThatReturnsNoImage_FailsTheExposure()
    {
        var rig = await Connected(d => d.Image = null);

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Contains("no image", failure.Message);
    }

    [Fact]
    public async Task AnImageArrayThatTheDriverCannotGive_IsTranslated()
    {
        var rig = await Connected(d => d.ImageThrows = new ASCOM.InvalidOperationException("no image available"));

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ExposeAsync(Second));

        Assert.Contains("Could not read the image of Test Camera", failure.Message);
    }

    [Fact]
    public async Task ValuesBeyondTheRange_AreClamped_AndTheLogSaysHowMany()
    {
        var image = new int[4, 3];
        image[0, 0] = -5;
        image[1, 1] = 70000;
        var rig = await Connected(d => d.Image = image);

        var frame = await rig.Camera.ExposeAsync(Second);

        Assert.Equal(0, frame.Pixels.Span[0]);
        Assert.Equal(65535, frame.Pixels.Span[1 * 4 + 1]);
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("2 of 12 pixel values"));
    }

    [Fact]
    public async Task ACameraWithALowerMaxAdu_IsWarnedAbout_AndNotScaled()
    {
        var image = new int[4, 3];
        image[2, 2] = 4095;
        var rig = await Connected(d => (d.MaxAduValue, d.Image) = (4095, image));

        var frame = await rig.Camera.ExposeAsync(Second);

        Assert.Equal(4095, frame.Pixels.Span[2 * 4 + 2]);
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MaxADU of 4095"));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }
}

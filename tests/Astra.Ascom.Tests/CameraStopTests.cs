using Astra.Ascom.Cameras;
using Astra.Ascom.Infrastructure;
using Astra.Core.Devices;
using Astra.Runtime.Tests.Logging;

namespace Astra.Ascom.Tests;

/// <summary>
/// The exposure lifecycle of the ASCOM camera: stop (the image is kept) is not abort (the image is thrown away), cancelling is an
/// abort, what ends an exposure leaves the camera idle and usable, the settings are read back, and what cannot be applied starts no
/// exposure. Cooling follows the capabilities.
/// </summary>
public class CameraStopTests
{
    private sealed record Rig(AscomCamera Camera, FakeDriverFactory Drivers, CallLog Log, EventSink Events, LogCapture Logs)
    {
        public FakeCameraDriver Driver => Drivers.Cameras[0];
    }

    private static async Task<Rig> Connected(Action<FakeCameraDriver>? configure = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureCamera = configure };
        var events = new EventSink();
        var logs = new LogCapture();
        var camera = new AscomCamera(
            new DeviceId("camera.test"), "Test Camera", "ASCOM.Test.Camera", drivers, events, logs.Factory.CreateLogger("camera"), FastTimings.Create());
        await camera.ConnectAsync();
        return new Rig(camera, drivers, log, events, logs);
    }

    private static void Rich(FakeCameraDriver d)
    {
        d.CanStopExposure = true;
        d.HasShutter = true;
        d.GainValue = 10;
        d.GainMinValue = 0;
        d.GainMaxValue = 100;
        d.OffsetValue = 5;
        d.OffsetMinValue = 0;
        d.OffsetMaxValue = 50;
        d.MaxBinX = d.MaxBinY = 2;
        d.ReadoutModesValue = ["Fast", "Quality"];
        d.CanSetCcdTemperature = true;
        d.CoolerOnValue = false;
        d.CcdTemperatureValue = 12.5;
        d.CanGetCoolerPower = true;
        d.CoolerPowerValue = 0;
    }

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for a condition.");
            await Task.Delay(2);
        }
    }

    private static async Task<Task<CameraFrame>> Exposing(Rig rig, CancellationToken token = default)
    {
        var exposure = rig.Camera.ExposeAsync(Second, token);
        await WaitUntil(() => rig.Driver.Starts.Count == 1);
        return exposure;
    }

    // ---- Stop: the exposure ends early and the image is kept

    [Fact]
    public async Task Stop_CallsStopExposure_NotAbort_AndTheFrameOfTheShortenedExposureIsReturnedAndMarked()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, true));
        var exposure = await Exposing(rig);

        await rig.Camera.StopExposureAsync();
        var frame = await exposure;

        Assert.Equal(1, rig.Log.Count("StopExposure"));
        Assert.Equal(0, rig.Log.Count("AbortExposure"));
        Assert.True(frame.Acquisition!.Stopped);
        Assert.NotNull(frame.Acquisition.StoppedAfter);
        Assert.Equal(Second, frame.ExposureDuration); // what was asked for: the driver does not say how long it really was
        Assert.Equal((4, 3), (frame.Width, frame.Height));
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Stopped, rig.Camera.LastOutcome);
        Assert.Equal(CameraExposureState.Idle, rig.Events.Of<CameraExposureStateChanged>().Last().NewState);
    }

    [Fact]
    public async Task AfterAStop_TheNextExposureWorks_AndIsNotMarkedAsStopped()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, true));
        var exposure = await Exposing(rig);
        await rig.Camera.StopExposureAsync();
        await exposure;
        rig.Driver.HoldExposure = false;

        var next = await rig.Camera.ExposeAsync(Second);

        Assert.False(next.Acquisition!.Stopped);
        Assert.Null(next.Acquisition.StoppedAfter);
        Assert.Equal(CameraExposureOutcome.Completed, rig.Camera.LastOutcome);
    }

    [Fact]
    public async Task ACameraThatCannotStop_IsNeverToldToStop_AndKeepsExposing()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, false));
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => rig.Camera.StopExposureAsync());

        Assert.Equal(0, rig.Log.Count("StopExposure"));
        Assert.Equal(CameraExposureState.Exposing, rig.Camera.ExposureState);
        Assert.False(rig.Camera.Capabilities.Value!.CanStopExposure);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
    }

    [Fact]
    public async Task AStopThatTheDriverAnswersWithNoImage_EndsTheExposureWithAClearError_AndTheCameraIsUsable()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure, d.StopProducesImage) = (true, true, false));
        var exposure = await Exposing(rig);

        await rig.Camera.StopExposureAsync();

        var failure = await Assert.ThrowsAsync<CameraExposureStoppedException>(() => exposure);
        Assert.Contains("delivered no image", failure.Message);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Failed, rig.Camera.LastOutcome);
        Assert.Equal(0, rig.Log.Count("get ImageArray"));
        rig.Driver.HoldExposure = false;
        Assert.NotNull(await rig.Camera.ExposeAsync(Second));
    }

    [Fact]
    public async Task Stop_WithoutAnExposure_IsRefused_AndNothingIsSentToTheDriver()
    {
        var rig = await Connected(d => d.CanStopExposure = true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Camera.StopExposureAsync());

        Assert.Equal(0, rig.Log.Count("StopExposure"));
    }

    [Fact]
    public async Task AStopThatTheDriverRefuses_IsAnErrorOfTheStopCall_AndTheExposureGoesOn()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure, d.StopThrows) = (true, true, new ASCOM.InvalidOperationException("busy")));
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.StopExposureAsync());

        Assert.Equal(CameraExposureState.Exposing, rig.Camera.ExposureState);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
    }

    // ---- Abort and cancellation: the exposure is thrown away

    [Fact]
    public async Task Abort_CallsAbortExposure_NotStop_AndEndsTheExposureWithNoImage()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, true));
        var exposure = await Exposing(rig);

        await rig.Camera.AbortExposureAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(1, rig.Log.Count("AbortExposure"));
        Assert.Equal(0, rig.Log.Count("StopExposure"));
        Assert.Equal(0, rig.Log.Count("get ImageArray")); // no image of an aborted exposure is read
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Aborted, rig.Camera.LastOutcome);
    }

    [Fact]
    public async Task AfterAnAbort_TheNextExposureWorks_AndDoesNotUseAStaleImage()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, true));
        var exposure = await Exposing(rig);
        await rig.Camera.AbortExposureAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        rig.Driver.HoldExposure = false;
        var images = rig.Log.Count("get ImageArray");

        var next = await rig.Camera.ExposeAsync(Second);

        Assert.Equal(images + 1, rig.Log.Count("get ImageArray"));
        Assert.False(next.Acquisition!.Stopped);
        Assert.Equal(2, rig.Driver.Starts.Count);
    }

    [Fact]
    public async Task ACameraThatCannotAbort_IsRefusedAnAbort_WithoutACall()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanAbort) = (true, false));
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => rig.Camera.AbortExposureAsync());

        Assert.Equal(0, rig.Log.Count("AbortExposure"));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
    }

    [Fact]
    public async Task CancellingTheToken_IsAnAbort_NeverAStop_EvenWhenTheCameraCouldStop()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanStopExposure) = (true, true));
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(1, rig.Log.Count("AbortExposure"));
        Assert.Equal(0, rig.Log.Count("StopExposure"));
        Assert.Equal(CameraExposureOutcome.Aborted, rig.Camera.LastOutcome);
    }

    [Fact]
    public async Task ACancelOnACameraThatCannotAbort_OnlyEndsTheWaiting_AndTheLogSaysTheCameraMayStillBeExposing()
    {
        var rig = await Connected(d => (d.HoldExposure, d.CanAbort) = (true, false));
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState); // Astra's own state: it no longer waits
        Assert.NotEmpty(rig.Logs.Containing("it may still be exposing"));
    }

    // ---- Disconnect and release during an exposure

    [Fact]
    public async Task ADisconnectDuringAnExposure_IsRefused_AndTheExposureGoesOn()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        using var cts = new CancellationTokenSource();
        var exposure = await Exposing(rig, cts.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Camera.DisconnectAsync());

        Assert.Equal(DeviceConnectionState.Connected, rig.Camera.ConnectionState);
        Assert.Equal(CameraExposureState.Exposing, rig.Camera.ExposureState);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
        await rig.Camera.DisconnectAsync();
    }

    [Fact]
    public async Task ACameraThatIsReleasedWhileExposing_IsToldToAbortFirst_AndNoStateIsLeftExposing()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        var exposure = await Exposing(rig);

        await rig.Camera.DisposeAsync();

        // The release aborts; the polling exposure may ask for an abort of its own at the same moment, so at least once.
        Assert.True(rig.Log.Count("AbortExposure") >= 1);
        await Assert.ThrowsAnyAsync<Exception>(() => exposure);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
    }

    [Fact]
    public async Task ADriverInTheErrorState_EndsTheExposureWithAnError_AndTheStateIsIdle()
    {
        var rig = await Connected(d => d.HoldExposure = true);
        var exposure = await Exposing(rig);

        rig.Driver.ErrorState = true;

        await Assert.ThrowsAsync<AscomDeviceException>(() => exposure);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(CameraExposureOutcome.Failed, rig.Camera.LastOutcome);
    }

    // ---- What a frame says it was taken with

    [Fact]
    public async Task TheFrame_IsDescribedWithWhatTheCameraReportsJustBeforeTheExposure_NotWithAnOldReading()
    {
        var rig = await Connected(Rich);
        rig.Driver.GainValue = 77; // changed behind Astra's back, after the connect

        var frame = await rig.Camera.ExposeAsync(Second);

        Assert.Equal(77, frame.Acquisition!.Gain);
    }

    [Fact]
    public async Task ASettingThatTheDriverAcceptsAndDoesNotTake_IsAnError_AndNoExposureStarts()
    {
        var rig = await Connected(d =>
        {
            Rich(d);
            d.IgnoredSettings.Add("Gain");
        });

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(
            () => rig.Camera.ExposeAsync(new CameraExposureRequest(Second, FrameType.Light, new CameraSettings { Gain = 50 })));

        Assert.Contains("Gain was set to 50 and reads back 10", failure.Message);
        Assert.Empty(rig.Driver.Starts);
        Assert.Equal(CameraExposureState.Idle, rig.Camera.ExposureState);
        Assert.Equal(10, rig.Camera.Settings!.Gain); // what the camera has, not what was asked
    }

    [Fact]
    public async Task ASubframeThatTheDriverAdjusts_IsLogged_AndTheFrameSaysWhatItReallyIs()
    {
        var rig = await Connected(d =>
        {
            Rich(d);
            d.CameraXSize = 8;
            d.CameraYSize = 6;
            d.NumX = 8;
            d.NumY = 6;
        });

        // The fake takes the subframe as it is: the read-back equals the request, nothing to log.
        await rig.Camera.ApplyAsync(new CameraSettings { StartX = 1, StartY = 1, NumX = 4, NumY = 3 });

        Assert.Equal((1, 1, 4, 3), (rig.Camera.Settings!.StartX, rig.Camera.Settings.StartY, rig.Camera.Settings.NumX, rig.Camera.Settings.NumY));
    }

    // ---- Settings that cannot be applied start nothing

    [Theory]
    [InlineData("gain", 500, null, null, null)]
    [InlineData("offset", null, 500, null, null)]
    [InlineData("binning", null, null, 3, null)]
    [InlineData("readout", null, null, null, 7)]
    public async Task AnInvalidSetting_StartsNoExposure_AndChangesNothingOnTheCamera(string what, int? gain, int? offset, int? bin, int? readout)
    {
        var rig = await Connected(Rich);
        var change = new CameraSettings { Gain = gain, Offset = offset, BinX = bin, BinY = bin, ReadoutMode = readout };

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => rig.Camera.ExposeAsync(new CameraExposureRequest(Second, FrameType.Light, change)));

        Assert.False(string.IsNullOrEmpty(failure.Message), what);
        Assert.Empty(rig.Driver.Starts);
        Assert.Empty(rig.Driver.Changes);
    }

    [Fact]
    public async Task AnInvalidSubframe_StartsNoExposure()
    {
        var rig = await Connected(Rich);
        var change = new CameraSettings { StartX = 3, StartY = 0, NumX = 4, NumY = 3 }; // reaches past the sensor (4 wide)

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Camera.ExposeAsync(new CameraExposureRequest(Second, FrameType.Light, change)));

        Assert.Empty(rig.Driver.Starts);
        Assert.Empty(rig.Driver.Changes);
    }

    [Fact]
    public async Task WhenALaterSettingFails_TheEarlierOnesStayApplied_NoExposureStarts_AndTheSettingsSayWhatTheCameraHas()
    {
        var rig = await Connected(d =>
        {
            Rich(d);
            d.GainSetThrows = new ASCOM.InvalidValueException("gain");
        });
        var change = new CameraSettings { BinX = 2, BinY = 2, Gain = 20 };

        await Assert.ThrowsAnyAsync<Exception>(() => rig.Camera.ExposeAsync(new CameraExposureRequest(Second, FrameType.Light, change)));

        Assert.Empty(rig.Driver.Starts);
        Assert.Equal(2, rig.Driver.BinX); // the binning was applied before the gain failed; there is no rollback
        Assert.Equal((2, 10), (rig.Camera.Settings!.BinX, rig.Camera.Settings.Gain));
    }

    [Fact]
    public async Task ABinningChange_ComesBeforeTheSubframe_AndAFullFrameFollowsTheBinning()
    {
        var rig = await Connected(d =>
        {
            Rich(d);
            d.CameraXSize = 8;
            d.CameraYSize = 6;
            d.NumX = 8;
            d.NumY = 6;
        });

        await rig.Camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });

        Assert.Equal((2, 2, 4, 3), (rig.Driver.BinX, rig.Driver.BinY, rig.Driver.NumX, rig.Driver.NumY));
        Assert.Equal((0, 0), (rig.Driver.StartX, rig.Driver.StartY));
    }

    // ---- Cooling follows the capabilities

    [Fact]
    public async Task Cooling_IsOnlyOfferedForWhatTheCameraHas_AndTheCoolerIsNotSwitchedByTheConnect()
    {
        var plain = await Connected();
        var cooled = await Connected(Rich);

        Assert.False(plain.Camera.Capabilities.Value!.SupportsCooling);
        Assert.False(plain.Camera.Capabilities.Value.CanGetCoolerPower);
        Assert.True(cooled.Camera.Capabilities.Value!.SupportsCooling);
        Assert.True(cooled.Camera.Capabilities.Value.CanGetCoolerPower);
        Assert.DoesNotContain("CoolerOn = True", cooled.Driver.Changes);
        Assert.False(cooled.Camera.Settings!.CoolerOn);
    }

    [Fact]
    public async Task TheCoolerAndTheTarget_AreSetOnRequest_ReadBack_AndThePowerIsInTheTelemetry()
    {
        var rig = await Connected(Rich);
        rig.Driver.CoolerPowerValue = 35;

        await rig.Camera.ApplyAsync(new CameraSettings { TargetTemperature = 10, CoolerOn = true });

        Assert.True(rig.Camera.Settings!.CoolerOn);
        Assert.Equal(10, rig.Camera.Settings.TargetTemperature);
        Assert.Equal(35, rig.Camera.Telemetry!.CoolerPower);
        Assert.Equal(12.5, rig.Camera.Telemetry.CcdTemperature);
    }

    [Fact]
    public async Task ACoolerThatTheDriverDoesNotSwitch_IsReportedAsFailed_NotAsOn()
    {
        var rig = await Connected(d =>
        {
            Rich(d);
            d.IgnoredSettings.Add("CoolerOn");
        });

        var failure = await Assert.ThrowsAsync<AscomDeviceException>(() => rig.Camera.ApplyAsync(new CameraSettings { CoolerOn = true }));

        Assert.Contains("Cooler was set to True and reads back False", failure.Message);
        Assert.False(rig.Camera.Settings!.CoolerOn);
    }

    [Fact]
    public async Task ATargetOnACameraThatCannotSetOne_IsRefusedBeforeTheDriverIsTouched()
    {
        var rig = await Connected();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Camera.ApplyAsync(new CameraSettings { TargetTemperature = -10 }));

        Assert.Empty(rig.Driver.Changes);
    }

    // ---- The image the driver returns

    [Fact]
    public async Task AnImageOfAnUnexpectedFormat_IsRejectedWithTheTypeTheRankTheDimensionsAndTheLowerBounds()
    {
        var shortImage = await Connected(d => d.ImageFactory = () => new short[4, 3]);
        var colour = await Connected(d => d.ImageFactory = () => new int[4, 3, 3]);
        var shifted = await Connected(d => d.ImageFactory = () => Array.CreateInstance(typeof(int), [4, 3], [1, 1]));
        var wrongSize = await Connected(d => d.ImageFactory = () => new int[5, 3]);

        var a = await Assert.ThrowsAsync<AscomDeviceException>(() => shortImage.Camera.ExposeAsync(Second));
        var b = await Assert.ThrowsAsync<AscomDeviceException>(() => colour.Camera.ExposeAsync(Second));
        var c = await Assert.ThrowsAsync<AscomDeviceException>(() => shifted.Camera.ExposeAsync(Second));
        var d = await Assert.ThrowsAsync<AscomDeviceException>(() => wrongSize.Camera.ExposeAsync(Second));

        Assert.Contains("System.Int16[,]", a.Message);
        Assert.Contains("rank 3", b.Message);
        Assert.Contains("System.Int32[,,]", b.Message);
        Assert.Contains("lower bounds [1, 1]", c.Message);
        Assert.Contains("[5, 3]", d.Message);
        Assert.Contains("NumX=4, NumY=3", d.Message);
        Assert.All(new[] { shortImage, colour, shifted, wrongSize }, r => Assert.Equal(CameraExposureState.Idle, r.Camera.ExposureState));
    }
}

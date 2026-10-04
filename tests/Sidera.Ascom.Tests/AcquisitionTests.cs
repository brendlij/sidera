using Sidera.Ascom.Cameras;
using Sidera.Ascom.Infrastructure;
using Sidera.Core.Devices;

namespace Sidera.Ascom.Tests;

/// <summary>
/// The ASCOM camera taking an exposure together with its acquisition settings: applied first, as one operation with the
/// exposure; a setting that cannot be applied stops the exposure; the frame type decides the light flag; the frame says what it was
/// taken with. Against a fake driver.
/// </summary>
public class AcquisitionTests
{
    private static async Task<(AscomCamera Camera, FakeDriverFactory Drivers, CallLog Log)> Connected(Action<FakeCameraDriver>? configure = null)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log)
        {
            ConfigureCamera = d =>
            {
                d.CameraXSize = 8;
                d.CameraYSize = 6;
                d.NumX = 8;
                d.NumY = 6;
                d.Image = new int[8, 6];
                d.GainValue = 10;
                d.GainMinValue = 0;
                d.GainMaxValue = 100;
                d.OffsetValue = 5;
                d.OffsetMinValue = 0;
                d.OffsetMaxValue = 50;
                d.MaxBinX = d.MaxBinY = 4;
                d.ReadoutModesValue = ["Normal", "Slow"];
                d.HasShutter = true;
                configure?.Invoke(d);
            },
        };
        var camera = new AscomCamera(new DeviceId("camera.acq"), "Acq Camera", "ASCOM.Test.Camera", drivers, null, null, FastTimings.Create());
        await camera.ConnectAsync();
        return (camera, drivers, log);
    }

    private static int IndexOf(CallLog log, string prefix) => log.Names.ToList().FindIndex(n => n.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task TheSettingsAreAppliedBeforeTheExposureIsStarted()
    {
        var (camera, _, log) = await Connected();

        await camera.ExposeAsync(new CameraExposureRequest(
            TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings { Gain = 50, Offset = 7, ReadoutMode = 1 }));

        var gain = IndexOf(log, "Gain = 50");
        var offset = IndexOf(log, "Offset = 7");
        var start = IndexOf(log, "StartExposure");
        Assert.True(gain >= 0 && offset >= 0 && start > gain && start > offset);
        Assert.Equal(1, log.Count("StartExposure 1 light=True"));
    }

    [Fact]
    public async Task BinningIsAppliedBeforeTheSubframe_AndTheFrameHasTheSizeOfTheRegion()
    {
        var (camera, drivers, _) = await Connected(d => d.Image = new int[4, 3]);

        var frame = await camera.ExposeAsync(new CameraExposureRequest(
            TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings { BinX = 2, BinY = 2, StartX = 0, StartY = 0, NumX = 4, NumY = 3 }));

        Assert.Equal((4, 3), (frame.Width, frame.Height));
        Assert.Equal((2, 2, 4, 3), (drivers.Cameras[0].BinX, drivers.Cameras[0].BinY, drivers.Cameras[0].NumX, drivers.Cameras[0].NumY));
        Assert.Equal((2, 2), (frame.Acquisition!.BinX, frame.Acquisition.BinY));
        Assert.Equal((4, 3), (frame.Acquisition.Width, frame.Acquisition.Height));
    }

    [Fact]
    public async Task ASettingThatTheDriverRejects_StopsTheExposure_AndWhatWasNotReachedIsNotApplied()
    {
        var (camera, _, log) = await Connected(d => d.GainSetThrows = new InvalidOperationException("gain refused"));

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => camera.ExposeAsync(new CameraExposureRequest(
            TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings { Gain = 50, Offset = 9 })));

        Assert.Contains("gain refused", failure.Message);
        Assert.Equal(-1, IndexOf(log, "StartExposure"));
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(5, camera.Settings!.Offset); // the failing part came first, so the later one was never reached
    }

    [Fact]
    public async Task ASettingTheCameraDoesNotSupport_IsRefusedWithoutTouchingTheDriver()
    {
        var (camera, drivers, log) = await Connected();

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => camera.ExposeAsync(new CameraExposureRequest(
            TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings { Gain = 500 })));

        Assert.Contains("Gain must be", failure.Message);
        Assert.Equal(-1, IndexOf(log, "StartExposure"));
        Assert.Empty(drivers.Cameras[0].Changes);
    }

    [Theory]
    [InlineData(FrameType.Light, true)]
    [InlineData(FrameType.Flat, true)]
    [InlineData(FrameType.Dark, false)]
    [InlineData(FrameType.Bias, false)]
    public async Task TheFrameTypeDecidesTheLightFlag_AndIsPartOfTheFrameMetadata(FrameType type, bool light)
    {
        var (camera, drivers, _) = await Connected();

        var frame = await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(1), type, new CameraSettings()));

        Assert.Equal(light, drivers.Cameras[0].Starts[0].Light);
        Assert.Equal(type, frame.Acquisition!.FrameType);
    }

    [Fact]
    public async Task ADarkOrABiasOfACameraWithoutAShutter_IsRefusedBeforeAnythingHappens()
    {
        var (camera, drivers, log) = await Connected(d => d.HasShutter = false);

        await Assert.ThrowsAsync<AscomUnsupportedException>(() => camera.ExposeAsync(
            new CameraExposureRequest(TimeSpan.FromSeconds(1), FrameType.Dark, new CameraSettings { Gain = 50 })));

        Assert.Equal(-1, IndexOf(log, "StartExposure"));
        Assert.Empty(drivers.Cameras[0].Changes);
        var light = await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings()));
        Assert.Equal(FrameType.Light, light.Acquisition!.FrameType);
    }

    [Fact]
    public async Task TheFrameSaysWhatItWasTakenWith_IncludingTheNamesOfListsAndTheReadoutMode()
    {
        var (camera, _, _) = await Connected(d =>
        {
            d.GainMinValue = d.GainMaxValue = null;
            d.GainsValue = ["Low", "High"];
            d.GainValue = 0;
        });

        var frame = await camera.ExposeAsync(new CameraExposureRequest(
            TimeSpan.FromSeconds(2), FrameType.Light, new CameraSettings { Gain = 1, ReadoutMode = 1 }));

        Assert.Equal(("High", 1), (frame.Acquisition!.GainName, frame.Acquisition.Gain));
        Assert.Equal("Slow", frame.Acquisition.ReadoutMode);
        Assert.Equal(TimeSpan.FromSeconds(2), frame.ExposureDuration);
    }

    [Fact]
    public async Task AnExposureWithoutSettings_ReadsAsItAlwaysDid_AndTouchesNoSetting()
    {
        var (camera, drivers, log) = await Connected();

        var frame = await camera.ExposeAsync(TimeSpan.FromSeconds(1));

        Assert.Empty(drivers.Cameras[0].Changes);
        Assert.Equal(1, log.Count("StartExposure 1 light=True"));
        Assert.Equal(FrameType.Light, frame.Acquisition!.FrameType);
    }

    [Fact]
    public async Task TheCameraIsNotRestoredAfterTheExposure()
    {
        var (camera, drivers, _) = await Connected();

        await camera.ExposeAsync(new CameraExposureRequest(TimeSpan.FromSeconds(1), FrameType.Light, new CameraSettings { Gain = 40 }));

        Assert.Equal(40, drivers.Cameras[0].GainValue);
        Assert.Equal(40, camera.Settings!.Gain);
        Assert.Equal(["Gain = 40"], drivers.Cameras[0].Changes);
    }
}

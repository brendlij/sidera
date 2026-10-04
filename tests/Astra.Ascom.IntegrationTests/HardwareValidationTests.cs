using Astra.Ascom.Cameras;
using Astra.Ascom.Drivers;
using Astra.Ascom.Focusers;
using Astra.Ascom.Infrastructure;
using Astra.Core.Devices;
using Astra.Core.Focusers;
using Xunit.Abstractions;

namespace Astra.Ascom.IntegrationTests;

/// <summary>
/// The real camera and focuser, end to end through Astra's adapters. Every test leaves the device the way it found it: the
/// acquisition settings of the camera are put back, the focuser returns to its starting position. Short exposures only, small
/// moves only. A test runs when the variable naming the device is set (<c>ASTRA_ASCOM_CAMERA</c>, <c>ASTRA_ASCOM_FOCUSER</c>).
/// </summary>
public sealed class HardwareValidationTests(ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private AscomCamera NewCamera() => new(new DeviceId("camera.real"), "Real Camera", Env("ASTRA_ASCOM_CAMERA"), Drivers);

    private static CameraExposureRequest Request(double seconds, CameraSettings? change = null, FrameType type = FrameType.Light) =>
        new(TimeSpan.FromSeconds(seconds), type, change ?? new CameraSettings());

    private static (int Min, int Max, double Mean) Stats(CameraFrame frame)
    {
        int min = int.MaxValue, max = 0;
        long sum = 0;
        foreach (var p in frame.Pixels.Span)
        {
            min = Math.Min(min, p);
            max = Math.Max(max, p);
            sum += p;
        }

        return (min, max, (double)sum / frame.Pixels.Length);
    }

    // The settings that describe the frame, as the camera reports them now; what a test puts back.
    private static CameraSettings Snapshot(CameraSettings s) => new()
    {
        Gain = s.Gain,
        Offset = s.Offset,
        BinX = s.BinX,
        BinY = s.BinY,
        StartX = s.StartX,
        StartY = s.StartY,
        NumX = s.NumX,
        NumY = s.NumY,
        ReadoutMode = s.ReadoutMode,
        FastReadout = s.FastReadout,
    };

    // Back to the full frame first (the subframe is in binned pixels, so binning and subframe go together), then the rest.
    private static CameraSettings RestoreOf(CameraSettings original) => Snapshot(original);

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ConnectsDisconnectsAndReconnects_WithTheSameCapabilities()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        var first = camera.Capabilities.Value!;
        output.WriteLine($"connected: {first.Driver.Name} {first.SensorWidth}x{first.SensorHeight} maxADU {first.MaxAdu} bin {first.MaxBinX}x{first.MaxBinY} " +
            $"gain {first.Gain?.Minimum}..{first.Gain?.Maximum} offset {first.Offset?.Minimum}..{first.Offset?.Maximum} readout [{string.Join(",", first.ReadoutModes)}] " +
            $"fast {first.CanFastReadout} abort {first.CanAbortExposure} stop {first.CanStopExposure} cooling {first.SupportsCooling} power {first.CanGetCoolerPower}");
        var t = camera.Telemetry;
        output.WriteLine($"telemetry: temp {t?.CcdTemperature} power {t?.CoolerPower} cooler {t?.CoolerOn}");
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);

        await camera.DisconnectAsync();
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);

        await camera.ConnectAsync();
        try
        {
            Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
            Assert.Equal(first.SensorWidth, camera.Capabilities.Value!.SensorWidth);
            Assert.Equal(first.MaxAdu, camera.Capabilities.Value!.MaxAdu);
            Assert.Equal(first.Gain, camera.Capabilities.Value!.Gain);
            Assert.Equal(first.CanAbortExposure, camera.Capabilities.Value!.CanAbortExposure);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ImageArray_HasTheRuntimeTypeAstraConverts()
    {
        using var dispatcher = new AscomDispatcher("validation raw camera");
        IAscomCameraDriver? driver = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                driver = Drivers.CreateCamera(Env("ASTRA_ASCOM_CAMERA"));
                driver.Connected = true;
                driver.StartExposure(0.1, true);
            });
            for (var i = 0; i < 100 && !await dispatcher.InvokeAsync(() => driver!.ImageReady); i++)
            {
                await Task.Delay(100);
            }

            var description = await dispatcher.InvokeAsync(() =>
            {
                var array = driver!.ImageArray;
                var type = array?.GetType().FullName ?? "null";
                var rank = (array as Array)?.Rank ?? 0;
                var dims = array is Array a ? string.Join("x", Enumerable.Range(0, a.Rank).Select(a.GetLength)) : "-";
                var lower = array is Array b ? string.Join(",", Enumerable.Range(0, b.Rank).Select(b.GetLowerBound)) : "-";
                return $"{type} rank {rank} dims {dims} lower bounds {lower} imageReady {driver.ImageReady}";
            });
            output.WriteLine("raw ImageArray of the driver: " + description);
            Assert.StartsWith("System.Int32[,]", description);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (driver is not null)
                {
                    driver.Connected = false;
                    driver.Dispose();
                }
            });
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_Exposes_AFrameWithSanePixelsAndMetadata()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        try
        {
            var caps = camera.Capabilities.Value!;
            var frame = await camera.ExposeAsync(Request(0.2));
            var (min, max, mean) = Stats(frame);
            output.WriteLine($"0.2 s light: {frame.Width}x{frame.Height}, pixels min {min} max {max} mean {mean:0.0}, acquisition {frame.Acquisition}");
            Assert.Equal(caps.SensorWidth, frame.Width);
            Assert.Equal(caps.SensorHeight, frame.Height);
            Assert.InRange(max, 1, caps.MaxAdu);
            Assert.Equal(FrameType.Light, frame.Acquisition!.FrameType);
            Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ExposesSeveralTimesInARow_AndCancellationStillWorksAfterwards()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        try
        {
            var caps = camera.Capabilities.Value!;
            for (var i = 1; i <= 5; i++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var frame = await camera.ExposeAsync(Request(0.1));
                var (_, max, mean) = Stats(frame);
                output.WriteLine($"#{i}: {frame.Width}x{frame.Height} max {max} mean {mean:0.0} in {clock.ElapsedMilliseconds} ms, state {camera.ExposureState}");
                Assert.Equal((caps.SensorWidth, caps.SensorHeight), (frame.Width, frame.Height));
                Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
            }

            using var cts = new CancellationTokenSource();
            var long_ = camera.ExposeAsync(Request(8), cts.Token);
            await Task.Delay(1500);
            Assert.Equal(CameraExposureState.Exposing, camera.ExposureState);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => long_);
            output.WriteLine($"after the abort the state is {camera.ExposureState}");
            Assert.Equal(CameraExposureState.Idle, camera.ExposureState);

            var after = await camera.ExposeAsync(Request(0.1));
            output.WriteLine($"exposure after the abort: {after.Width}x{after.Height}");
            Assert.Equal(caps.SensorWidth, after.Width);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_AppliesEachSettingOnItsOwn_ReadsItBack_ExposesWithIt_AndIsRestored()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        var original = Snapshot(camera.Settings!);
        output.WriteLine($"original settings: {original}");
        try
        {
            var caps = camera.Capabilities.Value!;

            // Gain
            var gain = original.Gain == 100 ? 120 : 100;
            await camera.ApplyAsync(new CameraSettings { Gain = gain });
            Assert.Equal(gain, camera.Settings!.Gain);
            var frame = await camera.ExposeAsync(Request(0.1));
            output.WriteLine($"gain {gain}: read back {camera.Settings!.Gain}, frame {frame.Width}x{frame.Height}, acquisition gain {frame.Acquisition?.Gain}");
            Assert.Equal(gain, frame.Acquisition!.Gain);
            await camera.ApplyAsync(new CameraSettings { Gain = original.Gain });
            Assert.Equal(original.Gain, camera.Settings!.Gain);

            // Offset
            var offset = original.Offset == 30 ? 20 : 30;
            await camera.ApplyAsync(new CameraSettings { Offset = offset });
            Assert.Equal(offset, camera.Settings!.Offset);
            frame = await camera.ExposeAsync(Request(0.1));
            output.WriteLine($"offset {offset}: read back {camera.Settings!.Offset}, acquisition offset {frame.Acquisition?.Offset}");
            Assert.Equal(offset, frame.Acquisition!.Offset);
            await camera.ApplyAsync(new CameraSettings { Offset = original.Offset });
            Assert.Equal(original.Offset, camera.Settings!.Offset);

            // Binning 2x2
            await camera.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });
            frame = await camera.ExposeAsync(Request(0.1));
            output.WriteLine($"bin 2x2: read back {camera.Settings!.BinX}x{camera.Settings.BinY}, frame {frame.Width}x{frame.Height}, acquisition bin {frame.Acquisition?.BinX}x{frame.Acquisition?.BinY}");
            Assert.Equal((caps.SensorWidth / 2, caps.SensorHeight / 2), (frame.Width, frame.Height));
            await camera.ApplyAsync(RestoreOf(original));
            Assert.Equal((original.BinX, original.NumX, original.NumY), (camera.Settings!.BinX, camera.Settings.NumX, camera.Settings.NumY));

            // Subframe
            await camera.ApplyAsync(new CameraSettings { StartX = 1000, StartY = 800, NumX = 640, NumY = 480 });
            frame = await camera.ExposeAsync(Request(0.1));
            output.WriteLine($"subframe 1000,800 640x480: read back {camera.Settings!.StartX},{camera.Settings.StartY} {camera.Settings.NumX}x{camera.Settings.NumY}, frame {frame.Width}x{frame.Height}");
            Assert.Equal((640, 480), (frame.Width, frame.Height));
            await camera.ApplyAsync(RestoreOf(original));
            Assert.Equal((original.StartX, original.StartY, original.NumX, original.NumY),
                (camera.Settings!.StartX, camera.Settings.StartY, camera.Settings.NumX, camera.Settings.NumY));

            // Readout mode and fast readout: only when the driver has a choice
            output.WriteLine($"readout modes [{string.Join(",", caps.ReadoutModes)}], fast readout {caps.CanFastReadout}: " +
                (caps.ReadoutModes.Count > 1 || caps.CanFastReadout ? "tested elsewhere" : "nothing to choose, NOT SUPPORTED"));
        }
        finally
        {
            await camera.ApplyAsync(RestoreOf(original));
            output.WriteLine($"restored settings: {Snapshot(camera.Settings!)}");
            await camera.DisconnectAsync();
        }

        Assert.Equal(original, Snapshot(camera.Settings ?? original));
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_AppliesACombinedRequest_AndTheFrameFollows_AndIsRestored()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        var original = Snapshot(camera.Settings!);
        try
        {
            var change = new CameraSettings { Gain = 100, Offset = 30, BinX = 2, BinY = 2, StartX = 100, StartY = 100, NumX = 800, NumY = 600 };
            var frame = await camera.ExposeAsync(Request(0.1, change));
            output.WriteLine($"combined: frame {frame.Width}x{frame.Height}, acquisition {frame.Acquisition}, settings {Snapshot(camera.Settings!)}");
            Assert.Equal((800, 600), (frame.Width, frame.Height));
            Assert.Equal((100, 30, 2, 2), (frame.Acquisition!.Gain, frame.Acquisition.Offset, frame.Acquisition.BinX, frame.Acquisition.BinY));
        }
        finally
        {
            await camera.ApplyAsync(RestoreOf(original));
            output.WriteLine($"restored settings: {Snapshot(camera.Settings!)}");
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_RefusesAnInvalidSetting_WithoutExposing()
    {
        var camera = NewCamera();
        await camera.ConnectAsync();
        try
        {
            var caps = camera.Capabilities.Value!;
            var before = Snapshot(camera.Settings!);
            await Assert.ThrowsAnyAsync<Exception>(() => camera.ExposeAsync(Request(0.1, new CameraSettings { BinX = caps.MaxBinX + 1, BinY = caps.MaxBinX + 1 })));
            Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
            Assert.Equal(before, Snapshot(camera.Settings!));
            output.WriteLine("an invalid binning was refused, no exposure started, the settings did not change");
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_ReportsItself_MovesByTheTwoPathsAndComesBack()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", Env("ASTRA_ASCOM_FOCUSER"), Drivers, logger: new OutputLogger(output));
        await focuser.ConnectAsync();
        var start = focuser.Position;
        try
        {
            var c = focuser.Capabilities.Value!;
            output.WriteLine($"focuser: absolute {c.Absolute} max {c.MaxStep} maxIncrement {c.MaxIncrement} stepSize {c.StepSizeMicrons?.ToString() ?? "none"} " +
                $"temperature {c.HasTemperature} ({focuser.Telemetry?.Temperature}) tempComp available {c.TempCompAvailable}, position {start}");
            Assert.True(c.Absolute);
            Assert.True(focuser.IsAbsolute);

            var target = start + 200 <= focuser.MaxPosition ? start + 200 : start - 200;
            await focuser.MoveToAsync(target);
            output.WriteLine($"MoveTo {target}: now {focuser.Position}, state {focuser.MotionState}");
            Assert.Equal(target, focuser.Position);
            Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);

            await focuser.MoveToAsync(start);
            Assert.Equal(start, focuser.Position);

            var by = start + 150 <= focuser.MaxPosition ? 150 : -150;
            await focuser.MoveByAsync(by);
            output.WriteLine($"MoveBy {by}: now {focuser.Position} (expected {start + by})");
            Assert.Equal(start + by, focuser.Position);
            await focuser.MoveByAsync(-by);
            Assert.Equal(start, focuser.Position);
        }
        finally
        {
            if (focuser.Position != start)
            {
                await focuser.MoveToAsync(start);
            }

            await focuser.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_Halt_StopsAMove_AndTheFocuserStaysUsable()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", Env("ASTRA_ASCOM_FOCUSER"), Drivers, logger: new OutputLogger(output));
        await focuser.ConnectAsync();
        var start = focuser.Position;
        try
        {
            var far = start + 2500 <= focuser.MaxPosition ? start + 2500 : start - 2500;
            using var cts = new CancellationTokenSource();
            var move = focuser.MoveToAsync(far, cts.Token);
            await Task.Delay(1500);
            Assert.Equal(FocuserMotionState.Moving, focuser.MotionState);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            var stopped = focuser.Position;
            await Task.Delay(500);
            output.WriteLine($"asked for {far} from {start}; halted at {stopped}; half a second later {focuser.Position}; state {focuser.MotionState}");
            Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
            Assert.Equal(stopped, focuser.Position);
            Assert.NotEqual(far, stopped);
        }
        finally
        {
            await focuser.MoveToAsync(start);
            output.WriteLine($"back at {focuser.Position} (started at {start})");
            Assert.Equal(start, focuser.Position);
            await focuser.DisconnectAsync();
        }
    }
}

internal sealed class OutputLogger(ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => output.WriteLine($"  log {logLevel}: {formatter(state, exception)}");
}

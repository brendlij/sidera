using Astra.Ascom.Drivers;
using Astra.Ascom.Infrastructure;
using Xunit.Abstractions;

namespace Astra.Ascom.IntegrationTests;

/// <summary>
/// Diagnostics of real drivers through the raw driver wrappers on a dispatcher of their own: what the driver says itself, not what
/// Astra makes of it. This class only reads. Each test runs when the variable naming the device is set
/// (<c>ASTRA_ASCOM_CAMERA</c>, <c>ASTRA_ASCOM_FOCUSER</c>, <c>ASTRA_ASCOM_MOUNT</c>). The classes below it are the investigations
/// behind the findings of the hardware validation: they expose, move the focuser by a few thousand steps, or switch the camera's
/// offset, and put everything back.
/// </summary>
public sealed class HardwareDiagnosticsTests(ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    private static string Show<T>(Func<T> read)
    {
        try
        {
            return Convert.ToString(read(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}: {ex.Message.Split('\n', 2)[0]}>";
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_TelemetryAtConnectAndAfterAWhile_ReadOnly()
    {
        using var dispatcher = new AscomDispatcher("diagnostics mount");
        IAscomMountDriver? mount = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                mount = Drivers.CreateMount(Env("ASTRA_ASCOM_MOUNT"));
                mount.Connected = true;
            });
            for (var round = 0; round < 3; round++)
            {
                var text = await dispatcher.InvokeAsync(() =>
                {
                    var m = mount!;
                    return string.Join(
                        " | ",
                        $"Connected={Show(() => m.Connected)}",
                        $"RA={Show(() => m.RightAscension)}",
                        $"Dec={Show(() => m.Declination)}",
                        $"Alt={Show(() => m.Altitude)}",
                        $"Az={Show(() => m.Azimuth)}",
                        $"LST={Show(() => m.SiderealTime)}",
                        $"Lat={Show(() => m.SiteLatitude)}",
                        $"Lon={Show(() => m.SiteLongitude)}",
                        $"Elev={Show(() => m.SiteElevation)}",
                        $"UTC={Show(() => m.UtcDate.ToString("o"))}",
                        $"PC-UTC={DateTime.UtcNow:o}",
                        $"Tracking={Show(() => m.Tracking)}",
                        $"Rate={Show(() => m.TrackingRate)}",
                        $"Slewing={Show(() => m.Slewing)}",
                        $"AtPark={Show(() => m.AtPark)}",
                        $"AtHome={Show(() => m.AtHome)}",
                        $"Pier={Show(() => m.SideOfPier)}",
                        $"Align={Show(() => m.AlignmentMode)}");
                });
                output.WriteLine($"[{round}] {text}");
                if (round == 0)
                {
                    var caps = await dispatcher.InvokeAsync(() => string.Join(
                        " ",
                        $"Slew={Show(() => mount!.CanSlew)}",
                        $"SlewAsync={Show(() => mount!.CanSlewAsync)}",
                        $"Sync={Show(() => mount!.CanSync)}",
                        $"Park={Show(() => mount!.CanPark)}",
                        $"Unpark={Show(() => mount!.CanUnpark)}",
                        $"FindHome={Show(() => mount!.CanFindHome)}",
                        $"SetTracking={Show(() => mount!.CanSetTracking)}",
                        $"PulseGuide={Show(() => mount!.CanPulseGuide)}"));
                    output.WriteLine("capabilities: " + caps);
                }

                await Task.Delay(TimeSpan.FromSeconds(4));
            }
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (mount is not null)
                {
                    mount.Connected = false;
                    mount.Dispose();
                }
            });
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_Properties_ReadOnly()
    {
        using var dispatcher = new AscomDispatcher("diagnostics camera");
        IAscomCameraDriver? c = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                c = Drivers.CreateCamera(Env("ASTRA_ASCOM_CAMERA"));
                c.Connected = true;
            });
            var text = await dispatcher.InvokeAsync(() => string.Join(
                "\n",
                $"Identity={Show(() => c!.Identity)}",
                $"Size={Show(() => c!.CameraXSize)}x{Show(() => c!.CameraYSize)} Num={Show(() => c!.NumX)}x{Show(() => c!.NumY)} Start={Show(() => c!.StartX)},{Show(() => c!.StartY)} Bin={Show(() => c!.BinX)}x{Show(() => c!.BinY)} MaxBin={Show(() => c!.MaxBinX)}x{Show(() => c!.MaxBinY)} Asym={Show(() => c!.CanAsymmetricBin)}",
                $"MaxADU={Show(() => c!.MaxAdu)} Pixel={Show(() => c!.PixelSizeX)}x{Show(() => c!.PixelSizeY)} e/ADU={Show(() => c!.ElectronsPerAdu)} Sensor={Show(() => c!.SensorType)} Bayer={Show(() => c!.BayerOffsetX)},{Show(() => c!.BayerOffsetY)} Shutter={Show(() => c!.HasShutter)}",
                $"Exposure min={Show(() => c!.ExposureMin)} max={Show(() => c!.ExposureMax)} res={Show(() => c!.ExposureResolution)} CanAbort={Show(() => c!.CanAbortExposure)} CanStop={Show(() => c!.CanStopExposure)} State={Show(() => c!.CameraState)}",
                $"Gain={Show(() => c!.Gain)} [{Show(() => c!.GainMin)}..{Show(() => c!.GainMax)}] Offset={Show(() => c!.Offset)} [{Show(() => c!.OffsetMin)}..{Show(() => c!.OffsetMax)}]",
                $"Readout={Show(() => c!.ReadoutMode)} of [{Show(() => string.Join(",", c!.ReadoutModes))}] CanFast={Show(() => c!.CanFastReadout)} Fast={Show(() => c!.FastReadout)}",
                $"Cooling: CanSetTemp={Show(() => c!.CanSetCcdTemperature)} CanPower={Show(() => c!.CanGetCoolerPower)} Temp={Show(() => c!.CcdTemperature)} Target={Show(() => c!.SetCcdTemperature)} CoolerOn={Show(() => c!.CoolerOn)} Power={Show(() => c!.CoolerPower)} HeatSink={Show(() => c!.HeatSinkTemperature)}"));
            output.WriteLine(text);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (c is not null)
                {
                    c.Connected = false;
                    c.Dispose();
                }
            });
        }
    }

    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_Properties_ReadOnly()
    {
        using var dispatcher = new AscomDispatcher("diagnostics focuser");
        IAscomFocuserDriver? f = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                f = Drivers.CreateFocuser(Env("ASTRA_ASCOM_FOCUSER"));
                f.Connected = true;
            });
            output.WriteLine(await dispatcher.InvokeAsync(() => string.Join(
                " | ",
                $"Identity={Show(() => f!.Identity)}",
                $"Absolute={Show(() => f!.Absolute)}",
                $"MaxStep={Show(() => f!.MaxStep)}",
                $"MaxIncrement={Show(() => f!.MaxIncrement)}",
                $"StepSize={Show(() => f!.StepSize)}",
                $"Position={Show(() => f!.Position)}",
                $"IsMoving={Show(() => f!.IsMoving)}",
                $"Temperature={Show(() => f!.Temperature)}",
                $"TempCompAvailable={Show(() => f!.TempCompAvailable)}",
                $"TempComp={Show(() => f!.TempComp)}")));
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (f is not null)
                {
                    f.Connected = false;
                    f.Dispose();
                }
            });
        }
    }
}

public sealed class HardwareExposureDiagnostics(ITestOutputHelper output)
{
    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ShortExposures_RawArrayContent()
    {
        var drivers = new ComAscomDriverFactory();
        using var dispatcher = new AscomDispatcher("diagnostics exposures");
        IAscomCameraDriver? c = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                c = drivers.CreateCamera(Environment.GetEnvironmentVariable("ASTRA_ASCOM_CAMERA")!);
                c.Connected = true;
            });
            foreach (var seconds in new[] { 0.1, 0.1, 0.2, 0.5, 1.0, 0.1 })
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await dispatcher.InvokeAsync(() => c!.StartExposure(seconds, true));
                while (!await dispatcher.InvokeAsync(() => c!.ImageReady))
                {
                    await Task.Delay(20);
                }

                var ready = clock.ElapsedMilliseconds;
                var text = await dispatcher.InvokeAsync(() =>
                {
                    var a = (int[,])c!.ImageArray!;
                    int w = a.GetLength(0), h = a.GetLength(1);
                    long zeros = 0, sum = 0;
                    int firstZeroColumn = -1;
                    for (var x = 0; x < w; x++)
                    {
                        for (var y = 0; y < h; y++)
                        {
                            var v = a[x, y];
                            sum += v;
                            if (v == 0)
                            {
                                zeros++;
                                if (firstZeroColumn < 0)
                                {
                                    firstZeroColumn = x;
                                }
                            }
                        }
                    }

                    return $"dims {w}x{h} mean {(double)sum / ((long)w * h):0.0} zeros {zeros} ({100.0 * zeros / ((long)w * h):0.0} %) firstZeroColumn {firstZeroColumn} " +
                        $"sample a[10,10]={a[10, 10]} a[w/2,h/2]={a[w / 2, h / 2]} a[w-10,h-10]={a[w - 10, h - 10]}";
                });
                output.WriteLine($"{seconds} s: ready after {ready} ms; {text}");
            }
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (c is not null)
                {
                    c.Connected = false;
                    c.Dispose();
                }
            });
        }
    }
}

public sealed class HardwareAstraFrameDiagnostics(ITestOutputHelper output)
{
    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_AstraFrames_ZeroStatistics()
    {
        var camera = new Astra.Ascom.Cameras.AscomCamera(new Astra.Core.Devices.DeviceId("camera.real"), "Real Camera", Environment.GetEnvironmentVariable("ASTRA_ASCOM_CAMERA")!, new ComAscomDriverFactory());
        await camera.ConnectAsync();
        try
        {
            foreach (var seconds in new[] { 0.1, 0.1, 0.2, 0.1, 0.1 })
            {
                var frame = await camera.ExposeAsync(new Astra.Core.Devices.CameraExposureRequest(TimeSpan.FromSeconds(seconds), Astra.Core.Devices.FrameType.Light, new Astra.Core.Devices.CameraSettings()));
                var p = frame.Pixels.Span;
                long zeros = 0, sum = 0;
                for (var i = 0; i < p.Length; i++)
                {
                    sum += p[i];
                    if (p[i] == 0)
                    {
                        zeros++;
                    }
                }

                var first = p.IndexOf((ushort)0);
                output.WriteLine($"{seconds} s: {frame.Width}x{frame.Height} mean {(double)sum / p.Length:0.0} zeros {zeros} firstZeroIndex {first} p[0]={p[0]} p[mid]={p[p.Length / 2]} p[last]={p[^1]}");
            }
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }
}

public sealed class HardwareOffsetDiagnostics(ITestOutputHelper output)
{
    private static async Task<(int Reported, double Mean)> Session(ComAscomDriverFactory drivers, int? setOffset)
    {
        using var dispatcher = new AscomDispatcher("diagnostics offset");
        IAscomCameraDriver? c = null;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                c = drivers.CreateCamera(Environment.GetEnvironmentVariable("ASTRA_ASCOM_CAMERA")!);
                c.Connected = true;
            });
            var reported = await dispatcher.InvokeAsync(() => c!.Offset);
            if (setOffset is { } o)
            {
                await dispatcher.InvokeAsync(() => c!.Offset = o);
            }

            await dispatcher.InvokeAsync(() => c!.StartExposure(0.1, true));
            while (!await dispatcher.InvokeAsync(() => c!.ImageReady))
            {
                await Task.Delay(20);
            }

            var mean = await dispatcher.InvokeAsync(() =>
            {
                var a = (int[,])c!.ImageArray!;
                long sum = 0;
                foreach (var v in a)
                {
                    sum += v;
                }

                return (double)sum / a.Length;
            });
            return (reported, mean);
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (c is not null)
                {
                    c.Connected = false;
                    c.Dispose();
                }
            });
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_ReportedOffset_VersusTheFrames()
    {
        var drivers = new ComAscomDriverFactory();
        try
        {
            output.WriteLine("session 1 (nothing set): " + await Session(drivers, null));
            output.WriteLine("session 2 (set offset 0): " + await Session(drivers, 0));
            output.WriteLine("session 3 (nothing set):  " + await Session(drivers, null));
        }
        finally
        {
            output.WriteLine("session 4 (set offset 50, the original): " + await Session(drivers, 50));
            output.WriteLine("session 5 (nothing set):  " + await Session(drivers, null));
        }
    }
}

public sealed class HardwareFocuserMotionDiagnostics(ITestOutputHelper output)
{
    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_RawMotion_PositionAndIsMovingOverTime()
    {
        var drivers = new ComAscomDriverFactory();
        using var dispatcher = new AscomDispatcher("diagnostics focuser motion");
        IAscomFocuserDriver? f = null;
        int start = 0;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                f = drivers.CreateFocuser(Environment.GetEnvironmentVariable("ASTRA_ASCOM_FOCUSER")!);
                f.Connected = true;
                start = f.Position;
            });
            output.WriteLine($"start {start}");
            await dispatcher.InvokeAsync(() => f!.Move(start + 2500));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 40; i++)
            {
                var (pos, moving) = await dispatcher.InvokeAsync(() => (f!.Position, f.IsMoving));
                output.WriteLine($"{clock.ElapsedMilliseconds,5} ms: position {pos} moving {moving}");
                if (!moving && i > 2)
                {
                    break;
                }

                await Task.Delay(250);
            }

            await dispatcher.InvokeAsync(() => f!.Move(start));
            for (var i = 0; i < 80; i++)
            {
                var (pos, moving) = await dispatcher.InvokeAsync(() => (f!.Position, f.IsMoving));
                if (!moving && i > 2)
                {
                    output.WriteLine($"back: position {pos}");
                    break;
                }

                await Task.Delay(250);
            }
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (f is not null)
                {
                    f.Connected = false;
                    f.Dispose();
                }
            });
        }
    }
}

public sealed class HardwareFocuserHaltDiagnostics(ITestOutputHelper output)
{
    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_RawHalt_PositionAfterwards()
    {
        var drivers = new ComAscomDriverFactory();
        using var dispatcher = new AscomDispatcher("diagnostics focuser halt");
        IAscomFocuserDriver? f = null;
        int start = 0;
        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                f = drivers.CreateFocuser(Environment.GetEnvironmentVariable("ASTRA_ASCOM_FOCUSER")!);
                f.Connected = true;
                start = f.Position;
                f.Move(start + 2500);
            });
            await Task.Delay(1500);
            await dispatcher.InvokeAsync(() => f!.Halt());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < 25; i++)
            {
                var (pos, moving) = await dispatcher.InvokeAsync(() => (f!.Position, f.IsMoving));
                output.WriteLine($"{clock.ElapsedMilliseconds,5} ms after Halt: position {pos} moving {moving}");
                await Task.Delay(100);
            }

            await dispatcher.InvokeAsync(() => f!.Move(start));
            while (await dispatcher.InvokeAsync(() => f!.IsMoving))
            {
                await Task.Delay(200);
            }

            output.WriteLine($"back at {await dispatcher.InvokeAsync(() => f!.Position)} (start {start})");
        }
        finally
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (f is not null)
                {
                    f.Connected = false;
                    f.Dispose();
                }
            });
        }
    }
}

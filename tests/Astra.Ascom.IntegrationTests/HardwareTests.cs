using Astra.Ascom.Cameras;
using Astra.Ascom.Drivers;
using Astra.Ascom.Focusers;
using Astra.Ascom.Mounts;
using Astra.Core.Devices;
using Astra.Core.Focusing;
using Astra.Core.Focusers;
using Astra.Core.Mounts;
using Astra.Core.Rigs;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Sequencing;

namespace Astra.Ascom.IntegrationTests;

/// <summary>
/// Real hardware, in the order of the manual validation: focuser, mount, camera, camera and the frame analysis, and
/// camera with focuser in an autofocus. Every test is off until its variable names a ProgId, and every test keeps to small,
/// reversible actions: the focuser moves a few hundred steps and comes back; the mount is only read unless a small
/// slew is allowed explicitly; nothing is ever unparked.
/// <list type="bullet">
/// <item><c>ASTRA_ASCOM_FOCUSER</c> the ProgId of a focuser: connects, reads, moves 200 steps and back.</item>
/// <item><c>ASTRA_ASCOM_MOUNT</c> the ProgId of a mount: connects and reads its state. With <c>ASTRA_ASCOM_MOUNT_SLEW_OK=1</c>
/// (the mount is in a safe place, clear of obstacles, not parked) it also slews 0.05 h east and back.</item>
/// <item><c>ASTRA_ASCOM_CAMERA</c> the ProgId of a camera: connects, exposes 1 s, analyses the frame.</item>
/// <item><c>ASTRA_ASCOM_CAMERA</c> and <c>ASTRA_ASCOM_FOCUSER</c> with <c>ASTRA_ASCOM_AUTOFOCUS_OK=1</c> (the telescope points
/// at stars and the focuser is within reach of focus): an autofocus run with the unchanged algorithm.</item>
/// </list>
/// </summary>
public class HardwareTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly ComAscomDriverFactory Drivers = new();
    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? string.Empty;

    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_ConnectsReadsMovesAndComesBack()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", Env("ASTRA_ASCOM_FOCUSER"), Drivers);
        await focuser.ConnectAsync();
        try
        {
            var start = focuser.Position;
            var there = start + 200 <= focuser.MaxPosition ? start + 200 : start - 200;

            await focuser.MoveToAsync(there);
            Assert.InRange(focuser.Position, there - 5, there + 5);
            await focuser.MoveToAsync(start);

            Assert.InRange(focuser.Position, start - 5, start + 5);
        }
        finally
        {
            await focuser.DisconnectAsync();
        }
    }

    private sealed class TestLogger(Xunit.Abstractions.ITestOutputHelper output) : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = $"{logLevel}: {formatter(state, exception)}";
            Lines.Add(line);
            output.WriteLine(line);
        }
    }

    [HardwareFact("ASTRA_ASCOM_FOCUSER")]
    public async Task TheRealFocuser_IsHaltedWhenAMoveIsCancelled_AndReportsWhereItStopped()
    {
        var logger = new TestLogger(output);
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", Env("ASTRA_ASCOM_FOCUSER"), Drivers, logger: logger);
        await focuser.ConnectAsync();
        var start = focuser.Position;
        try
        {
            var far = start + 800 <= focuser.MaxPosition ? start + 800 : start - 800;
            using var cts = new CancellationTokenSource();
            var move = focuser.MoveToAsync(far, cts.Token);
            await Task.Delay(300);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
            output.WriteLine($"started at {start}, asked for {far}, reported {focuser.Position}");
            Assert.Contains(logger.Lines, l => l.Contains("confirmed stopped") || l.Contains("could not be confirmed stopped"));
        }
        finally
        {
            await focuser.MoveToAsync(start); // back to where it was
            await focuser.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_IsAbortedWhenAnExposureIsCancelled_AndIsIdleAgain()
    {
        var logger = new TestLogger(output);
        var camera = new AscomCamera(new DeviceId("camera.real"), "Real Camera", Env("ASTRA_ASCOM_CAMERA"), Drivers, logger: logger);
        await camera.ConnectAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            var exposure = camera.ExposeAsync(TimeSpan.FromSeconds(8), cts.Token);
            await Task.Delay(1500);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exposure);
            Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
            Assert.Contains(logger.Lines, l => l.Contains("confirmed idle after the cancelled exposure"));

            var next = await camera.ExposeAsync(TimeSpan.FromSeconds(0.2)); // and it can expose again
            Assert.True(next.Width > 0);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_ConnectsAndReportsItsState_WithoutMoving()
    {
        var mount = new AscomMount(new DeviceId("mount.real"), "Real Mount", Env("ASTRA_ASCOM_MOUNT"), Drivers);
        await mount.ConnectAsync();
        try
        {
            Assert.Equal(DeviceConnectionState.Connected, mount.ConnectionState);
            Assert.InRange(mount.Coordinates.RightAscensionHours, 0, 24);
            Assert.InRange(mount.Coordinates.DeclinationDegrees, -90, 90);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_MOUNT")]
    public async Task TheRealMount_SlewsAShortWayAndBack_OnlyWhenTheUserSaidItIsSafe()
    {
        if (Env("ASTRA_ASCOM_MOUNT_SLEW_OK") != "1")
        {
            return; // reading is always safe; a slew is not: it needs the explicit word
        }

        var mount = new AscomMount(new DeviceId("mount.real"), "Real Mount", Env("ASTRA_ASCOM_MOUNT"), Drivers);
        await mount.ConnectAsync();
        try
        {
            var start = mount.Coordinates;
            await mount.SlewToAsync(new CelestialCoordinates((start.RightAscensionHours + 0.05) % 24, start.DeclinationDegrees));
            await mount.SlewToAsync(start);

            Assert.Equal(start.RightAscensionHours, mount.Coordinates.RightAscensionHours, 1);
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [HardwareFact("ASTRA_ASCOM_CAMERA")]
    public async Task TheRealCamera_Exposes_AndTheFrameGoesThroughTheUnchangedAnalysis()
    {
        await using var host = new AstraRuntimeHost();
        var camera = new AscomCamera(new DeviceId("camera.real"), "Real Camera", Env("ASTRA_ASCOM_CAMERA"), Drivers, host.EventBus);
        host.AddDevice(camera);
        await host.DeviceOperations.ConnectAsync(camera.Id);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frame = await host.DeviceOperations.ExposeAsync(camera.Id, TimeSpan.FromSeconds(1));
            output.WriteLine($"1 s exposure delivered a {frame.Width} x {frame.Height} frame after {clock.ElapsedMilliseconds} ms");

            Assert.True(frame.Width > 0 && frame.Height > 0);
            var span = frame.Pixels.Span;
            var (min, max, sum) = (int.MaxValue, 0, 0L);
            foreach (var p in span)
            {
                min = Math.Min(min, p);
                max = Math.Max(max, p);
                sum += p;
            }

            output.WriteLine($"pixels: min {min}, max {max}, mean {(double)sum / span.Length:0.0}");
            Assert.True(max > 0);
            Astra.Core.Imaging.FrameAnalysisResult? analysis = null;
            var failure = Record.Exception(() => analysis = host.FrameAnalyzer.Analyze(frame));
            output.WriteLine(analysis is null
                ? $"analysis: {failure?.Message}"
                : $"analysis: {analysis.Metrics.UsableStarCount} usable stars, median HFR {analysis.Metrics.MedianHfr}");
            Assert.True(failure is null or Astra.Core.Imaging.FrameAnalysisException, failure?.ToString());
        }
        finally
        {
            await host.DeviceOperations.DisconnectAsync(camera.Id);
        }
    }

    [HardwareFact("ASTRA_ASCOM_AUTOFOCUS_OK")]
    public async Task TheRealCameraAndFocuser_RunTheUnchangedAutofocus()
    {
        var cameraProgId = Env("ASTRA_ASCOM_CAMERA");
        var focuserProgId = Env("ASTRA_ASCOM_FOCUSER");
        if (cameraProgId.Length == 0 || focuserProgId.Length == 0 || Env("ASTRA_ASCOM_AUTOFOCUS_OK") != "1")
        {
            return;
        }

        await using var host = new AstraRuntimeHost();
        var camera = new AscomCamera(new DeviceId("camera.real"), "Real Camera", cameraProgId, Drivers, host.EventBus);
        var focuser = new AscomFocuser(new DeviceId("focuser.real"), "Real Focuser", focuserProgId, Drivers, host.EventBus);
        host.AddDevice(camera);
        host.AddDevice(focuser);
        var rig = new Rig(new RigId("rig.real"), "Real Rig", camera.Id, new OpticalTrain(500, 100, 3.76, 23.5, 15.7, 6248, 4176), focuser.Id);
        host.AddRig(rig);
        await host.DeviceOperations.ConnectAsync(camera.Id);
        await host.DeviceOperations.ConnectAsync(focuser.Id);
        try
        {
            var start = focuser.Position;
            var action = AutofocusAction.ForRig(
                host.DeviceRegistry, rig, new AutofocusOptions(TimeSpan.FromSeconds(2), 100, 7), host.FocusMetricProvider, host.EventBus);

            var result = (AutofocusResult)(await action.ExecuteAsync(new NoContext(), CancellationToken.None)).Payload!;

            Assert.Equal(result.FinalPosition, focuser.Position);
            Assert.True(result.Measurements.Count >= 7);
            _ = start;
        }
        finally
        {
            await host.DeviceOperations.DisconnectAsync(camera.Id);
            await host.DeviceOperations.DisconnectAsync(focuser.Id);
        }
    }

    private sealed class NoContext : ISequenceStepContext
    {
        public Task<SequenceStepResult> ExecuteChildAsync(ISequenceStep child, int index, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SequenceStepResult> ExecuteBranchAsync(
            ISequenceStep child, int index, int count, Astra.Core.Coordination.CoordinationGroupId? group, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReachSafePointAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }
}

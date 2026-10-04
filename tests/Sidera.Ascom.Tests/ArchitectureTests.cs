using Sidera.Ascom.Cameras;
using Sidera.Ascom.Focusers;
using Sidera.Ascom.Mounts;
using Sidera.Core.Coordination;
using Sidera.Core.Devices;
using Sidera.Core.Focusers;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;
using Sidera.Core.Focusing;

namespace Sidera.Ascom.Tests;

/// <summary>
/// The acceptance tests of the architecture: ASCOM devices (fake drivers underneath, the real adapters on top) go
/// through the runtime that was written for simulators, and nothing above the adapters knows about ASCOM. Frame
/// analysis, autofocus, the actions of the sequencer and the device operations are the unchanged runtime classes.
/// </summary>
public class ArchitectureTests
{
    private static readonly RigId MainRig = new("rig.main");
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly DeviceId FocuserId = new("focuser.main");
    private static readonly DeviceId MountId = new("mount.main");
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 23.5, 15.7, 800, 600);

    // The sigma of the stars that a focus model gives at a position: the same relation the simulated camera uses.
    private static readonly double HfrPerSigma = Math.Sqrt(2 * Math.Log(2));

    private sealed class NoContext : ISequenceStepContext
    {
        public static NoContext Instance { get; } = new();

        public Task<SequenceStepResult> ExecuteChildAsync(ISequenceStep child, int index, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SequenceStepResult> ExecuteBranchAsync(
            ISequenceStep child, int index, int count, CoordinationGroupId? group, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReachSafePointAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    // What an ASCOM camera returns for a frame of Sidera's: int[x, y].
    private static int[,] ToAscomArray(CameraFrame frame)
    {
        var image = new int[frame.Width, frame.Height];
        var pixels = frame.Pixels.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                image[x, y] = pixels[y * frame.Width + x];
            }
        }

        return image;
    }

    private sealed class Setup : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required AscomCamera Camera { get; init; }
        public required AscomFocuser Focuser { get; init; }
        public required FakeCameraDriver CameraDriver { get; init; }
        public required FakeFocuserDriver FocuserDriver { get; init; }
        public required CallLog Log { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Camera.DisposeAsync();
            await Focuser.DisposeAsync();
        }
    }

    // A camera and a focuser that are ASCOM devices, registered with a host like any others, with a camera whose stars
    // follow the focuser, as a real sky does. The drivers know nothing of Sidera's models: the camera only sees where the
    // focuser is, and draws.
    private static async Task<Setup> CreateAscomRig(int start, int best = 20000)
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log);
        var sky = new SimulatedSky(1);
        var model = new SimulatedFocusModel(best);
        var exposures = 0;
        FakeFocuserDriver? focuserDriver = null;
        drivers.ConfigureFocuser = d =>
        {
            focuserDriver = d;
            d.PositionValue = start;
            d.MovePolls = 1;
            d.MaxStep = 60000;
        };
        drivers.ConfigureCamera = d =>
        {
            (d.CameraXSize, d.CameraYSize, d.NumX, d.NumY) = (SimulatedSky.Width, SimulatedSky.Height, SimulatedSky.Width, SimulatedSky.Height);
            d.ExposePolls = 1;
            d.ImageFactory = () =>
            {
                var sigma = model.HfrAt(focuserDriver!.PositionValue) / HfrPerSigma;
                return ToAscomArray(sky.Render(sigma, TimeSpan.FromMilliseconds(20), Interlocked.Increment(ref exposures)));
            };
        };

        var host = new SideraRuntimeHost();
        var timings = FastTimings.Create();
        var camera = new AscomCamera(CameraId, "ASCOM Camera", "ASCOM.Test.Camera", drivers, host.EventBus, null, timings);
        var focuser = new AscomFocuser(FocuserId, "ASCOM Focuser", "ASCOM.Test.Focuser", drivers, host.EventBus, null, timings);
        host.AddDevice(camera);
        host.AddDevice(focuser);
        host.AddRig(new Rig(MainRig, "Main Rig", CameraId, Optics, FocuserId));
        await host.DeviceOperations.ConnectAsync(CameraId);
        await host.DeviceOperations.ConnectAsync(FocuserId);

        return new Setup
        {
            Host = host, Camera = camera, Focuser = focuser, CameraDriver = drivers.Cameras[0], FocuserDriver = focuserDriver!, Log = log,
        };
    }

    // ASCOM Camera -> ICamera -> CameraFrame -> FrameAnalyzer -> star detection -> HFR

    [Fact]
    public async Task AnAscomCamera_FeedsTheUnchangedFrameAnalysis_WithTheSameStarsAtTheSamePlaces()
    {
        await using var setup = await CreateAscomRig(start: 20000);
        var original = new SimulatedSky(1).Render(
            new SimulatedFocusModel(20000).HfrAt(20000) / HfrPerSigma, TimeSpan.FromMilliseconds(20), 1);

        var frame = await setup.Host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromMilliseconds(20));
        var seen = setup.Host.FrameAnalyzer.Analyze(frame);
        var direct = setup.Host.FrameAnalyzer.Analyze(original);

        // The pixels went through int[x, y] and came back as rows: the frames are identical, so the stars are too.
        Assert.True(frame.Pixels.Span.SequenceEqual(original.Pixels.Span));
        Assert.True(seen.Metrics.UsableStarCount >= 15, $"{seen.Metrics.UsableStarCount} usable stars");
        Assert.Equal(direct.Stars.Select(s => (s.X, s.Y)), seen.Stars.Select(s => (s.X, s.Y)));
        Assert.Equal(direct.Metrics.MedianHfr, seen.Metrics.MedianHfr);
        Assert.InRange(seen.Metrics.MedianHfr!.Value, 1.5, 2.2); // in focus
    }

    [Fact]
    public async Task ATransposedImage_WouldMoveEveryStar_SoTheOrientationMattersToTheMeasurement()
    {
        await using var setup = await CreateAscomRig(start: 20000);
        var frame = await setup.Host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromMilliseconds(20));
        var stars = setup.Host.FrameAnalyzer.Analyze(frame).Stars;

        // A star of the 800 x 600 frame at x = 700 cannot be at x = 700 of a 600-wide row: that is what a wrong
        // orientation would do. The adapter keeps the width the camera announced.
        Assert.Equal((800, 600), (frame.Width, frame.Height));
        Assert.Contains(stars, s => s.X > 600);
    }

    // The actions of the sequencer, unchanged

    [Fact]
    public async Task TheExposureAction_ExposesAnAscomCamera_WithoutKnowingIt()
    {
        await using var setup = await CreateAscomRig(start: 20000);
        var action = new CameraExposureAction(setup.Host.DeviceRegistry, CameraId, TimeSpan.FromMilliseconds(20));

        var result = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        var frame = Assert.IsType<CameraFrame>(result.Payload);
        Assert.Equal((800, 600), (frame.Width, frame.Height));
        Assert.Single(setup.CameraDriver.Starts);
        Assert.True(setup.CameraDriver.Starts[0].Light);
    }

    [Fact]
    public async Task TheMoveFocuserAction_MovesAnAscomFocuser_WithoutKnowingIt()
    {
        await using var setup = await CreateAscomRig(start: 20000);
        var action = new MoveFocuserAction(setup.Host.DeviceRegistry, FocuserId, 21500);

        await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Equal(21500, setup.Focuser.Position);
        Assert.Equal(21500, setup.FocuserDriver.PositionValue);
    }

    [Fact]
    public async Task TheSlewAction_SlewsAnAscomMount_WithoutKnowingIt()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log);
        await using var host = new SideraRuntimeHost();
        var mount = new AscomMount(MountId, "ASCOM Mount", "ASCOM.Test.Telescope", drivers, host.EventBus, null, FastTimings.Create());
        host.AddDevice(mount);
        await host.DeviceOperations.ConnectAsync(MountId);
        var target = new CelestialCoordinates(5.5, 22.5);

        await new SlewAction(host.DeviceRegistry, MountId, target).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Equal(["SlewToCoordinatesAsync 5.5 22.5"], log.Names.Where(n => n.StartsWith("SlewToCoordinates")));
        Assert.Equal((5.5, 22.5), (mount.Coordinates.RightAscensionHours, mount.Coordinates.DeclinationDegrees));
        Assert.Equal(MountMotionState.Tracking, mount.MotionState);
        await mount.DisposeAsync();
    }

    [Fact]
    public async Task TheHostConnectsAndDisconnectsAscomDevices_ThroughTheDeviceOperations_AndTheStateStoreFollows()
    {
        await using var setup = await CreateAscomRig(start: 20000);

        Assert.True(setup.Host.StateStore.TryGet(FocuserId, out var connected));
        Assert.Equal(DeviceConnectionState.Connected, connected!.ConnectionState);

        await setup.Host.DeviceOperations.DisconnectAsync(FocuserId);

        Assert.True(setup.Host.StateStore.TryGet(FocuserId, out var disconnected));
        Assert.Equal(DeviceConnectionState.Disconnected, disconnected!.ConnectionState);
        Assert.True(setup.FocuserDriver.Disposed);
    }

    // ASCOM Camera + ASCOM Focuser -> AutofocusAction -> frames -> engine -> final move

    [Fact]
    public async Task Autofocus_RunsOnAnAscomCameraAndAnAscomFocuser_MeasuresRealFrames_AndEndsWithAFocuserMove()
    {
        await using var setup = await CreateAscomRig(start: 18200);
        setup.Host.RigRegistry.TryGet(MainRig, out var rig);
        var options = new AutofocusOptions(TimeSpan.FromMilliseconds(20), 400, 7);
        var action = AutofocusAction.ForRig(setup.Host.DeviceRegistry, rig!, options, setup.Host.FocusMetricProvider, setup.Host.EventBus);

        var result = (AutofocusResult)(await action.ExecuteAsync(NoContext.Instance, CancellationToken.None)).Payload!;

        Assert.InRange(result.BestPosition, 19700, 20300); // the true focus is at 20000
        Assert.Equal(result.FinalPosition, setup.Focuser.Position);
        Assert.Equal(result.FinalPosition, setup.FocuserDriver.PositionValue); // the last move really reached the driver
        Assert.True(result.Measurements.Count >= 7);
        Assert.All(result.Measurements, m => Assert.InRange(m.Hfr, 1.0, 12.0));
        Assert.True(setup.CameraDriver.Starts.Count >= 7); // a real exposure for every sample
        Assert.All(setup.CameraDriver.Starts, s => Assert.True(s.Light));
        Assert.True(setup.Log.Names.Count(n => n.StartsWith("Move ")) >= 7); // each move is one Move call; none is repeated blindly

        // Two devices, two apartments: every call came on an STA thread, and each device kept to its own.
        Assert.All(setup.Log.Calls, c => Assert.Equal(ApartmentState.STA, c.Apartment));
        Assert.Equal(2, setup.Log.Calls.Select(c => c.ThreadId).Distinct().Count());
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, setup.Log.Calls.Select(c => c.ThreadId));
    }

    [Fact]
    public async Task AMixedRig_ASimulatedCameraAndAnAscomFocuser_WorksInOneRuntime()
    {
        var log = new CallLog();
        var drivers = new FakeDriverFactory(log) { ConfigureFocuser = d => (d.PositionValue, d.MovePolls, d.MaxStep) = (18200, 1, 60000) };
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Simulated Camera", seed: 1);
        var focuser = new AscomFocuser(FocuserId, "ASCOM Focuser", "ASCOM.Test.Focuser", drivers, host.EventBus, null, FastTimings.Create());
        host.AddDevice(focuser);
        host.AddRig(new Rig(MainRig, "Main Rig", CameraId, Optics, FocuserId));
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000));
        await host.DeviceOperations.ConnectAsync(CameraId);
        await host.DeviceOperations.ConnectAsync(FocuserId);
        host.RigRegistry.TryGet(MainRig, out var rig);

        var action = AutofocusAction.ForRig(host.DeviceRegistry, rig!, new AutofocusOptions(TimeSpan.FromMilliseconds(20), 400, 7), host.FocusMetricProvider, host.EventBus);
        var result = (AutofocusResult)(await action.ExecuteAsync(NoContext.Instance, CancellationToken.None)).Payload!;

        Assert.InRange(result.BestPosition, 19700, 20300);
        Assert.Equal(result.FinalPosition, drivers.Focusers[0].PositionValue);
        Assert.IsType<SimulatedCamera>(camera);
        await focuser.DisposeAsync();
    }
}

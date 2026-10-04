using Astra.Ascom.Cameras;
using Astra.Ascom.Discovery;
using Astra.Ascom.Drivers;
using Astra.Ascom.Focusers;
using Astra.Ascom.Mounts;
using Astra.Core.Devices;
using Astra.Core.Events;
using Astra.Core.Focusers;
using Astra.Core.Mounts;
using Astra.Runtime;

namespace Astra.Ascom.IntegrationTests;

/// <summary>
/// The adapters against the real ASCOM simulators of the installed Platform (Camera V3, Focuser, Telescope for .NET),
/// through the real drivers, COM and the STA dispatcher. Run with <c>ASTRA_ASCOM_TESTS=1</c>.
/// </summary>
public class SimulatorTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string CameraProgId = "ASCOM.Simulator.Camera";
    private const string FocuserProgId = "ASCOM.Simulator.Focuser";
    private const string MountProgId = "ASCOM.Simulator.Telescope";

    private static readonly ComAscomDriverFactory Drivers = new();

    private sealed class Events : IEventPublisher
    {
        public List<IAstraEvent> Seen { get; } = [];

        public Task PublishAsync<TEvent>(TEvent astraEvent, CancellationToken cancellationToken = default) where TEvent : IAstraEvent
        {
            lock (Seen)
            {
                Seen.Add(astraEvent);
            }

            return Task.CompletedTask;
        }
    }

    [AscomFact]
    public async Task Discovery_FindsTheSimulators_ThroughAscomCom()
    {
        var discovery = new AscomDiscovery();

        var cameras = await discovery.DiscoverAsync(AscomDeviceKind.Camera);
        var mounts = await discovery.DiscoverAsync(AscomDeviceKind.Mount);
        var focusers = await discovery.DiscoverAsync(AscomDeviceKind.Focuser);

        Assert.True(cameras.PlatformAvailable);
        Assert.Null(cameras.Problem);
        Assert.Contains(cameras.Drivers, d => d.ProgId == CameraProgId);
        Assert.Contains(mounts.Drivers, d => d.ProgId == MountProgId);
        Assert.Contains(focusers.Drivers, d => d.ProgId == FocuserProgId);
        Assert.All(cameras.Drivers.Concat(mounts.Drivers).Concat(focusers.Drivers), d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
    }

    [AscomFact]
    public async Task TheFocuserSimulator_ConnectsMovesAndDisconnects_AsAnAbsoluteFocuser()
    {
        var events = new Events();
        var focuser = new AscomFocuser(new DeviceId("focuser.sim"), "Focuser Simulator", FocuserProgId, Drivers, events);

        await focuser.ConnectAsync();
        try
        {
            Assert.Equal(DeviceConnectionState.Connected, focuser.ConnectionState);
            Assert.Equal(0, focuser.MinPosition);
            Assert.True(focuser.MaxPosition > 1000, $"MaxPosition {focuser.MaxPosition}");
            var start = focuser.Position;
            var target = Math.Min(focuser.MaxPosition, start + 400);

            await focuser.MoveToAsync(target);

            Assert.Equal(target, focuser.Position);
            Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
            lock (events.Seen)
            {
                Assert.Contains(events.Seen, e => e is FocuserPositionChanged { Position: var p } && p == target);
            }
        }
        finally
        {
            await focuser.DisconnectAsync();
        }

        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
    }

    [AscomFact]
    public async Task TheFocuserSimulator_CanBeCancelledMidMove_AndStopsWhereItWas()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.sim"), "Focuser Simulator", FocuserProgId, Drivers);
        await focuser.ConnectAsync();
        try
        {
            var start = focuser.Position;
            var far = start + 20000 <= focuser.MaxPosition ? start + 20000 : Math.Max(0, start - 20000);
            using var cts = new CancellationTokenSource();
            var move = focuser.MoveToAsync(far, cts.Token);
            await Task.Delay(700);

            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
            Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
            Assert.NotEqual(far, focuser.Position); // it was stopped on the way
        }
        finally
        {
            await focuser.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheTelescopeSimulator_ConnectsAndSlewsToCoordinates_AndEndsTracking()
    {
        var events = new Events();
        var mount = new AscomMount(new DeviceId("mount.sim"), "Telescope Simulator", MountProgId, Drivers, events);

        await mount.ConnectAsync();
        try
        {
            var from = mount.Coordinates;
            var target = new CelestialCoordinates((from.RightAscensionHours + 0.1) % 24, Math.Clamp(from.DeclinationDegrees - 1, -80, 80));

            await mount.SlewToAsync(target);

            Assert.Equal(target.RightAscensionHours, mount.Coordinates.RightAscensionHours, 2);
            Assert.Equal(target.DeclinationDegrees, mount.Coordinates.DeclinationDegrees, 1);
            Assert.Equal(MountMotionState.Tracking, mount.MotionState);
            lock (events.Seen)
            {
                Assert.Contains(events.Seen, e => e is MountMotionStateChanged { NewState: MountMotionState.Slewing });
                Assert.Contains(events.Seen, e => e is MountMotionStateChanged { NewState: MountMotionState.Tracking });
            }
        }
        finally
        {
            await mount.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheCameraSimulator_ExposesALightFrame_OfTheAnnouncedSize_AndWithRealPixels()
    {
        var camera = new AscomCamera(new DeviceId("camera.sim"), "Camera Simulator", CameraProgId, Drivers);
        await camera.ConnectAsync();
        try
        {
            var frame = await camera.ExposeAsync(TimeSpan.FromSeconds(0.5));

            Assert.Equal((800, 600), (frame.Width, frame.Height));
            Assert.Equal(TimeSpan.FromSeconds(0.5), frame.ExposureDuration);
            var pixels = frame.Pixels.Span;
            Assert.Equal(800 * 600, pixels.Length);
            Assert.Contains(pixels.ToArray(), p => p > 0);
            Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        }
        finally
        {
            await camera.DisconnectAsync();
        }
    }

    [AscomFact]
    public async Task TheCameraSimulator_FeedsTheUnchangedFrameAnalysis()
    {
        await using var host = new AstraRuntimeHost();
        var camera = new AscomCamera(new DeviceId("camera.sim"), "Camera Simulator", CameraProgId, Drivers, host.EventBus);
        host.AddDevice(camera);
        await host.DeviceOperations.ConnectAsync(camera.Id);
        try
        {
            var frame = await host.DeviceOperations.ExposeAsync(camera.Id, TimeSpan.FromSeconds(1));

            // The image of the simulator holds stars; the analysis reads the frame without being told anything about ASCOM.
            Astra.Core.Imaging.FrameAnalysisResult? analysis = null;
            var failure = Record.Exception(() => analysis = host.FrameAnalyzer.Analyze(frame));
            Assert.True(failure is null, failure?.ToString());
            Assert.True(analysis!.Metrics.UsableStarCount > 0, "the frame of the simulator has stars the analysis finds");
            output.WriteLine(analysis is null
                ? $"analysis: {failure!.Message}"
                : $"analysis: {analysis.Metrics.UsableStarCount} usable stars, median HFR {analysis.Metrics.MedianHfr}, background {analysis.Metrics.Background}");
        }
        finally
        {
            await host.DeviceOperations.DisconnectAsync(camera.Id);
        }
    }

    [AscomFact]
    public async Task SeveralConnections_OneAfterTheOther_EachWithItsOwnDriverAndThread()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.sim"), "Focuser Simulator", FocuserProgId, Drivers);

        for (var round = 0; round < 3; round++)
        {
            await focuser.ConnectAsync();
            Assert.Equal(DeviceConnectionState.Connected, focuser.ConnectionState);
            await focuser.DisconnectAsync();
            Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
        }
    }

    [AscomFact]
    public async Task TheHostRunsTheSimulatorsLikeAnyDevices_ThroughTheDeviceOperations()
    {
        await using var host = new AstraRuntimeHost();
        var focuser = new AscomFocuser(new DeviceId("focuser.sim"), "Focuser Simulator", FocuserProgId, Drivers, host.EventBus);
        var mount = new AscomMount(new DeviceId("mount.sim"), "Telescope Simulator", MountProgId, Drivers, host.EventBus);
        host.AddDevice(focuser);
        host.AddDevice(mount);
        await host.DeviceOperations.ConnectAsync(focuser.Id);
        await host.DeviceOperations.ConnectAsync(mount.Id);
        try
        {
            var position = Math.Min(focuser.MaxPosition, focuser.Position + 300);
            await host.DeviceOperations.MoveFocuserToAsync(focuser.Id, position);
            await host.DeviceOperations.SlewToAsync(
                mount.Id, new CelestialCoordinates((mount.Coordinates.RightAscensionHours + 0.05) % 24, Math.Clamp(mount.Coordinates.DeclinationDegrees, -80, 80)));

            Assert.Equal(position, focuser.Position);
            Assert.True(host.StateStore.TryGet(mount.Id, out var state));
            Assert.Equal(DeviceConnectionState.Connected, state!.ConnectionState);
        }
        finally
        {
            await host.DeviceOperations.DisconnectAsync(focuser.Id);
            await host.DeviceOperations.DisconnectAsync(mount.Id);
        }
    }

    [AscomFact]
    public async Task ADriverThatDoesNotExist_FailsToConnect_WithASentence_AndLeavesTheDeviceDisconnected()
    {
        var focuser = new AscomFocuser(new DeviceId("focuser.none"), "Nothing", "ASCOM.DoesNotExist.Focuser", Drivers);

        var failure = await Assert.ThrowsAsync<Infrastructure.AscomDeviceException>(() => focuser.ConnectAsync());

        Assert.Contains("Could not connect Nothing (ASCOM.DoesNotExist.Focuser)", failure.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, focuser.ConnectionState);
    }

    [ManualFact]
    public async Task TheSetupDialog_OfTheCameraSimulator_OpensInItsOwnApartment_AndReturnsWhenClosed()
    {
        var setup = new AscomSetupService(Drivers);

        // A window of the simulator opens now: close it by hand.
        var result = await setup.ShowAsync(AscomDeviceKind.Camera, CameraProgId);

        Assert.True(result.Completed, result.Problem);
    }

    [AscomFact]
    public async Task TheSetupService_NeverThrows_ForADriverThatDoesNotExist()
    {
        var setup = new AscomSetupService(Drivers);

        var result = await setup.ShowAsync(AscomDeviceKind.Focuser, "ASCOM.DoesNotExist.Focuser");

        Assert.False(result.Completed);
        Assert.Contains("could not be opened", result.Problem);
    }
}

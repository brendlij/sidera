using Astra.Core.Devices;
using Astra.Core.Sequencing;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;
using Astra.Runtime.Tests.Devices;

namespace Astra.Runtime.Tests.Sequencing;

/// <summary>
/// An exposure with acquisition settings: resolved against the camera, applied and exposed as one operation, never started when
/// the camera does not support what it asks for, and the same for a sequence step, a manual exposure and autofocus.
/// </summary>
public class AcquisitionExposureTests
{
    private static readonly DeviceId CameraId = new("camera.test");

    /// <summary>A camera that says what it supports and records what it is asked to do, in order.</summary>
    private sealed class RecordingCamera : ICameraControl
    {
        private CameraSettings _settings = new()
        {
            Gain = 10, Offset = 5, BinX = 1, BinY = 1, StartX = 0, StartY = 0, NumX = 800, NumY = 600, ReadoutMode = 0, FastReadout = false,
        };

        public List<string> Calls { get; } = [];
        public List<CameraExposureRequest> Requests { get; } = [];
        public Exception? ApplyThrows { get; set; }
        public CameraCapabilities Caps { get; set; } = new()
        {
            SensorWidth = 800, SensorHeight = 600, MaxAdu = 65535, MaxBinX = 4, MaxBinY = 4, SupportsSubframe = true,
            Gain = IntegerControl.Range(0, 100), Offset = IntegerControl.Range(0, 50), ReadoutModes = ["Normal", "Slow"], HasShutter = true,
        };

        public bool Connected { get; set; } = true;

        public DeviceId Id => CameraId;
        public string Name => "Test Camera";
        public DeviceType Type => DeviceType.Camera;
        public DeviceConnectionState ConnectionState => Connected ? DeviceConnectionState.Connected : DeviceConnectionState.Disconnected;
        public CameraExposureState ExposureState => CameraExposureState.Idle;
        public TimeSpan? ExposureDuration => null;
        public TimeSpan ExposureElapsed => TimeSpan.Zero;
        public double ExposureProgress => 0;
        public DeviceCapabilities<CameraCapabilities> Capabilities => Connected ? DeviceCapabilities<CameraCapabilities>.Of(Caps) : DeviceCapabilities<CameraCapabilities>.Unknown;
        public CameraSettings? Settings => Connected ? _settings : null;
        public CameraTelemetry? Telemetry => null;

        public event EventHandler? ExposureProgressChanged { add { } remove { } }
        public event EventHandler? CapabilitiesChanged { add { } remove { } }
        public event EventHandler? StateChanged { add { } remove { } }

        public CameraExposureOutcome LastOutcome => CameraExposureOutcome.None;

        public Task StopExposureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AbortExposureAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyAsync(CameraSettings change, CancellationToken cancellationToken = default)
        {
            Calls.Add("apply");
            if (ApplyThrows is not null)
            {
                throw ApplyThrows;
            }

            _settings = _settings with
            {
                Gain = change.Gain ?? _settings.Gain, Offset = change.Offset ?? _settings.Offset, BinX = change.BinX ?? _settings.BinX,
                BinY = change.BinY ?? _settings.BinY, StartX = change.StartX ?? _settings.StartX, StartY = change.StartY ?? _settings.StartY,
                NumX = change.NumX ?? _settings.NumX, NumY = change.NumY ?? _settings.NumY,
                ReadoutMode = change.ReadoutMode ?? _settings.ReadoutMode, FastReadout = change.FastReadout ?? _settings.FastReadout,
            };
            return Task.CompletedTask;
        }

        public async Task<CameraFrame> ExposeAsync(CameraExposureRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (!request.Change.IsEmpty)
            {
                await ApplyAsync(request.Change, cancellationToken);
            }

            Calls.Add("expose");
            return new CameraFrame(_settings.NumX!.Value, _settings.NumY!.Value, new ushort[_settings.NumX.Value * _settings.NumY.Value], request.Duration)
            {
                Acquisition = new FrameAcquisition { FrameType = request.FrameType, Gain = _settings.Gain, BinX = _settings.BinX },
            };
        }

        public Task<CameraFrame> ExposeAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        {
            Calls.Add("expose without settings");
            return Task.FromResult(new CameraFrame(2, 2, new ushort[4], duration));
        }
    }

    private static (DeviceRegistry Registry, RecordingCamera Camera) Fixture()
    {
        var registry = new DeviceRegistry();
        var camera = new RecordingCamera();
        registry.Register(camera);
        return (registry, camera);
    }

    private static Task<SequenceStepResult> Run(CameraExposureAction action) => action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

    // ---- Resolve, apply, expose

    [Fact]
    public async Task TheSettingsAreAppliedBeforeTheExposureStarts_AsPartOfTheSameOperation()
    {
        var (registry, camera) = Fixture();
        var action = new CameraExposureAction(
            registry, CameraId, TimeSpan.FromSeconds(300), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50), BinX = 2, BinY = 2 });

        var result = await Run(action);

        Assert.Equal(["apply", "expose"], camera.Calls);
        var request = Assert.Single(camera.Requests);
        Assert.Equal(50, request.Change.Gain);
        Assert.Equal(TimeSpan.FromSeconds(300), request.Duration);
        var frame = Assert.IsType<CameraFrame>(result.Payload);
        Assert.Equal((400, 300), (frame.Width, frame.Height));
        Assert.Equal(50, frame.Acquisition!.Gain);
    }

    [Fact]
    public async Task AnExposureThatSetsNothing_ChangesNothing_AndInheritsTheCameraDefaults()
    {
        var (registry, camera) = Fixture();
        var defaults = new AcquisitionDefaultsRegistry();
        defaults.Set(CameraId, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(70), Offset = AcquisitionLevel.OfNumber(9) });

        await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), null, defaults));

        Assert.Equal((70, 9), (camera.Requests[0].Change.Gain, camera.Requests[0].Change.Offset));

        // And without defaults, with nothing to change: the camera is left exactly as it is.
        var (registry2, camera2) = Fixture();
        await Run(new CameraExposureAction(registry2, CameraId, TimeSpan.FromSeconds(1)));
        Assert.True(camera2.Requests[0].Change.IsEmpty);
        Assert.Equal(["expose"], camera2.Calls);
    }

    [Fact]
    public async Task AnExplicitSetting_BeatsTheDefault_AndOnlyThatOne()
    {
        var (registry, camera) = Fixture();
        var defaults = new AcquisitionDefaultsRegistry();
        defaults.Set(CameraId, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(70), Offset = AcquisitionLevel.OfNumber(9) });

        await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(20) }, defaults));

        Assert.Equal((20, 9), (camera.Requests[0].Change.Gain, camera.Requests[0].Change.Offset));
    }

    [Fact]
    public async Task WhenTheCameraDoesNotSupportASetting_NoExposureIsStarted_AndTheMessageSaysWhich()
    {
        var (registry, camera) = Fixture();
        var action = new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(300) });

        var failure = await Assert.ThrowsAsync<AcquisitionException>(() => Run(action));

        Assert.Contains("rejects gain 300", failure.Message);
        Assert.Contains("Test Camera", failure.Message);
        Assert.Empty(camera.Calls);
    }

    [Fact]
    public async Task ADefaultThatNoLongerFits_StopsTheExposure_AndSaysItIsTheDefault()
    {
        var (registry, camera) = Fixture();
        var defaults = new AcquisitionDefaultsRegistry();
        defaults.Set(CameraId, new AcquisitionIntent { BinX = 9, BinY = 9 });

        var failure = await Assert.ThrowsAsync<AcquisitionException>(
            () => Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), null, defaults)));

        Assert.Contains("(the camera default)", failure.Message);
        Assert.Empty(camera.Calls);
    }

    [Fact]
    public async Task WhenApplyingFails_TheExposureIsNotStarted_AndNothingIsRetried()
    {
        var (registry, camera) = Fixture();
        camera.ApplyThrows = new InvalidOperationException("Camera rejected gain 50.");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50) })));

        Assert.Equal("Camera rejected gain 50.", failure.Message);
        Assert.Equal(["apply"], camera.Calls);
        Assert.Single(camera.Requests);
    }

    [Fact]
    public async Task TheCameraIsLeftAsConfigured_NothingIsRestoredAfterTheExposure()
    {
        var (registry, camera) = Fixture();

        await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50) }));

        Assert.Equal(50, camera.Settings!.Gain);
        Assert.Equal(["apply", "expose"], camera.Calls);
    }

    [Fact]
    public async Task AFrameTypeIsPassedOn_AndADarkNeedsAShutter()
    {
        var (registry, camera) = Fixture();
        await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { FrameType = FrameType.Dark }));
        Assert.Equal(FrameType.Dark, camera.Requests[0].FrameType);

        camera.Caps = camera.Caps with { HasShutter = false };
        await Assert.ThrowsAsync<AcquisitionException>(
            () => Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { FrameType = FrameType.Bias })));
    }

    [Fact]
    public async Task ADisconnectedCamera_CannotBeChecked_SoTheExposureIsLeftToFailAsItAlwaysDid()
    {
        var (registry, camera) = Fixture();
        camera.Connected = false;

        // Nothing is made up: the camera is asked, and it is the camera that says it is not connected.
        await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50) }));

        Assert.Single(camera.Requests);
        Assert.True(camera.Requests[0].Change.IsEmpty);
    }

    // ---- A camera that cannot say what it supports

    [Fact]
    public async Task ACameraWithoutCapabilities_TakesLightFramesAsBefore_AndRefusesSettingsItCannotApply()
    {
        var registry = new DeviceRegistry();
        var plain = new FakeCamera(CameraId.Value);
        registry.Register(plain);

        var frame = await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1)));
        Assert.IsType<CameraFrame>(frame.Payload);

        var failure = await Assert.ThrowsAsync<AcquisitionException>(
            () => Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromSeconds(1), new AcquisitionIntent { BinX = 2 })));
        Assert.Contains("cannot apply acquisition settings", failure.Message);
    }

    // ---- The simulated camera through the same path

    [Fact]
    public async Task TheSimulatedCamera_FollowsTheSameResolution_BinningAndTheRegionChangeTheFrame()
    {
        var registry = new DeviceRegistry();
        var sim = new SimulatedCamera(CameraId, seed: 1);
        registry.Register(sim);
        await sim.ConnectAsync();

        var binned = (CameraFrame)(await Run(new CameraExposureAction(
            registry, CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { BinX = 2, BinY = 2 }))).Payload!;
        var region = (CameraFrame)(await Run(new CameraExposureAction(
            registry, CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { BinX = 1, BinY = 1, Region = AcquisitionRegion.Of(10, 10, 100, 80) }))).Payload!;
        var full = (CameraFrame)(await Run(new CameraExposureAction(
            registry, CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { Region = AcquisitionRegion.Full }))).Payload!;

        Assert.Equal((400, 300), (binned.Width, binned.Height));
        Assert.Equal((100, 80), (region.Width, region.Height));
        Assert.Equal((800, 600), (full.Width, full.Height));
        Assert.Equal(2, binned.Acquisition!.BinX);
        Assert.Equal(FrameType.Light, full.Acquisition!.FrameType);
    }

    [Fact]
    public async Task TheSimulatedGainAndOffset_ChangeThePixelsDeterministically_AndADarkHasNoStars()
    {
        var registry = new DeviceRegistry();
        var sim = new SimulatedCamera(CameraId, seed: 1);
        registry.Register(sim);
        await sim.ConnectAsync();

        var plain = (CameraFrame)(await Run(new CameraExposureAction(registry, CameraId, TimeSpan.FromMilliseconds(10)))).Payload!;
        var offset = (CameraFrame)(await Run(new CameraExposureAction(
            registry, CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { Offset = AcquisitionLevel.OfNumber(20) }))).Payload!;
        var dark = (CameraFrame)(await Run(new CameraExposureAction(
            registry, CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { FrameType = FrameType.Dark, Offset = AcquisitionLevel.OfNumber(0) }))).Payload!;

        Assert.True(offset.Pixels.Span.ToArray().Average(p => p) > plain.Pixels.Span.ToArray().Average(p => p) + 150);
        Assert.True(dark.Pixels.Span.ToArray().Max() < 1000);
        Assert.Equal(FrameType.Dark, dark.Acquisition!.FrameType);
    }

    // ---- Autofocus, manual exposure, several cameras

    [Fact]
    public async Task Autofocus_AlwaysUsesTheWholeSensorUnbinned_WhateverAnImagingExposureLeftOnTheCamera()
    {
        var sim = new SimulatedCamera(CameraId, seed: 1);
        await sim.ConnectAsync();
        await sim.ApplyAsync(new CameraSettings { BinX = 2, BinY = 2 });
        await sim.ApplyAsync(new CameraSettings { StartX = 10, StartY = 10, NumX = 100, NumY = 100 });

        var frame = await AcquisitionExposer.ExposeAsync(
            sim, TimeSpan.FromMilliseconds(10), AcquisitionExposer.Autofocus, null, null, CancellationToken.None);

        Assert.Equal((800, 600), (frame.Width, frame.Height));
        Assert.Equal((1, 1), (sim.Settings!.BinX, sim.Settings.BinY));
    }

    [Fact]
    public async Task AutofocusMeasurement_UsesItsOwnAcquisition_ThroughTheHost()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera", seed: 1);
        await camera.ConnectAsync();
        await ((SimulatedCamera)camera).ApplyAsync(new CameraSettings { BinX = 4, BinY = 4 });

        var frame = await AcquisitionExposer.ExposeAsync(
            camera, TimeSpan.FromMilliseconds(10), AcquisitionExposer.Autofocus, host.AcquisitionDefaults, null, CancellationToken.None);

        Assert.Equal(800, frame.Width);
    }

    [Fact]
    public async Task AManualExposure_GoesThroughTheSamePipeline_WithTheCameraDefaults()
    {
        await using var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera", seed: 1);
        await camera.ConnectAsync();
        host.AcquisitionDefaults.Set(CameraId, new AcquisitionIntent { BinX = 2, BinY = 2 });

        var inherited = await host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromMilliseconds(10));
        var overridden = await host.DeviceOperations.ExposeAsync(
            CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { BinX = 1, BinY = 1 });
        var again = await host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromMilliseconds(10));

        Assert.Equal(400, inherited.Width);
        Assert.Equal(800, overridden.Width);
        Assert.Equal(400, again.Width);
        await Assert.ThrowsAsync<AcquisitionException>(
            () => host.DeviceOperations.ExposeAsync(CameraId, TimeSpan.FromMilliseconds(10), new AcquisitionIntent { BinX = 9, BinY = 9 }));
    }

    [Fact]
    public async Task TwoCameras_RunAtTheSameTime_EachWithItsOwnSettings_WithoutAGlobalLock()
    {
        var registry = new DeviceRegistry();
        var main = new SimulatedCamera(new DeviceId("camera.main"), seed: 1);
        var wide = new SimulatedCamera(new DeviceId("camera.wide"), seed: 2);
        registry.Register(main);
        registry.Register(wide);
        await main.ConnectAsync();
        await wide.ConnectAsync();
        var mainAction = new CameraExposureAction(
            registry, main.Id, TimeSpan.FromMilliseconds(300), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(100), BinX = 1, BinY = 1 });
        var wideAction = new CameraExposureAction(
            registry, wide.Id, TimeSpan.FromMilliseconds(300), new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(0), BinX = 2, BinY = 2 });

        var results = await Task.WhenAll(Run(mainAction), Run(wideAction));

        var mainFrame = (CameraFrame)results[0].Payload!;
        var wideFrame = (CameraFrame)results[1].Payload!;
        Assert.Equal((800, 100), (mainFrame.Width, mainFrame.Acquisition!.Gain));
        Assert.Equal((400, 0), (wideFrame.Width, wideFrame.Acquisition!.Gain));
    }
}

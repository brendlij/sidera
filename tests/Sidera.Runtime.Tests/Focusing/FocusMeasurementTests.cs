using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Tests.Devices;

namespace Sidera.Runtime.Tests.Focusing;

public class FocusMeasurementTests
{
    private static readonly RigId Rig = new("rig.main");
    private static readonly DeviceId CameraId = new("camera.main");
    private static readonly DeviceId FocuserId = new("focuser.main");
    private static readonly TimeSpan Exposure = TimeSpan.FromMilliseconds(40);

    private sealed class ScriptedMetric(Func<FocusMetricInput, FocusMeasurement> answer) : IFocusMetricProvider
    {
        public List<FocusMetricInput> Inputs { get; } = [];

        public Task<FocusMeasurement> MeasureAsync(FocusMetricInput input, CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return Task.FromResult(answer(input));
        }
    }

    private static async Task<(SideraRuntimeHost Host, SimulatedCamera Camera, SimulatedFocuser Focuser)> Create(int start = 18200)
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera", seed: 1);
        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF", start, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        await camera.ConnectAsync();
        await focuser.ConnectAsync();
        host.AddSimulatedFocusModel(Rig, new SimulatedFocusModel(20000));
        return (host, camera, focuser);
    }

    private static FocusMeasurementOperation Operation(SideraRuntimeHost host, IFocusMetricProvider? metric = null) =>
        new(host.DeviceRegistry, Rig, CameraId, FocuserId, metric ?? host.FocusMetrics);

    // The simulated metric

    [Fact]
    public void TheSimulatedHfr_IsLowestAtBestFocus_AndGrowsSmoothlyAwayFromIt()
    {
        var model = new SimulatedFocusModel(20000, 1.8, 0.0025);

        Assert.Equal(1.8, model.HfrAt(20000), 9);
        Assert.True(model.HfrAt(20100) > model.HfrAt(20000));
        Assert.True(model.HfrAt(20500) > model.HfrAt(20100));
        Assert.True(model.HfrAt(22000) > model.HfrAt(21000));
        Assert.Equal(model.HfrAt(19000), model.HfrAt(21000), 9); // symmetrical
        Assert.All(new[] { 0, 5000, 19999, 20000, 20001, 50000 }, p => Assert.True(double.IsFinite(model.HfrAt(p)) && model.HfrAt(p) >= 1.8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void AModel_NeedsAPositiveFiniteBestHfr(double hfr)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimulatedFocusModel(100, hfr));
    }

    [Fact]
    public async Task TheSimulatedMetric_HasNoNoiseByDefault_AndIsTheSameEveryTime()
    {
        var (host, camera, _) = await Create();
        await using var scope = host;
        var frame = await camera.ExposeAsync(Exposure);
        var input = new FocusMetricInput(Rig, CameraId, FocuserId, 19000, frame);

        var first = await host.FocusMetrics.MeasureAsync(input);
        var second = await host.FocusMetrics.MeasureAsync(input);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Noise_IsOnlyThereWhenAskedFor_AndDeterministicWithTheSameSeed()
    {
        var (host, camera, _) = await Create();
        await using var scope = host;
        var frame = await camera.ExposeAsync(Exposure);
        var input = new FocusMetricInput(Rig, CameraId, FocuserId, 19000, frame);
        var a = new SimulatedFocusMetricProvider(0.2, noiseSeed: 7);
        var b = new SimulatedFocusMetricProvider(0.2, noiseSeed: 7);
        a.SetModel(Rig, new SimulatedFocusModel(20000));
        b.SetModel(Rig, new SimulatedFocusModel(20000));

        var runA = new[] { await a.MeasureAsync(input), await a.MeasureAsync(input), await a.MeasureAsync(input) };
        var runB = new[] { await b.MeasureAsync(input), await b.MeasureAsync(input), await b.MeasureAsync(input) };

        Assert.Equal(runA, runB);
        Assert.NotEqual(runA[0].Hfr, runA[1].Hfr);
        Assert.All(runA, m => Assert.InRange(m.Hfr, new SimulatedFocusModel(20000).HfrAt(19000) - 0.2, new SimulatedFocusModel(20000).HfrAt(19000) + 0.2));
    }

    [Fact]
    public async Task ARigWithoutAModel_CannotBeMeasured()
    {
        var (host, camera, _) = await Create();
        await using var scope = host;
        var frame = await camera.ExposeAsync(Exposure);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.FocusMetrics.MeasureAsync(
            new FocusMetricInput(new RigId("rig.other"), CameraId, FocuserId, 100, frame)));

        Assert.Equal("There is no simulated focus for rig 'rig.other'.", error.Message);
    }

    [Fact]
    public void TheFocuserDoesNotKnowWhereFocusIs()
    {
        Assert.DoesNotContain(typeof(Sidera.Core.Focusers.IFocuser).GetProperties(), p => p.Name.Contains("Focus", StringComparison.Ordinal) && p.Name != "FocuserId");
        Assert.DoesNotContain(typeof(Sidera.Core.Focusers.IFocuser).GetMethods(), m => m.Name.Contains("Hfr", StringComparison.Ordinal) || m.Name.Contains("Best", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SimulatedFocuser).GetProperties(), p => p.Name.Contains("Best", StringComparison.Ordinal));
    }

    // The measurement operation

    [Fact]
    public async Task AMeasurement_IsAnExposureWithTheCameraAtTheFocuserPosition_JudgedByTheMetric()
    {
        var (host, camera, focuser) = await Create(19000);
        await using var scope = host;
        var metric = new ScriptedMetric(input => new FocusMeasurement(input.FocuserPosition, 2.75));
        var states = new List<CameraExposureState>();
        host.EventBus.Subscribe<CameraExposureStateChanged>((e, _) =>
        {
            lock (states) { states.Add(e.NewState); }
            return Task.CompletedTask;
        });

        var measurement = await Operation(host, metric).MeasureAsync(Exposure);

        Assert.Equal(new FocusMeasurement(19000, 2.75), measurement);
        var input = Assert.Single(metric.Inputs);
        Assert.Equal((Rig, CameraId, FocuserId, 19000), (input.Rig, input.CameraId, input.FocuserId, input.FocuserPosition));
        Assert.Equal(Exposure, input.Frame.ExposureDuration); // a frame of a real exposure
        Assert.Equal([CameraExposureState.Exposing, CameraExposureState.Idle], states);
        Assert.Equal(19000, focuser.Position); // measuring does not move the focuser
        _ = camera;
    }

    [Fact]
    public async Task TheSimulatedMeasurement_IsTheHfrOfTheCurveAtThePosition()
    {
        var (host, _, focuser) = await Create(19000);
        await using var scope = host;

        var measurement = await Operation(host).MeasureAsync(Exposure);

        Assert.Equal(19000, measurement.FocuserPosition);
        Assert.Equal(new SimulatedFocusModel(20000).HfrAt(19000), measurement.Hfr, 9);
        await focuser.MoveToAsync(20000);
        Assert.Equal(1.8, (await Operation(host).MeasureAsync(Exposure)).Hfr, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    public async Task AnInvalidHfrFromTheMetric_IsRejected(double hfr)
    {
        var (host, _, _) = await Create();
        await using var scope = host;
        var metric = new ScriptedMetric(input => new FocusMeasurement(input.FocuserPosition, hfr));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Operation(host, metric).MeasureAsync(Exposure));

        Assert.Equal("The focus metric returned an invalid HFR.", error.Message);
    }

    [Fact]
    public async Task AMeasurementAtAnotherPositionThanTheExposure_IsRejected()
    {
        var (host, _, _) = await Create(19000);
        await using var scope = host;
        var metric = new ScriptedMetric(input => new FocusMeasurement(input.FocuserPosition + 100, 2.0));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Operation(host, metric).MeasureAsync(Exposure));

        Assert.Contains("another focuser position", error.Message);
    }

    [Fact]
    public async Task AMetricThatFails_FailsTheMeasurement()
    {
        var (host, _, _) = await Create();
        await using var scope = host;
        var metric = new ScriptedMetric(_ => throw new InvalidOperationException("no stars found"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Operation(host, metric).MeasureAsync(Exposure));

        Assert.Equal("no stars found", error.Message);
    }

    [Fact]
    public async Task ADisconnectedCameraOrFocuser_CannotMeasure_AndNothingIsConnected()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(CameraId, "Camera");
        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF");
        host.AddSimulatedFocusModel(Rig, new SimulatedFocusModel(20000));

        var noFocuser = await Assert.ThrowsAsync<InvalidOperationException>(() => Operation(host).MeasureAsync(Exposure));
        await focuser.ConnectAsync();
        var noCamera = await Assert.ThrowsAsync<InvalidOperationException>(() => Operation(host).MeasureAsync(Exposure));

        Assert.Equal("Focuser 'focuser.main' is not connected.", noFocuser.Message);
        Assert.Equal("Camera 'camera.main' is not connected.", noCamera.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, camera.ConnectionState);
    }

    [Fact]
    public async Task AFailingExposure_FailsTheMeasurement()
    {
        await using var host = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main");
        await camera.ConnectAsync();
        camera.Failure = new InvalidOperationException("sensor error"); // from now on every exposure fails
        host.AddDevice(camera);
        var focuser = host.AddSimulatedFocuser(FocuserId, "EAF");
        await focuser.ConnectAsync();

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Operation(host).MeasureAsync(Exposure));

        Assert.Contains("sensor error", error.Message);
    }

    [Fact]
    public async Task ACancelledExposure_CancelsTheMeasurement()
    {
        var (host, camera, _) = await Create();
        await using var scope = host;
        using var cts = new CancellationTokenSource();

        var measuring = Operation(host).MeasureAsync(TimeSpan.FromSeconds(30), cts.Token);
        while (camera.ExposureState != CameraExposureState.Exposing)
        {
            await Task.Delay(2);
        }

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring);

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
    }

    [Fact]
    public async Task TheOperation_TakesNoResources_ItsCallerOwnsTheDevices()
    {
        var (host, _, _) = await Create();
        await using var scope = host;

        await Operation(host).MeasureAsync(Exposure);

        Assert.False(host.ResourceManager.IsHeld(Sidera.Core.Resources.ResourceId.ForDevice(CameraId)));
        Assert.False(host.ResourceManager.IsHeld(Sidera.Core.Resources.ResourceId.ForDevice(FocuserId)));
    }
}

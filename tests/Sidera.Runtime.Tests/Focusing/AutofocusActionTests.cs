using Sidera.Core.Devices;
using Sidera.Core.Events;
using Sidera.Core.FilterWheels;
using Sidera.Core.Focusing;
using Sidera.Core.Focusers;
using Sidera.Core.Resources;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Sequencing;
using Sidera.Runtime.Tests.Devices;
using Sidera.Runtime.Tests.Sequencing;

namespace Sidera.Runtime.Tests.Focusing;

/// <summary>The autofocus action: what it owns, what it leaves alone, and how it ends.</summary>
public class AutofocusActionTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly RigId MainRig = new("rig.main");
    private static readonly RigId WideRig = new("rig.wide");
    private static readonly DeviceId MainCamera = new("camera.main");
    private static readonly DeviceId MainFocuser = new("focuser.main");
    private static readonly DeviceId WideCamera = new("camera.wide");
    private static readonly DeviceId WideFocuser = new("focuser.wide");
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 23.5, 15.7, 6248, 4176);

    private static readonly AutofocusOptions Quick = new(TimeSpan.FromMilliseconds(40), 300, 7);

    private static async Task<SideraRuntimeHost> CreateHost(int mainStart = 19500, int stepsPerSecond = 20000)
    {
        var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(MainCamera, "Main Camera", seed: 1);
        host.AddSimulatedCamera(WideCamera, "Wide Camera", seed: 2);
        host.AddSimulatedFocuser(MainFocuser, "Main Focuser", mainStart, stepsPerSecond: stepsPerSecond, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFocuser(WideFocuser, "Wide Focuser", 5500, maxPosition: 12000, stepsPerSecond: stepsPerSecond, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000));
        host.AddSimulatedFocusModel(WideRig, new SimulatedFocusModel(6000));
        host.AddRig(new Rig(MainRig, "Main Rig", MainCamera, Optics, MainFocuser));
        host.AddRig(new Rig(WideRig, "Wide Rig", WideCamera, Optics, WideFocuser));
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        return host;
    }

    private static AutofocusAction Autofocus(
        SideraRuntimeHost host, string rig = "rig.main", AutofocusOptions? options = null, IFocusMetricProvider? metrics = null)
    {
        host.RigRegistry.TryGet(new RigId(rig), out var found);
        return AutofocusAction.ForRig(host.DeviceRegistry, found!, options ?? Quick, metrics ?? host.FocusMetrics, host.EventBus);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private sealed class FailingMetric : IFocusMetricProvider
    {
        public Task<FocusMeasurement> MeasureAsync(FocusMetricInput input, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the analysis crashed");
    }

    // The action

    [Fact]
    public async Task Action_NeedsTheCameraAndTheFocuserOfTheRig_AndNothingElse()
    {
        await using var host = await CreateHost();

        var action = Autofocus(host);

        Assert.Equal(
            new[] { ResourceId.ForDevice(MainCamera), ResourceId.ForDevice(MainFocuser) }.OrderBy(r => r.Value),
            action.RequiredResources.OrderBy(r => r.Value));
        Assert.Equal("Autofocus", action.Name);
        Assert.Equal((MainRig, MainCamera, MainFocuser), (action.RigId, action.CameraId, action.FocuserId));
    }

    [Fact]
    public async Task Action_ForARigWithoutAFocuser_IsRefused_WithoutFallingBackToAnotherFocuser()
    {
        await using var host = await CreateHost();
        var bare = new Rig(new RigId("rig.bare"), "Bare Rig", MainCamera, Optics);

        var error = Assert.Throws<InvalidOperationException>(
            () => AutofocusAction.ForRig(host.DeviceRegistry, bare, Quick, host.FocusMetrics));

        Assert.Equal("The rig 'rig.bare' has no focuser, so it cannot be focused.", error.Message);
    }

    [Fact]
    public async Task Action_RefusesOptionsThatCannotBeRun()
    {
        await using var host = await CreateHost();

        Assert.Throws<ArgumentException>(() => Autofocus(host, options: new AutofocusOptions(TimeSpan.Zero, 300, 7)));
        Assert.Throws<ArgumentException>(() => Autofocus(host, options: new AutofocusOptions(TimeSpan.FromSeconds(1), 0, 7)));
        Assert.Throws<ArgumentException>(() => Autofocus(host, options: new AutofocusOptions(TimeSpan.FromSeconds(1), 300, 8)));
    }

    [Fact]
    public async Task Action_Focuses_AndReturnsTheResultAsPayload()
    {
        await using var host = await CreateHost(mainStart: 19500);

        var result = await Autofocus(host).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        var payload = Assert.IsType<AutofocusResult>(result.Payload);
        Assert.InRange(payload.BestPosition, 19950, 20050);
        Assert.Equal(19500, payload.InitialPosition);
        Assert.Equal(7, payload.Measurements.Count);
        Assert.InRange(payload.BestHfr, 1.8, 1.85);
        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);
        Assert.Equal(payload.FinalPosition, focuser.Position);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
    }

    [Fact]
    public async Task Action_PublishesItsProgressForTheRig_AndEndsWithCompleted()
    {
        await using var host = await CreateHost();
        var seen = new List<AutofocusProgressChanged>();
        host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            lock (seen) { seen.Add(e); }
            return Task.CompletedTask;
        });

        await Autofocus(host).ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.All(seen, e => Assert.Equal(MainRig, e.RigId));
        Assert.Equal(AutofocusPhase.Measuring, seen[0].Progress.Phase);
        Assert.Equal(AutofocusPhase.Completed, seen[^1].Progress.Phase);
        Assert.Equal(7, seen.Count(e => e.Progress.Phase == AutofocusPhase.Measuring && e.Progress.SampleIndex > 0));
        Assert.NotNull(seen[^1].Progress.BestPosition);
    }

    [Fact]
    public async Task Action_KeepsNoResultState_ExecutionsAreIndependent()
    {
        await using var host = await CreateHost();
        var action = Autofocus(host);

        var first = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);
        var second = await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.NotSame(first.Payload, second.Payload);
        Assert.Equal(((AutofocusResult)second.Payload!).InitialPosition, ((AutofocusResult)first.Payload!).FinalPosition);
    }

    [Fact]
    public async Task Action_UsesTheFilterThatIsInTheLightPath_AndLeavesTheFilterWheelAlone()
    {
        await using var host = await CreateHost();
        var wheel = host.AddSimulatedFilterWheel(
            new DeviceId("filterwheel.main"), "EFW", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha"), new FilterSlot(2, "OIII")],
            startSlotIndex: 1, moveDuration: TimeSpan.FromMilliseconds(5));
        await wheel.ConnectAsync();
        var moves = 0;
        host.EventBus.Subscribe<FilterWheelMotionStateChanged>((_, _) =>
        {
            Interlocked.Increment(ref moves);
            return Task.CompletedTask;
        });

        var action = Autofocus(host);
        await action.ExecuteAsync(NoContext.Instance, CancellationToken.None);

        Assert.Equal(new FilterSlot(1, "Ha"), wheel.CurrentSlot);
        Assert.Equal(0, moves);
        Assert.DoesNotContain(action.RequiredResources, r => r.Value.Contains("filterwheel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Action_AsksNothingOfTheMountOrTheGuider()
    {
        await using var host = await CreateHost();
        host.AddSimulatedMount(new DeviceId("mount.eq6"), "EQ6");
        host.AddSimulatedGuider(new DeviceId("guider.main"), "Guider");

        var action = Autofocus(host);

        Assert.DoesNotContain(action.RequiredResources, r => r.Value.Contains("mount", StringComparison.Ordinal) || r.Value.Contains("guider", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Action_RejectsAnUnknownOrWrongOrDisconnectedDevice_WithAConciseMessage()
    {
        await using var host = await CreateHost();
        var wide = (SimulatedFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == WideFocuser);
        var unknown = new AutofocusAction(host.DeviceRegistry, MainRig, MainCamera, new DeviceId("focuser.none"), Quick, host.FocusMetrics);
        var wrongKind = new AutofocusAction(host.DeviceRegistry, MainRig, MainCamera, MainCamera, Quick, host.FocusMetrics);
        var noCamera = new AutofocusAction(host.DeviceRegistry, MainRig, new DeviceId("camera.none"), MainFocuser, Quick, host.FocusMetrics);

        var a = await Assert.ThrowsAsync<InvalidOperationException>(() => unknown.ExecuteAsync(NoContext.Instance, default));
        var b = await Assert.ThrowsAsync<InvalidOperationException>(() => wrongKind.ExecuteAsync(NoContext.Instance, default));
        var c = await Assert.ThrowsAsync<InvalidOperationException>(() => noCamera.ExecuteAsync(NoContext.Instance, default));
        await wide.DisconnectAsync();
        var d = await Assert.ThrowsAsync<InvalidOperationException>(() => Autofocus(host, "rig.wide").ExecuteAsync(NoContext.Instance, default));

        Assert.Contains("'focuser.none' is not registered", a.Message);
        Assert.Contains("'camera.main' is not a focuser", b.Message);
        Assert.Contains("'camera.none' is not registered", c.Message);
        Assert.Equal("Focuser 'focuser.wide' is not connected.", d.Message);
        Assert.Equal(DeviceConnectionState.Disconnected, wide.ConnectionState); // never connects anything
    }

    [Fact]
    public async Task Action_WithADisconnectedCamera_FailsWithoutMovingTheFocuser()
    {
        await using var host = await CreateHost();
        var camera = (SimulatedCamera)host.DeviceRegistry.GetAll().Single(d => d.Id == MainCamera);
        await camera.DisconnectAsync();
        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Autofocus(host).ExecuteAsync(NoContext.Instance, default));

        Assert.Equal("Camera 'camera.main' is not connected.", error.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(19500, focuser.Position); // it did not start by moving
    }

    // Failure

    [Fact]
    public async Task AMetricThatFails_FailsTheRun_AndPublishesThatItStopped()
    {
        await using var host = await CreateHost();
        var phases = new List<AutofocusPhase>();
        host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            lock (phases) { phases.Add(e.Progress.Phase); }
            return Task.CompletedTask;
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Autofocus(host, metrics: new FailingMetric()).ExecuteAsync(NoContext.Instance, default));

        Assert.Equal("the analysis crashed", error.Message);
        Assert.Equal(AutofocusPhase.Stopped, phases[^1]);
    }

    [Fact]
    public async Task AFlatCurve_FailsTheRun_WithTheUserFacingMessage()
    {
        await using var host = await CreateHost();
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000, 2.5, 0));

        var error = await Assert.ThrowsAsync<AutofocusFailedException>(() => Autofocus(host).ExecuteAsync(NoContext.Instance, default));

        Assert.Equal("Autofocus failed: no reliable focus minimum was found.", error.Message);
    }

    [Fact]
    public async Task AFailingFocuserMove_AndAFailingExposure_FailTheRun()
    {
        await using var host = new SideraRuntimeHost();
        var focuser = new ScriptedFocuser("focuser.main", 19500) { FailOnMove = 2 };
        host.AddDevice(focuser);
        host.AddSimulatedCamera(MainCamera, "Camera");
        host.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000));
        await host.DeviceRegistry.GetAll().OfType<ICamera>().Single().ConnectAsync();
        var action = new AutofocusAction(host.DeviceRegistry, MainRig, MainCamera, focuser.Id, Quick, host.FocusMetrics);

        var move = await Assert.ThrowsAsync<InvalidOperationException>(() => action.ExecuteAsync(NoContext.Instance, default));

        Assert.Equal("The focuser motor stalled.", move.Message);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);

        await using var other = new SideraRuntimeHost();
        var camera = new FakeCamera("camera.main");
        await camera.ConnectAsync();
        camera.Failure = new InvalidOperationException("sensor error");
        other.AddDevice(camera);
        var good = new ScriptedFocuser("focuser.main", 19500);
        other.AddDevice(good);
        other.AddSimulatedFocusModel(MainRig, new SimulatedFocusModel(20000));
        var failing = new AutofocusAction(other.DeviceRegistry, MainRig, camera.Id, good.Id, Quick, other.FocusMetrics);

        var exposure = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ExecuteAsync(NoContext.Instance, default));

        Assert.Equal("sensor error", exposure.Message);
    }

    // Resources, through the runner

    [Fact]
    public async Task WhileItRuns_BothDevicesAreHeld_AndAreFreeAfterwards_AfterSuccessFailureAndCancel()
    {
        await using var host = await CreateHost();
        var cameraResource = ResourceId.ForDevice(MainCamera);
        var focuserResource = ResourceId.ForDevice(MainFocuser);

        // Success
        var runner = new SequenceRunner(host.ResourceManager);
        var run = runner.RunAsync(new Sequence("s", [Autofocus(host)]));
        await WaitUntil(() => host.ResourceManager.IsHeld(cameraResource) && host.ResourceManager.IsHeld(focuserResource), "both held");
        await run.WaitAsync(Bound);
        Assert.False(host.ResourceManager.IsHeld(cameraResource) || host.ResourceManager.IsHeld(focuserResource));

        // Failure
        var failing = new SequenceRunner(host.ResourceManager);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.RunAsync(new Sequence("s", [Autofocus(host, metrics: new FailingMetric())])).WaitAsync(Bound));
        Assert.Equal(SequenceState.Failed, failing.State);
        Assert.False(host.ResourceManager.IsHeld(cameraResource) || host.ResourceManager.IsHeld(focuserResource));

        // Cancel
        var cancelling = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();
        var slow = Autofocus(host, options: new AutofocusOptions(TimeSpan.FromSeconds(30), 300, 7));
        var cancelled = cancelling.RunAsync(new Sequence("s", [slow]), cts.Token);
        await WaitUntil(() => host.ResourceManager.IsHeld(cameraResource), "held for the exposure");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(SequenceState.Cancelled, cancelling.State);
        Assert.False(host.ResourceManager.IsHeld(cameraResource) || host.ResourceManager.IsHeld(focuserResource));
    }

    [Fact]
    public async Task AnExposureWithTheSameCamera_CannotRunDuringAutofocus_ItWaitsForTheResource()
    {
        await using var host = await CreateHost();
        var runner = new SequenceRunner(host.ResourceManager);
        var completions = new List<(DateTime At, string Name)>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completions) { completions.Add((DateTime.UtcNow, e.Position.StepName)); }
        };
        var exposure = new CameraExposureAction(host.DeviceRegistry, MainCamera, TimeSpan.FromMilliseconds(30));

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [Autofocus(host), exposure])]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "the exposure waiting for the camera");
        await run.WaitAsync(Bound);

        var autofocusEnd = completions.Single(c => c.Name == "Autofocus").At;
        var exposureEnd = completions.Single(c => c.Name.StartsWith("Exposure", StringComparison.Ordinal)).At;
        Assert.True(exposureEnd >= autofocusEnd, "the exposure ran during the autofocus");
    }

    [Fact]
    public async Task AFocuserMoveOnTheSameFocuser_CannotRunDuringAutofocus()
    {
        await using var host = await CreateHost();
        var runner = new SequenceRunner(host.ResourceManager);
        var move = new MoveFocuserAction(host.DeviceRegistry, MainFocuser, 25000);

        var run = runner.RunAsync(new Sequence("s", [new ParallelStep("p", [Autofocus(host), move])]));
        await WaitUntil(() => host.ResourceManager.WaitingCount == 1, "the move waiting for the focuser");
        await run.WaitAsync(Bound);

        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);
        Assert.Equal(25000, focuser.Position); // the move came after the autofocus, and was not disturbed by it
    }

    [Fact]
    public async Task AnotherRigWithItsOwnCameraAndFocuser_ExposesAndMovesWhileThisOneFocuses()
    {
        await using var host = await CreateHost();
        var runner = new SequenceRunner(host.ResourceManager);
        var completions = new List<(DateTime At, string Name)>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completions) { completions.Add((DateTime.UtcNow, e.Position.StepName)); }
        };
        var wide = new SequenceGroup("wide", Enumerable.Range(0, 12).Select(_ =>
            (ISequenceStep)new CameraExposureAction(host.DeviceRegistry, WideCamera, TimeSpan.FromMilliseconds(40))).Append(
            new MoveFocuserAction(host.DeviceRegistry, WideFocuser, 6000)).ToList());
        // A slower autofocus: about two seconds.
        var slow = Autofocus(host, options: new AutofocusOptions(TimeSpan.FromMilliseconds(150), 300, 7));

        await runner.RunAsync(new Sequence("s", [new ParallelStep("p", [slow, wide])])).WaitAsync(Bound);

        var autofocusEnd = completions.Single(c => c.Name == "Autofocus").At;
        var wideExposures = completions.Where(c => c.Name.StartsWith("Exposure", StringComparison.Ordinal)).Select(c => c.At).ToList();
        Assert.True(wideExposures.Count(at => at < autofocusEnd) >= 6, "the other rig was held back by the autofocus");
        Assert.Equal(0, host.ResourceManager.WaitingCount);
    }

    // Cancel

    [Fact]
    public async Task CancelDuringAFocusExposure_StopsTheCamera_AndLeavesTheFocuserIdleWhereItLastArrived()
    {
        await using var host = await CreateHost();
        var camera = (ICamera)host.DeviceRegistry.GetAll().Single(d => d.Id == MainCamera);
        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);
        using var cts = new CancellationTokenSource();
        var run = Autofocus(host, options: new AutofocusOptions(TimeSpan.FromSeconds(30), 300, 7)).ExecuteAsync(NoContext.Instance, cts.Token);

        await WaitUntil(() => camera.ExposureState == CameraExposureState.Exposing, "the first focus exposure");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(19500 - 900, focuser.Position); // the lowest sample position: where it last arrived
    }

    [Fact]
    public async Task CancelDuringAFocuserMove_StopsTheMove_AndLeavesTheCameraIdle()
    {
        await using var host = await CreateHost(stepsPerSecond: 100);
        var camera = (ICamera)host.DeviceRegistry.GetAll().Single(d => d.Id == MainCamera);
        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);
        using var cts = new CancellationTokenSource();
        var run = Autofocus(host).ExecuteAsync(NoContext.Instance, cts.Token);

        await WaitUntil(() => focuser.MotionState == FocuserMotionState.Moving, "the first move");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(19500, focuser.Position);
    }

    [Fact]
    public async Task CancelThroughTheRunner_ThenAnotherOperationOnTheSameDevicesSucceeds()
    {
        await using var host = await CreateHost();
        var runner = new SequenceRunner(host.ResourceManager);
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(
            new Sequence("s", [Autofocus(host, options: new AutofocusOptions(TimeSpan.FromSeconds(30), 300, 7))]), cts.Token);
        await WaitUntil(() => host.ResourceManager.IsHeld(ResourceId.ForDevice(MainCamera)), "running");
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(Bound));

        await host.DeviceOperations.MoveFocuserToAsync(MainFocuser, 20000);
        var frame = await host.DeviceOperations.ExposeAsync(MainCamera, TimeSpan.FromMilliseconds(20));

        Assert.NotNull(frame);
    }

    // Pause

    [Fact]
    public async Task PauseDuringAutofocus_LetsItFinish_ThenPausesBeforeTheNextStep_AndResumeGoesOn()
    {
        await using var host = await CreateHost();
        var runner = new SequenceRunner(host.ResourceManager);
        var focuser = (IFocuser)host.DeviceRegistry.GetAll().Single(d => d.Id == MainFocuser);
        var next = new CameraExposureAction(host.DeviceRegistry, MainCamera, TimeSpan.FromMilliseconds(20));
        var seen = new List<AutofocusProgressChanged>();
        host.EventBus.Subscribe<AutofocusProgressChanged>((e, _) =>
        {
            lock (seen) { seen.Add(e); }
            return Task.CompletedTask;
        });
        var completions = new List<string>();
        runner.StepCompleted += (_, e) =>
        {
            lock (completions) { completions.Add(e.Position.StepName); }
        };

        var run = runner.RunAsync(new Sequence("s", [Autofocus(host), next]));
        await WaitUntil(() => seen.Any(e => e.Progress is { Phase: AutofocusPhase.Measuring, SampleIndex: >= 2 }), "a few samples");
        runner.RequestPause();
        await WaitUntil(() => runner.State == SequenceState.Paused, "paused");

        // The whole curve was done: no sample position was left standing, the focuser is at the best focus.
        Assert.Equal(AutofocusPhase.Completed, seen[^1].Progress.Phase);
        Assert.Contains("Autofocus", completions);
        Assert.DoesNotContain(completions, name => name.StartsWith("Exposure", StringComparison.Ordinal)); // not the next step yet
        Assert.InRange(focuser.Position, 19950, 20050);
        Assert.Equal(FocuserMotionState.Idle, focuser.MotionState);

        runner.Resume();
        await run.WaitAsync(Bound);
        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Contains(completions, name => name.StartsWith("Exposure", StringComparison.Ordinal));
    }
}

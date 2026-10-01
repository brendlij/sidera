using Astra.Core.Devices;
using Astra.Core.Resources;
using Astra.Desktop.ViewModels;
using Astra.Core.Sequencing;
using Astra.Runtime;
using Astra.Runtime.Devices;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop.Tests.ViewModels;

public class MainViewModelTests
{
    private static (MainViewModel Vm, SimulatedCamera Camera, AstraRuntimeHost Host) CreateWithHost(
        TimeSpan? exposure = null,
        TimeSpan? sequenceExposure = null,
        TimeSpan? sequenceDelay = null
    )
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var vm = new MainViewModel(camera, host, action => action(), exposure, sequenceExposure, sequenceDelay);
        return (vm, camera, host);
    }

    private static (MainViewModel Vm, SimulatedCamera Camera) Create(
        TimeSpan? exposure = null,
        TimeSpan? sequenceExposure = null,
        TimeSpan? sequenceDelay = null
    )
    {
        var (vm, camera, _) = CreateWithHost(exposure, sequenceExposure, sequenceDelay);
        return (vm, camera);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition.");
            await Task.Delay(10);
        }
    }

    private static void AssertManualControlsAvailableForConnectedCamera(MainViewModel vm)
    {
        Assert.False(vm.IsSequenceRunning);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.RunSequenceCommand.CanExecute(null));
        Assert.False(vm.CancelSequenceCommand.CanExecute(null));
    }

    private static async Task<(MainViewModel Vm, SimulatedCamera Camera)> CreateConnected(
        TimeSpan? sequenceExposure = null,
        bool registerCamera = true,
        TimeSpan? sequenceDelay = null
    )
    {
        var (vm, camera, host) = CreateWithHost(
            sequenceExposure: sequenceExposure,
            sequenceDelay: sequenceDelay ?? TimeSpan.FromMilliseconds(20));
        await vm.ConnectCommand.ExecuteAsync(null);

        if (!registerCamera)
        {
            // Connected manually, but the sequence can no longer find the camera.
            host.DeviceRegistry.Unregister(camera.Id);
        }

        return (vm, camera);
    }

    [Fact]
    public async Task RunSequence_IsOnlyAvailableWhenCameraIsConnectedAndIdle()
    {
        var (vm, _) = Create();
        Assert.False(vm.RunSequenceCommand.CanExecute(null));

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.RunSequenceCommand.CanExecute(null));

        await vm.DisconnectCommand.ExecuteAsync(null);
        Assert.False(vm.RunSequenceCommand.CanExecute(null));
    }

    [Fact]
    public void DemoSequence_IsRepeatOfGroupWithExposureAndDelay()
    {
        var (vm, _) = Create();

        var repeat = Assert.IsType<RepeatStep>(Assert.Single(vm.DemoSequence.Steps));
        Assert.Equal(3, repeat.Count);
        var group = Assert.IsType<SequenceGroup>(repeat.Child);
        Assert.Equal("Imaging Block", group.Name);
        Assert.Collection(
            group.Children,
            child => Assert.IsType<CameraExposureAction>(child),
            child => Assert.Equal(TimeSpan.FromSeconds(1), Assert.IsType<DelayAction>(child).Duration));
    }

    [Fact]
    public void SequenceOutline_DescribesRepeatGroupExposureAndWait()
    {
        var (vm, _) = Create();

        Assert.Collection(
            vm.SequenceOutline,
            item =>
            {
                Assert.Equal("Repeat", item.Title);
                Assert.Equal("×3", item.Detail);
                Assert.Equal(0, item.IndentWidth);
            },
            item =>
            {
                Assert.Equal("Imaging Block", item.Title);
                Assert.Equal(string.Empty, item.Detail);
                Assert.True(item.IndentWidth > 0);
            },
            item =>
            {
                Assert.Equal("Exposure", item.Title);
                Assert.Equal("2s", item.Detail);
                Assert.True(item.IndentWidth > vm.SequenceOutline[1].IndentWidth);
            },
            item =>
            {
                Assert.Equal("Wait", item.Title);
                Assert.Equal("1s", item.Detail);
                Assert.Equal(vm.SequenceOutline[2].IndentWidth, item.IndentWidth);
            });
    }

    [Fact]
    public async Task Sequence_ExposesHierarchyOfRunningStep()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SequenceStepText.StartsWith("Repeat × 3 · 2 / 3") && vm.CurrentStepName == "Exposure 0.4s");

        Assert.Equal(
            new[] { "Repeat × 3 · 2 / 3", "Imaging Block · 1 / 2", "Exposure 0.4s" },
            vm.SequenceStatusLines.Select(l => l.Text));
        Assert.Equal(new[] { true, true, false }, vm.SequenceStatusLines.Select(l => l.IsContainer));
        await run;

        Assert.True(vm.IsSequenceCompleted);
        Assert.False(vm.IsSequenceFailed);
        Assert.False(vm.IsSequenceCancelled);
    }

    [Fact]
    public async Task RunSequence_ExecutesThreeExposuresAndLeavesCameraConnected()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30));

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Completed, vm.SequenceState);
        Assert.Equal("Repeat × 3 · 3 / 3 › Imaging Block · 2 / 2 › Wait 0.02s", vm.SequenceStepText);
        Assert.Equal("Wait 0.02s", vm.CurrentStepName);
        Assert.Null(vm.SequenceError);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }

    [Fact]
    public async Task Sequence_ReflectsStateAndStepWhileRunning_AndDisablesManualControls()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SequenceStepText.StartsWith("Repeat × 3 · 2 / 3"));

        Assert.Equal(SequenceState.Running, vm.SequenceState);
        Assert.Equal("Exposure 0.4s", vm.CurrentStepName);
        Assert.True(vm.IsSequenceRunning);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
        Assert.False(vm.RunSequenceCommand.CanExecute(null));
        Assert.True(vm.CancelSequenceCommand.CanExecute(null));

        await run;
    }

    [Fact]
    public async Task Sequence_ShowsEveryRepeatIterationInOrder()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30));
        var shown = new List<string>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SequenceStepText))
            {
                shown.Add(vm.SequenceStepText);
            }
        };

        await vm.RunSequenceCommand.ExecuteAsync(null);

        // A lone child never adds "1 / 1" noise, and the iterations appear in order.
        Assert.DoesNotContain(shown, text => text.Contains("1 / 1"));
        var iterations = shown
            .Select(text => new[] { "1 / 3", "2 / 3", "3 / 3" }.FirstOrDefault(i => text.Contains($"· {i}")))
            .Where(i => i is not null)
            .Distinct()
            .ToArray();
        Assert.Equal(new[] { "1 / 3", "2 / 3", "3 / 3" }, iterations);
    }

    [Fact]
    public async Task SequenceExposures_UpdateLastFrameAfterEachStep()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30));
        Assert.Null(vm.LastFrame);
        var frames = new List<CameraFrame?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LastFrame))
            {
                frames.Add(vm.LastFrame);
            }
        };

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.NotNull(f));
        Assert.Equal(3, frames.Distinct().Count());
        Assert.Same(frames[2], vm.LastFrame);
    }

    [Fact]
    public async Task SequenceFrame_IsShownBeforeTheSequenceEnds()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(300));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.LastFrame is not null);

        Assert.True(vm.IsSequenceRunning);
        await run;
    }

    [Fact]
    public async Task CancelledSequenceExposure_ProducesNoFrame()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromSeconds(10));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => camera.ExposureState == CameraExposureState.Exposing);
        vm.CancelSequenceCommand.Execute(null);
        await run;

        Assert.Null(vm.LastFrame);
    }

    [Fact]
    public async Task LiveStatus_MovesFromExposureToWaitWithinTheBlock_AndKeepsTheFrame()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30), sequenceDelay: TimeSpan.FromSeconds(10));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.CurrentStepName == "Exposure 0.03s" && vm.LastFrame is null);
        Assert.Equal(
            new[] { "Repeat × 3 · 1 / 3", "Imaging Block · 1 / 2", "Exposure 0.03s" },
            vm.SequenceStatusLines.Select(l => l.Text));

        await WaitUntil(() => vm.CurrentStepName == "Wait 10s");

        Assert.Equal(
            new[] { "Repeat × 3 · 1 / 3", "Imaging Block · 2 / 2", "Wait 10s" },
            vm.SequenceStatusLines.Select(l => l.Text));
        // The frame of the finished exposure stays visible while waiting; no exposure runs.
        Assert.NotNull(vm.LastFrame);
        Assert.False(vm.IsExposing);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);

        vm.CancelSequenceCommand.Execute(null);
        await run;
    }

    [Fact]
    public async Task CancelDuringWait_CancelsSequence_KeepsFrame_AndLeavesCameraConnectedAndIdle()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30), sequenceDelay: TimeSpan.FromSeconds(10));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.CurrentStepName == "Wait 10s");
        var frame = vm.LastFrame;
        vm.CancelSequenceCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Cancelled, vm.SequenceState);
        Assert.True(vm.IsSequenceCancelled);
        Assert.Null(vm.SequenceError);
        Assert.False(vm.IsSequenceRunning);
        Assert.NotNull(frame);
        Assert.Same(frame, vm.LastFrame);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }

    [Fact]
    public async Task ActiveSequenceStatusLines_ShowTheSingleRunningBranchOfTheDemo_AndClearAfterwards()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.ActiveSequenceStatusLines.Count == 1 && vm.CurrentStepName == "Exposure 0.4s");

        var line = Assert.Single(vm.ActiveSequenceStatusLines);
        Assert.Equal("Repeat × 3 · 1 / 3 › Imaging Block · 1 / 2 › Exposure 0.4s", line.Text);
        await run;

        Assert.Empty(vm.ActiveSequenceStatusLines);
    }

    [Fact]
    public void ActiveBranches_OfAParallelStep_AreListedOnePerRunningLeaf()
    {
        var root = new SequenceExecutionPosition("Parallel", 0, 1);
        var branchA = new SequenceExecutionPosition("Exposure A", 0, 2, root);
        var branchB = new SequenceExecutionPosition("Wait 5s", 1, 2, root);

        var lines = SequenceStatusLine.ForActiveBranches([root, branchA, branchB]);

        Assert.Equal(
            new[] { "Parallel · 1 / 2 › Exposure A", "Parallel · 2 / 2 › Wait 5s" },
            lines.Select(l => l.Text));
    }

    [Fact]
    public async Task ManualExposure_WaitsForResourceHeldElsewhere_EvenThoughTheUiAllowsIt()
    {
        var (vm, camera, host) = CreateWithHost(exposure: TimeSpan.FromMilliseconds(30));
        await vm.ConnectCommand.ExecuteAsync(null);
        var cameraResource = ResourceId.ForDevice(camera.Id);
        var otherHolder = await host.ResourceManager.AcquireAsync([cameraResource]);

        // The command is enabled (UX), but the runtime makes it wait: correctness comes from the ResourceManager.
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        var run = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.False(run.IsCompleted);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Null(vm.LastFrame);

        otherHolder.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(vm.LastFrame);
        Assert.False(host.ResourceManager.IsHeld(cameraResource));
    }

    [Fact]
    public async Task ManualExposure_CancelledWhileWaitingForResource_NeverStarts()
    {
        var (vm, camera, host) = CreateWithHost();
        await vm.ConnectCommand.ExecuteAsync(null);
        var cameraResource = ResourceId.ForDevice(camera.Id);
        using var otherHolder = await host.ResourceManager.AcquireAsync([cameraResource]);

        var run = vm.StartExposureCommand.ExecuteAsync(null);
        Assert.True(vm.IsManualExposureRunning);
        vm.CancelExposureCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsManualExposureRunning);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Null(vm.LastFrame);
        Assert.True(host.ResourceManager.IsHeld(cameraResource)); // still the other holder's
    }

    [Fact]
    public async Task CancelExposure_IsOnlyAvailableWhileManualExposureRuns()
    {
        var (vm, _) = await CreateConnected();
        Assert.False(vm.CancelExposureCommand.CanExecute(null));

        var run = vm.StartExposureCommand.ExecuteAsync(null);
        Assert.True(vm.IsManualExposureRunning);
        Assert.True(vm.CancelExposureCommand.CanExecute(null));

        vm.CancelExposureCommand.Execute(null);
        await run;

        Assert.False(vm.IsManualExposureRunning);
        Assert.False(vm.CancelExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelExposure_StopsExposure_ProducesNoFrame_AndRestoresControls()
    {
        var (vm, camera) = Create(exposure: TimeSpan.FromSeconds(10));
        await vm.ConnectCommand.ExecuteAsync(null);

        var run = vm.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => camera.ExposureState == CameraExposureState.Exposing);
        vm.CancelExposureCommand.Execute(null);
        await run;

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureState.Idle, vm.ExposureState);
        Assert.Null(vm.LastFrame);
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
    }

    [Fact]
    public async Task CancelSequence_StopsRunAndRestoresControls()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromSeconds(10));

        var run = vm.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.SequenceStepText.StartsWith("Repeat × 3 · 1 / 3") && camera.ExposureState == CameraExposureState.Exposing);
        vm.CancelSequenceCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Cancelled, vm.SequenceState);
        Assert.Null(vm.SequenceError);
        Assert.False(vm.IsSequenceRunning);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        // The cancelled sequence left the camera connected; normal camera-state rules apply again.
        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
        Assert.True(vm.RunSequenceCommand.CanExecute(null));
        Assert.False(vm.CancelSequenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedSequence_SurfacesErrorWithoutThrowing_AndRecovers()
    {
        // Connected manually, but not registered, so the sequence cannot find the camera.
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30), registerCamera: false);

        await vm.RunSequenceCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Failed, vm.SequenceState);
        Assert.Contains("is not registered", vm.SequenceError);
        Assert.StartsWith("Repeat × 3 · 1 / 3", vm.SequenceStepText);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }


    [Fact]
    public void InitialState_OnlyConnectIsEnabled()
    {
        var (vm, _) = Create();

        Assert.Equal("Main Camera", vm.CameraName);
        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, vm.ExposureState);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.DisconnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_UpdatesStateAndEnablesCommands()
    {
        var (vm, _) = Create();

        await vm.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
        Assert.False(vm.ConnectCommand.CanExecute(null));
        Assert.True(vm.DisconnectCommand.CanExecute(null));
        Assert.True(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_ReturnsToDisconnected()
    {
        var (vm, _) = Create();
        await vm.ConnectCommand.ExecuteAsync(null);

        await vm.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.True(vm.ConnectCommand.CanExecute(null));
        Assert.False(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_ShowsExposingWhileRunningThenIdle()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(300));
        await vm.ConnectCommand.ExecuteAsync(null);

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.Equal(CameraExposureState.Exposing, vm.ExposureState);
        Assert.False(vm.StartExposureCommand.CanExecute(null));

        await running;

        Assert.Equal(CameraExposureState.Idle, vm.ExposureState);
        Assert.True(vm.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_IsDisabledWhileExposing()
    {
        var (vm, camera) = Create(TimeSpan.FromMilliseconds(300));
        await vm.ConnectCommand.ExecuteAsync(null);

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.False(vm.DisconnectCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        await running;

        Assert.True(vm.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_UpdatesProgressProperties()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(700));
        await vm.ConnectCommand.ExecuteAsync(null);
        var progressSeen = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ExposureProgress))
            {
                progressSeen.Add(vm.ExposureProgress);
            }
        };

        var running = vm.StartExposureCommand.ExecuteAsync(null);

        Assert.True(vm.IsExposing);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.ExposureDuration);

        await running;

        Assert.Contains(progressSeen, p => p > 0.0 && p < 1.0);
        Assert.Equal(1.0, vm.ExposureProgress);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.ExposureElapsed);
        Assert.False(vm.IsExposing);
    }

    [Fact]
    public async Task CompletedExposure_StoresFrame()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(50));
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Null(vm.LastFrame);

        await vm.StartExposureCommand.ExecuteAsync(null);

        Assert.NotNull(vm.LastFrame);
        Assert.Equal(800, vm.LastFrame!.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(50), vm.LastFrame.ExposureDuration);
    }

    [Fact]
    public async Task ConnectionChanges_AreMarshalledThroughUiDispatcher()
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var posted = new List<Action>();
        var vm = new MainViewModel(camera, host, posted.Add);

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, vm.ConnectionState);
        Assert.NotEmpty(posted);

        posted.ForEach(a => a());

        Assert.Equal(DeviceConnectionState.Connected, vm.ConnectionState);
    }
}

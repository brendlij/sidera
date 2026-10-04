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
    [Theory]
    [InlineData(AppPage.Dashboard)]
    [InlineData(AppPage.Equipment)]
    [InlineData(AppPage.Session)]
    [InlineData(AppPage.Imaging)]
    [InlineData(AppPage.Diagnostics)]
    [InlineData(AppPage.Settings)]
    public async Task Navigation_SelectsTheComposedPage(AppPage page)
    {
        await using var host = new AstraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        using var vm = new MainViewModel(host, action => action());
        vm.NavigateCommand.Execute(page);
        Assert.Equal(page, vm.SelectedPage);
        Assert.Same(page switch
        {
            AppPage.Equipment => (ViewModelBase)vm.Equipment,
            AppPage.Session => vm.SessionPage,
            AppPage.Imaging => vm.Imaging,
            AppPage.Diagnostics => vm.Diagnostics,
            AppPage.Settings => vm.Settings,
            _ => vm.Dashboard
        }, vm.CurrentPage);
        Assert.Same(vm.Sequencer, vm.Dashboard.Sequencer);
        Assert.Same(vm.Imaging, vm.Dashboard.Imaging);
    }
    private sealed record TestPages(CameraViewModel Camera, SequencerViewModel Sequencer, ImagingViewModel Imaging, Sequence Sequence);

    private static TestPages CreatePages(
        SimulatedCamera camera, AstraRuntimeHost host, Action<Action> postToUi,
        TimeSpan? exposure = null, TimeSpan? sequenceExposure = null, TimeSpan? sequenceDelay = null)
    {
        var activity = new SessionActivity();
        var imaging = new ImagingViewModel();
        var cameraVm = new CameraViewModel(camera, host, postToUi, activity, imaging, exposure ?? TimeSpan.FromSeconds(5));
        var sequence = new Sequence("Demo", [new RepeatStep(3, new SequenceGroup("Imaging Block", [
            new CameraExposureAction(host.DeviceRegistry, camera.Id, sequenceExposure ?? TimeSpan.FromSeconds(2)),
            new DelayAction(sequenceDelay ?? TimeSpan.FromSeconds(1))]))]);
        var sequencer = new SequencerViewModel(host, postToUi, activity, imaging, [cameraVm], sequence,
            () => cameraVm.IsConnected && cameraVm.ExposureState == CameraExposureState.Idle && !cameraVm.IsManualExposureRunning
                ? null : "Connect the camera and wait for its exposure.");
        cameraVm.Refreshed += (_, _) => sequencer.RefreshReadiness();
        return new TestPages(cameraVm, sequencer, imaging, sequence);
    }
    private static (TestPages Vm, SimulatedCamera Camera, AstraRuntimeHost Host) CreateWithHost(
        TimeSpan? exposure = null,
        TimeSpan? sequenceExposure = null,
        TimeSpan? sequenceDelay = null
    )
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var vm = CreatePages(camera, host, action => action(), exposure, sequenceExposure, sequenceDelay);
        return (vm, camera, host);
    }

    private static (TestPages Vm, SimulatedCamera Camera) Create(
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

    private static void AssertManualControlsAvailableForConnectedCamera(TestPages vm)
    {
        Assert.False(vm.Sequencer.IsRunning);
        Assert.False(vm.Camera.ConnectCommand.CanExecute(null));
        Assert.True(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
        Assert.True(vm.Sequencer.RunCommand.CanExecute(null));
        Assert.False(vm.Sequencer.CancelCommand.CanExecute(null));
    }

    private static async Task<(TestPages Vm, SimulatedCamera Camera)> CreateConnected(
        TimeSpan? sequenceExposure = null,
        bool registerCamera = true,
        TimeSpan? sequenceDelay = null
    )
    {
        var (vm, camera, host) = CreateWithHost(
            sequenceExposure: sequenceExposure,
            sequenceDelay: sequenceDelay ?? TimeSpan.FromMilliseconds(20));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);

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
        Assert.False(vm.Sequencer.RunCommand.CanExecute(null));

        await vm.Camera.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.Sequencer.RunCommand.CanExecute(null));

        await vm.Camera.DisconnectCommand.ExecuteAsync(null);
        Assert.False(vm.Sequencer.RunCommand.CanExecute(null));
    }

    [Fact]
    public void DemoSequence_IsRepeatOfGroupWithExposureAndDelay()
    {
        var (vm, _) = Create();

        var repeat = Assert.IsType<RepeatStep>(Assert.Single(vm.Sequence.Steps));
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
            vm.Sequencer.Definition,
            item =>
            {
                Assert.Equal("Repeat", item.Title);
                Assert.Equal("×3", item.Detail);
                Assert.Equal(0, item.IndentWidth);
            },
            item =>
            {
                Assert.Equal("Imaging Block", item.Title);
                Assert.Equal("Group", item.Detail);
                Assert.True(item.IndentWidth > 0);
            },
            item =>
            {
                Assert.Equal("Exposure", item.Title);
                Assert.Equal("2 s", item.Detail);
                Assert.True(item.IndentWidth > vm.Sequencer.Definition[1].IndentWidth);
            },
            item =>
            {
                Assert.Equal("Wait", item.Title);
                Assert.Equal("1 s", item.Detail);
                Assert.Equal(vm.Sequencer.Definition[2].IndentWidth, item.IndentWidth);
            });
    }

    [Fact]
    public async Task Sequence_ExposesHierarchyOfRunningStep()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.StepText.StartsWith("Repeat × 3 · 2 / 3") && vm.Sequencer.StatusLines.LastOrDefault()?.Text == "Exposure 0.4s");

        Assert.Equal(
            new[] { "Repeat × 3 · 2 / 3", "Imaging Block · 1 / 2", "Exposure 0.4s" },
            vm.Sequencer.StatusLines.Select(l => l.Text));
        Assert.Equal(new[] { true, true, false }, vm.Sequencer.StatusLines.Select(l => l.IsContainer));
        await run;

        Assert.True(vm.Sequencer.IsCompleted);
        Assert.False(vm.Sequencer.IsFailed);
        Assert.False(vm.Sequencer.IsCancelled);
    }

    [Fact]
    public async Task RunSequence_ExecutesThreeExposuresAndLeavesCameraConnected()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30));

        await vm.Sequencer.RunCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.Equal("Repeat × 3 · 3 / 3 › Imaging Block · 2 / 2 › Wait 0.02s", vm.Sequencer.StepText);
        Assert.Equal("Wait 0.02s", vm.Sequencer.StatusLines.LastOrDefault()?.Text);
        Assert.Null(vm.Sequencer.ErrorMessage);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }

    [Fact]
    public async Task Sequence_ReflectsStateAndStepWhileRunning_AndDisablesManualControls()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.StepText.StartsWith("Repeat × 3 · 2 / 3"));

        Assert.Equal(SequenceState.Running, vm.Sequencer.State);
        Assert.Equal("Exposure 0.4s", vm.Sequencer.StatusLines.LastOrDefault()?.Text);
        Assert.True(vm.Sequencer.IsRunning);
        Assert.False(vm.Camera.ConnectCommand.CanExecute(null));
        Assert.False(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.False(vm.Camera.StartExposureCommand.CanExecute(null));
        Assert.False(vm.Sequencer.RunCommand.CanExecute(null));
        Assert.True(vm.Sequencer.CancelCommand.CanExecute(null));

        await run;
    }

    [Fact]
    public async Task Sequence_ShowsEveryRepeatIterationInOrder()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30));
        var shown = new List<string>();
        vm.Sequencer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SequencerViewModel.StepText))
            {
                shown.Add(vm.Sequencer.StepText);
            }
        };

        await vm.Sequencer.RunCommand.ExecuteAsync(null);

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
        Assert.Null(vm.Imaging.LatestFrame);
        var frames = new List<CameraFrame?>();
        vm.Imaging.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImagingViewModel.LatestFrame))
            {
                frames.Add(vm.Imaging.LatestFrame);
            }
        };

        await vm.Sequencer.RunCommand.ExecuteAsync(null);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.NotNull(f));
        Assert.Equal(3, frames.Distinct().Count());
        Assert.Same(frames[2], vm.Imaging.LatestFrame);
    }

    [Fact]
    public async Task SequenceFrame_IsShownBeforeTheSequenceEnds()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(300));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Imaging.LatestFrame is not null);

        Assert.True(vm.Sequencer.IsRunning);
        await run;
    }

    [Fact]
    public async Task CancelledSequenceExposure_ProducesNoFrame()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromSeconds(10));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => camera.ExposureState == CameraExposureState.Exposing);
        vm.Sequencer.CancelCommand.Execute(null);
        await run;

        Assert.Null(vm.Imaging.LatestFrame);
    }

    [Fact]
    public async Task LiveStatus_MovesFromExposureToWaitWithinTheBlock_AndKeepsTheFrame()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30), sequenceDelay: TimeSpan.FromSeconds(10));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.StatusLines.LastOrDefault()?.Text == "Exposure 0.03s" && vm.Imaging.LatestFrame is null);
        Assert.Equal(
            new[] { "Repeat × 3 · 1 / 3", "Imaging Block · 1 / 2", "Exposure 0.03s" },
            vm.Sequencer.StatusLines.Select(l => l.Text));

        await WaitUntil(() => vm.Sequencer.StatusLines.LastOrDefault()?.Text == "Wait 10s");

        Assert.Equal(
            new[] { "Repeat × 3 · 1 / 3", "Imaging Block · 2 / 2", "Wait 10s" },
            vm.Sequencer.StatusLines.Select(l => l.Text));
        // The frame of the finished exposure stays visible while waiting; no exposure runs.
        Assert.NotNull(vm.Imaging.LatestFrame);
        Assert.False(vm.Camera.IsExposing);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);

        vm.Sequencer.CancelCommand.Execute(null);
        await run;
    }

    [Fact]
    public async Task CancelDuringWait_CancelsSequence_KeepsFrame_AndLeavesCameraConnectedAndIdle()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromMilliseconds(30), sequenceDelay: TimeSpan.FromSeconds(10));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.StatusLines.LastOrDefault()?.Text == "Wait 10s");
        var frame = vm.Imaging.LatestFrame;
        vm.Sequencer.CancelCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Cancelled, vm.Sequencer.State);
        Assert.True(vm.Sequencer.IsCancelled);
        Assert.Null(vm.Sequencer.ErrorMessage);
        Assert.False(vm.Sequencer.IsRunning);
        Assert.NotNull(frame);
        Assert.Same(frame, vm.Imaging.LatestFrame);
        Assert.Equal(DeviceConnectionState.Connected, camera.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }

    [Fact]
    public async Task ActiveSequenceStatusLines_ShowTheSingleRunningBranchOfTheDemo_AndClearAfterwards()
    {
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(400));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.ActiveBranches.Count == 1 && vm.Sequencer.StatusLines.LastOrDefault()?.Text == "Exposure 0.4s");

        var line = Assert.Single(vm.Sequencer.ActiveBranches);
        Assert.Equal("Repeat × 3 · 1 / 3 › Imaging Block · 1 / 2 › Exposure 0.4s", $"{line.Context} › {line.Title}");
        await run;

        Assert.Empty(vm.Sequencer.ActiveBranches);
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
        await vm.Camera.ConnectCommand.ExecuteAsync(null);
        var cameraResource = ResourceId.ForDevice(camera.Id);
        var otherHolder = await host.ResourceManager.AcquireAsync([cameraResource]);

        // The command is enabled (UX), but the runtime makes it wait: correctness comes from the ResourceManager.
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
        var run = vm.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.False(run.IsCompleted);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Null(vm.Imaging.LatestFrame);

        otherHolder.Dispose();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(vm.Imaging.LatestFrame);
        Assert.False(host.ResourceManager.IsHeld(cameraResource));
    }

    [Fact]
    public async Task ManualExposure_CancelledWhileWaitingForResource_NeverStarts()
    {
        var (vm, camera, host) = CreateWithHost();
        await vm.Camera.ConnectCommand.ExecuteAsync(null);
        var cameraResource = ResourceId.ForDevice(camera.Id);
        using var otherHolder = await host.ResourceManager.AcquireAsync([cameraResource]);

        var run = vm.Camera.StartExposureCommand.ExecuteAsync(null);
        Assert.True(vm.Camera.IsManualExposureRunning);
        vm.Camera.CancelExposureCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.Camera.IsManualExposureRunning);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Null(vm.Imaging.LatestFrame);
        Assert.True(host.ResourceManager.IsHeld(cameraResource)); // still the other holder's
    }

    [Fact]
    public async Task CancelExposure_IsOnlyAvailableWhileManualExposureRuns()
    {
        var (vm, _) = await CreateConnected();
        Assert.False(vm.Camera.CancelExposureCommand.CanExecute(null));

        var run = vm.Camera.StartExposureCommand.ExecuteAsync(null);
        Assert.True(vm.Camera.IsManualExposureRunning);
        Assert.True(vm.Camera.CancelExposureCommand.CanExecute(null));

        vm.Camera.CancelExposureCommand.Execute(null);
        await run;

        Assert.False(vm.Camera.IsManualExposureRunning);
        Assert.False(vm.Camera.CancelExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelExposure_StopsExposure_ProducesNoFrame_AndRestoresControls()
    {
        var (vm, camera) = Create(exposure: TimeSpan.FromSeconds(10));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);

        var run = vm.Camera.StartExposureCommand.ExecuteAsync(null);
        await WaitUntil(() => camera.ExposureState == CameraExposureState.Exposing);
        vm.Camera.CancelExposureCommand.Execute(null);
        await run;

        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        Assert.Equal(CameraExposureState.Idle, vm.Camera.ExposureState);
        Assert.Null(vm.Imaging.LatestFrame);
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
        Assert.True(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.Equal(DeviceConnectionState.Connected, vm.Camera.ConnectionState);
    }

    [Fact]
    public async Task CancelSequence_StopsRunAndRestoresControls()
    {
        var (vm, camera) = await CreateConnected(TimeSpan.FromSeconds(10));

        var run = vm.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => vm.Sequencer.StepText.StartsWith("Repeat × 3 · 1 / 3") && camera.ExposureState == CameraExposureState.Exposing);
        vm.Sequencer.CancelCommand.Execute(null);
        await run;

        Assert.Equal(SequenceState.Cancelled, vm.Sequencer.State);
        Assert.Null(vm.Sequencer.ErrorMessage);
        Assert.False(vm.Sequencer.IsRunning);
        Assert.Equal(CameraExposureState.Idle, camera.ExposureState);
        // The cancelled sequence left the camera connected; normal camera-state rules apply again.
        Assert.Equal(DeviceConnectionState.Connected, vm.Camera.ConnectionState);
        Assert.True(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
        Assert.True(vm.Sequencer.RunCommand.CanExecute(null));
        Assert.False(vm.Sequencer.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedSequence_SurfacesErrorWithoutThrowing_AndRecovers()
    {
        // Connected manually, but not registered, so the sequence cannot find the camera.
        var (vm, _) = await CreateConnected(TimeSpan.FromMilliseconds(30), registerCamera: false);

        await vm.Sequencer.RunCommand.ExecuteAsync(null);

        Assert.Equal(SequenceState.Failed, vm.Sequencer.State);
        Assert.Contains("is not registered", vm.Sequencer.ErrorMessage);
        Assert.StartsWith("Repeat × 3 · 1 / 3", vm.Sequencer.StepText);
        AssertManualControlsAvailableForConnectedCamera(vm);
    }


    [Fact]
    public void InitialState_OnlyConnectIsEnabled()
    {
        var (vm, _) = Create();

        Assert.Equal("Main Camera", vm.Camera.Name);
        Assert.Equal(DeviceConnectionState.Disconnected, vm.Camera.ConnectionState);
        Assert.Equal(CameraExposureState.Idle, vm.Camera.ExposureState);
        Assert.True(vm.Camera.ConnectCommand.CanExecute(null));
        Assert.False(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.False(vm.Camera.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connect_UpdatesStateAndEnablesCommands()
    {
        var (vm, _) = Create();

        await vm.Camera.ConnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Connected, vm.Camera.ConnectionState);
        Assert.False(vm.Camera.ConnectCommand.CanExecute(null));
        Assert.True(vm.Camera.DisconnectCommand.CanExecute(null));
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_ReturnsToDisconnected()
    {
        var (vm, _) = Create();
        await vm.Camera.ConnectCommand.ExecuteAsync(null);

        await vm.Camera.DisconnectCommand.ExecuteAsync(null);

        Assert.Equal(DeviceConnectionState.Disconnected, vm.Camera.ConnectionState);
        Assert.True(vm.Camera.ConnectCommand.CanExecute(null));
        Assert.False(vm.Camera.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_ShowsExposingWhileRunningThenIdle()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(300));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);

        var running = vm.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.Equal(CameraExposureState.Exposing, vm.Camera.ExposureState);
        Assert.False(vm.Camera.StartExposureCommand.CanExecute(null));

        await running;

        Assert.Equal(CameraExposureState.Idle, vm.Camera.ExposureState);
        Assert.True(vm.Camera.StartExposureCommand.CanExecute(null));
    }

    [Fact]
    public async Task Disconnect_IsDisabledWhileExposing()
    {
        var (vm, camera) = Create(TimeSpan.FromMilliseconds(300));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);

        var running = vm.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.False(vm.Camera.DisconnectCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => camera.DisconnectAsync());

        await running;

        Assert.True(vm.Camera.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task Exposure_UpdatesProgressProperties()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(700));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);
        var progressSeen = new List<double>();
        vm.Camera.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CameraViewModel.ExposureProgress))
            {
                progressSeen.Add(vm.Camera.ExposureProgress);
            }
        };

        var running = vm.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.True(vm.Camera.IsExposing);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.Camera.ExposureDuration);

        await running;

        Assert.Contains(progressSeen, p => p > 0.0 && p < 1.0);
        Assert.Equal(1.0, vm.Camera.ExposureProgress);
        Assert.Equal(TimeSpan.FromMilliseconds(700), vm.Camera.ExposureElapsed);
        Assert.False(vm.Camera.IsExposing);
    }

    [Fact]
    public async Task CompletedExposure_StoresFrame()
    {
        var (vm, _) = Create(TimeSpan.FromMilliseconds(50));
        await vm.Camera.ConnectCommand.ExecuteAsync(null);
        Assert.Null(vm.Imaging.LatestFrame);

        await vm.Camera.StartExposureCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Imaging.LatestFrame);
        Assert.Equal(800, vm.Imaging.LatestFrame!.Width);
        Assert.Equal(TimeSpan.FromMilliseconds(50), vm.Imaging.LatestFrame.ExposureDuration);
    }

    [Fact]
    public async Task ConnectionChanges_AreMarshalledThroughUiDispatcher()
    {
        var host = new AstraRuntimeHost();
        var camera = host.AddSimulatedCamera(new DeviceId("camera.main"), "Main Camera");
        var posted = new List<Action>();
        var vm = CreatePages(camera, host, posted.Add);

        await camera.ConnectAsync();

        Assert.Equal(DeviceConnectionState.Disconnected, vm.Camera.ConnectionState);
        Assert.NotEmpty(posted);

        posted.ForEach(a => a());

        Assert.Equal(DeviceConnectionState.Connected, vm.Camera.ConnectionState);
    }
}

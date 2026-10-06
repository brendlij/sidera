using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>
/// How devices and imaging setups are resolved, and what the workflow shows because of it: one camera needs nothing to be set up and shows nothing about several; two usable setups show the setups, what
/// they share and run side by side; a configured camera that is not connected does not turn a single-camera session into a multi-device one; two cameras without a setup are refused instead of guessed.
/// </summary>
public sealed class MultiDeviceSetupTests : IAsyncLifetime
{
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];
    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (host, vm) in _apps)
        {
            vm.Dispose();
            await host.DisposeAsync();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private SideraRuntimeHost NewHost()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        return host;
    }

    private static void AddCamera(SideraRuntimeHost host, string name, bool withFocuser = false)
    {
        host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
        if (withFocuser)
        {
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }
    }

    private static async Task ConnectAsync(SideraRuntimeHost host, params string[] ids)
    {
        foreach (var id in ids)
        {
            host.DeviceRegistry.TryGet(new DeviceId(id), out var device); await device!.ConnectAsync();
        }
    }

    private MainViewModel NewApp(SideraRuntimeHost host)
    {
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));
        return vm;
    }

    // ---- the order of resolution

    [Fact]
    public async Task ADeviceIsResolved_ByTheInstruction_ThenTheSetup_ThenTheOnlyDevice_AndNeverByGuessing()
    {
        var host = NewHost();
        AddCamera(host, "a");
        AddCamera(host, "b");

        var explicitly = DeviceResolver.Resolve<ICamera>(host.DeviceRegistry, new DeviceId("camera.b"), new DeviceId("camera.a"), "camera");
        var bySetup = DeviceResolver.Resolve<ICamera>(host.DeviceRegistry, null, new DeviceId("camera.a"), "camera");
        var several = DeviceResolver.Resolve<ICamera>(host.DeviceRegistry, null, null, "camera");

        Assert.Equal((DeviceResolutionSource.Explicit, new DeviceId("camera.b")), (explicitly.Source, explicitly.Device!.Value));
        Assert.Equal((DeviceResolutionSource.ImagingSetup, new DeviceId("camera.a")), (bySetup.Source, bySetup.Device!.Value));
        Assert.Equal(DeviceResolutionSource.Ambiguous, several.Source);
        Assert.Null(several.Device);
        Assert.Contains("camera.a, camera.b", several.Problem, StringComparison.Ordinal);

        await ConnectAsync(host, "camera.b");
        var connected = DeviceResolver.Resolve<ICamera>(host.DeviceRegistry, null, null, "camera");
        Assert.Equal((DeviceResolutionSource.OnlyDevice, new DeviceId("camera.b")), (connected.Source, connected.Device!.Value)); // one of two is connected: that one is meant

        var none = DeviceResolver.Resolve<Sidera.Core.Focusers.IFocuser>(host.DeviceRegistry, null, null, "focuser");
        Assert.Equal(DeviceResolutionSource.None, none.Source);
        Assert.Contains("no focuser", none.Problem, StringComparison.Ordinal);
    }

    // ---- the implicit setup

    [Fact]
    public async Task OneCamera_IsASetupOnItsOwn_WithTheOnlyDeviceOfEachKind_AndIsNeverStored()
    {
        var host = NewHost();
        AddCamera(host, "main", withFocuser: true);
        host.AddSimulatedMount(new("mount.1"), "AM3", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "PHD2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        var catalog = new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);

        var setup = Assert.Single(catalog.GetAll());

        Assert.Equal(ImagingSetupCatalog.ImplicitId, setup.Id);
        Assert.Equal("Camera main", setup.Name);
        Assert.Equal(new DeviceId("camera.main"), setup.CameraId);
        Assert.Equal(new DeviceId("focuser.main"), setup.FocuserId);
        Assert.Equal(new DeviceId("mount.1"), setup.MountId);
        Assert.Equal(new DeviceId("guider.1"), setup.GuiderId);
        Assert.Empty(host.RigRegistry.GetAll()); // not a registered rig: nothing of it is configured or saved
        Assert.False(catalog.IsMultiSetup);
    }

    [Fact]
    public async Task TwoCameras_AndNoSetup_HaveNoImplicitSetup_AndOnlyTheConnectedOneCounts_WhenOneIs()
    {
        var host = NewHost();
        AddCamera(host, "a");
        AddCamera(host, "b");
        var catalog = new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);

        Assert.Empty(catalog.GetAll()); // nothing is guessed

        await ConnectAsync(host, "camera.b");
        Assert.Equal([new DeviceId("camera.b")], catalog.GetAll().Select(s => s.CameraId));
    }

    [Fact]
    public async Task ACameraThatIsInASetup_HasNoImplicitOne()
    {
        var host = NewHost();
        AddCamera(host, "a");
        host.AddRig(new Rig(new RigId("rig.a"), "Main 750", new("camera.a"), Optics));
        var catalog = new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);

        var setup = Assert.Single(catalog.GetAll());

        Assert.Equal("Main 750", setup.Name);
    }

    // ---- usable setups

    private SideraRuntimeHost TwoSetups(bool sameMount, bool sameGuider)
    {
        var host = NewHost();
        AddCamera(host, "a", withFocuser: true);
        AddCamera(host, "b", withFocuser: true);
        host.AddSimulatedMount(new("mount.1"), "AM3", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "PHD2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        if (!sameMount)
        {
            host.AddSimulatedMount(new("mount.2"), "EQ6", TimeSpan.FromMilliseconds(20));
        }

        if (!sameGuider)
        {
            host.AddSimulatedGuider(new("guider.2"), "Second guider", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        }

        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", new("camera.a"), Optics, new("focuser.a"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(
            new RigId("rig.wide"), "Wide 400", new("camera.b"), Optics, new("focuser.b"), null, null, null, new(sameMount ? "mount.1" : "mount.2"), new(sameGuider ? "guider.1" : "guider.2")));
        return host;
    }

    [Fact]
    public async Task ASecondCameraThatIsConfiguredButNotConnected_DoesNotMakeTheSessionMultiDevice()
    {
        var host = TwoSetups(sameMount: true, sameGuider: true);
        await ConnectAsync(host, "camera.a");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.Load(WorkflowDefinition.NewEmpty());
        editor.AddImagingBlockCommand.Execute(null);

        Assert.False(editor.IsMultiSetup);
        Assert.Equal("IMAGING", editor.ImagingHeaderText);
        Assert.Empty(editor.SharedResources);
        Assert.True(vm.SequenceDraft.IsValid || vm.SequenceDraft.ValidationErrors.All(e => !e.Contains("setup", StringComparison.OrdinalIgnoreCase)), string.Join(" ", vm.SequenceDraft.ValidationErrors));
        var block = ((MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft)).Tracks.Single();
        Assert.Equal(new RigId("rig.main"), block.RigId); // "Auto" is the setup that can image

        // Connecting the second camera makes it a session with two setups, and an "Auto" block can no longer be resolved by guessing.
        await ConnectAsync(host, "camera.b");
        editor.RefreshAvailability();
        editor.RefreshAvailability();
        Assert.True(editor.IsMultiSetup);
        Assert.Equal("PARALLEL IMAGING", editor.ImagingHeaderText);
    }

    [Fact]
    public async Task WithNothingConnected_AllConfiguredSetupsCount_SoASessionCanBePlannedOffline()
    {
        var host = TwoSetups(sameMount: true, sameGuider: true);
        var vm = NewApp(host);

        Assert.True(vm.Setups.IsMultiSetup);
        Assert.Equal(2, vm.Setups.UsableSetups().Count);
    }

    [Fact]
    public async Task TwoUsableSetups_ShowParallelImaging_AndWhatTheyShare()
    {
        var host = TwoSetups(sameMount: true, sameGuider: true);
        await ConnectAsync(host, "camera.a", "camera.b");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.Load(WorkflowDefinition.NewEmpty());
        editor.AddImagingBlockCommand.Execute(null);
        editor.AddImagingBlockCommand.Execute(null);

        Assert.True(editor.IsMultiSetup);
        Assert.Equal("PARALLEL IMAGING", editor.ImagingHeaderText);
        Assert.Equal(["Mount · AM3 — Main 750, Wide 400", "Guider · PHD2 — Main 750, Wide 400"], editor.SharedResources);
        Assert.True(editor.HasSharedResources);
        var tracks = ((MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft)).Tracks;
        Assert.Equal([new RigId("rig.main"), new RigId("rig.wide")], tracks.Select(t => t.RigId!.Value));
    }

    [Fact]
    public async Task SetupsOnIndependentMountsAndGuiders_ShareNothing()
    {
        var host = TwoSetups(sameMount: false, sameGuider: false);
        await ConnectAsync(host, "camera.a", "camera.b");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.Load(WorkflowDefinition.NewEmpty());
        editor.AddImagingBlockCommand.Execute(null);
        editor.AddImagingBlockCommand.Execute(null);

        Assert.True(editor.IsMultiSetup);
        Assert.Empty(editor.SharedResources);
        Assert.False(editor.HasSharedResources);
    }

    [Fact]
    public async Task TwoCamerasWithoutASetup_AreRefused_AndSayWhatToDo()
    {
        var host = NewHost();
        AddCamera(host, "a");
        AddCamera(host, "b");
        await ConnectAsync(host, "camera.a", "camera.b");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.Load(WorkflowDefinition.Empty with { Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 1, 3)] });

        Assert.Contains(editor.Problems, p => p.Contains("no imaging setup", StringComparison.OrdinalIgnoreCase) && p.Contains("does not guess", StringComparison.Ordinal));
        Assert.False(vm.SequenceDraft.IsValid);
    }

    [Fact]
    public async Task ABlockThatIsAutoWithSeveralUsableSetups_IsRefused_NotGuessed()
    {
        var host = TwoSetups(sameMount: true, sameGuider: true);
        await ConnectAsync(host, "camera.a", "camera.b");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.Load(WorkflowDefinition.Empty with { Imaging = [new ImagingBlock(Guid.NewGuid(), null, null, 1, 3)] });

        Assert.Contains(editor.Problems, p => p.Contains("several imaging setups", StringComparison.Ordinal) && p.Contains("Main 750", StringComparison.Ordinal));
    }

    // ---- one camera, one session

    [Fact]
    public async Task OneCamera_NeedsNoSetup_ForTheWorkflow_AndImagesWithTheCamera()
    {
        var host = NewHost();
        AddCamera(host, "only");
        await ConnectAsync(host, "camera.only");
        var vm = NewApp(host);
        var editor = vm.Workflow;
        editor.StartFromTemplateCommand.Execute(null);

        Assert.False(editor.IsMultiSetup);
        var block = editor.Definition!.Imaging.Single();
        Assert.Null(block.Setup); // "Auto": the implicit setup is not named in the workflow
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));

        vm.Sequencer.RunCommand.Execute(null);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (vm.Sequencer.State is SequenceState.Running or SequenceState.Idle)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            await Task.Delay(10);
        }

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
    }
}

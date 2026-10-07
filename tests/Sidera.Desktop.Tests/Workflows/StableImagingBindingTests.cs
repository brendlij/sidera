using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.Tests.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>
/// What the references of a session to imaging setups mean when the equipment changes under them: an implicit setup that becomes an explicit one keeps the sequence, its blocks and their automation; a
/// rename changes nothing; a replaced or removed camera is reported and never silently replaced by another; documents that name a setup by its id still open, and a version 8 file is read without being
/// rewritten.
/// </summary>
public sealed class StableImagingBindingTests : IAsyncLifetime
{
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly DeviceId Main = new("camera.main");
    private static readonly DeviceId Wide = new("camera.wide");
    private static readonly ImagingBindingId MainPath = ImagingBindingId.For(Main);
    private static readonly ImagingBindingId WidePath = ImagingBindingId.For(Wide);
    private readonly List<(SideraRuntimeHost Host, MainViewModel Vm)> _apps = [];
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sidera-binding-" + Guid.NewGuid().ToString("N"));

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

        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private SideraRuntimeHost NewHost(bool withWide)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedCamera(Main, "ASI2600MM", 1);
        host.AddSimulatedFocuser(new("focuser.main"), "EAF", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedMount(new("mount.1"), "AM3", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.1"), "PHD2", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        if (withWide)
        {
            host.AddSimulatedCamera(Wide, "ASI533MC", 2);
            host.AddSimulatedFocuser(new("focuser.wide"), "Wide focuser", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        return host;
    }

    private static async Task ConnectAsync(SideraRuntimeHost host, params DeviceId[] ids)
    {
        foreach (var id in ids)
        {
            host.DeviceRegistry.TryGet(id, out var device);
            await device!.ConnectAsync();
        }
    }

    private MainViewModel NewApp(SideraRuntimeHost host)
    {
        var vm = new MainViewModel(host, a => a(), new DemoOptions());
        _apps.Add((host, vm));
        return vm;
    }

    private static Rig MainSetup(string id = "rig.main", string name = "Main 750") =>
        new(new RigId(id), name, Main, Optics, new DeviceId("focuser.main"), null, null, null, new DeviceId("mount.1"), new DeviceId("guider.1"));

    private static Rig WideSetup(string id = "rig.wide", string name = "Wide 400") =>
        new(new RigId(id), name, Wide, Optics, new DeviceId("focuser.wide"), null, null, null, new DeviceId("mount.1"), new DeviceId("guider.1"));

    private static SetupAutofocus Focus(ImagingBindingId setup) => new(setup, true, true, 30, false, AutofocusSettings.Default);

    // A block of 60 s × 5 that dithers every 3 frames and focuses at its start and every 30 minutes, as the old policies of a setup said.
    private static SequenceBlock Automated(double seconds = 60, int frames = 5, bool dither = true) =>
        SessionFixture.Block(null, seconds, frames, b =>
        {
            if (dither)
            {
                b.Dither(3);
            }

            b.Focus(atStart: true, everyMinutes: 30);
        });

    private static SequenceBlock Plain(double seconds = 60, int frames = 5) => SessionFixture.Block(null, seconds, frames);

    private static SessionDefinition OneTarget(params SetupLane[] lanes) => SessionFixture.Session(SessionFixture.Target("M31", lanes));

    private static (MultiRigStepDraft Step, RigTrackDraft Track) TrackOf(MainViewModel vm)
    {
        var step = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        return (step, step.Tracks.First());
    }

    // ---- implicit → explicit

    [Fact]
    public async Task AutoSequencesAndTheirAutomation_ContinueWhenTheOnlyCameraGetsAnExplicitSetup()
    {
        var host = NewHost(withWide: false);
        await ConnectAsync(host, Main);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(null, Automated())));

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        var (implicitStep, implicitTrack) = TrackOf(vm);
        Assert.Equal(ImagingSetupCatalog.ImplicitIdFor(Main), implicitTrack.RigId);
        Assert.True(implicitTrack.AutofocusPolicy!.IsActive);
        Assert.Equal(30, implicitTrack.AutofocusPolicy.IntervalMinutes);
        Assert.True(implicitStep.DitherPolicy!.Enabled);
        Assert.Equal(implicitTrack.RigId, implicitStep.DitherPolicy.TriggerRigId);

        // The user makes an imaging setup for the camera.
        host.AddRig(MainSetup());
        editor.RefreshSetups();

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        var (explicitStep, explicitTrack) = TrackOf(vm);
        Assert.Equal(new RigId("rig.main"), explicitTrack.RigId); // the imaging is now done with the explicit setup
        Assert.True(explicitTrack.AutofocusPolicy!.IsActive); // and the automation of the block is the same
        Assert.Equal(30, explicitTrack.AutofocusPolicy.IntervalMinutes);
        Assert.True(explicitStep.DitherPolicy!.Enabled);
        Assert.Equal(explicitTrack.RigId, explicitStep.DitherPolicy.TriggerRigId);

        // Nothing in the session had to change for it.
        var lane = editor.Session!.Targets[0].Lanes.Single();
        Assert.Null(lane.Setup);
        Assert.NotNull(lane.Blocks.Single().Automation.Dither);
        Assert.True(lane.Blocks.Single().Automation.Focus!.IsActive);
    }

    [Fact]
    public async Task AVersion8Workflow_ThatNamesTheImplicitSetup_StillMeansTheCamera_AfterAnExplicitSetupExists()
    {
        var host = NewHost(withWide: false);
        await ConnectAsync(host, Main);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        host.AddRig(MainSetup());

        // A document from before: the implicit setup by its derived id, by the constant id it had earlier, and a policy for it.
        editor.LoadLegacy(WorkflowDefinition.Empty with
        {
            Imaging = [new ImagingBlock(Guid.NewGuid(), new RigId("setup.implicit:camera.main"), null, 60, 5), new ImagingBlock(Guid.NewGuid(), new RigId("setup.implicit"), null, 30, 3)],
            AutofocusPolicies = [Focus(new RigId("setup.implicit"))],
        });

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        var lane = editor.Session!.Targets.Single().Lanes.Single(); // both blocks are for the camera: one sequence
        Assert.Equal(2, lane.Blocks.Count);
        Assert.All(lane.Blocks, b => Assert.True(b.Automation.Focus!.IsActive));
        Assert.True(TrackOf(vm).Track.AutofocusPolicy!.IsActive);
        Assert.Equal(new RigId("rig.main"), TrackOf(vm).Track.RigId);
    }

    [Fact]
    public async Task ASessionOfSeveralSetups_KeepsItsSequencesAndAutomation_WhenTheSetupsAreRecreatedOrRenamed()
    {
        var host = NewHost(withWide: true);
        host.AddRig(MainSetup());
        host.AddRig(WideSetup());
        await ConnectAsync(host, Main, Wide);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(MainPath, Automated(dither: false)), SessionFixture.Lane(WidePath, Automated(30, 3))));
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));

        // The wide setup is made again under another id and another name, and the main one is renamed.
        host.RigRegistry.Unregister(new RigId("rig.wide"));
        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.AddRig(WideSetup("rig.wide-2", "Wide, second version"));
        host.AddRig(MainSetup("rig.main", "Main, renamed"));
        editor.RefreshSetups();

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        var step = (MultiRigStepDraft)vm.SequenceDraft.Snapshot().Single(s => s is MultiRigStepDraft);
        Assert.Equal([new RigId("rig.main"), new RigId("rig.wide-2")], step.Tracks.Select(t => t.RigId!.Value).OrderBy(i => i.Value));
        Assert.All(step.Tracks, t => Assert.True(t.AutofocusPolicy!.IsActive));
        Assert.Equal(new RigId("rig.wide-2"), step.DitherPolicy!.TriggerRigId); // counted on the wide setup, as its block says
        Assert.Equal([MainPath, WidePath], editor.Session!.Targets[0].Lanes.Select(l => l.Setup!.Value)); // what the lanes name is the camera: it did not change
    }

    [Fact]
    public async Task TheOpticsOfAnExplicitSetup_AreKnownAtOnce_WithoutChangingWhatTheSessionRefersTo()
    {
        var host = NewHost(withWide: false);
        await ConnectAsync(host, Main);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(null, Plain())));
        Assert.Null(vm.Setups.GetAll().Single().Optics);

        host.AddRig(MainSetup());
        editor.RefreshSetups();

        Assert.Equal(750, vm.Setups.GetAll().Single().Optics!.FocalLengthMm);
        Assert.Null(editor.Session!.Targets[0].Lanes.Single().Setup);
    }

    // ---- one camera and a camera that is replaced or removed

    [Fact]
    public async Task ReplacingTheCameraOfTheOnlySetup_KeepsTheSequence_BecauseItNamesNoCamera_AndItsAutomationIsTheBlocks()
    {
        var host = NewHost(withWide: true);
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Main, Optics, new DeviceId("focuser.main")));
        await ConnectAsync(host, Main);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(null, Automated(dither: false))));
        Assert.True(TrackOf(vm).Track.AutofocusPolicy!.IsActive);

        // The same setup, with another camera in it.
        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Wide, Optics, new DeviceId("focuser.wide")));
        await ConnectAsync(host, Wide);
        editor.RefreshSetups();

        var track = TrackOf(vm).Track;
        Assert.Equal(new RigId("rig.main"), track.RigId); // "Auto" is the setup that can image: it names no camera
        Assert.True(track.AutofocusPolicy!.IsActive); // the block says when it focuses, whatever camera it focuses for
        Assert.Null(editor.Session!.Targets[0].Lanes.Single().Setup);
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
    }

    [Fact]
    public async Task ASequenceThatNamesACameraThatWasReplaced_IsReported_AndNotBoundToTheNewOne()
    {
        var host = NewHost(withWide: true);
        host.AddRig(MainSetup());
        host.AddRig(WideSetup());
        await ConnectAsync(host, Main, Wide);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(MainPath, Plain()), SessionFixture.Lane(WidePath, Plain())));
        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));

        // The main setup now has the wide camera in it (and the wide setup is gone), so camera.main is not part of any setup any more.
        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.RigRegistry.Unregister(new RigId("rig.wide"));
        host.AddRig(new Rig(new RigId("rig.main"), "Main 750", Wide, Optics, new DeviceId("focuser.wide")));
        editor.RefreshSetups();

        var problem = editor.Problems.First(p => p.Contains("camera 'camera.main'", StringComparison.Ordinal));
        Assert.Contains("is not available", problem, StringComparison.Ordinal);
        Assert.Contains("removed or replaced", problem, StringComparison.Ordinal);
        var lane = editor.Targets[0].Lanes[0];
        Assert.Equal(MainPath, editor.Session!.Targets[0].Lanes[0].Setup); // still what it was: reported, not replaced
        Assert.Null(lane.SelectedSetup); // nothing is chosen for it
        Assert.True(lane.NeedsSetupChoice); // the choice is shown so that it can be repaired
        Assert.False(vm.SequenceDraft.IsValid);
    }

    [Fact]
    public async Task ARemovedCamera_LeavesTheSequenceThatNamedIt_Unresolved_WithAClearMessage()
    {
        var host = NewHost(withWide: true);
        host.AddRig(MainSetup());
        host.AddRig(WideSetup());
        await ConnectAsync(host, Main, Wide);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(MainPath, Plain()), SessionFixture.Lane(WidePath, Plain())));

        host.RigRegistry.Unregister(new RigId("rig.main"));
        host.DeviceRegistry.TryGet(Main, out var camera);
        await camera!.DisconnectAsync();
        Assert.True(host.RemoveDevice(Main));
        editor.RefreshSetups();

        var problem = editor.Problems.First(p => p.Contains("camera 'camera.main'", StringComparison.Ordinal));
        Assert.Contains("removed or replaced", problem, StringComparison.Ordinal);
        Assert.Equal(WidePath, editor.Session!.Targets[0].Lanes[1].Setup);
        Assert.Equal("Wide 400", editor.Targets[0].Lanes[1].SetupName); // the other sequence is not affected
    }

    [Fact]
    public async Task EditingABlock_NeverRewritesWhatItsSequenceIsBoundTo_EvenWhenThatIsNotThereAnyMore()
    {
        var host = NewHost(withWide: true);
        host.AddRig(MainSetup());
        host.AddRig(WideSetup());
        await ConnectAsync(host, Main, Wide);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(MainPath, Plain())));

        editor.SelectBlock(editor.Session!.Targets[0].Lanes[0].Blocks[0].Id);
        var drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.ExposureText = "90";
        Assert.Equal(MainPath, editor.Session!.Targets[0].Lanes[0].Setup);
        Assert.Equal(90, editor.Session.Targets[0].Lanes[0].Blocks[0].FirstExposure!.Seconds);

        // The main setup is gone: one setup is left, so the selectors are not there for a session of one setup, but the sequence names a setup that is not, so its choice is.
        host.RigRegistry.Unregister(new RigId("rig.main"));
        editor.RefreshSetups();
        Assert.False(editor.IsMultiSetup);
        var lane = editor.Targets[0].Lanes[0];
        Assert.True(lane.NeedsSetupChoice);
        drawer = Assert.IsType<BlockDrawerViewModel>(editor.Drawer);
        drawer.ExposureText = "95";

        Assert.Equal(MainPath, editor.Session!.Targets[0].Lanes[0].Setup); // not turned into "Auto", which would be the wide camera
        Assert.Equal(95, editor.Session.Targets[0].Lanes[0].Blocks[0].FirstExposure!.Seconds);
        Assert.Contains(editor.Problems, p => p.Contains("camera 'camera.main'", StringComparison.Ordinal));

        // Choosing the setup is the repair.
        lane = editor.Targets[0].Lanes[0];
        lane.SelectedSetup = lane.SetupChoices.Single();
        Assert.Equal(WidePath, editor.Session!.Targets[0].Lanes[0].Setup); // what the user chose, named
        Assert.DoesNotContain(editor.Problems, p => p.Contains("camera 'camera.main'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeveralCamerasAndNoSetup_StayAmbiguous_ASequenceOfACameraIsNotGuessed()
    {
        var host = NewHost(withWide: true);
        await ConnectAsync(host, Main, Wide);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;
        editor.Load(OneTarget(SessionFixture.Lane(MainPath, Plain()), SessionFixture.Lane(null, Plain())));

        Assert.Empty(vm.Setups.GetAll()); // no implicit setup: nothing is guessed between the cameras
        Assert.Contains(editor.Problems, p => p.Contains("camera 'camera.main'", StringComparison.Ordinal));
        Assert.Contains(editor.Problems, p => p.Contains("no imaging setup", StringComparison.OrdinalIgnoreCase));
        Assert.False(vm.SequenceDraft.IsValid);
    }

    // ---- documents

    [Fact]
    public async Task ADocumentThatNamesASetupById_StillOpens_ResolvesToTheSetup_AndIsWrittenAsItWas()
    {
        var host = NewHost(withWide: false);
        host.AddRig(MainSetup());
        await ConnectAsync(host, Main);
        var vm = NewApp(host);
        var editor = vm.SessionEditor;

        // What the file holds is read; the reference in it is the id of a setup.
        var serializer = new JsonSequenceDocumentSerializer();
        var original = new SequenceDocument("Old session", [], null, null, OneTarget(SessionFixture.Lane(new RigId("rig.main"), Automated())));
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, original, CancellationToken.None);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.Contains("\"rig.main\"", text, StringComparison.Ordinal); // a reference by setup id is written as it was

        stream.Position = 0;
        var loaded = await serializer.LoadAsync(stream, CancellationToken.None);
        Assert.Equal("rig.main", loaded.Session!.Targets.Single().Lanes.Single().Setup!.Value.Value);

        editor.Load(loaded.Session);

        Assert.False(editor.HasProblems, string.Join(" ", editor.Problems));
        Assert.Equal("rig.main", editor.Session!.Targets[0].Lanes[0].Setup!.Value.Value);
        Assert.True(TrackOf(vm).Track.AutofocusPolicy!.IsActive);
        Assert.Equal(new RigId("rig.main"), TrackOf(vm).Track.RigId);
    }

    [Fact]
    public async Task ABindingByPath_IsWrittenAsItIs_AndComesBack()
    {
        var serializer = new JsonSequenceDocumentSerializer();
        var document = new SequenceDocument("Session", [], null, null, OneTarget(SessionFixture.Lane(MainPath, Automated()), SessionFixture.Lane(WidePath, Plain())));
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, document, CancellationToken.None);
        var text = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"version\": 9", text, StringComparison.Ordinal);
        Assert.Contains("\"imaging:auto:camera.main\"", text, StringComparison.Ordinal);

        stream.Position = 0;
        var loaded = (await serializer.LoadAsync(stream, CancellationToken.None)).Session!;
        Assert.Equal([MainPath, WidePath], loaded.Targets[0].Lanes.Select(l => l.Setup!.Value));
    }

    [Fact]
    public async Task OpeningAVersion8File_ChangesNeitherTheFileNorTheDirtyState_AndKeepsWhatItNamed()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "Old session.astraseq");

        // A file as it was written when a workflow named the setup object.
        var legacy = new SequenceDocument("Old session", [], null, WorkflowDefinition.Empty with
        {
            Target = WorkflowTarget.Default with { PointingSetup = new RigId("rig.main") },
            Imaging = [new ImagingBlock(Guid.NewGuid(), new RigId("rig.main"), null, 60, 5)],
            Dither = new WorkflowDither(true, 3, new RigId("rig.main")),
            AutofocusPolicies = [Focus(new RigId("rig.main"))],
        });
        using (var stream = new MemoryStream())
        {
            await new JsonSequenceDocumentSerializer().SaveAsync(stream, legacy, CancellationToken.None);
            var text = Encoding.UTF8.GetString(stream.ToArray()).Replace("\"version\": 9", "\"version\": 8", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, text);
        }

        var bytes = await File.ReadAllBytesAsync(path);

        var host = NewHost(withWide: false);
        host.AddRig(MainSetup());
        var picker = new FilePicker { OpenPath = path };
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), SequenceDocumentStore.CreateDefault(), picker);
        _apps.Add((host, vm));
        await vm.SequenceDocument.OpenCommand.ExecuteAsync(null);
        if (vm.SequenceDocument.IsConfirmingDiscard)
        {
            await vm.SequenceDocument.ConfirmDiscardCommand.ExecuteAsync(null);
        }

        Assert.True(vm.SessionEditor.IsStructured);
        Assert.False(vm.SequenceDocument.IsDirty);
        var lane = vm.SessionEditor.Session!.Targets.Single().Lanes.Single();
        Assert.Null(lane.Setup); // one setup to image with: "Auto" stays "Auto", the reference to the setup object is not carried into the session
        Assert.NotNull(lane.Blocks.Single().Automation.Dither);
        Assert.True(lane.Blocks.Single().Automation.Focus!.IsActive);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); // opening does not rewrite what was saved
    }

    private sealed class FilePicker : ISequenceFilePicker
    {
        public string? OpenPath { get; set; }

        public Task<string?> PickOpenPathAsync() => Task.FromResult(OpenPath);

        public Task<string?> PickSavePathAsync(string suggestedFileName) => Task.FromResult<string?>(null);
    }
}

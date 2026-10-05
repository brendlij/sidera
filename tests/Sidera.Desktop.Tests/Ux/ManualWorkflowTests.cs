using Sidera.Ascom;
using Sidera.Ascom.Discovery;
using Sidera.Core.Devices;
using Sidera.Core.Astrometry;
using Sidera.Core.Events;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Hardware;
using Sidera.Desktop.Imaging;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;

namespace Sidera.Desktop.Tests.Ux;

/// <summary>
/// The manual workflow of the V2 slice, end to end on simulators and through the view models the pages bind to: two rigs that share a mount and a guider, a manual frame that is viewed and
/// saved, a manual autofocus, a target that goes into the session, a parallel exposure of both rigs, and a run in which the shared mount is never used by two rigs at once.
/// </summary>
public sealed class ManualWorkflowTests : IAsyncLifetime
{
    private sealed class NoDiscovery : IAscomDiscovery
    {
        public Task<AscomDiscoveryResult> DiscoverAsync(AscomDeviceKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AscomDiscoveryResult(true, [], null));
    }

    private sealed class NoSetup : IAscomSetupService
    {
        public Task<AscomSetupResult> ShowAsync(AscomDeviceKind kind, string progId, CancellationToken cancellationToken = default) => Task.FromResult(new AscomSetupResult(true, null));
    }

    // Solves where the mount points, with a fixed rotation: the plate solver of a simulated sky.
    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    private sealed class FakeCatalog : Sidera.Sky.ICelestialObjectCatalog
    {
        public string Name => "Fake";

        public Task<IReadOnlyList<Sidera.Sky.CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Sidera.Sky.CelestialObject>>([new Sidera.Sky.CelestialObject("M31", [], new CelestialCoordinates(0.7123, 41.269), "Galaxy", 189.1, Name)]);
    }

    private sealed class FakeProvider(Sidera.Sky.SkySurveyDescriptor survey) : Sidera.Sky.ISkySurveyProvider
    {
        public string Id => survey.Id;
        public string DisplayName => survey.Title;

        public Task<Sidera.Sky.SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<Sidera.Sky.SkySurveyInfo?>(new Sidera.Sky.SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl });

        public Task<Sidera.Sky.SkyImage> GetImageAsync(Sidera.Core.Framing.SkyViewport viewport, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Sidera.Sky.SkyImage(
                viewport.WidthPixels, viewport.HeightPixels, new byte[viewport.WidthPixels * viewport.HeightPixels * 4], 5, 0, 2,
                new Sidera.Sky.SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl }, null));
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "astra-workflow-" + Guid.NewGuid().ToString("N"));
    private SideraRuntimeHost? _host;
    private MainViewModel? _vm;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _vm?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task TheManualWorkflow_FromTwoRigsToAParallelRun_Works()
    {
        // Simulators, fast.
        var options = new DemoOptions
        {
            FocuserStepsPerSecond = 1_000_000, FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(1), ManualExposure = TimeSpan.FromMilliseconds(30),
            SlewDuration = TimeSpan.FromMilliseconds(150), FilterWheelMoveDuration = TimeSpan.FromMilliseconds(5),
        };
        _host = new SideraRuntimeHost();
        var factories = new DeviceFactoryRegistry([new SimulatorDeviceFactory(options)]);
        var service = new EquipmentService(_host, new EquipmentConfigurationStore(Path.Combine(_directory, "equipment.json")), factories);
        service.Load();
        _host.ConfigurePlateSolver(new Solver(_host));
        _vm = new MainViewModel(_host, action => action(), options, equipmentManagement: new EquipmentManagement(service, new NoDiscovery(), new NoSetup()),
            objectCatalog: new FakeCatalog(), skyProviders: survey => new FakeProvider(survey));
        var equipment = _vm.Equipment;
        equipment.AddDemoEquipmentCommand.Execute(null);

        // 1.-4. Two rigs, each with its own camera and focuser, on the one mount and the one guider.
        foreach (var existing in equipment.Contexts.Where(c => c.Rig is not null).ToList())
        {
            existing.SelectCommand.Execute(null);
            equipment.RigSetup!.RemoveCommand.Execute(null);
            equipment.RigSetup!.RemoveCommand.Execute(null);
        }

        Assert.DoesNotContain(equipment.Contexts, c => c.Rig is not null);
        var cameras = service.Configuration.Devices.Where(d => d.Type == DeviceType.Camera).Take(2).ToList();
        foreach (var (name, camera) in new[] { ("Alpha", cameras[0]), ("Beta", cameras[1]) })
        {
            equipment.AddRig!.BeginCommand.Execute(null);
            equipment.AddRig.NameText = name;
            equipment.AddRig.SelectedCamera = equipment.AddRig.FreeCameras.Single(c => c.Id == camera.Id);
            equipment.AddRig.AddCommand.Execute(null);
            Assert.Equal(name, equipment.SelectedContext!.Title);
            foreach (var role in new[] { RigRole.Mount, RigRole.Guider, RigRole.Focuser }) // mount and guider are shared; a focuser belongs to one rig
            {
                var assignment = equipment.RigSetup!.Assignments.Single(a => a.Role == role);
                assignment.Selected = assignment.Choices.First(c => c.Id is not null);
            }
        }

        var rigs = service.Configuration.Rigs;
        Assert.Equal(2, rigs.Count);
        Assert.NotNull(rigs[0].MountId);
        Assert.Equal(rigs[0].MountId, rigs[1].MountId); // the same device on both
        Assert.Equal(rigs[0].GuiderId, rigs[1].GuiderId);
        Assert.NotEqual(rigs[0].CameraId, rigs[1].CameraId);

        foreach (var device in _host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        // The simulated sky focuses best a little away from where each focuser stands (the demo rigs have such a model; these rigs are new).
        foreach (var rig in rigs)
        {
            Assert.True(_host.DeviceRegistry.TryGet(new DeviceId(rig.FocuserId!), out var device));
            var focuser = (Sidera.Core.Focusers.IFocuser)device!;
            _host.AddSimulatedFocusModel(new RigId(rig.Id), new Sidera.Runtime.Focusing.SimulatedFocusModel(focuser.Position + 120));
        }

        // 5.-6. Imaging: a manual frame of the first rig, through the acquisition pipeline.
        var imaging = _vm.Imaging;
        imaging.Capture!.SelectedTarget = imaging.Capture.Targets.First(t => t.RigId is not null);
        imaging.Capture.ExposureText = "0.05";
        await imaging.Capture.CaptureCommand.ExecuteAsync(null);
        Assert.NotNull(imaging.LatestFrame);
        Assert.False(imaging.Capture.HasError, imaging.Capture.ErrorText);

        // 7. The view: zoom and pan are the viewer's (a custom view), the stretch is only the display, and 1:1 and Fit are one click.
        imaging.IsCustomView = true;
        imaging.ActualSizeCommand.Execute(null);
        Assert.True(imaging.IsActualSize);
        imaging.FitCommand.Execute(null);
        Assert.True(imaging.IsFitToView);
        imaging.AutoStretch = false;
        imaging.AutoStretch = true;

        // 8. Saving: the data as taken (FITS) and the display (PNG), both explicit.
        var fits = Path.Combine(_directory, "manual.fits");
        var png = Path.Combine(_directory, "manual.png");
        Assert.True(imaging.SaveFits(fits));
        Assert.True(imaging.SavePng(png));
        Assert.True(new FileInfo(fits).Length > 2880);
        Assert.True(new FileInfo(png).Length > 8);

        // 9. A manual autofocus on the same rig, with its samples live and a result.
        imaging.Autofocus!.ExposureText = "0.04";
        await imaging.Autofocus.StartCommand.ExecuteAsync(null);
        Assert.False(imaging.Autofocus.IsRunning);
        Assert.True(imaging.Autofocus.BestPosition is not null, imaging.Autofocus.ErrorText + " / " + imaging.Autofocus.StatusText);

        // 10.-12. Framing: a target and the field of the current scope; the target goes into the session.
        var framing = _vm.Framing;
        framing.RefreshEquipment();
        framing.RefreshCurrent();
        Assert.NotNull(framing.Current); // the connected mount of the rig gives the live field
        framing.SearchText = "M31";
        await framing.SearchCommand.ExecuteAsync(null);
        Assert.True(framing.HasTarget);
        var before = _vm.SequenceDraft.Rows.Count;
        framing.AddToSessionCommand.Execute(null);
        Assert.Equal(before + 1, _vm.SequenceDraft.Rows.Count);

        // 13. Parallel exposures of both rigs.
        var draft = _vm.SequenceDraft;
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        foreach (var rig in rigs)
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig.Id);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            var exposure = Assert.IsType<RigExposureStepDraftViewModel>(draft.SelectedStep);
            exposure.ExposureText = "0.2";
        }

        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal(["Overview", "Alpha", "Beta", "Shared"], draft.ScopeTabs.Select(t => t.Title)); // the session by rig, over the same steps

        // 14. Run.
        var slews = new List<(DateTime Start, DateTime? End)>();
        var gate = new object();
        _host.EventBus.Subscribe<MountMotionStateChanged>((motion, _) =>
        {
            lock (gate)
            {
                if (motion.NewState == MountMotionState.Slewing)
                {
                    slews.Add((DateTime.UtcNow, null));
                }
                else if (motion.PreviousState == MountMotionState.Slewing && slews.Count > 0 && slews[^1].End is null)
                {
                    slews[^1] = (slews[^1].Start, DateTime.UtcNow);
                }
            }

            return Task.CompletedTask;
        });
        _vm.Sequencer.RunCommand.Execute(null);
        await WaitAsync(() => _vm.Sequencer.State is SequenceState.Completed or SequenceState.Failed or SequenceState.Cancelled, "the sequence to finish");

        // 15. The run finished, nothing deadlocked, and the one shared mount was only ever slewing for one operation at a time.
        Assert.Equal(SequenceState.Completed, _vm.Sequencer.State);
        lock (gate)
        {
            Assert.NotEmpty(slews);
            for (var i = 1; i < slews.Count; i++)
            {
                Assert.True(slews[i].Start >= (slews[i - 1].End ?? DateTime.MaxValue), "two slews of the shared mount overlapped");
            }
        }
    }
}

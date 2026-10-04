using System.Text;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;
namespace Sidera.Desktop.Tests.Documents;

public sealed class PlateSolveIntegrationTests
{
    [Fact]
    public async Task LastFrameFromAnotherCameraIsRejectedBeforeSolverRuns()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera");
        var rig = new Rig(new("rig"), "Rig", camera.Id); host.AddRig(rig); host.ConfigurePlateSolver(new Solver());
        using var imaging = new ImagingViewModel();
        imaging.Publish(new CameraFrame(1, 1, [1], TimeSpan.Zero), "Other camera", new DeviceId("other"));
        using var vm = new PlateSolveViewModel(host, imaging, null, action => action());
        await vm.SolveLastFrameCommand.ExecuteAsync(null);
        Assert.Contains("whose camera", vm.StatusText); Assert.Null(host.PlateSolving!.LastResult);
    }
    private sealed class Solver : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));
        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolveResult { Success = true, Center = new(5, 30) });
    }
    [Fact]
    public async Task PlateSolvePersistsAndCompilesAsBackendNeutralAction()
    {
        await using var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera");
        var rig = new Rig(new("rig"), "Rig", camera.Id); host.AddRig(rig); host.ConfigurePlateSolver(new Solver());
        var step = new PlateSolveStepDraft(Guid.NewGuid(), rig.Id, .01);
        var serializer = new JsonSequenceDocumentSerializer(); using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument([step]), CancellationToken.None);
        Assert.Contains("\"type\": \"plateSolve\"", Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        var document = await serializer.LoadAsync(stream, CancellationToken.None);
        var loaded = Assert.IsType<PlateSolveStepDraft>(Assert.Single(SequenceDocumentMapper.ToDrafts(document)));
        Assert.Equal(step, loaded);
        var context = new SequenceDraftContext(Rigs: host.RigRegistry, PlateSolving: host.PlateSolving);
        Assert.Contains(camera.Id, SequenceDraftBuilder.RequiredDeviceIds([loaded], context));
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [loaded], context);
        Assert.IsType<PlateSolveAction>(built.Sequence.Steps[0]);
        await camera.ConnectAsync();
        var runner = new SequenceRunner(host.ResourceManager);
        await runner.RunAsync(built.Sequence, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(host.PlateSolving!.LastResult!.Success);
    }
    [Fact]
    public void SettingsRoundTripAndSiteUpdatesPreserveSolverPreferences()
    {
        var path = Path.Combine(Path.GetTempPath(), "sidera-settings-test-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            var service = new SiteService(new(path)); service.Load();
            var solver = new PlateSolvingSettings { ExecutablePath = "astap.exe", DatabasePath = "catalog", TimeoutSeconds = 42,
                SearchRadiusDegrees = 4, DownsampleFactor = 2, BlindFallback = false, ExposureSeconds = 2,
                CenteringToleranceArcseconds = 15, MaxCenteringAttempts = 3 };
            Assert.True(service.SetPlateSolving(solver).Succeeded);
            Assert.True(service.Set(new ObservingSite(48, 8, 400)).Succeeded);
            var loaded = new SideraSettingsStore(path).Load();
            Assert.Equal(solver, loaded.PlateSolving); Assert.NotNull(loaded.Site);
            Assert.DoesNotContain("LastResult", File.ReadAllText(path));
            Assert.True(service.Clear().Succeeded); Assert.Equal(solver, new SideraSettingsStore(path).Load().PlateSolving);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    [Fact]
    public void OldSettingsWithoutSolverReceiveDefaults() =>
        Assert.Equal(new PlateSolvingSettings(), SideraSettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"format\":\"sidera-settings\",\"version\":1}")).PlateSolving);

    [Fact]
    public void DiagnosticEstimateUsesBinnedPixelSize()
    {
        var request = new PlateSolveRequest(new PlateSolveImage(new CameraFrame(1, 1, [1], TimeSpan.Zero), PixelSizeXMicrons: 4))
        { FocalLengthMm = 400, PixelScaleXArcsecPerPixel = 2 };
        var d = PlateSolveDiagnostics.From(request, new PlateSolveResult { PixelScaleXArcsecPerPixel = 2.2 });
        Assert.Equal(10, d.ScaleDifferencePercent!.Value, 8);
        Assert.Equal(206.264806247 * 4 / 2.2, d.EstimatedFocalLengthMm!.Value, 8);
    }
}

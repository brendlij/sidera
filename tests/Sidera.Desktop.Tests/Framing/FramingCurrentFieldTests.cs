using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Sky;

namespace Sidera.Desktop.Tests.Framing;

/// <summary>The live current field of the framing: it follows the mount of the rig, takes the solved position and rotation while they are current, and never claims a rotation it does not know.</summary>
public sealed class FramingCurrentFieldTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidera-framing-live-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
            // Temporary files of a test.
        }
    }

    private sealed class FakeCatalog : ICelestialObjectCatalog
    {
        public string Name => "Fake";
        public Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CelestialObject>>([new CelestialObject("M31", [], new CelestialCoordinates(0.7123, 41.269), "Galaxy", 189.1, Name)]);
    }

    private sealed class FakeProvider(SkySurveyDescriptor survey) : ISkySurveyProvider
    {
        public string Id => survey.Id;
        public string DisplayName => survey.Title;
        public Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult<SkySurveyInfo?>(new SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl });

        public Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SkyImage(viewport.WidthPixels, viewport.HeightPixels, new byte[viewport.WidthPixels * viewport.HeightPixels * 4], 5, 0, 2, new SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl }, null));
    }

    // Solves where the mount of the rig points, with a fixed rotation.
    private sealed class Solver(SideraRuntimeHost host, double rotation) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = rotation, Backend = Name });
        }
    }

    private sealed record Harness(FramingViewModel Framing, SideraRuntimeHost Host, SimulatedMount Mount1, SimulatedMount Mount2);

    // Rig A on mount 1, rig B on mount 2.
    private async Task<Harness> CreateAsync(bool connectMounts = true, TimeSpan? slew = null)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var a = host.AddSimulatedCamera(new("camera.a"), "Camera A", 1);
        var b = host.AddSimulatedCamera(new("camera.b"), "Camera B", 2);
        var m1 = host.AddSimulatedMount(new("mount.1"), "Mount 1", slew ?? TimeSpan.FromMilliseconds(1));
        var m2 = host.AddSimulatedMount(new("mount.2"), "Mount 2", slew ?? TimeSpan.FromMilliseconds(1));
        var optics = new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176);
        host.AddRig(new Rig(new("rig.a"), "Rig A", a.Id, optics, mountId: m1.Id));
        host.AddRig(new Rig(new("rig.b"), "Rig B", b.Id, optics, mountId: m2.Id));
        host.ConfigurePlateSolver(new Solver(host, 81.2));
        await a.ConnectAsync();
        await b.ConnectAsync();
        if (connectMounts)
        {
            await m1.ConnectAsync();
            await m2.ConnectAsync();
        }

        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var session = new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, rigs: host.RigRegistry, plateSolving: host.PlateSolving);
        Directory.CreateDirectory(_folder);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_folder, "settings.json")));
        settings.Load();
        settings.SetPlateSolving(new PlateSolvingSettings { ExposureSeconds = 0.01, MaxCenteringAttempts = 3, CenteringToleranceArcseconds = 30 });
        var framing = new FramingViewModel(host, settings, session, new FakeCatalog(), survey => new FakeProvider(survey), x => x());
        return new Harness(framing, host, m1, m2);
    }

    [Fact]
    public async Task WithoutAConnectedMount_ThereIsNoCurrentField_AndTheLegendSaysSo()
    {
        var h = await CreateAsync(connectMounts: false);

        h.Framing.RefreshCurrent();

        Assert.Null(h.Framing.Current);
        Assert.False(h.Framing.HasCurrent);
        Assert.Contains("unknown", h.Framing.CurrentText);
    }

    [Fact]
    public async Task TheCurrentField_IsWhereTheMountPoints_WithAnUnknownRotation()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(5.5, 22));

        h.Framing.RefreshCurrent();

        Assert.Equal(new CelestialCoordinates(5.5, 22), h.Framing.Current!.Center);
        Assert.Null(h.Framing.Current.RotationDegrees);
        Assert.Equal(CurrentViewSource.Mount, h.Framing.Current.Source);
        Assert.Contains("rotation unknown", h.Framing.CurrentText);
        Assert.Contains("mount position", h.Framing.CurrentText);
    }

    [Fact]
    public async Task TheCurrentField_FollowsTheMountOfTheSelectedRig()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(5.5, 22));
        await h.Mount2.SlewToAsync(new CelestialCoordinates(12, -30));

        h.Framing.SelectedRig = h.Framing.Rigs.Single(r => r.Name == "Rig B");

        Assert.Equal(new DeviceId("mount.2"), h.Framing.SelectedMount!.Id);
        Assert.Equal(new CelestialCoordinates(12, -30), h.Framing.Current!.Center);

        h.Framing.SelectedRig = h.Framing.Rigs.Single(r => r.Name == "Rig A");

        Assert.Equal(new DeviceId("mount.1"), h.Framing.SelectedMount!.Id);
        Assert.Equal(new CelestialCoordinates(5.5, 22), h.Framing.Current!.Center);
    }

    [Fact]
    public async Task TheCurrentField_MovesWhileTheMountSlews()
    {
        var h = await CreateAsync(slew: TimeSpan.FromSeconds(2));
        await h.Mount1.SlewToAsync(new CelestialCoordinates(0, 0)).WaitAsync(TimeSpan.FromSeconds(5));
        var seen = new List<CelestialCoordinates>();

        var slew = h.Mount1.SlewToAsync(new CelestialCoordinates(6, 60));
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(150);
            h.Framing.RefreshCurrent();
            seen.Add(h.Framing.Current!.Center);
        }

        await slew;
        h.Framing.RefreshCurrent();

        Assert.True(seen.Distinct().Count() >= 4, "the field moves with the mount");
        Assert.All(seen, c => Assert.InRange(c.RightAscensionHours, 0, 6));
        Assert.True(seen.Zip(seen.Skip(1), (x, y) => y.DeclinationDegrees >= x.DeclinationDegrees).All(up => up), "on its way, not jumping about");
        Assert.Equal(new CelestialCoordinates(6, 60), h.Framing.Current!.Center);
    }

    [Fact]
    public async Task ASolveOfTheRig_GivesTheSolvedPositionAndRotation_WhileTheMountStaysPut()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(5.5, 22));
        var rig = h.Framing.SelectedRig!;
        var solve = await h.Host.PlateSolving!.CaptureAndSolveAsync(rig, h.Mount1.Id, TimeSpan.FromMilliseconds(10), new PlateSolveDefaults());

        h.Framing.RefreshCurrent();

        Assert.True(solve.Success);
        var current = h.Framing.Current!;
        Assert.Equal(CurrentViewSource.Solved, current.Source);
        Assert.Equal(solve.Center, current.Center);
        Assert.Equal(81.2, current.RotationDegrees);
        Assert.Contains("solved position", h.Framing.CurrentText);
        Assert.Contains("rotation 81.2°", h.Framing.CurrentText);
    }

    [Fact]
    public async Task AfterTheMountMoved_TheMountPositionIsLive_AndTheRotationOfTheSolveRemains()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(5.5, 22));
        await h.Host.PlateSolving!.CaptureAndSolveAsync(h.Framing.SelectedRig!, h.Mount1.Id, TimeSpan.FromMilliseconds(10), new PlateSolveDefaults());

        await h.Mount1.SlewToAsync(new CelestialCoordinates(8, 10));
        h.Framing.RefreshCurrent();

        Assert.Equal(CurrentViewSource.Mount, h.Framing.Current!.Source);
        Assert.Equal(new CelestialCoordinates(8, 10), h.Framing.Current.Center);
        Assert.Equal(81.2, h.Framing.Current.RotationDegrees);
    }

    [Fact]
    public async Task TheSolveOfOneRig_IsNotTakenForTheOther()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(5.5, 22));
        await h.Host.PlateSolving!.CaptureAndSolveAsync(h.Framing.SelectedRig!, h.Mount1.Id, TimeSpan.FromMilliseconds(10), new PlateSolveDefaults());

        h.Framing.SelectedRig = h.Framing.Rigs.Single(r => r.Name == "Rig B");

        Assert.Null(h.Framing.Current!.RotationDegrees);
        Assert.Equal(CurrentViewSource.Mount, h.Framing.Current.Source);
    }

    [Fact]
    public async Task SlewAndCenter_UpdatesTheCurrentField_TowardTheTarget_AndNeverSyncs()
    {
        var h = await CreateAsync();
        await h.Mount1.SlewToAsync(new CelestialCoordinates(0, 0));
        h.Framing.SearchText = "M31";
        await h.Framing.SearchCommand.ExecuteAsync(null);
        var target = h.Framing.Target!.Center;
        var before = SkyMath.AngularSeparationDegrees(h.Mount1.Coordinates, target);

        await h.Framing.SlewAndCenterCommand.ExecuteAsync(null);
        h.Framing.RefreshCurrent();

        var after = SkyMath.AngularSeparationDegrees(h.Framing.Current!.Center, target);
        Assert.True(before > 10);
        Assert.True(after < 0.1, $"the current field is on the target after centering ({after}°)");
        Assert.Equal(0, h.Mount1.SyncCount);
    }
}

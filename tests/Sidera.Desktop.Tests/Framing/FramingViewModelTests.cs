using System.Text;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Sky;

namespace Sidera.Desktop.Tests.Framing;

public sealed class FramingViewModelTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidera-framing-" + Guid.NewGuid().ToString("N"));

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

        public Task<IReadOnlyList<CelestialObject>> SearchAsync(string query, int maxResults = 8, CancellationToken cancellationToken = default)
        {
            var key = CatalogNames.Normalize(query);
            IReadOnlyList<CelestialObject> found = key switch
            {
                "M31" or "NGC224" => [new CelestialObject("M31", ["NGC 224", "Andromeda Galaxy"], new CelestialCoordinates(0.7123, 41.269), "Galaxy", 189.1, Name)],
                "NGC7000" => [new CelestialObject("NGC 7000", ["North America Nebula"], new CelestialCoordinates(20.9883, 44.3333), "Nebula", 130, Name)],
                "WRAP" => [new CelestialObject("Wrap", [], new CelestialCoordinates(23.9998, 5), null, null, Name)],
                _ => [],
            };
            return Task.FromResult(found);
        }
    }

    private sealed class FakeProvider(SkySurveyDescriptor survey, bool offline = false, TimeSpan? delay = null) : ISkySurveyProvider
    {
        public List<SkyViewport> Requests { get; } = [];
        public int Cancelled;
        public string Id => survey.Id;
        public string DisplayName => survey.Title;

        public Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult<SkySurveyInfo?>(Info);

        public SkySurveyInfo Info { get; } = new()
        {
            Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl, Copyright = "Test Observatory", License = "CC-BY-4.0",
        };

        public async Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default)
        {
            lock (Requests)
            {
                Requests.Add(viewport);
            }

            try
            {
                if (delay is { } wait)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }

            return offline
                ? new SkyImage(viewport.WidthPixels, viewport.HeightPixels, new byte[viewport.WidthPixels * viewport.HeightPixels * 4], 5, 5, 0, Info, "No imagery for this view (offline, or not cached).")
                : new SkyImage(viewport.WidthPixels, viewport.HeightPixels, new byte[viewport.WidthPixels * viewport.HeightPixels * 4], 5, 0, 2, Info, null);
        }
    }

    private sealed class ScriptedSolver : IPlateSolver
    {
        public Queue<(CelestialCoordinates Center, double Rotation)> Answers { get; } = new();
        public List<PlateSolveRequest> Requests { get; } = [];
        public string Name => "Scripted";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var (center, rotation) = Answers.Dequeue();
            return Task.FromResult(new PlateSolveResult { Success = true, Center = center, RotationDegrees = rotation, Backend = Name });
        }
    }

    private sealed record Harness(FramingViewModel Framing, SideraRuntimeHost Host, SequenceDraftViewModel Session, FakeProvider Provider, ScriptedSolver Solver, SiteService Settings);

    private async Task<Harness> CreateAsync(bool offline = false, TimeSpan? delay = null, bool connectMount = true)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        await camera.ConnectAsync();
        if (connectMount)
        {
            await mount.ConnectAsync();
        }

        host.AddRig(new Rig(new("rig.main"), "Main Rig", camera.Id, new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176)));
        var solver = new ScriptedSolver();
        host.ConfigurePlateSolver(solver);
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var session = new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, rigs: host.RigRegistry, plateSolving: host.PlateSolving);
        Directory.CreateDirectory(_folder);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_folder, "settings.json")));
        settings.Load();
        settings.SetPlateSolving(new PlateSolvingSettings { ExposureSeconds = 0.01, MaxCenteringAttempts = 3, CenteringToleranceArcseconds = 30 });
        FakeProvider? provider = null;
        var framing = new FramingViewModel(host, settings, session, new FakeCatalog(), survey => provider = new FakeProvider(survey, offline, delay), a => a());
        // The provider is made on the first use; this makes it now so that a test can look at it.
        framing.ScheduleImageLoad();
        provider ??= new FakeProvider(SkySurveys.Defaults[0]);
        return new Harness(framing, host, session, provider, solver, settings);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }

    private static async Task SearchAsync(FramingViewModel framing, string text)
    {
        framing.SearchText = text;
        await framing.SearchCommand.ExecuteAsync(null);
    }

    // ---- Search

    [Fact]
    public async Task ASearch_ForAKnownObject_PlansAFramingThere_WithTheSelectedRig_AndDoesNotTouchTheMount()
    {
        var h = await CreateAsync();
        var mount = h.Host.DeviceRegistry.GetAll().OfType<IMount>().Single();
        var before = mount.Coordinates;

        await SearchAsync(h.Framing, "m 31");

        var target = h.Framing.Target!;
        Assert.Equal("M31", target.Name);
        Assert.Equal(new CelestialCoordinates(0.7123, 41.269), target.Center);
        Assert.Equal(new RigId("rig.main"), target.RigId);
        Assert.Equal("M31", target.CatalogId);
        Assert.Equal("CDS/P/DSS2/color", target.SurveyId);
        Assert.Equal(target.Center, h.Framing.ViewCenter);
        Assert.Equal(before, mount.Coordinates);
        Assert.Contains("Galaxy", h.Framing.Results[0].Type);
    }

    [Fact]
    public async Task ASearch_ForAnUnknownObject_SaysSo_AndPlansNothing()
    {
        var h = await CreateAsync();

        await SearchAsync(h.Framing, "XYZ 12345");

        Assert.Null(h.Framing.Target);
        Assert.Empty(h.Framing.Results);
        Assert.Contains("Nothing found", h.Framing.SearchStatus);
    }

    [Fact]
    public async Task AnAlias_FindsTheSameObject()
    {
        var h = await CreateAsync();

        await SearchAsync(h.Framing, "NGC 224");

        Assert.Equal("M31", h.Framing.Target!.Name);
    }

    // ---- The field of the rig

    [Fact]
    public async Task TheField_IsTheOneOfTheRigsGeometry_AndNothingOfItIsStoredInThePlan()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");

        Assert.Equal("1.79° × 1.20°", h.Framing.FovText);
        Assert.Equal("1.03 \"/px", h.Framing.PixelScaleText);
        Assert.DoesNotContain(typeof(FramingTarget).GetProperties(), p => p.Name.Contains("Field", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SwitchingTheRig_ResizesTheFieldAtOnce()
    {
        var h = await CreateAsync();
        var camera = h.Host.DeviceRegistry.GetAll().OfType<ICamera>().Single();
        h.Host.AddRig(new Rig(new("rig.wide"), "Wide Rig", camera.Id, new OpticalTrain(250, 60, 3.76, 3.76, 6248, 4176)));
        h.Framing.RefreshEquipment();
        await SearchAsync(h.Framing, "M31");
        var narrow = h.Framing.Field!;

        h.Framing.SelectedRig = h.Framing.Rigs.Single(r => r.Name == "Wide Rig");

        Assert.True(h.Framing.Field!.WidthDegrees > 2.9 * narrow.WidthDegrees);
        Assert.Equal(new RigId("rig.wide"), h.Framing.Target!.RigId);
        Assert.Equal("5.38° × 3.60°", h.Framing.FovText);
    }

    [Fact]
    public async Task ARigWithoutAFocalLength_HasNoFieldToDraw_ButThePlanStillWorks()
    {
        var h = await CreateAsync();
        var camera = h.Host.DeviceRegistry.GetAll().OfType<ICamera>().Single();
        h.Host.AddRig(new Rig(new("rig.bare"), "Bare Rig", new DeviceId("camera")));
        h.Framing.RefreshEquipment();

        h.Framing.SelectedRig = h.Framing.Rigs.Single(r => r.Name == "Bare Rig");
        await SearchAsync(h.Framing, "M31");

        Assert.False(h.Framing.HasField);
        Assert.NotNull(h.Framing.Target);
        Assert.Contains("Not known", h.Framing.FovText);
        Assert.NotNull(camera);
    }

    // ---- Drag and rotation

    [Fact]
    public async Task DraggingTheFrame_MovesThePlanByTheDraggedAngle_NotByADifferenceOfCoordinates_AndNotTheMount()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "NGC 7000");
        var mount = h.Host.DeviceRegistry.GetAll().OfType<IMount>().Single();
        var before = mount.Coordinates;
        var view = h.Framing.Viewport;

        h.Framing.MoveTarget(view.ToSky(view.WidthPixels / 2.0 - 100, view.HeightPixels / 2.0)); // 100 pixels to the left of the center: to the east

        var moved = h.Framing.Target!.Center;
        Assert.Equal(100 * view.DegreesPerPixel, SkyMath.AngularSeparationDegrees(view.Center, moved), 2);
        Assert.True(moved.RightAscensionHours > view.Center.RightAscensionHours); // east
        Assert.Equal(before, mount.Coordinates);
        Assert.Equal("NGC 7000", h.Framing.Target.Name);
    }

    [Fact]
    public async Task ADragAcrossZeroHours_StaysOnTheSky()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "wrap");
        var view = h.Framing.Viewport;

        h.Framing.MoveTarget(view.ToSky(view.WidthPixels / 2.0 - 200, view.HeightPixels / 2.0));

        var center = h.Framing.Target!.Center;
        Assert.InRange(center.RightAscensionHours, 0, 24);
        Assert.True(SkyMath.AngularSeparationDegrees(view.Center, center) < 5);
    }

    [Fact]
    public async Task TheRotation_IsEditedAsNumbers_AndNormalizedLikeTheRotationOfASolve()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");

        h.Framing.RotationText = "87.5";
        Assert.Equal(87.5, h.Framing.Target!.DesiredRotationDegrees);

        h.Framing.RotationText = "190";
        Assert.Equal(-170, h.Framing.Target.DesiredRotationDegrees);

        h.Framing.RotationText = "abc";
        Assert.Contains("number of degrees", h.Framing.ProblemText);
        Assert.Equal(-170, h.Framing.Target.DesiredRotationDegrees); // an invalid entry changes nothing

        h.Framing.RotateByCommand.Execute(10.0);
        Assert.Equal(-160, h.Framing.Target.DesiredRotationDegrees);
        Assert.Equal("-160", h.Framing.RotationText);
        Assert.Empty(h.Framing.ProblemText);
    }

    [Fact]
    public async Task TheRotation_IsKeptWhenAnotherObjectIsChosen()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "40";

        await SearchAsync(h.Framing, "NGC 7000");

        Assert.Equal("NGC 7000", h.Framing.Target!.Name);
        Assert.Equal(40, h.Framing.Target.DesiredRotationDegrees);
    }

    // ---- Zoom and pan

    [Fact]
    public async Task ZoomAndPan_ChangeTheView_NotThePlan()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");
        var target = h.Framing.Target!;
        var width = h.Framing.FieldDegrees;

        h.Framing.ZoomCommand.Execute(0.5);
        h.Framing.PanCommand.Execute(new ViewDelta(120, 40));

        Assert.Equal(width / 2, h.Framing.FieldDegrees, 9);
        Assert.NotEqual(target.Center, h.Framing.ViewCenter);
        Assert.Equal(target, h.Framing.Target);
    }

    // ---- Imagery

    [Fact]
    public async Task TheImagery_IsLoadedForTheViewOffTheUiThread_WithItsAttribution()
    {
        var h = await CreateAsync();

        await SearchAsync(h.Framing, "M31");
        await WaitAsync(() => h.Framing.Image is not null, "the imagery");

        Assert.Equal(h.Framing.Viewport.Center, h.Framing.ImageViewport!.Center);
        Assert.Contains("Test Observatory", h.Framing.AttributionText);
        Assert.Contains("CC-BY-4.0", h.Framing.AttributionText);
        Assert.Empty(h.Framing.ImageNote);
    }

    [Fact]
    public async Task WithoutTheNetwork_TheFrameAndTheCoordinatesStillWork_AndTheMissingImageryIsSaid()
    {
        var h = await CreateAsync(offline: true);

        await SearchAsync(h.Framing, "M31");
        await WaitAsync(() => h.Framing.Image is not null, "the answer of the provider");
        h.Framing.RotationText = "30";

        Assert.True(h.Framing.Image!.HasNoImagery);
        Assert.Contains("No imagery", h.Framing.ImageNote);
        Assert.True(h.Framing.HasField);
        Assert.Equal("1.79° × 1.20°", h.Framing.FovText);
        Assert.Equal(30, h.Framing.Target!.DesiredRotationDegrees);
        Assert.NotEmpty(h.Framing.CenterText);
    }

    [Fact]
    public async Task ANewView_CancelsTheLoadThatIsRunning_SoADragIsNeverBehind()
    {
        var h = await CreateAsync(delay: TimeSpan.FromSeconds(30));
        await SearchAsync(h.Framing, "M31");
        await WaitAsync(() => h.Provider.Requests.Count >= 1, "the first request");

        h.Framing.PanCommand.Execute(new ViewDelta(10, 0));
        await WaitAsync(() => h.Provider.Cancelled >= 1, "the first load to be cancelled");

        Assert.True(h.Provider.Cancelled >= 1);
    }

    [Fact]
    public async Task ASurveyThatThrows_IsANote_NotACrash()
    {
        var h = await CreateAsync();
        var failing = new FramingViewModel(
            h.Host, h.Settings, h.Session, new FakeCatalog(), _ => new ThrowingProvider(), a => a());

        await SearchAsync(failing, "M31");
        await WaitAsync(() => failing.ImageNote.Length > 0, "the note");

        Assert.Contains("could not be loaded", failing.ImageNote);
        Assert.NotNull(failing.Target);
    }

    private sealed class ThrowingProvider : ISkySurveyProvider
    {
        public string Id => "x";
        public string DisplayName => "x";
        public Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult<SkySurveyInfo?>(null);
        public Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");
    }

    // ---- Slew & Center

    [Fact]
    public async Task WithoutAConnectedMount_SlewAndCenterIsDisabled_WithTheReason()
    {
        var h = await CreateAsync(connectMount: false);
        await SearchAsync(h.Framing, "M31");

        Assert.False(h.Framing.SlewAndCenterCommand.CanExecute(null));
        Assert.Contains("Connect the mount", h.Framing.SlewDisabledText);
        Assert.True(h.Framing.AddToSessionCommand.CanExecute(null)); // planning needs no hardware
    }

    [Fact]
    public async Task WithoutATarget_SlewAndCenterIsDisabled()
    {
        var h = await CreateAsync();

        Assert.False(h.Framing.SlewAndCenterCommand.CanExecute(null));
        Assert.Contains("Choose a target", h.Framing.SlewDisabledText);
        Assert.False(h.Framing.AddToSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task SlewAndCenter_UsesTheCenterOfTheFraming_ShowsPositionAndRotation_AndNeverSyncs()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "87.5";
        var target = h.Framing.Target!.Center;
        var mount = h.Host.DeviceRegistry.GetAll().OfType<IMount>().Single();
        var off = SkyMath.FromTangentOffset(target, 0.02, 0.01);
        h.Solver.Answers.Enqueue((off, 81.2));
        h.Solver.Answers.Enqueue((target, 81.2));

        await h.Framing.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.StartsWith("Centered", h.Framing.PositionText);
        Assert.Equal("Centered", h.Framing.StatusText);
        Assert.Equal("Target 87.5°\nCurrent 81.2°\nDifference -6.3°", h.Framing.RotationCompareText);
        Assert.Equal(2, h.Solver.Requests.Count);
        // The first slew went to the center of the framing; a sync would have put the mount on the solved position instead.
        Assert.True(SkyMath.AngularSeparationDegrees(h.Solver.Requests[0].ApproximateCenter!, target) < 1e-6);
        Assert.True(SkyMath.AngularSeparationDegrees(mount.Coordinates, off) > 0.005);
        Assert.False(h.Framing.IsBusy);
    }

    [Fact]
    public async Task ACenteringThatFails_IsShown_AndNothingIsRotated()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "M31");
        for (var i = 0; i < 20; i++)
        {
            h.Solver.Answers.Enqueue((SkyMath.FromTangentOffset(h.Framing.Target!.Center, 0.1, 0), 10));
        }

        await h.Framing.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.Contains("attempts", h.Framing.PositionText);
        Assert.Equal("Not centered", h.Framing.StatusText);
    }

    // ---- Session

    [Fact]
    public async Task AddToSession_PutsASlewAndCenterWithTheCoordinatesInTheSession_AndNothingElse()
    {
        var h = await CreateAsync();
        await SearchAsync(h.Framing, "NGC 7000");
        h.Framing.RotationText = "73.5";
        var center = h.Framing.Target!.Center;

        h.Framing.AddToSessionCommand.Execute(null);

        var step = Assert.IsType<SlewAndCenterStepDraft>(Assert.Single(DraftsOf(h.Session)));
        Assert.Equal(center.RightAscensionHours, step.RightAscensionHours);
        Assert.Equal(center.DeclinationDegrees, step.DeclinationDegrees);
        Assert.Equal("NGC 7000", step.TargetName);
        Assert.Equal(73.5, step.DesiredRotationDegrees);
        Assert.Equal(new RigId("rig.main"), step.RigId);
        Assert.DoesNotContain(DraftsOf(h.Session), s => s is SyncMountStepDraft);
        Assert.Contains("Added Slew & Center for NGC 7000", h.Framing.StatusText);
    }

    private static IReadOnlyList<SequenceStepDraft> DraftsOf(SequenceDraftViewModel session) => session.Snapshot();

    [Fact]
    public async Task TheFramingMetadata_SurvivesTheDocument_AndAnOldStepWithoutItStillLoads()
    {
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount"), new RigId("rig.main"), 20.9883, 44.3333, 60, 5, 5, "NGC 7000", 73.5);
        var serializer = new JsonSequenceDocumentSerializer();
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument([step]), CancellationToken.None);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        var loaded = Assert.IsType<SlewAndCenterStepDraft>(Assert.Single(SequenceDocumentMapper.ToDrafts(await serializer.LoadAsync(stream, CancellationToken.None))));

        Assert.Equal(step, loaded);
        Assert.Contains("\"targetName\": \"NGC 7000\"", text);
        Assert.Contains("\"desiredRotationDegrees\": 73.5", text);
        Assert.DoesNotContain("fieldOfView", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("viewport", text, StringComparison.OrdinalIgnoreCase);

        var plain = step with { TargetName = null, DesiredRotationDegrees = null };
        using var second = new MemoryStream();
        await serializer.SaveAsync(second, SequenceDocumentMapper.ToDocument([plain]), CancellationToken.None);
        Assert.DoesNotContain("targetName", Encoding.UTF8.GetString(second.ToArray()));
        second.Position = 0;
        Assert.Equal(plain, Assert.Single(SequenceDocumentMapper.ToDrafts(await serializer.LoadAsync(second, CancellationToken.None))));
    }

    // ---- Settings

    [Fact]
    public void TheSkyAtlasSettings_AreSavedAndLoaded_AndAnOldFileGetsTheDefaults()
    {
        var path = Path.Combine(_folder, "atlas", "settings.json");
        var service = new SiteService(new SideraSettingsStore(path));
        service.Load();
        Assert.Equal(new SkyAtlasSettings(), service.SkyAtlas);

        var changed = new SkyAtlasSettings { DefaultSurveyId = "CDS/P/2MASS/color", CacheDirectory = @"D:\sky", MaxCacheMegabytes = 2048, NetworkTimeoutSeconds = 20 };
        Assert.True(service.SetSkyAtlas(changed).Succeeded);

        var reloaded = new SiteService(new SideraSettingsStore(path));
        reloaded.Load();
        Assert.Equal(changed, reloaded.SkyAtlas);

        var old = SideraSettingsSerializer.Deserialize(Encoding.UTF8.GetBytes("{\"format\":\"sidera-settings\",\"version\":1}"));
        Assert.Equal(new SkyAtlasSettings(), old.SkyAtlas);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(512, 0)]
    [InlineData(512, 1000)]
    public void SkyAtlasSettingsThatAreNotValid_AreRefused(int megabytes, double timeout)
    {
        var path = Path.Combine(_folder, "atlas2", "settings.json");
        var service = new SiteService(new SideraSettingsStore(path));
        service.Load();

        var result = service.SetSkyAtlas(new SkyAtlasSettings { MaxCacheMegabytes = megabytes, NetworkTimeoutSeconds = timeout });

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(path));
    }
}

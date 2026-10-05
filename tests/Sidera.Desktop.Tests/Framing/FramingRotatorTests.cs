using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Framing;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Desktop.Settings;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Sky;

namespace Sidera.Desktop.Tests.Framing;

/// <summary>The framing page with and without a rotator: Center &amp; Rotate or Slew &amp; Center, what is added to the session, and the manual rotation helper. Nothing synchronizes the mount.</summary>
public sealed class FramingRotatorTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sidera-framing-rot-" + Guid.NewGuid().ToString("N"));

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
            IReadOnlyList<CelestialObject> found = CatalogNames.Normalize(query) == "M31"
                ? [new CelestialObject("M31", ["NGC 224"], new CelestialCoordinates(0.7123, 41.269), "Galaxy", 189.1, Name)]
                : [];
            return Task.FromResult(found);
        }
    }

    private sealed class FakeProvider(SkySurveyDescriptor survey) : ISkySurveyProvider
    {
        public string Id => survey.Id;
        public string DisplayName => survey.Title;
        public Task<SkySurveyInfo?> GetInfoAsync(CancellationToken cancellationToken = default) => Task.FromResult<SkySurveyInfo?>(new SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl });

        public Task<SkyImage> GetImageAsync(SkyViewport viewport, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SkyImage(viewport.WidthPixels, viewport.HeightPixels, new byte[viewport.WidthPixels * viewport.HeightPixels * 4], 5, 0, 2, new SkySurveyInfo { Id = survey.Id, Title = survey.Title, SourceUrl = survey.BaseUrl }, null));
    }

    /// <summary>Reports the real rotation of the simulated rotator (or a fixed one when there is none) and where the mount points.</summary>
    private sealed class Solver(SimulatedMount mount, SimulatedRotator? rotator) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public double FixedRotation { get; set; }
        public Func<CancellationToken, Task>? Gate { get; set; }
        public int Calls { get; private set; }
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public async Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Gate is not null)
            {
                await Gate(cancellationToken);
            }

            return new PlateSolveResult
            {
                Success = true, Center = mount.Coordinates, RotationDegrees = rotator?.CurrentSkyRotation ?? FixedRotation, Backend = Name,
            };
        }
    }

    private sealed record Harness(FramingViewModel Framing, SideraRuntimeHost Host, SequenceDraftViewModel Session, Solver Solver, SimulatedMount Mount, SimulatedRotator? Rotator);

    private async Task<Harness> CreateAsync(bool withRotator, bool calibrated = true, bool connectRotator = true, double trueOffset = 0, double? modelOffset = null, bool modelReversed = false)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var camera = host.AddSimulatedCamera(new("camera"), "Camera", 3);
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        var rotator = withRotator ? host.AddSimulatedRotator(new("rotator"), "Rotator", 0, 3600, trueOffset) : null;
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        if (rotator is not null && connectRotator)
        {
            await rotator.ConnectAsync();
        }

        host.AddRig(new Rig(
            new("rig.main"), "Main Rig", camera.Id, new OpticalTrain(750, 150, 3.76, 3.76, 6248, 4176),
            rotatorId: rotator?.Id, rotatorModel: rotator is not null && calibrated ? new RotatorSkyModel(modelOffset ?? trueOffset, modelReversed) : null));
        var solver = new Solver(mount, rotator) { FixedRotation = 81.2 };
        host.ConfigurePlateSolver(solver);
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var session = new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, rigs: host.RigRegistry, plateSolving: host.PlateSolving, rotation: host.Rotation);
        Directory.CreateDirectory(_folder);
        var settings = new SiteService(new SideraSettingsStore(Path.Combine(_folder, "settings.json")));
        settings.Load();
        settings.SetPlateSolving(new PlateSolvingSettings { ExposureSeconds = 0.01, MaxCenteringAttempts = 3, CenteringToleranceArcseconds = 30, RotationToleranceDegrees = 0.7, MaxRotationAttempts = 3 });
        var framing = new FramingViewModel(host, settings, session, new FakeCatalog(), survey => new FakeProvider(survey), a => a());
        return new Harness(framing, host, session, solver, mount, rotator);
    }

    private static async Task SearchAsync(FramingViewModel framing, string text)
    {
        framing.SearchText = text;
        await framing.SearchCommand.ExecuteAsync(null);
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

    // ---- Which action the page offers

    [Fact]
    public async Task ARigWithARotator_OffersCenterAndRotate_AndNotSlewAndCenter()
    {
        var h = await CreateAsync(withRotator: true);

        Assert.True(h.Framing.HasRotator);
        Assert.False(h.Framing.HasNoRotator);
    }

    [Fact]
    public async Task ARigWithoutARotator_OffersSlewAndCenter()
    {
        var h = await CreateAsync(withRotator: false);

        Assert.False(h.Framing.HasRotator);
        Assert.True(h.Framing.HasNoRotator);
    }

    [Fact]
    public async Task CenterAndRotate_WithoutACalibration_IsDisabled_WithTheReason_ButPlanningStillWorks()
    {
        var h = await CreateAsync(withRotator: true, calibrated: false);
        await SearchAsync(h.Framing, "M31");

        Assert.False(h.Framing.CenterAndRotateCommand.CanExecute(null));
        Assert.Contains("Calibrate the rotator", h.Framing.SlewDisabledText);
        Assert.True(h.Framing.AddToSessionCommand.CanExecute(null));
    }

    [Fact]
    public async Task CenterAndRotate_WithADisconnectedRotator_IsDisabled_WithTheReason()
    {
        var h = await CreateAsync(withRotator: true, connectRotator: false);
        await SearchAsync(h.Framing, "M31");

        Assert.False(h.Framing.CenterAndRotateCommand.CanExecute(null));
        Assert.Contains("Connect the rotator", h.Framing.SlewDisabledText);
    }

    [Fact]
    public async Task CenterAndRotate_NeedsATarget()
    {
        var h = await CreateAsync(withRotator: true);

        Assert.False(h.Framing.CenterAndRotateCommand.CanExecute(null));
        Assert.Contains("Choose a target", h.Framing.SlewDisabledText);
    }

    // ---- Center & Rotate

    [Fact]
    public async Task CenterAndRotate_CentersAndTurnsTheSky_ShowsTargetCurrentAndDifference_AndNeverSyncs()
    {
        var h = await CreateAsync(withRotator: true, trueOffset: 10);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "45";
        var target = h.Framing.Target!.Center;

        await h.Framing.CenterAndRotateCommand.ExecuteAsync(null);

        Assert.Equal("Centered and rotated", h.Framing.StatusText);
        Assert.StartsWith("Centered", h.Framing.PositionText);
        Assert.Equal("Target 45°\nCurrent 45°\nDifference 0°", h.Framing.RotationCompareText);
        Assert.Equal(45, h.Rotator!.CurrentSkyRotation, 6);
        Assert.True(SkyMath.AngularSeparationDegrees(h.Mount.Coordinates, target) < 0.01);
        Assert.Equal(0, h.Mount.SyncCount);
        Assert.False(h.Framing.IsBusy);
        Assert.Equal(string.Empty, h.Framing.RotationAdjustmentText); // there is a rotator: nothing is asked of the person
    }

    [Fact]
    public async Task CenterAndRotate_UsesTheToleranceOfTheSettings()
    {
        // A calibration that is 0.6 degrees off is within the 0.7 of the settings: one pass, no correction.
        var h = await CreateAsync(withRotator: true, trueOffset: 0.6, modelOffset: 0);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "30";

        await h.Framing.CenterAndRotateCommand.ExecuteAsync(null);

        Assert.Equal("Centered and rotated", h.Framing.StatusText);
        Assert.Equal(1, h.Rotator!.MovesStarted);
    }

    [Fact]
    public async Task CenterAndRotate_ARotationThatCannotBeVerified_IsShownAsAFailure()
    {
        var h = await CreateAsync(withRotator: true, modelReversed: true); // the calibration says reversed; the rotator is not
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "20";

        await h.Framing.CenterAndRotateCommand.ExecuteAsync(null);

        Assert.NotEqual("Centered and rotated", h.Framing.StatusText);
        Assert.False(h.Framing.IsBusy);
        Assert.Equal(0, h.Mount.SyncCount);
    }

    [Fact]
    public async Task CenterAndRotate_CanBeCancelled_AndLeavesThePageUsable()
    {
        var h = await CreateAsync(withRotator: true);
        await SearchAsync(h.Framing, "M31");
        var started = new TaskCompletionSource();
        h.Solver.Gate = async token =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };

        var run = h.Framing.CenterAndRotateCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(h.Framing.IsBusy);
        h.Framing.CancelCommand.Execute(null);
        await run;

        Assert.Equal("Cancelled", h.Framing.StatusText);
        Assert.False(h.Framing.IsBusy);
        Assert.False(h.Host.Rotation!.IsBusy);
        Assert.Equal(0, h.Mount.SyncCount);
    }

    // ---- Add to session

    [Fact]
    public async Task AddToSession_WithARotator_PutsACenterAndRotateInTheSession_WithTheSettingsTolerances_AndNoSync()
    {
        var h = await CreateAsync(withRotator: true);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "73.5";
        var center = h.Framing.Target!.Center;

        h.Framing.AddToSessionCommand.Execute(null);

        var steps = h.Session.Snapshot();
        var step = Assert.IsType<CenterAndRotateStepDraft>(Assert.Single(steps));
        Assert.Equal(center.RightAscensionHours, step.RightAscensionHours);
        Assert.Equal(center.DeclinationDegrees, step.DeclinationDegrees);
        Assert.Equal(73.5, step.SkyRotationDegrees);
        Assert.Equal(0.7, step.RotationToleranceDegrees);
        Assert.Equal(3, step.MaxRotationAttempts);
        Assert.Equal("M31", step.TargetName);
        Assert.Equal(new RigId("rig.main"), step.RigId);
        Assert.Equal(new DeviceId("mount"), step.MountId);
        Assert.DoesNotContain(steps, s => s is SyncMountStepDraft);
        Assert.Contains("Added Center & Rotate for M31", h.Framing.StatusText);
    }

    [Fact]
    public async Task AddToSession_WithoutARotator_KeepsTheSlewAndCenter_WithTheRotationAsMetadata_AndNoRotationStep()
    {
        var h = await CreateAsync(withRotator: false);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "73.5";

        h.Framing.AddToSessionCommand.Execute(null);

        var steps = h.Session.Snapshot();
        var step = Assert.IsType<SlewAndCenterStepDraft>(Assert.Single(steps));
        Assert.Equal(73.5, step.DesiredRotationDegrees);
        Assert.DoesNotContain(steps, s => s is SyncMountStepDraft or RotateToAngleStepDraft or RotateAndVerifyStepDraft or CenterAndRotateStepDraft);
    }

    // ---- The manual helper for a rig without a rotator

    [Fact]
    public async Task SolveAgain_ShowsTargetCurrentDifferenceAndTheAdjustment_AsSignedDegrees_AndMovesNothing()
    {
        var h = await CreateAsync(withRotator: false);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "87.5";
        var before = h.Mount.Coordinates;

        await h.Framing.SolveAgainCommand.ExecuteAsync(null);

        Assert.Equal("Target 87.5°\nCurrent 81.2°\nDifference -6.3°", h.Framing.RotationCompareText);
        Assert.Contains("+6.3°", h.Framing.RotationAdjustmentText);
        Assert.Contains("solve again", h.Framing.RotationAdjustmentText);
        Assert.DoesNotContain("clockwise", h.Framing.RotationAdjustmentText, StringComparison.OrdinalIgnoreCase); // the sign is the sky rotation's; the camera's direction is not guessed
        Assert.Equal(before, h.Mount.Coordinates);
        Assert.Equal(0, h.Mount.SyncCount);
        Assert.Equal("Solved", h.Framing.StatusText);
    }

    [Fact]
    public async Task SolveAgain_AfterTheCameraWasTurned_ShowsTheNewDifference()
    {
        var h = await CreateAsync(withRotator: false);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "-170";
        h.Solver.FixedRotation = 175;
        await h.Framing.SolveAgainCommand.ExecuteAsync(null);
        Assert.Contains("+15°", h.Framing.RotationAdjustmentText); // the shortest way round the wrap: -170 - 175 = -345 = +15

        h.Solver.FixedRotation = -169.8;
        await h.Framing.SolveAgainCommand.ExecuteAsync(null);

        Assert.Contains("-0.2°", h.Framing.RotationAdjustmentText);
        Assert.Equal(2, h.Solver.Calls);
    }

    [Fact]
    public async Task SolveAgain_NeedsATarget()
    {
        var h = await CreateAsync(withRotator: false);

        Assert.False(h.Framing.SolveAgainCommand.CanExecute(null));
    }

    [Fact]
    public async Task SlewAndCenter_WithoutARotator_ShowsTheAdjustmentToo_AndStillNeverRotatesOrSyncs()
    {
        var h = await CreateAsync(withRotator: false);
        await SearchAsync(h.Framing, "M31");
        h.Framing.RotationText = "90";

        await h.Framing.SlewAndCenterCommand.ExecuteAsync(null);

        Assert.Equal("Centered", h.Framing.StatusText);
        Assert.Contains("+8.8°", h.Framing.RotationAdjustmentText);
        Assert.Equal(0, h.Mount.SyncCount);
    }
}

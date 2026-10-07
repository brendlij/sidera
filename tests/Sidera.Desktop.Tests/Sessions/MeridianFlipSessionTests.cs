using Sidera.Core.Astrometry;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Rigs;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Focusing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>The meridian flip as the automation of a session: what it compiles to, how it is saved, what the drawer shows and says, and one run through the editor and the sequencer on the simulator.</summary>
public sealed class MeridianFlipSessionTests : IAsyncLifetime
{
    private static readonly ObservingSite Site = new(50.1, 8.6, 120);
    private static readonly DateTime Start = new(2026, 3, 1, 22, 0, 0, DateTimeKind.Utc);
    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);
    private static readonly RigId A = new("rig.a");
    private static readonly RigId B = new("rig.b");

    private sealed class SkyClock : TimeProvider
    {
        private readonly object _gate = new();
        private DateTime _now = Start;
        public double TargetRightAscensionHours => MeridianFlipTiming.LocalSiderealTimeHours(Start, Site.LongitudeDegrees) + 10.0 / 60;

        public DateTime UtcNow
        {
            get { lock (_gate) { return _now; } }
        }

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);

        public void SetHourAngle(double minutes)
        {
            lock (_gate)
            {
                _now = Start + TimeSpan.FromMinutes((minutes + 10) / 1.00273790935);
            }
        }
    }

    private sealed class Solver(SideraRuntimeHost host) : IPlateSolver
    {
        public bool Fails { get; set; }
        public string Name => "Simulated";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            if (Fails)
            {
                return Task.FromResult(new PlateSolveResult { Success = false, Backend = Name, Message = "No stars" });
            }

            var at = request.ApproximateCenter ?? host.DeviceRegistry.GetAll().OfType<IMount>().First().Coordinates;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = SkyMath.FromTangentOffset(at, 0.01, 0), RotationDegrees = 0, Backend = Name });
        }
    }

    private readonly List<SideraRuntimeHost> _hosts = [];
    private readonly List<MainViewModel> _apps = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var vm in _apps)
        {
            vm.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private async Task<(SideraRuntimeHost Host, SkyClock Clock, Solver Solver, SimulatedMount Mount)> CreateAsync(bool connect = true, bool solver = true)
    {
        var clock = new SkyClock();
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        }

        var mount = host.AddSimulatedMount(new("mount.1"), "Mount 1", TimeSpan.FromMilliseconds(20), () => clock.UtcNow, new MountSite(Site.LatitudeDegrees, Site.LongitudeDegrees, Site.ElevationMeters));
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50));
        host.AddRig(new Rig(A, "Main", new("camera.a"), Optics, new("focuser.a"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(B, "Wide", new("camera.b"), Optics, new("focuser.b"), null, null, null, new("mount.1"), new("guider.1")));
        foreach (var rig in new[] { A, B })
        {
            host.AddSimulatedFocusModel(rig, new SimulatedFocusModel(2600));
        }

        var s = new Solver(host);
        if (solver)
        {
            host.ConfigurePlateSolver(s);
        }

        if (connect)
        {
            foreach (var device in host.DeviceRegistry.GetAll())
            {
                await device.ConnectAsync();
            }
        }

        clock.SetHourAngle(-10);
        return (host, clock, s, mount);
    }

    private static ImagingSetupCatalog Catalog(SideraRuntimeHost host) => new(host.DeviceRegistry, host.RigRegistry);

    /// <summary>The session of the tests: M31 for one or two setups that share the mount and the guider, guiding started once and stopped at the end, the flip as given (<c>null</c> follows the application).</summary>
    private static SessionDefinition Session(SkyClock clock, MeridianFlipSettings? flip, bool twoSetups = true, double? rotation = null)
    {
        var lanes = twoSetups
            ? new[] { Lane(A, Block(null, 0.5, 8)), Lane(B, Block(null, 0.2, 20)) }
            : [Lane(A, Block(null, 0.5, 8))];
        var target = new SessionTarget(
            Guid.NewGuid(), "M31", clock.TargetRightAscensionHours, 41.3, rotation, true, [new StartGuidingAction(Guid.NewGuid())], lanes, []);
        return SessionDefinition.Empty with
        {
            Automation = flip is null ? SessionAutomation.Defaults : new SessionAutomation(flip),
            Targets = [target],
            End = [new StopGuidingAction(Guid.NewGuid())],
        };
    }

    private static MeridianFlipSettings Settings(Func<MeridianFlipSettings, MeridianFlipSettings>? change = null)
    {
        var s = new MeridianFlipSettings { Enabled = true, SolveExposureSeconds = 0.05, MaxCenteringAttempts = 3, VerifyRotationAfterFlip = false };
        return change?.Invoke(s) ?? s;
    }

    // ---- compiling

    [Fact]
    public async Task AnEnabledFlip_IsAPolicyOfTheImagingBlock_WithTheTargetOfTheSession()
    {
        var (host, clock, _, _) = await CreateAsync();
        var session = Session(clock, Settings(s => s with { DitherAfterFlip = true }), rotation: 81.5);

        var compiled = SessionCompiler.Compile(session, Catalog(host));

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var block = Assert.IsType<MultiRigStepDraft>(compiled.Steps.Single(s => s is MultiRigStepDraft));
        var flip = block.MeridianFlip!;
        Assert.True(flip.IsEnabled);
        Assert.True(flip.Settings.DitherAfterFlip);
        Assert.Equal("M31", flip.TargetName);
        Assert.Equal(clock.TargetRightAscensionHours, flip.RightAscensionHours, 9);
        Assert.Equal(81.5, flip.DesiredRotationDegrees);
        Assert.DoesNotContain(compiled.Steps, s => s.Kind == SequenceStepKind.Dither); // no steps for it: a policy
    }

    [Fact]
    public async Task ADisabledFlip_ChangesNothing()
    {
        var (host, clock, _, _) = await CreateAsync();

        Assert.Null(((MultiRigStepDraft)SessionCompiler.Compile(Session(clock, null), Catalog(host)).Steps.Single(s => s is MultiRigStepDraft)).MeridianFlip);
        Assert.Null(((MultiRigStepDraft)SessionCompiler.Compile(Session(clock, new MeridianFlipSettings()), Catalog(host)).Steps.Single(s => s is MultiRigStepDraft)).MeridianFlip);
    }

    [Fact]
    public async Task SettingsThatMakeNoSense_AreProblemsOfTheSession()
    {
        var (host, clock, _, _) = await CreateAsync();

        var compiled = SessionCompiler.Compile(Session(clock, Settings(s => s with { FlipAfterMeridianMinutes = 20, LatestAllowedFlipMinutes = 10, MaxFlipAttempts = 9 })), Catalog(host));

        Assert.Contains(compiled.Problems, p => p.Message.StartsWith("Meridian flip: ", StringComparison.Ordinal) && p.Message.Contains("latest allowed flip", StringComparison.Ordinal));
        Assert.Contains(compiled.Problems, p => p.Message.Contains("flip attempts", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecenteringWithoutAPlateSolver_IsSaidBeforeTheRun()
    {
        var (host, clock, _, _) = await CreateAsync(solver: false);
        var compiled = SessionCompiler.Compile(Session(clock, Settings()), Catalog(host));
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, compiled.Steps, new SequenceDraftContext(host.RigRegistry, shared, host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving));

        Assert.Contains(validation.StepProblems.Values.SelectMany(p => p), p => p.Contains("plate solving", StringComparison.Ordinal));
    }

    // ---- persistence

    [Fact]
    public async Task TheFlipOfASession_IsSaved_AndComesBack()
    {
        var (_, clock, _, _) = await CreateAsync();
        var settings = Settings(s => s with
        {
            PauseBeforeMeridianMinutes = 7, FlipAfterMeridianMinutes = 3, LatestAllowedFlipMinutes = 20, StopGuidingBeforeFlip = false, RecenterAfterFlip = false, AutofocusAfterFlip = true,
            RestartGuidingAfterFlip = false, DitherAfterFlip = true, PauseAfterFlipMinutes = 1.5, MaxFlipAttempts = 3, FailureBehavior = MeridianFlipFailureBehavior.AbortSession,
            CenteringToleranceArcseconds = 30, MaxCenteringAttempts = 4,
        });
        var serializer = new JsonSequenceDocumentSerializer();

        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, new SequenceDocument("T", [], null, null, Session(clock, settings)), CancellationToken.None);
        stream.Position = 0;
        var back = (await serializer.LoadAsync(stream, CancellationToken.None)).Session!;

        Assert.Equal(settings, back.Automation.Flip);
    }

    [Fact]
    public async Task ASessionThatFollowsTheApplication_WritesNoFlip_AndLoadsWithTheFlipOfTheApplication()
    {
        var (_, clock, _, _) = await CreateAsync();
        var serializer = new JsonSequenceDocumentSerializer();
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, new SequenceDocument("T", [], null, null, Session(clock, null)), CancellationToken.None);
        stream.Position = 0;
        var text = await new StreamReader(stream).ReadToEndAsync();

        Assert.DoesNotContain("failureBehavior", text, StringComparison.Ordinal); // nothing of the application's settings is copied into the file
        using var again = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        var back = (await serializer.LoadAsync(again, CancellationToken.None)).Session!;
        Assert.True(back.Automation.UsesDefaultFlip);
        Assert.Null(back.Automation.Flip);
    }

    [Fact]
    public async Task TheFlipOfAnExplicitBlock_IsSaved_WithItsTarget_AndComesBack()
    {
        var policy = new MeridianFlipPolicyDraft(Settings(), 5.5, 22, "Target", 12, B, 1.2, 0.4, 2, 30);
        var block = new MultiRigStepDraft(Guid.NewGuid(), [new RigTrackDraft(Guid.NewGuid(), A, [new RigExposureStepDraft(Guid.NewGuid(), 1)])], null, true, policy);
        var serializer = new JsonSequenceDocumentSerializer();

        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument([block]), CancellationToken.None);
        stream.Position = 0;
        var back = SequenceDocumentMapper.ToDrafts(await serializer.LoadAsync(stream, CancellationToken.None));

        Assert.Equal(policy, ((MultiRigStepDraft)back[0]).MeridianFlip);
    }

    [Fact]
    public async Task ABrokenFlipSetting_IsRefusedWithASentence()
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":9,\"steps\":[],\"session\":{\"automation\":{\"meridianFlip\":{\"failureBehavior\":\"explode\"}},\"start\":[],\"targets\":[],\"end\":[]}}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

        var ex = await Assert.ThrowsAsync<SequenceDocumentException>(() => new JsonSequenceDocumentSerializer().LoadAsync(stream, CancellationToken.None));

        Assert.Contains("failureBehavior", ex.Message, StringComparison.Ordinal);
    }

    // ---- the editor

    private async Task<(MainViewModel Vm, SessionEditorViewModel Editor, SkyClock Clock, Solver Solver, SimulatedMount Mount)> EditorAsync(Func<MeridianFlipSettings, MeridianFlipSettings>? change = null, bool flip = true)
    {
        var (host, clock, solver, mount) = await CreateAsync();
        var options = new DemoOptions();
        var vm = new MainViewModel(host, a => a(), options);
        _apps.Add(vm);
        vm.SequenceDraft.Clock = clock;
        vm.SequenceDraft.SiteProvider = () => Site;
        vm.SessionEditor.Load(Session(clock, flip ? Settings(change) : null));
        return (vm, vm.SessionEditor, clock, solver, mount);
    }

    private static FlipEditorViewModel OpenFlip(SessionEditorViewModel editor)
    {
        editor.SelectFlip();
        return Assert.IsType<FlipEditorViewModel>(editor.Drawer);
    }

    [Fact]
    public async Task TheFlipFields_ChangeTheSequence_AndTheSummary()
    {
        var (vm, editor, _, _, _) = await EditorAsync(flip: false);
        var drawer = OpenFlip(editor);
        Assert.True(drawer.UsesDefaults); // the application's flip is off, and the session follows it
        Assert.Equal("Off", drawer.Summary);
        Assert.Null(editor.Session!.Automation.Flip); // untouched settings are not written

        drawer.CustomizeCommand.Execute(null);
        drawer.Enabled = true;
        drawer.PauseBeforeText = "8";
        drawer.AfterText = "3";
        drawer.LatestText = "25";
        drawer.Autofocus = true;
        drawer.Dither = true;
        drawer.PauseAfterText = "2";

        var block = (MultiRigStepDraftViewModel)vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig);
        Assert.True(block.MeridianFlip!.IsEnabled);
        Assert.Equal(8, block.MeridianFlip.Settings.PauseBeforeMeridianMinutes);
        Assert.True(block.MeridianFlip.Settings.AutofocusAfterFlip);
        Assert.True(block.MeridianFlip.Settings.DitherAfterFlip);
        Assert.Equal(2, block.MeridianFlip.Settings.PauseAfterFlipMinutes);
        Assert.Contains("Hold new exposures 8 min before the meridian, flip 3 min after it", drawer.Summary, StringComparison.Ordinal);
        Assert.Equal(editor.Session!.Automation.Flip, block.MeridianFlip.Settings);
        Assert.True(drawer.FinishCurrentExposure); // an exposure is never aborted for a flip
    }

    [Fact]
    public async Task AFlipTextThatIsNoNumber_IsToldAndStopsSaving()
    {
        var (_, editor, _, _, _) = await EditorAsync();
        var drawer = OpenFlip(editor);

        drawer.AfterText = "soon";

        Assert.True(editor.HasUnreadableFields);
        Assert.Contains("flip after the meridian", drawer.ProblemText, StringComparison.OrdinalIgnoreCase);
        drawer.AfterText = "2";
        Assert.False(editor.HasUnreadableFields);
    }

    [Fact]
    public async Task TheTree_KeepsTheFlipOfTheSession()
    {
        var (vm, editor, _, _, _) = await EditorAsync();
        editor.ShowTreeCommand.Execute(null);
        editor.ShowTreeCommand.Execute(null);

        Assert.True(editor.IsTree);
        var snapshot = vm.SequenceDraft.Snapshot();
        Assert.True(((MultiRigStepDraft)snapshot.Single(s => s is MultiRigStepDraft)).MeridianFlip!.IsEnabled);
        var description = vm.SequenceDraft.Steps.Single(s => s.Kind == SequenceStepKind.MultiRig).Summary;
        Assert.Contains("Meridian flip", description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCountdown_SaysWhereTheTargetIs_AndFollowsTheClock()
    {
        var (_, editor, clock, _, _) = await EditorAsync();

        editor.RefreshMeridian();
        Assert.StartsWith("Meridian: 05:", editor.MeridianInfoText, StringComparison.Ordinal); // 10 min before the crossing, 5 before the hold
        clock.SetHourAngle(-3);
        editor.RefreshMeridian();
        Assert.Contains("until the crossing", editor.MeridianInfoText, StringComparison.Ordinal);
        clock.SetHourAngle(3);
        editor.RefreshMeridian();
        Assert.Contains("the flip is due", editor.MeridianInfoText, StringComparison.Ordinal);
        editor.Load(Session(clock, new MeridianFlipSettings()));
        editor.RefreshMeridian();
        Assert.Equal(string.Empty, editor.MeridianInfoText);
    }

    // ---- one run through the editor and the sequencer

    private static bool ShowsFrames(SessionEditorViewModel editor) =>
        editor.Targets.SelectMany(t => t.Lanes).SelectMany(l => l.Blocks).Any(b => b.ProgressText.StartsWith("Frame", StringComparison.Ordinal));

    [Fact]
    public async Task TheFlip_RunsThroughTheSequencer_AndTheStatusFollowsIt()
    {
        var (vm, editor, clock, _, mount) = await EditorAsync(s => s with { AutofocusAfterFlip = false });
        Assert.True(vm.SequenceDraft.IsValid, string.Join(" ", vm.SequenceDraft.ValidationErrors));

        vm.Sequencer.RunCommand.Execute(null);
        await WaitAsync(() => editor.HasFlipStatus, "the status of the flip");
        await WaitAsync(() => ShowsFrames(editor), "frames on the blocks");
        clock.SetHourAngle(3);
        var seen = new HashSet<MeridianFlipState>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (vm.Sequencer.State is SequenceState.Running or SequenceState.Idle)
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the sequence.");
            foreach (var status in editor.FlipStatuses)
            {
                status.Refresh();
                seen.Add(status.State);
            }

            await Task.Delay(5);
        }

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.Equal(MeridianFlipState.Completed, editor.FlipStatuses.Single().State);
        Assert.Equal("Mount 1", editor.FlipStatuses.Single().MountName);
        Assert.Equal("Main, Wide", editor.FlipStatuses.Single().SetupsText);
        Assert.Equal(0, mount.SyncCount);
    }

    [Fact]
    public async Task AFailedFlip_ShowsRetryAndAbort_AndRetryFinishesTheSession()
    {
        var (vm, editor, clock, solver, mount) = await EditorAsync(s => s with { MaxFlipAttempts = 1 });
        solver.Fails = true;

        vm.Sequencer.RunCommand.Execute(null);
        await WaitAsync(() => editor.HasFlipStatus, "the status of the flip");
        await WaitAsync(() => ShowsFrames(editor), "frames on the blocks");
        clock.SetHourAngle(3);
        await WaitAsync(() => editor.FlipStatuses.Single().Refresh_IsWaiting(), "the failed flip to wait");

        var status = editor.FlipStatuses.Single();
        Assert.True(status.RetryCommand.CanExecute(null));
        Assert.True(status.AbortCommand.CanExecute(null));
        Assert.Contains("Retry the flip or abort", status.Message, StringComparison.Ordinal);

        solver.Fails = false;
        status.RetryCommand.Execute(null);
        await WaitAsync(() => vm.Sequencer.State is SequenceState.Completed or SequenceState.Failed, "the sequence to end");

        Assert.Equal(SequenceState.Completed, vm.Sequencer.State);
        Assert.Equal(0, mount.SyncCount);
    }

    private static async Task WaitAsync(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for: " + what);
            await Task.Delay(5);
        }
    }
}

internal static class FlipStatusTestExtensions
{
    // Reads the group again, and says whether the flip waits for the user.
    public static bool Refresh_IsWaiting(this MeridianFlipStatusViewModel status)
    {
        status.Refresh();
        return status.IsWaitingForDecision;
    }
}

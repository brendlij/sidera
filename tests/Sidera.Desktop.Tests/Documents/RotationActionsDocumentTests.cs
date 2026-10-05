using System.Text;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Desktop.Documents;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Documents;

/// <summary>The three rotation steps as drafts, in the document, as validated and built, and run: backward compatible with files that have none.</summary>
public sealed class RotationActionsDocumentTests
{
    private sealed class Solver(SimulatedMount mount, SimulatedRotator rotator) : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlateSolveResult { Success = true, Center = mount.Coordinates, RotationDegrees = rotator.CurrentSkyRotation, Backend = Name });
    }

    private sealed record Setup(SequenceDraftContext Context, SideraRuntimeHost Host, Rig Rig, SimulatedMount Mount, SimulatedRotator Rotator);

    private static async Task<Setup> CreateAsync(bool withRotator = true, bool calibrated = true, double trueOffset = 0)
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera");
        var mount = host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        var rotator = host.AddSimulatedRotator(new("rotator"), "Rotator", 0, 3600, trueOffset);
        var rig = new Rig(
            new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76),
            rotatorId: withRotator ? rotator.Id : null, rotatorModel: withRotator && calibrated ? new RotatorSkyModel(trueOffset) : null, mountId: mount.Id);
        host.AddRig(rig);
        host.ConfigurePlateSolver(new Solver(mount, rotator));
        await camera.ConnectAsync();
        await mount.ConnectAsync();
        await rotator.ConnectAsync();
        return new Setup(new SequenceDraftContext(Rigs: host.RigRegistry, PlateSolving: host.PlateSolving, Rotation: host.Rotation), host, rig, mount, rotator);
    }

    private static async Task<(string Text, SequenceStepDraft Loaded)> RoundTripAsync(SequenceStepDraft step)
    {
        var serializer = new JsonSequenceDocumentSerializer();
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument([step]), CancellationToken.None);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        var document = await serializer.LoadAsync(stream, CancellationToken.None);
        return (text, Assert.Single(SequenceDocumentMapper.ToDrafts(document)));
    }

    private static readonly RotateToAngleStepDraft RotateTo = new(Guid.NewGuid(), new RigId("rig"), -42.5);
    private static readonly RotateAndVerifyStepDraft Verify = new(Guid.NewGuid(), new RigId("rig"), 120, 0.25, 5, 3);
    private static readonly CenterAndRotateStepDraft CenterRotate = new(
        Guid.NewGuid(), new DeviceId("mount"), new RigId("rig"), 0.7123, 41.269, 45, 4, 87.5, 0.4, 6, 2, 4, "M31");

    // ---- The document

    [Fact]
    public async Task RotateToAngle_RoundTrips_AndCarriesOnlyWhatWasChosen()
    {
        var (text, loaded) = await RoundTripAsync(RotateTo);

        Assert.Equal(RotateTo, loaded);
        Assert.Contains("\"type\": \"rotateToAngle\"", text);
        Assert.Contains("skyRotationDegrees", text);
        Assert.DoesNotContain("position", text, StringComparison.OrdinalIgnoreCase); // the rotator's position is not part of the step; the calibration is of the rig
        Assert.DoesNotContain("offset", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RotateAndVerify_RoundTrips()
    {
        var (text, loaded) = await RoundTripAsync(Verify);

        Assert.Equal(Verify, loaded);
        Assert.Contains("\"type\": \"rotateAndVerify\"", text);
        Assert.DoesNotContain("astap", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CenterAndRotate_RoundTrips_WithItsTargetName()
    {
        var (text, loaded) = await RoundTripAsync(CenterRotate);

        Assert.Equal(CenterRotate, loaded);
        Assert.Contains("\"type\": \"centerAndRotate\"", text);
        Assert.Contains("M31", text);
        Assert.DoesNotContain("syncMount", text);
    }

    [Fact]
    public async Task CenterAndRotate_WithoutAName_RoundTrips()
    {
        var step = CenterRotate with { Id = Guid.NewGuid(), TargetName = null };

        var (text, loaded) = await RoundTripAsync(step);

        Assert.Equal(step, loaded);
        Assert.DoesNotContain("targetName", text);
    }

    [Fact]
    public async Task ASequenceWithAllThree_KeepsItsOrder_AlsoInsideARepeat()
    {
        var serializer = new JsonSequenceDocumentSerializer();
        var steps = new SequenceStepDraft[]
        {
            new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount"), new RigId("rig"), 5, 30, 60, 5, 5),
            RotateTo,
            Verify,
            new RepeatStepDraft(Guid.NewGuid(), 2, [CenterRotate, new DelayStepDraft(Guid.NewGuid(), 1)]),
        };
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, SequenceDocumentMapper.ToDocument(steps), CancellationToken.None);
        stream.Position = 0;

        var loaded = SequenceDocumentMapper.ToDrafts(await serializer.LoadAsync(stream, CancellationToken.None));

        Assert.Equal(steps.Select(s => s.GetType()), loaded.Select(s => s.GetType()));
        Assert.Equal(CenterRotate, ((RepeatStepDraft)loaded[3]).Children[0]);
    }

    [Fact]
    public async Task AFileWithoutRotationSteps_StillLoadsAsBefore()
    {
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount"), new RigId("rig"), 5.588, -5.39, 45, 4, 3, "Orion", 12.5);

        var (_, loaded) = await RoundTripAsync(step);

        Assert.Equal(step, loaded);
    }

    [Theory]
    [InlineData("rotateToAngle", "{\"id\":\"{ID}\",\"type\":\"rotateToAngle\",\"rigId\":\"rig\"}")]
    [InlineData("rotateAndVerify", "{\"id\":\"{ID}\",\"type\":\"rotateAndVerify\",\"rigId\":\"rig\",\"skyRotationDegrees\":10}")]
    [InlineData("centerAndRotate", "{\"id\":\"{ID}\",\"type\":\"centerAndRotate\",\"mountId\":\"mount\",\"rigId\":\"rig\",\"raHours\":1}")]
    public async Task AStepWithAMissingValue_IsRefused_NotFilledIn(string type, string step)
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":7,\"steps\":[" + step.Replace("{ID}", Guid.NewGuid().ToString()) + "]}";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new JsonSequenceDocumentSerializer().LoadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None));

        Assert.Contains(type, ex.Message);
    }

    // ---- Validation

    [Fact]
    public async Task AValidRotation_HasNoProblems()
    {
        var s = await CreateAsync();
        await using var host = s.Host;

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [RotateTo, Verify, CenterRotate], s.Context);

        Assert.True(result.IsValid, string.Join(" ", result.StepProblems.SelectMany(p => p.Value)));
    }

    [Fact]
    public async Task ARigWithoutARotator_IsReported_ForEveryRotationStep()
    {
        var s = await CreateAsync(withRotator: false);
        await using var host = s.Host;

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [RotateTo, Verify, CenterRotate], s.Context);

        Assert.False(result.IsValid);
        foreach (var step in new SequenceStepDraft[] { RotateTo, Verify, CenterRotate })
        {
            Assert.Contains(result.StepProblems[step.Id], p => p.Contains("has no rotator"));
        }
    }

    [Fact]
    public async Task ARotatorThatIsNotCalibrated_IsReported_BeforeTheRun()
    {
        var s = await CreateAsync(calibrated: false);
        await using var host = s.Host;

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [RotateTo], s.Context);

        Assert.False(result.IsValid);
        Assert.Contains(result.StepProblems[RotateTo.Id], p => p.Contains("not calibrated"));
    }

    [Fact]
    public async Task ADisconnectedRotatorIsNotAnError_OfTheDraft_ButAMissingOneIs()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var noRig = RotateTo with { Id = Guid.NewGuid(), RigId = null };

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [noRig], s.Context);

        Assert.Contains(result.StepProblems[noRig.Id], p => p.Contains("Select an available rig"));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    [InlineData(100, 4)]
    [InlineData(0.5, 0)]
    [InlineData(0.5, 21)]
    [InlineData(double.NaN, 4)]
    public async Task ANonsensicalToleranceOrNumberOfAttempts_IsReported(double tolerance, int attempts)
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var step = Verify with { Id = Guid.NewGuid(), ToleranceDegrees = tolerance, MaxAttempts = attempts };

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [step], s.Context);

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ANonFiniteAngleOrBadRounds_IsReported()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var angle = RotateTo with { Id = Guid.NewGuid(), SkyRotationDegrees = double.PositiveInfinity };
        var rounds = CenterRotate with { Id = Guid.NewGuid(), MaxRounds = 0 };

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [angle, rounds], s.Context);

        Assert.Contains(result.StepProblems[angle.Id], p => p.Contains("degrees"));
        Assert.Contains(result.StepProblems[rounds.Id], p => p.Contains("rounds"));
    }

    [Fact]
    public async Task WithoutAPlateSolver_TheRotationStepsAreReported()
    {
        var s = await CreateAsync();
        await using var host = s.Host;

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [Verify], new SequenceDraftContext(Rigs: host.RigRegistry));

        Assert.Contains(result.StepProblems[Verify.Id], p => p.Contains("No plate solver"));
    }

    [Fact]
    public async Task ACenterAndRotateThatNamesAnotherMountThanItsRig_IsReported()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        host.AddSimulatedMount(new("mount.other"), "Other", TimeSpan.FromMilliseconds(1));
        var step = CenterRotate with { Id = Guid.NewGuid(), MountId = new DeviceId("mount.other") };

        var result = SequenceDraftBuilder.Validate(host.DeviceRegistry, [step], s.Context);

        Assert.False(result.IsValid);
        Assert.Contains(result.StepProblems[step.Id], p => p.Contains("mount.other") && p.Contains("mount"));
    }

    // ---- Building and running

    [Fact]
    public async Task TheStepsBuild_ToBackendNeutralActions_AndNeedTheRotatorAndTheCamera()
    {
        var s = await CreateAsync();
        await using var host = s.Host;

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [RotateTo, Verify, CenterRotate], s.Context);

        Assert.IsType<RotateToAngleAction>(built.Sequence.Steps[0]);
        Assert.IsType<RotateAndVerifyAction>(built.Sequence.Steps[1]);
        Assert.IsType<CenterAndRotateAction>(built.Sequence.Steps[2]);
        var required = SequenceDraftBuilder.RequiredDeviceIds([RotateTo], s.Context);
        Assert.Contains(new DeviceId("rotator"), required);
        Assert.Contains(s.Rig.CameraId, required);
        Assert.Contains(new DeviceId("mount"), SequenceDraftBuilder.RequiredDeviceIds([CenterRotate], s.Context));
    }

    [Fact]
    public async Task ASequenceOfRotationSteps_Runs_ReachesTheSky_AndNeverSynchronizesTheMount()
    {
        var s = await CreateAsync(trueOffset: 15);
        await using var host = s.Host;
        var steps = new SequenceStepDraft[]
        {
            Verify with { Id = Guid.NewGuid(), SkyRotationDegrees = 60, ExposureSeconds = 0.01 },
            CenterRotate with { Id = Guid.NewGuid(), SkyRotationDegrees = -30, ExposureSeconds = 0.01 },
        };
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, s.Context);

        await new SequenceRunner(host.ResourceManager).RunAsync(built.Sequence, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(-30, s.Rotator.CurrentSkyRotation, 1);
        Assert.Equal(0, s.Mount.SyncCount);
    }

    [Fact]
    public async Task ARotationThatFails_FailsTheSequence_WithItsReason()
    {
        var s = await CreateAsync(trueOffset: 0);
        await using var host = s.Host;
        var calibratedWrong = s.Rig.WithRotatorModel(new RotatorSkyModel(0, Reversed: true));
        host.RigRegistry.Unregister(s.Rig.Id);
        host.RigRegistry.Register(calibratedWrong);
        var step = Verify with { Id = Guid.NewGuid(), SkyRotationDegrees = 50, ExposureSeconds = 0.01 };
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [step], s.Context);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => new SequenceRunner(host.ResourceManager).RunAsync(built.Sequence, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));

        Assert.Contains("direction", ex.ToString());
        Assert.Equal(0, s.Mount.SyncCount);
    }

    // ---- The editor

    private static SequenceDraftViewModel CreateEditor(Setup s) =>
        new(s.Host.DeviceRegistry, SequenceDraftDefaults.From(new DemoOptions(), s.Host.DeviceRegistry), null, rigs: s.Host.RigRegistry, plateSolving: s.Host.PlateSolving, rotation: s.Host.Rotation);

    [Fact]
    public async Task TheEditor_AddsTheThreeSteps_WithDefaults_AndTheyAreValid()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var editor = CreateEditor(s);

        foreach (var kind in new[] { SequenceStepKind.RotateToAngle, SequenceStepKind.RotateAndVerify, SequenceStepKind.CenterAndRotate })
        {
            editor.AddStepCommand.Execute(kind);
        }

        Assert.Equal(3, editor.Steps.Count);
        Assert.IsType<RotateToAngleStepDraftViewModel>(editor.Steps[0]);
        Assert.IsType<RotateAndVerifyStepDraftViewModel>(editor.Steps[1]);
        Assert.IsType<CenterAndRotateStepDraftViewModel>(editor.Steps[2]);
        var verify = Assert.IsType<RotateAndVerifyStepDraft>(editor.Snapshot()[1]);
        Assert.Equal(RotationService.DefaultToleranceDegrees, verify.ToleranceDegrees);
        Assert.Equal(RotationService.DefaultMaxAttempts, verify.MaxAttempts);
        var centered = Assert.IsType<CenterAndRotateStepDraft>(editor.Snapshot()[2]);
        Assert.Equal(RotationService.DefaultMaxRounds, centered.MaxRounds);
        Assert.False(editor.IsValid); // no rig is chosen yet
        ((RotateToAngleStepDraftViewModel)editor.Steps[0]).Rig.Selected = ((RotateToAngleStepDraftViewModel)editor.Steps[0]).Rig.Options.First(o => o.Id == s.Rig.Id);
        ((RotateAndVerifyStepDraftViewModel)editor.Steps[1]).Rig.Selected = ((RotateAndVerifyStepDraftViewModel)editor.Steps[1]).Rig.Options.First(o => o.Id == s.Rig.Id);
        var center = (CenterAndRotateStepDraftViewModel)editor.Steps[2];
        center.Rig.Selected = center.Rig.Options.First(o => o.Id == s.Rig.Id);
        Assert.Contains("mount", center.MountText, StringComparison.OrdinalIgnoreCase); // the mount is the one of the rig
        Assert.True(editor.IsValid, string.Join(" ", editor.ValidationErrors));
    }

    [Fact]
    public async Task TheEditor_ShowsTheStepsByTheirNames()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var editor = CreateEditor(s);
        editor.AddStepCommand.Execute(SequenceStepKind.RotateAndVerify);
        editor.AddStepCommand.Execute(SequenceStepKind.CenterAndRotate);

        Assert.Equal(["Rotate & Verify", "Center & Rotate"], editor.Steps.Select(x => x.Title));
    }

    [Fact]
    public async Task EditingTheAngle_ChangesTheDraft_AndAnInvalidTextIsReported()
    {
        var s = await CreateAsync();
        await using var host = s.Host;
        var editor = CreateEditor(s);
        editor.AddStepCommand.Execute(SequenceStepKind.RotateToAngle);
        var step = Assert.IsType<RotateToAngleStepDraftViewModel>(editor.Steps[0]);

        step.AngleText = "123.5";
        Assert.Equal(123.5, Assert.IsType<RotateToAngleStepDraft>(editor.Snapshot()[0]).SkyRotationDegrees);

        step.AngleText = "turn it";
        Assert.True(step.HasProblems);
        Assert.Contains(step.Problems, p => p.Contains("Sky rotation"));
    }

    [Fact]
    public async Task TheRotationSteps_AreOfferedInTheMenu_OfTheSession()
    {
        var xaml = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "Sidera.Desktop", "Views", "Session", "WorkflowView.axaml"));

        Assert.Contains("SequenceStepKind.RotateToAngle", xaml);
        Assert.Contains("SequenceStepKind.RotateAndVerify", xaml);
        Assert.Contains("SequenceStepKind.CenterAndRotate", xaml);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sidera.slnx")) && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}

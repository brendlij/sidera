using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Rotators;
using Sidera.Core.Sequencing;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests;

/// <summary>Rig-local steps use the mount and guider of their own rig, never a global one; what each kind of step is and holds; and dithering of the mount that the trigger rig sits on.</summary>
public sealed class RigAwareSessionTests : IAsyncLifetime
{
    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private static readonly OpticalTrain Optics = new(750, 150, 3.76, 3.76, 6248, 4176);

    private sealed class Solver : IPlateSolver
    {
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));
        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Rigs A and B on mount 1 and guider 1 (shared), C on mount 2 and guider 2 (its own), D without a mount or guider.
    private SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name.ToUpperInvariant()}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name.ToUpperInvariant()}", 1000, maxPosition: 5000);
            host.AddSimulatedRotator(new($"rotator.{name}"), $"Rotator {name.ToUpperInvariant()}");
        }

        host.AddSimulatedMount(new("mount.1"), "Mount 1", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedMount(new("mount.2"), "Mount 2", TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.2"), "Guider 2", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        var model = new RotatorSkyModel(0);
        host.AddRig(new Rig(new("rig.a"), "Rig A", new("camera.a"), Optics, new("focuser.a"), null, new("rotator.a"), model, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(new("rig.b"), "Rig B", new("camera.b"), Optics, new("focuser.b"), null, new("rotator.b"), model, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(new("rig.c"), "Rig C", new("camera.c"), Optics, new("focuser.c"), null, new("rotator.c"), model, new("mount.2"), new("guider.2")));
        host.AddRig(new Rig(new("rig.d"), "Rig D", new("camera.d"), Optics, new("focuser.d"), null, new("rotator.d"), model));
        host.ConfigurePlateSolver(new Solver());
        return host;
    }

    private static SequenceDraftContext Context(SideraRuntimeHost host, SharedEquipmentDraft? shared = null) =>
        new(host.RigRegistry, shared, FocusMetrics: host.FocusMetrics, PlateSolving: host.PlateSolving, Rotation: host.Rotation);

    private static RigId Rig(string name) => new("rig." + name);

    // ---- What each kind of step is

    [Fact]
    public void EveryKindOfStep_HasAScope()
    {
        foreach (var kind in Enum.GetValues<SequenceStepKind>())
        {
            var scope = StepScopes.ScopeOf(kind);
            Assert.True(Enum.IsDefined(scope), kind.ToString());
        }
    }

    [Theory]
    [InlineData(SequenceStepKind.Autofocus, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.PlateSolve, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.SlewAndCenter, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.RotateToAngle, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.RotateAndVerify, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.CenterAndRotate, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.RigExposure, StepScope.RigLocal)]
    [InlineData(SequenceStepKind.SyncMountToSolved, StepScope.Device)]
    [InlineData(SequenceStepKind.Slew, StepScope.Device)]
    [InlineData(SequenceStepKind.StartGuiding, StepScope.Device)]
    [InlineData(SequenceStepKind.StopGuiding, StepScope.Device)]
    [InlineData(SequenceStepKind.Dither, StepScope.Device)]
    [InlineData(SequenceStepKind.Exposure, StepScope.Device)]
    [InlineData(SequenceStepKind.Repeat, StepScope.Session)]
    [InlineData(SequenceStepKind.MultiRig, StepScope.Session)]
    [InlineData(SequenceStepKind.RigTrack, StepScope.Session)]
    public void TheKindsAreClassified(SequenceStepKind kind, StepScope expected) => Assert.Equal(expected, StepScopes.ScopeOf(kind));

    // ---- What a step resolves to, from its rig

    public static IEnumerable<object[]> ResolutionTable()
    {
        static object[] Row(string name, SequenceStepDraft step, params string[] devices) => [name, step, devices];
        var id = () => Guid.NewGuid();
        yield return Row("Autofocus A", new AutofocusStepDraft(id(), Rig("a"), 1, 100, 5), "camera:camera.a", "focuser:focuser.a");
        yield return Row("Autofocus C", new AutofocusStepDraft(id(), Rig("c"), 1, 100, 5), "camera:camera.c", "focuser:focuser.c");
        yield return Row("PlateSolve C", new PlateSolveStepDraft(id(), Rig("c"), 1), "camera:camera.c");
        yield return Row("SlewAndCenter A", new SlewAndCenterStepDraft(id(), null, Rig("a"), 1, 10, 60, 3, 1), "camera:camera.a", "mount:mount.1");
        yield return Row("SlewAndCenter C", new SlewAndCenterStepDraft(id(), null, Rig("c"), 1, 10, 60, 3, 1), "camera:camera.c", "mount:mount.2");
        yield return Row("RotateToAngle B", new RotateToAngleStepDraft(id(), Rig("b"), 10), "rotator:rotator.b", "camera:camera.b");
        yield return Row("RotateAndVerify C", new RotateAndVerifyStepDraft(id(), Rig("c"), 10, 0.5, 3, 1), "rotator:rotator.c", "camera:camera.c");
        yield return Row(
            "CenterAndRotate C", new CenterAndRotateStepDraft(id(), null, Rig("c"), 1, 10, 60, 3, 0, 0.5, 3, 3, 1),
            "rotator:rotator.c", "camera:camera.c", "mount:mount.2");
        yield return Row("Sync (names its mount)", new SyncMountStepDraft(id(), new DeviceId("mount.2")), "mount:mount.2");
        yield return Row("Slew (names its mount)", new SlewStepDraft(id(), new DeviceId("mount.1"), 1, 10), "mount:mount.1");
        yield return Row("StartGuiding (names its guider)", new StartGuidingStepDraft(id(), new DeviceId("guider.2")), "guider:guider.2");
        yield return Row("StopGuiding (names its guider)", new StopGuidingStepDraft(id(), new DeviceId("guider.1")), "guider:guider.1");
        yield return Row(
            "Dither (names all three)", new DitherStepDraft(id(), new DeviceId("guider.2"), new DeviceId("mount.2"), new DeviceId("camera.c"), 1, 1, 1, 5),
            "guider:guider.2", "mount:mount.2", "camera:camera.c");
        yield return Row("Exposure (names its camera)", new ExposureStepDraft(id(), new DeviceId("camera.b"), 1), "camera:camera.b");
        yield return Row("Delay", new DelayStepDraft(id(), 1));
    }

    [Theory]
    [MemberData(nameof(ResolutionTable))]
    public void EveryStep_ResolvesToTheDevicesOfItsRig_OrOfTheDeviceItNames(string name, SequenceStepDraft step, string[] expected)
    {
        var host = CreateHost();

        var resolved = StepScopes.Resolve(step, Context(host)).Select(d => $"{d.Role}:{d.Device.Value}").Order();

        Assert.Equal(expected.Order(), resolved);
        Assert.NotNull(name);
    }

    [Fact]
    public void ARepeat_ResolvesToWhatItsBodyUses()
    {
        var host = CreateHost();
        var repeat = new RepeatStepDraft(Guid.NewGuid(), 3, [new SlewStepDraft(Guid.NewGuid(), new DeviceId("mount.1"), 1, 1), new StartGuidingStepDraft(Guid.NewGuid(), new DeviceId("guider.1"))]);

        Assert.Equal(["guider:guider.1", "mount:mount.1"], StepScopes.Resolve(repeat, Context(host)).Select(d => $"{d.Role}:{d.Device.Value}").Order());
    }

    [Fact]
    public void ATrackStep_ResolvesToTheRigOfItsTrack()
    {
        var host = CreateHost();
        host.RigRegistry.TryGet(Rig("c"), out var rigC);

        Assert.Equal(["camera:camera.c"], StepScopes.Resolve(new RigExposureStepDraft(Guid.NewGuid(), 1), Context(host), rigC).Select(d => $"{d.Role}:{d.Device.Value}"));
        Assert.Equal(["focuser:focuser.c"], StepScopes.Resolve(new RigMoveFocuserStepDraft(Guid.NewGuid(), 10), Context(host), rigC).Select(d => $"{d.Role}:{d.Device.Value}"));
    }

    [Fact]
    public void TheMountOfAStep_IsTheRigs_ThenTheOneAnOlderStepNames_ThenTheSharedOne()
    {
        var host = CreateHost();
        host.RigRegistry.TryGet(Rig("a"), out var a);
        host.RigRegistry.TryGet(Rig("d"), out var d);
        var shared = new SharedEquipmentDraft(new DeviceId("mount.2"), new DeviceId("guider.2"));

        Assert.Equal(new DeviceId("mount.1"), StepScopes.EffectiveMount(a, null, shared));
        Assert.Equal(new DeviceId("mount.1"), StepScopes.EffectiveMount(a, new DeviceId("mount.2"), shared)); // the rig decides
        Assert.Equal(new DeviceId("mount.1"), StepScopes.EffectiveMount(d, new DeviceId("mount.1"), shared)); // a rig without one keeps what an older step names
        Assert.Equal(new DeviceId("mount.2"), StepScopes.EffectiveMount(d, null, shared)); // the shared one is the last resort
        Assert.Null(StepScopes.EffectiveMount(d, null, null));
        Assert.Equal(new DeviceId("guider.1"), StepScopes.EffectiveGuider(a, null, shared));
        Assert.Equal(new DeviceId("guider.2"), StepScopes.EffectiveGuider(d, null, shared));
    }

    // ---- Validation and building

    [Fact]
    public void ASlewAndCenter_OfARigWithAMount_NeedsNoMountOfItsOwn()
    {
        var host = CreateHost();
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), null, Rig("c"), 1, 10, 60, 3, 1);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [step], Context(host));

        Assert.True(validation.IsValid, string.Join(" ", validation.ProblemsOf(step.Id)));
    }

    [Fact]
    public void ASlewAndCenter_OfARigWithoutAMount_SaysSo_AndDoesNotBorrowAnother()
    {
        var host = CreateHost();
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), null, Rig("d"), 1, 10, 60, 3, 1);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [step], Context(host));

        Assert.Contains(validation.ProblemsOf(step.Id), p => p.Contains("The imaging setup 'Rig D' has no mount"));
    }

    [Fact]
    public void AStepThatNamesAnotherMountThanItsRig_IsReported()
    {
        var host = CreateHost();
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount.2"), Rig("a"), 1, 10, 60, 3, 1);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [step], Context(host));

        Assert.Contains(validation.ProblemsOf(step.Id), p => p.Contains("mount.2") && p.Contains("mount.1"));
    }

    [Fact]
    public void AStepOfAnOlderFile_ThatNamesTheMountOfItsRig_StillBuilds()
    {
        var host = CreateHost();
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount.1"), Rig("a"), 1, 10, 60, 3, 1);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [step], Context(host));

        Assert.Equal(new DeviceId("mount.1"), Assert.IsType<SlewAndCenterAction>(built.Sequence.Steps[0]).MountId);
    }

    [Fact]
    public void TheBuiltActions_UseTheMountOfTheirRig()
    {
        var host = CreateHost();
        var steps = new SequenceStepDraft[]
        {
            new SlewAndCenterStepDraft(Guid.NewGuid(), null, Rig("a"), 1, 10, 60, 3, 1),
            new SlewAndCenterStepDraft(Guid.NewGuid(), null, Rig("c"), 1, 10, 60, 3, 1),
            new CenterAndRotateStepDraft(Guid.NewGuid(), null, Rig("c"), 1, 10, 60, 3, 0, 0.5, 3, 3, 1),
        };

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, steps, Context(host)).Sequence.Steps;

        Assert.Equal(new DeviceId("mount.1"), ((SlewAndCenterAction)built[0]).MountId);
        Assert.Equal(new DeviceId("mount.2"), ((SlewAndCenterAction)built[1]).MountId);
        Assert.Equal(new DeviceId("mount.2"), ((CenterAndRotateAction)built[2]).MountId);
    }

    [Fact]
    public void ThePlateSolveHint_UsesTheMountOfItsRig_NotAGlobalOne()
    {
        var host = CreateHost();
        var shared = new SharedEquipmentDraft(new DeviceId("mount.1"), new DeviceId("guider.1"));
        var step = new PlateSolveStepDraft(Guid.NewGuid(), Rig("c"), 1);

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [step], Context(host, shared));

        Assert.NotNull(Assert.IsType<PlateSolveAction>(built.Sequence.Steps[0]));
        Assert.Equal(new DeviceId("mount.2"), StepScopes.EffectiveMount(host.RigRegistry.GetAll().Single(r => r.Id == Rig("c")), null, shared));
    }

    [Fact]
    public void TheRequiredDevices_OfARigLocalStep_AreItsRigsDevices()
    {
        var host = CreateHost();
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), null, Rig("c"), 1, 10, 60, 3, 1);

        var ids = SequenceDraftBuilder.RequiredDeviceIds([step], Context(host));

        Assert.Equal(["camera.c", "mount.2"], ids.Select(i => i.Value).Order());
    }

    [Theory]
    [InlineData("a", "b", "mount.1")]
    [InlineData("a", "c", null)]
    public void TheSharedFallback_IsWhatTheRigsHaveInCommon(string first, string second, string? common)
    {
        var host = CreateHost();
        var rigs = host.RigRegistry.GetAll().Where(r => r.Id == Rig(first) || r.Id == Rig(second));

        var shared = SharedEquipmentDraft.FromRigs(rigs, new DeviceId("mount.default"), new DeviceId("guider.default"), useDefaults: false);

        Assert.Equal(common, shared.MountId?.Value);
        Assert.Equal(common is null ? null : "guider.1", shared.GuiderId?.Value);
    }

    [Fact]
    public void WithoutRigs_TheSharedFallback_IsTheDefaultsOfTheSession()
    {
        var shared = SharedEquipmentDraft.FromRigs([], new DeviceId("mount.default"), new DeviceId("guider.default"), useDefaults: true);

        Assert.Equal("mount.default", shared.MountId?.Value);
    }

    // ---- Dithering the mount of the trigger rig

    private static RigTrackDraft Track(string rig, double seconds = 1) =>
        new(Guid.NewGuid(), Rig(rig), [new RepeatStepDraft(Guid.NewGuid(), 2, [new RigExposureStepDraft(Guid.NewGuid(), seconds)])]);

    private static MultiRigStepDraft Block(string trigger, params RigTrackDraft[] tracks) =>
        new(Guid.NewGuid(), tracks, MultiRigDitherPolicyDraft.Default with { Enabled = true, TriggerRigId = Rig(trigger) });

    [Fact]
    public void ADitherPolicy_UsesTheMountAndGuiderOfTheTriggerRig_NotTheSessions()
    {
        var host = CreateHost();
        var block = Block("c", Track("c"), Track("a"));

        var domain = SequenceDraftBuilder.DitherDomain(block, block.DitherPolicy!, Context(host, new SharedEquipmentDraft(new DeviceId("mount.1"), new DeviceId("guider.1"))))!;

        Assert.Equal(new DeviceId("mount.2"), domain.Mount);
        Assert.Equal(new DeviceId("guider.2"), domain.Guider);
        Assert.Equal([block.Tracks[0].Id], domain.Tracks); // only the track on the dithered mount
    }

    [Fact]
    public void TheTracks_OnTheDitheredMount_AreItsDomain_AndOthersAreNot()
    {
        var host = CreateHost();
        var block = Block("a", Track("a"), Track("b"), Track("c"));

        var domain = SequenceDraftBuilder.DitherDomain(block, block.DitherPolicy!, Context(host))!;

        Assert.Equal([block.Tracks[0].Id, block.Tracks[1].Id], domain.Tracks);
    }

    [Fact]
    public void RigsOnOneMount_AreOneCoordinationGroup_AsBefore()
    {
        var host = CreateHost();
        var block = Block("a", Track("a"), Track("b"));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var parallel = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps));
        Assert.NotNull(parallel.CoordinationGroup);
        Assert.Equal(2, parallel.Children.Count);
        Assert.All(parallel.Children, c => Assert.IsType<RigTrackStep>(c));
    }

    [Fact]
    public void ARigOnAnotherMount_IsNotMadeToWaitForTheDither_AndHasNoSafePoints()
    {
        var host = CreateHost();
        var block = Block("a", Track("a"), Track("b"), Track("c"));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var top = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps));
        Assert.Null(top.CoordinationGroup);
        Assert.Equal(2, top.Children.Count);
        var dithered = Assert.IsType<ParallelStep>(top.Children[0]);
        Assert.NotNull(dithered.CoordinationGroup);
        Assert.Equal(["Rig A", "Rig B"], dithered.Children.Select(c => c.Name));
        var independent = Assert.IsType<RigTrackStep>(top.Children[1]);
        Assert.Equal("Rig C", independent.Name);
        Assert.DoesNotContain(independent.Steps.SelectMany(Flatten), s => s is SafePointStep or DitherEveryNthFrameStep);
    }

    [Fact]
    public void ATriggerRigThatIsAloneOnItsMount_DithersWithoutAnyoneElseWaiting()
    {
        var host = CreateHost();
        var block = Block("c", Track("a"), Track("c"));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], Context(host));

        var top = Assert.IsType<ParallelStep>(Assert.Single(built.Sequence.Steps));
        Assert.Null(top.CoordinationGroup);
        Assert.All(top.Children, c => Assert.IsType<RigTrackStep>(c));
        var other = (RigTrackStep)top.Children.Single(c => c.Name == "Rig A");
        Assert.DoesNotContain(other.Steps.SelectMany(Flatten), s => s is SafePointStep);
    }

    [Fact]
    public void ATriggerRigWithoutAMount_IsReported_WithTheRigsName()
    {
        var host = CreateHost();
        var block = Block("d", Track("d"), Track("a"));

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host));

        Assert.Contains(validation.ProblemsOf(block.Id), p => p.Contains("Dither needs a mount") && p.Contains("Rig D"));
        Assert.Contains(validation.ProblemsOf(block.Id), p => p.Contains("Dither needs a guider") && p.Contains("Rig D"));
    }

    [Fact]
    public void ATriggerRigWithoutAMount_UsesTheSharedFallback_WhenThereIsOne()
    {
        var host = CreateHost();
        var block = Block("d", Track("d"), Track("a"));
        var shared = new SharedEquipmentDraft(new DeviceId("mount.1"), new DeviceId("guider.1"));

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], Context(host, shared));

        Assert.DoesNotContain(validation.ProblemsOf(block.Id), p => p.Contains("Dither needs"));
    }

    [Fact]
    public void TheDevicesOfADitheringBlock_AreTheTriggerRigsMountAndGuider()
    {
        var host = CreateHost();
        var block = Block("c", Track("c"), Track("a"));

        var ids = SequenceDraftBuilder.RequiredDeviceIds([block], Context(host)).Select(i => i.Value);

        Assert.Contains("mount.2", ids);
        Assert.Contains("guider.2", ids);
        Assert.DoesNotContain("mount.1", ids);
        Assert.DoesNotContain("guider.1", ids);
    }

    private static IEnumerable<ISequenceStep> Flatten(ISequenceStep step)
    {
        yield return step;
        if (step is SequenceGroup group)
        {
            foreach (var child in group.Children.SelectMany(Flatten))
            {
                yield return child;
            }
        }
        else if (step is RepeatStep repeat)
        {
            foreach (var child in Flatten(repeat.Child))
            {
                yield return child;
            }
        }
    }
}

using Sidera.Core.Devices;
using Sidera.Core.FilterWheels;
using Sidera.Core.Rigs;
using Sidera.Desktop;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Workflows;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.Workflows;

/// <summary>A workflow compiles to the existing steps: Prepare, one Multi-Rig block of rig tracks with their policies, Finish; devices come from the setup.</summary>
public sealed class WorkflowCompilerTests : IAsyncLifetime
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
    private static readonly RigId A = new("rig.a");
    private static readonly RigId B = new("rig.b");
    private static readonly RigId C = new("rig.c");
    private static readonly RigId D = new("rig.d");

    // A and B: own camera and focuser, on mount 1 and guider 1 (shared). C: on mount 2 and guider 2. D: camera only.
    private SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        foreach (var name in new[] { "a", "b", "c", "d" })
        {
            host.AddSimulatedCamera(new($"camera.{name}"), $"Camera {name}", 1);
            host.AddSimulatedFocuser(new($"focuser.{name}"), $"Focuser {name}", 1000, maxPosition: 5000);
        }

        host.AddSimulatedFilterWheel(new("wheel.a"), "Wheel A", [new FilterSlot(0, "L"), new FilterSlot(1, "Ha")]);
        host.AddSimulatedMount(new("mount.1"), "Mount 1");
        host.AddSimulatedMount(new("mount.2"), "Mount 2");
        host.AddSimulatedGuider(new("guider.1"), "Guider 1", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        host.AddSimulatedGuider(new("guider.2"), "Guider 2", TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1));
        host.AddRig(new Rig(A, "Main", new("camera.a"), Optics, new("focuser.a"), new("wheel.a"), null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(B, "Wide", new("camera.b"), Optics, new("focuser.b"), null, null, null, new("mount.1"), new("guider.1")));
        host.AddRig(new Rig(C, "Other", new("camera.c"), Optics, new("focuser.c"), null, null, null, new("mount.2"), new("guider.2")));
        host.AddRig(new Rig(D, "Bare", new("camera.d"), Optics));
        return host;
    }

    private static WorkflowStep Step(WorkflowStepKind kind, RigId? setup = null, bool enabled = true) => new(Guid.NewGuid(), kind, setup, enabled);

    private static ImagingBlock Block(RigId setup, int frames = 10, double seconds = 60, int? slot = null) => new(Guid.NewGuid(), setup, slot, seconds, frames);

    private static WorkflowDefinition Workflow(IEnumerable<WorkflowStep> prepare, IEnumerable<ImagingBlock> imaging, IEnumerable<WorkflowStep> finish, WorkflowDither? dither = null, params SetupAutofocus[] policies) =>
        WorkflowDefinition.Empty with { Prepare = prepare.ToList(), Imaging = imaging.ToList(), Finish = finish.ToList(), Dither = dither ?? WorkflowDither.Off, AutofocusPolicies = policies };

    [Fact]
    public void ASingleSetup_CompilesToPrepareImagingAndFinish_WithTheDevicesOfTheSetup()
    {
        var host = CreateHost();
        var workflow = Workflow(
            [Step(WorkflowStepKind.SlewAndCenter), Step(WorkflowStepKind.Autofocus), Step(WorkflowStepKind.StartGuiding)],
            [Block(A, 40, 300)],
            [Step(WorkflowStepKind.StopGuiding)]);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        Assert.Equal(
            [SequenceStepKind.SlewAndCenter, SequenceStepKind.Autofocus, SequenceStepKind.StartGuiding, SequenceStepKind.MultiRig, SequenceStepKind.StopGuiding],
            compiled.Steps.Select(s => s.Kind));
        var slew = Assert.IsType<SlewAndCenterStepDraft>(compiled.Steps[0]);
        Assert.Equal(A, slew.RigId); // the camera, the mount and the optics of the setup: nothing else is chosen
        Assert.Equal(A, Assert.IsType<AutofocusStepDraft>(compiled.Steps[1]).RigId);
        Assert.Equal(new DeviceId("guider.1"), Assert.IsType<StartGuidingStepDraft>(compiled.Steps[2]).GuiderId);
        var block = Assert.IsType<MultiRigStepDraft>(compiled.Steps[3]);
        var track = Assert.Single(block.Tracks);
        Assert.Equal(A, track.RigId);
        var repeat = Assert.IsType<RepeatStepDraft>(Assert.Single(track.Steps));
        Assert.Equal(40, repeat.Count);
        Assert.Equal(300, Assert.IsType<RigExposureStepDraft>(Assert.Single(repeat.Children)).Seconds);
        Assert.Equal(new DeviceId("guider.1"), Assert.IsType<StopGuidingStepDraft>(compiled.Steps[4]).GuiderId);
    }

    [Fact]
    public void TwoSetupsOnOneMountAndGuider_AreOneParallelBlock_CenteredOnce_AndGuidedOnce()
    {
        var host = CreateHost();
        var workflow = Workflow(
            [Step(WorkflowStepKind.SlewAndCenter), Step(WorkflowStepKind.StartGuiding)],
            [Block(A, 40, 300), Block(B, 120, 60)],
            [Step(WorkflowStepKind.StopGuiding)]);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        Assert.Single(compiled.Steps, s => s is SlewAndCenterStepDraft); // one target, one mount: one center, not one per camera
        Assert.Single(compiled.Steps, s => s is StartGuidingStepDraft); // one guider, started once
        Assert.Single(compiled.Steps, s => s is StopGuidingStepDraft);
        var block = Assert.IsType<MultiRigStepDraft>(compiled.Steps.Single(s => s is MultiRigStepDraft));
        Assert.Equal([A, B], block.Tracks.Select(t => t.RigId!.Value)); // no Parallel or Repeat built by hand
    }

    [Fact]
    public void TheFirstSetupOfAMountSolvesForIt_UnlessTheTargetNamesAnother()
    {
        var host = CreateHost();
        var blocks = new[] { Block(A), Block(B) };

        var automatic = WorkflowCompiler.Compile(Workflow([Step(WorkflowStepKind.SlewAndCenter)], blocks, []), host.RigRegistry);
        var named = WorkflowCompiler.Compile(
            Workflow([Step(WorkflowStepKind.SlewAndCenter)], blocks, []) with { Target = WorkflowTarget.Default with { PointingSetup = B } }, host.RigRegistry);

        Assert.Equal(A, ((SlewAndCenterStepDraft)automatic.Steps[0]).RigId);
        Assert.Equal(B, ((SlewAndCenterStepDraft)named.Steps[0]).RigId);
    }

    [Fact]
    public void SetupsOnDifferentMounts_AreCenteredSeparately_AndGuidedByTheirOwnGuiders()
    {
        var host = CreateHost();
        var workflow = Workflow(
            [Step(WorkflowStepKind.SlewAndCenter), Step(WorkflowStepKind.StartGuiding)],
            [Block(A), Block(C)],
            [Step(WorkflowStepKind.StopGuiding)]);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var centers = compiled.Steps.OfType<SlewAndCenterStepDraft>().ToList();
        Assert.Equal([A, C], centers.Select(c => c.RigId!.Value)); // two pointing systems: each is centered on its own, never one slew for two mounts
        Assert.Equal(2, compiled.Steps.OfType<StartGuidingStepDraft>().Select(g => g.GuiderId).Distinct().Count());
        Assert.Equal(2, compiled.Steps.OfType<StopGuidingStepDraft>().Count());
    }

    [Fact]
    public void ABlockWithAFilter_ChangesTheFilterOnceBeforeItsFrames_AndBlocksOfOneSetupShareATrack()
    {
        var host = CreateHost();
        var workflow = Workflow([], [Block(A, 5, 60, slot: 1), Block(A, 5, 60, slot: 1), Block(A, 5, 60, slot: 0)], []);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        var track = Assert.Single(Assert.IsType<MultiRigStepDraft>(Assert.Single(compiled.Steps)).Tracks);
        Assert.Equal(
            [SequenceStepKind.RigChangeFilter, SequenceStepKind.Repeat, SequenceStepKind.Repeat, SequenceStepKind.RigChangeFilter, SequenceStepKind.Repeat],
            track.Steps.Select(s => s.Kind));
        Assert.Equal(1, ((RigChangeFilterStepDraft)track.Steps[0]).SlotIndex);
        Assert.Equal(0, ((RigChangeFilterStepDraft)track.Steps[3]).SlotIndex);
    }

    [Fact]
    public void Dither_IsAPolicyOfTheBlock_CountedOnOneSetup_WithoutAGuiderChoice()
    {
        var host = CreateHost();
        var workflow = Workflow([], [Block(A), Block(B)], [], new WorkflowDither(true, 3, B));

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var policy = Assert.IsType<MultiRigStepDraft>(Assert.Single(compiled.Steps)).DitherPolicy!;
        Assert.True(policy.Enabled);
        Assert.Equal(B, policy.TriggerRigId);
        Assert.Equal(3, policy.EveryNFrames);
        Assert.DoesNotContain(compiled.Steps, s => s is DitherStepDraft); // no explicit Exposure, Exposure, Dither
        // The guider is the one of the counted setup: the draft validator (which also runs on the compiled steps) finds it.
        var draftProblems = SequenceDraftBuilder.Validate(host.DeviceRegistry, compiled.Steps, new SequenceDraftContext(host.RigRegistry, SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false)));
        Assert.DoesNotContain(draftProblems.StepProblems.Values.SelectMany(p => p), p => p.Contains("guider", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DitherOnASetupWithoutAMountOrGuider_IsAProblemOfTheWorkflow()
    {
        var host = CreateHost();

        var compiled = WorkflowCompiler.Compile(Workflow([], [Block(D)], [], new WorkflowDither(true, 2)), host.RigRegistry);

        Assert.Contains(compiled.Problems, p => p.Message.Contains("Dither needs a mount", StringComparison.Ordinal));
        Assert.Contains(compiled.Problems, p => p.Message.Contains("Dither needs a guider", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAutofocusPolicyOfASetup_BecomesThePolicyOfItsTrack_WithTheInterval()
    {
        var host = CreateHost();
        var policy = new SetupAutofocus(A, true, true, 60, true, new AutofocusSettings(2, 300, 9));
        var workflow = Workflow([], [Block(A, slot: 1), Block(B)], [], null, policy);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var tracks = Assert.IsType<MultiRigStepDraft>(Assert.Single(compiled.Steps)).Tracks;
        Assert.Equal(new RigAutofocusPolicyDraft(true, true, true, 2, 300, 9, 60), tracks[0].AutofocusPolicy);
        Assert.Null(tracks[1].AutofocusPolicy); // the other setup does not focus by itself
    }

    [Fact]
    public void WhatASetupLacks_IsSaidAtTheWorkflow_NotFoundAtRuntime()
    {
        var host = CreateHost();
        var workflow = Workflow(
            [Step(WorkflowStepKind.Autofocus, D), Step(WorkflowStepKind.StartGuiding, D), Step(WorkflowStepKind.SlewAndCenter, D)],
            [Block(D, slot: 1)],
            [],
            null,
            new SetupAutofocus(D, true, true, 0, true, AutofocusSettings.Default));

        var messages = WorkflowCompiler.Compile(workflow, host.RigRegistry).Problems.Select(p => p.Message).ToList();

        Assert.Contains(messages, m => m.Contains("Bare has no focuser", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Bare has no guider", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Bare has no mount", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Contains("Bare has no filter wheel", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledStepOrBlock_IsLeftOut()
    {
        var host = CreateHost();
        var workflow = Workflow([Step(WorkflowStepKind.StartGuiding, null, false)], [Block(A) with { Enabled = false }, Block(B)], []);

        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);

        Assert.Equal([SequenceStepKind.MultiRig], compiled.Steps.Select(s => s.Kind));
        Assert.Equal([B], ((MultiRigStepDraft)compiled.Steps[0]).Tracks.Select(t => t.RigId!.Value));
    }

    [Fact]
    public void CompilingTwice_GivesTheSameStepsWithTheSameIds()
    {
        var host = CreateHost();
        var workflow = Workflow([Step(WorkflowStepKind.SlewAndCenter), Step(WorkflowStepKind.StartGuiding)], [Block(A), Block(B)], [Step(WorkflowStepKind.StopGuiding)], new WorkflowDither(true, 2));

        static IEnumerable<Guid> Ids(IEnumerable<SequenceStepDraft> steps) => steps.SelectMany(s => s switch
        {
            MultiRigStepDraft m => m.Tracks.SelectMany(t => t.Steps.SelectMany(i => i is RepeatStepDraft r ? r.Children.Select(c => c.Id).Prepend(r.Id) : [i.Id]).Prepend(t.Id)).Prepend(m.Id),
            _ => [s.Id],
        });

        Assert.Equal(Ids(WorkflowCompiler.Compile(workflow, host.RigRegistry).Steps), Ids(WorkflowCompiler.Compile(workflow, host.RigRegistry).Steps));
    }

    [Fact]
    public void ACompiledWorkflow_PassesTheValidationOfTheDraft_AndEveryDraftStepKnowsItsBlock()
    {
        var host = CreateHost();
        var block = Block(A, 4, 1);
        var workflow = Workflow([Step(WorkflowStepKind.StartGuiding)], [block], [Step(WorkflowStepKind.StopGuiding)]);
        var compiled = WorkflowCompiler.Compile(workflow, host.RigRegistry);
        var shared = SharedEquipmentDraft.FromRigs(host.RigRegistry.GetAll(), null, null, false);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, compiled.Steps, new SequenceDraftContext(host.RigRegistry, shared));

        Assert.Empty(validation.SequenceProblems);
        var track = ((MultiRigStepDraft)compiled.Steps[1]).Tracks[0];
        Assert.Equal(block.Id, compiled.Origins[track.Id]);
        Assert.Equal(block.Id, compiled.Origins[((RepeatStepDraft)track.Steps[0]).Id]);
    }

    [Fact]
    public void AWorkflow_IsSavedInTheDocument_AndComesBackAsTheSameWorkflow()
    {
        var workflow = Workflow(
            [Step(WorkflowStepKind.SlewAndCenter), new WorkflowStep(Guid.NewGuid(), WorkflowStepKind.Wait, null, true, 12.5)],
            [Block(A, 40, 300, 1), Block(B, 120, 60)],
            [Step(WorkflowStepKind.StopGuiding)],
            new WorkflowDither(true, 2, B, 1.2, 0.4, 2, 20),
            new SetupAutofocus(A, true, true, 45, false, new AutofocusSettings(1.5, 250, 9))) with
        {
            Target = new WorkflowTarget("NGC 7000", 20.9, 44.3, 81.5, A),
        };
        var serializer = new JsonSequenceDocumentSerializer();

        using var stream = new MemoryStream();
        serializer.SaveAsync(stream, new SequenceDocument("Test", [], null, workflow), CancellationToken.None).GetAwaiter().GetResult();
        stream.Position = 0;
        var text = new StreamReader(stream).ReadToEnd();
        stream.Position = 0;
        var back = serializer.LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Workflow!;

        Assert.Contains("\"version\": 8", text, StringComparison.Ordinal);
        Assert.Equal(workflow.Target, back.Target);
        Assert.Equal(workflow.Dither, back.Dither);
        Assert.Equal(workflow.Prepare, back.Prepare);
        Assert.Equal(workflow.Imaging, back.Imaging);
        Assert.Equal(workflow.Finish, back.Finish);
        Assert.Equal(workflow.AutofocusPolicies, back.AutofocusPolicies);
    }

    [Fact]
    public void AVersion7Document_StillLoads_WithoutAWorkflow()
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":7,\"steps\":[{\"type\":\"delay\",\"id\":\"11111111-1111-4111-8111-111111111111\",\"durationSeconds\":3}]}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

        var document = new JsonSequenceDocumentSerializer().LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult();

        Assert.Null(document.Workflow);
        Assert.IsType<DelayDocumentStep>(Assert.Single(document.Steps));
    }

    [Fact]
    public void AWorkflowInAVersion7Document_IsIgnored_ItDoesNotBelongThere()
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":7,\"steps\":[],\"workflow\":{\"target\":{}}}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

        Assert.Null(new JsonSequenceDocumentSerializer().LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult().Workflow);
    }

    [Fact]
    public void ABrokenWorkflow_IsRefusedWithASentence_NotACrash()
    {
        var text = "{\"format\":\"astra-sequence\",\"version\":8,\"steps\":[],\"workflow\":{\"target\":{\"name\":\"x\"}}}";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));

        var ex = Assert.Throws<SequenceDocumentException>(() => new JsonSequenceDocumentSerializer().LoadAsync(stream, CancellationToken.None).GetAwaiter().GetResult());

        Assert.StartsWith("Invalid workflow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplate_StartsWithWhatTheFirstSetupCanDo()
    {
        var host = CreateHost();
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);

        var template = WorkflowDefinition.Template(host.RigRegistry.GetAll().ToList(), defaults);

        Assert.Equal([WorkflowStepKind.SlewAndCenter, WorkflowStepKind.Autofocus, WorkflowStepKind.StartGuiding], template.Prepare.Select(s => s.Kind));
        Assert.Equal([WorkflowStepKind.StopGuiding], template.Finish.Select(s => s.Kind));
        Assert.Single(template.Imaging);
        Assert.True(WorkflowCompiler.Compile(template, host.RigRegistry).IsValid);
    }
}

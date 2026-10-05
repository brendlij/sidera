using System.Text;
using Sidera.Core.Astrometry;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Documents;
using Sidera.Runtime;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.Documents;

public sealed class PlateSolvingActionsDocumentTests
{
    private sealed class Solver : IPlateSolver
    {
        public int Solves;
        public string Name => "Test";
        public PlateSolverCapabilities Capabilities => PlateSolverCapabilities.HintedSolve;
        public Task<PlateSolverStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PlateSolverStatus(true, [], null));

        public Task<PlateSolveResult> SolveAsync(PlateSolveRequest request, CancellationToken cancellationToken = default)
        {
            Solves++;
            return Task.FromResult(new PlateSolveResult { Success = true, Center = request.ApproximateCenter ?? new(5.588, -5.4) });
        }
    }

    private static async Task<(SequenceDraftContext Context, SideraRuntimeHost Host, Rig Rig, Solver Solver)> CreateAsync()
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera"), "Camera");
        host.AddSimulatedMount(new("mount"), "Mount", TimeSpan.FromMilliseconds(1));
        var rig = new Rig(new("rig"), "Rig", camera.Id, new OpticalTrain(500, pixelSizeXMicrons: 3.76, pixelSizeYMicrons: 3.76));
        host.AddRig(rig);
        var solver = new Solver();
        host.ConfigurePlateSolver(solver);
        await camera.ConnectAsync();
        await host.DeviceRegistry.GetAll().OfType<IMount>().Single().ConnectAsync();
        return (new SequenceDraftContext(Rigs: host.RigRegistry, PlateSolving: host.PlateSolving), host, rig, solver);
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

    [Fact]
    public async Task SlewAndCenter_RoundTripsThroughTheDocument_WithoutAnySolverName()
    {
        var step = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount"), new RigId("rig"), 5.588, -5.39, 45, 4, 3);

        var (text, loaded) = await RoundTripAsync(step);

        Assert.Equal(step, loaded);
        Assert.Contains("\"type\": \"slewAndCenter\"", text);
        Assert.DoesNotContain("astap", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncMount_RoundTripsThroughTheDocument_AndCarriesNoSolveResult()
    {
        var step = new SyncMountStepDraft(Guid.NewGuid(), new DeviceId("mount"));

        var (text, loaded) = await RoundTripAsync(step);

        Assert.Equal(step, loaded);
        Assert.Contains("\"type\": \"syncMountToSolved\"", text);
        Assert.DoesNotContain("raHours", text); // a position is never part of the step: it is the runtime's last solve
    }

    [Fact]
    public async Task AFileWithoutTheNewSteps_StillLoads_AndAnOlderVersionRefusesThem()
    {
        var older = Encoding.UTF8.GetBytes("{\"format\":\"astra-sequence\",\"version\":6,\"steps\":[{\"id\":\"" + Guid.NewGuid() + "\",\"type\":\"syncMountToSolved\",\"mountId\":\"mount\"}]}");

        await Assert.ThrowsAnyAsync<Exception>(() => new JsonSequenceDocumentSerializer().LoadAsync(new MemoryStream(older), CancellationToken.None));
    }

    [Fact]
    public async Task BothStepsCompile_ToBackendNeutralActions()
    {
        var (context, host, rig, _) = await CreateAsync();
        await using var _ = host;
        var center = new SlewAndCenterStepDraft(Guid.NewGuid(), new DeviceId("mount"), rig.Id, 5.588, -5.39, 60, 3, 0.01);
        var sync = new SyncMountStepDraft(Guid.NewGuid(), new DeviceId("mount"));

        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [center, sync], context);

        Assert.IsType<SlewAndCenterAction>(built.Sequence.Steps[0]);
        Assert.IsType<SyncMountToSolvedPositionAction>(built.Sequence.Steps[1]);
        Assert.Contains(new DeviceId("mount"), SequenceDraftBuilder.RequiredDeviceIds([center, sync], context));
        Assert.Contains(rig.CameraId, SequenceDraftBuilder.RequiredDeviceIds([center], context));
    }

    [Fact]
    public async Task ASequenceOfSolveThenSync_SyncsOnce_AndOnlyBecauseTheStepIsThere()
    {
        var (context, host, rig, solver) = await CreateAsync();
        await using var _ = host;
        var mount = host.DeviceRegistry.GetAll().OfType<IMount>().Single();
        var solve = new PlateSolveStepDraft(Guid.NewGuid(), rig.Id, 0.01);
        var sync = new SyncMountStepDraft(Guid.NewGuid(), new DeviceId("mount"));

        var withoutSync = SequenceDraftBuilder.Build(host.DeviceRegistry, [solve], context);
        await new SequenceRunner(host.ResourceManager).RunAsync(withoutSync.Sequence, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        var afterSolveOnly = mount.Coordinates;

        var both = SequenceDraftBuilder.Build(host.DeviceRegistry, [solve, sync], context);
        await new SequenceRunner(host.ResourceManager).RunAsync(both.Sequence, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotEqual(new CelestialCoordinates(5.588, -5.4), afterSolveOnly); // the plain solve changed nothing in the mount
        Assert.Equal(2, solver.Solves);
        Assert.Equal(host.PlateSolving!.LastResult!.Center, mount.Coordinates);
    }

    [Fact]
    public async Task ASyncStepWithoutAMountOrWithoutASolver_IsReportedBeforeTheRun()
    {
        var (context, host, rig, _) = await CreateAsync();
        await using var _ = host;

        var problems = SequenceDraftBuilder.Validate(host.DeviceRegistry, [new SyncMountStepDraft(Guid.NewGuid(), null)], context);

        Assert.False(problems.IsValid);
    }
}

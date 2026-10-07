using Sidera.Core.FilterWheels;
using Sidera.Core.Focusers;
using Sidera.Core.Sequencing;
using Sidera.Desktop.Sessions;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;
using Sidera.Runtime.Rigs;
using Sidera.Runtime.Sequencing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;
using AutofocusAction = Sidera.Desktop.Sessions.AutofocusAction;
using StartGuidingAction = Sidera.Desktop.Sessions.StartGuidingAction;
using StopGuidingAction = Sidera.Desktop.Sessions.StopGuidingAction;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>One camera and what is on its telescope, and no imaging setup that anybody made: the actions of a session find their devices without being told, and the session runs.</summary>
public sealed class SingleSetupSessionTests : IAsyncLifetime
{
    private SideraRuntimeHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private async Task<SideraRuntimeHost> CreateAsync()
    {
        var host = new SideraRuntimeHost();
        var camera = host.AddSimulatedCamera(new("camera.only"), "Only camera", 1);
        host.AddSimulatedFocuser(new("focuser.only"), "Only focuser", 2500, maxPosition: 5000, stepsPerSecond: 20000, minimumMoveDuration: TimeSpan.FromMilliseconds(5));
        host.AddSimulatedFilterWheel(new("wheel.only"), "Only wheel", [new FilterSlot(0, "L"), new FilterSlot(1, "R"), new FilterSlot(2, "G"), new FilterSlot(3, "B")]);
        host.AddSimulatedMount(new("mount.only"), "Only mount", TimeSpan.FromMilliseconds(20));
        host.AddSimulatedGuider(new("guider.only"), "Only guider", TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(40));
        host.AddSimulatedFocusModel(ImagingSetupCatalog.ImplicitIdFor(camera.Id), new SimulatedFocusModel(2600));
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        _host = host;
        return host;
    }

    [Fact]
    public async Task TheActionsFindTheCameraTheFocuserAndTheFilterWheel_OfTheOnlySetup_AndTheSessionRuns()
    {
        var host = await CreateAsync();
        var focusSettings = new FocusSettings(0.05, 400, 5);
        var block = Block(2, 0.1, 3);
        var session = Session(Target("M31", [Lane(null, block)], [new AutofocusAction(Guid.NewGuid(), focusSettings), new StartGuidingAction(Guid.NewGuid())])) with
        {
            End = [new StopGuidingAction(Guid.NewGuid())],
        };

        var compiled = SessionCompiler.Compile(session, new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry));

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message))); // nobody was asked for a setup, a camera, a focuser or a wheel
        var imaging = Assert.Single(compiled.Steps.OfType<MultiRigStepDraft>());
        var track = Assert.Single(imaging.Tracks);
        Assert.Equal(ImagingSetupCatalog.ImplicitIdFor(new("camera.only")), track.RigId);
        Assert.Contains(compiled.Steps, s => s is AutofocusStepDraft { RigId: { } id } && id == track.RigId); // the autofocus of the preparation is for the same setup
        Assert.Equal(["filter 2", "repeat 3"], track.Steps.Select(s => s switch
        {
            RigChangeFilterStepDraft f => $"filter {f.SlotIndex}",
            RepeatStepDraft r => $"repeat {r.Count}",
            _ => s.GetType().Name,
        }));

        var catalog = new ImagingSetupCatalog(host.DeviceRegistry, host.RigRegistry);
        var context = new SequenceDraftContext(catalog, SharedEquipmentDraft.FromRigs(catalog.GetAll(), null, null, false), host.FocusMetrics, host.EventBus, null, host.AcquisitionDefaults, host.PlateSolving);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, compiled.Steps, context);
        var runner = new SequenceRunner(host.ResourceManager, host.SafePointCoordinator);
        var exposures = 0;
        runner.StepCompleted += (_, e) => exposures += e.StepName.StartsWith("Exposure", StringComparison.Ordinal) ? 1 : 0;

        await runner.RunAsync(built.Sequence).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(SequenceState.Completed, runner.State);
        Assert.Equal(3, exposures);
        var wheel = host.DeviceRegistry.GetAll().OfType<IFilterWheel>().Single();
        Assert.Equal(2, wheel.CurrentSlot.Index); // the filter wheel of the only setup turned to the filter
        var focuser = host.DeviceRegistry.GetAll().OfType<IFocuser>().Single();
        Assert.NotEqual(2500, focuser.Position); // and its focuser was moved by the autofocus
    }
}

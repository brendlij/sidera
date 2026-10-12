using Sidera.Core.Devices;
using Sidera.Core.Domes;
using Sidera.Desktop.Documents;
using Sidera.Desktop.Sessions;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>
/// The dome in the sequencer: the actions Open Dome, Close Dome, Turn Dome, Park Dome and Dome Home, from the library to the runtime. They belong to the start, the end and the preparation of a target, work
/// with the domes of the observatory (not with an imaging setup), are saved in the session, run on the simulated dome, and ask first before a real shutter is opened.
/// </summary>
public sealed class DomeActionTests : IAsyncLifetime
{
    private readonly List<SessionFixture> _fixtures = [];
    private readonly List<SideraRuntimeHost> _hosts = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var fixture in _fixtures)
        {
            await fixture.DisposeAsync();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }
    }

    private static readonly DeviceId Dome = new("dome.main");

    private SessionFixture Equipment()
    {
        var fixture = Create();
        _fixtures.Add(fixture);
        return fixture;
    }

    private static SessionDefinition WithStartAndEnd(IReadOnlyList<SessionAction> start, IReadOnlyList<SessionAction> end) =>
        Session(Target("M42", [Lane(null, Block(1, 60, 2))])) with { Start = start, End = end };

    // ---- the library and the file

    [Fact]
    public void TheDomeActions_AreInTheLibrary_InTheirOwnGroup_ForTheSessionAndThePreparation_AndNotForABlock()
    {
        var dome = ActionCatalog.Entries.Where(a => a.Category == ActionCategory.Dome).ToList();

        Assert.Equal(
            [SessionActionKind.OpenDome, SessionActionKind.CloseDome, SessionActionKind.TurnDome, SessionActionKind.ParkDome, SessionActionKind.FindDomeHome], dome.Select(a => a.Kind));
        Assert.All(dome, a => Assert.Equal(ActionScope.Session | ActionScope.Preparation, a.Scope));
        Assert.DoesNotContain(ActionCatalog.For(ActionScope.Block), a => a.Category == ActionCategory.Dome);
        Assert.Equal(["Open Dome", "Close Dome", "Turn Dome", "Park Dome", "Dome Home"], dome.Select(a => a.Title));
    }

    [Fact]
    public void ADomeActionSummary_SaysTheAzimuth()
    {
        Assert.Equal("to 90°", ActionSummary.Of(new TurnDomeAction(Guid.NewGuid(), 90)));
        Assert.Equal("to 12.5°", ActionSummary.Of(new TurnDomeAction(Guid.NewGuid(), 12.5)));
        Assert.Equal(string.Empty, ActionSummary.Of(new OpenDomeAction(Guid.NewGuid())));
    }

    [Fact]
    public async Task TheDomeActions_AreSavedInTheSession_AndComeBackWithTheirValues()
    {
        var session = WithStartAndEnd(
            [new OpenDomeAction(Guid.NewGuid()), new TurnDomeAction(Guid.NewGuid(), 123.5), new ParkDomeAction(Guid.NewGuid()) { Enabled = false }],
            [new FindDomeHomeAction(Guid.NewGuid()), new CloseDomeAction(Guid.NewGuid())]);
        var document = new SequenceDocument("dome", [], null, null, session);
        var serializer = new JsonSequenceDocumentSerializer();
        using var stream = new MemoryStream();
        await serializer.SaveAsync(stream, document, CancellationToken.None);
        stream.Position = 0;

        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        stream.Position = 0;
        var back = (await serializer.LoadAsync(stream, CancellationToken.None)).Session!;

        Assert.Contains("\"type\": \"openDome\"", text, StringComparison.Ordinal);
        Assert.Contains("\"azimuthDegrees\": 123.5", text, StringComparison.Ordinal);
        Assert.Equal(session.Start.Select(a => a.Kind), back.Start.Select(a => a.Kind));
        Assert.Equal(123.5, ((TurnDomeAction)back.Start[1]).AzimuthDegrees);
        Assert.False(back.Start[2].Enabled);
        Assert.Equal([SessionActionKind.FindDomeHome, SessionActionKind.CloseDome], back.End.Select(a => a.Kind));
    }

    // ---- the compiler

    [Fact]
    public void WithADome_EachActionBecomesAStepOnThatDome_InOrder()
    {
        var equipment = Equipment();
        var session = WithStartAndEnd(
            [new OpenDomeAction(Guid.NewGuid()), new TurnDomeAction(Guid.NewGuid(), 200), new ParkDomeAction(Guid.NewGuid()), new FindDomeHomeAction(Guid.NewGuid())],
            [new CloseDomeAction(Guid.NewGuid())]);

        var compiled = SessionCompiler.Compile(session, equipment.Catalog, domes: [Dome]);

        Assert.True(compiled.IsValid, string.Join(" ", compiled.Problems.Select(p => p.Message)));
        var steps = compiled.Steps.OfType<DeviceOperationStepDraft>().ToList();
        Assert.Equal(
            [DeviceOperation.OpenDome, DeviceOperation.TurnDome, DeviceOperation.ParkDome, DeviceOperation.FindDomeHome, DeviceOperation.CloseDome], steps.Select(s => s.Operation));
        Assert.All(steps, s => Assert.Equal(Dome, s.DeviceId));
        Assert.All(steps, s => Assert.True(s.IsDome));
        Assert.Equal(200, steps[1].AzimuthDegrees);
        Assert.Equal(DeviceOperation.OpenDome, ((DeviceOperationStepDraft)compiled.Steps[0]).Operation); // the start comes first
        Assert.Equal(DeviceOperation.CloseDome, ((DeviceOperationStepDraft)compiled.Steps[^1]).Operation); // the end comes last
    }

    [Fact]
    public void EverySeveralDomes_GetTheAction_AndTheStepsKeepTheirIds_FromRunToRun()
    {
        var equipment = Equipment();
        var open = new OpenDomeAction(Guid.NewGuid());
        var session = WithStartAndEnd([open], []);
        var second = new DeviceId("dome.second");

        var first = SessionCompiler.Compile(session, equipment.Catalog, domes: [Dome, second]);
        var again = SessionCompiler.Compile(session, equipment.Catalog, domes: [Dome, second]);

        var ids = first.Steps.OfType<DeviceOperationStepDraft>().Select(s => s.Id).ToList();
        Assert.Equal([Dome, second], first.Steps.OfType<DeviceOperationStepDraft>().Select(s => s.DeviceId!.Value));
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(ids, again.Steps.OfType<DeviceOperationStepDraft>().Select(s => s.Id));
    }

    [Fact]
    public void WithoutADome_TheActionIsAProblem_AtThatAction_NotASilentNothing()
    {
        var equipment = Equipment();
        var open = new OpenDomeAction(Guid.NewGuid());

        var compiled = SessionCompiler.Compile(WithStartAndEnd([open], []), equipment.Catalog);

        Assert.False(compiled.IsValid);
        var problem = Assert.Single(compiled.Problems);
        Assert.Equal(open.Id, problem.ElementId);
        Assert.Contains("There is no dome", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADomeActionInTheSessionEditor_UsesTheDomesOfTheEquipment()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        DemoSetup.AddDemoEquipment(host);
        host.AddSimulatedDome(Dome, "Observatory dome");
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), withDemoSequence: false);
        var editor = vm.SessionEditor;
        editor.Load(WithStartAndEnd([new OpenDomeAction(Guid.NewGuid())], [new CloseDomeAction(Guid.NewGuid())]));

        var steps = vm.SequenceDraft.Snapshot().OfType<DeviceOperationStepDraft>().ToList();

        Assert.Equal([DeviceOperation.OpenDome, DeviceOperation.CloseDome], steps.Select(s => s.Operation));
        Assert.All(steps, s => Assert.Equal(Dome, s.DeviceId));
        Assert.DoesNotContain(editor.Problems, p => p.Contains("no dome", StringComparison.OrdinalIgnoreCase));
        vm.Dispose();
    }

    // ---- the draft and the tree

    [Fact]
    public void ADomeStepThatIsNotValid_IsNamedInTheProblems()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedDome(Dome, "Dome");
        var draft = new SequenceDraftViewModel(host.DeviceRegistry, SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry), []);

        draft.ReplaceSteps([new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.TurnDome, Dome, AzimuthDegrees: 400), new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.OpenDome, null)]);

        Assert.False(draft.IsValid);
        Assert.Contains(draft.ValidationErrors, p => p.Contains("azimuth of the dome", StringComparison.Ordinal));
        Assert.Contains(draft.ValidationErrors, p => p.Contains("No dome selected", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTreeEditor_OffersTheDomeOperations_WithADomePickerAndAnAzimuth()
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        host.AddSimulatedDome(Dome, "Dome");
        var draft = new SequenceDraftViewModel(host.DeviceRegistry, SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry), []);
        draft.ReplaceSteps([new DeviceOperationStepDraft(Guid.NewGuid(), DeviceOperation.TurnDome, Dome, AzimuthDegrees: 45)]);
        var editor = Assert.IsType<DeviceOperationStepDraftViewModel>(draft.Steps[0]);

        Assert.True(editor.ShowsDome);
        Assert.True(editor.ShowsAzimuth);
        Assert.False(editor.ShowsMount);
        Assert.False(editor.ShowsCamera);
        var read = Assert.IsType<DeviceOperationStepDraft>(draft.Snapshot().Single());
        Assert.Equal((DeviceOperation.TurnDome, Dome, 45.0), (read.Operation, read.DeviceId!.Value, read.AzimuthDegrees));

        editor.SelectedOperation = editor.OperationChoices.Single(c => c.Operation == DeviceOperation.OpenDome);
        Assert.False(editor.ShowsAzimuth);
        Assert.True(editor.ShowsDome);
        Assert.Equal(DeviceOperation.OpenDome, ((DeviceOperationStepDraft)draft.Snapshot().Single()).Operation);
    }

    // ---- the runtime

    private async Task<(SideraRuntimeHost Host, SimulatedDome Dome)> HostWithDome(bool roof = false)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        var dome = host.AddSimulatedDome(Dome, "Dome", shutterOnly: roof, shutterTime: TimeSpan.FromMilliseconds(20));
        await dome.ConnectAsync();
        return (host, dome);
    }

    private static Task Run(SideraRuntimeHost host, DeviceOperation operation, double azimuth = 0) =>
        new DeviceOperationAction(host.DeviceRegistry, operation, Dome, azimuthDegrees: azimuth).ExecuteAsync(null!, CancellationToken.None);

    [Fact]
    public async Task TheOperations_OpenCloseTurnParkAndHomeTheDome()
    {
        var (host, dome) = await HostWithDome();

        await Run(host, DeviceOperation.OpenDome);
        Assert.Equal(DomeShutterState.Open, dome.ShutterState);
        await Run(host, DeviceOperation.TurnDome, 135);
        Assert.Equal(135, dome.Azimuth!.Value, 3);
        await Run(host, DeviceOperation.FindDomeHome);
        Assert.True(dome.Telemetry!.AtHome);
        await Run(host, DeviceOperation.ParkDome);
        Assert.True(dome.Telemetry!.AtPark);
        await Run(host, DeviceOperation.CloseDome);
        Assert.Equal(DomeShutterState.Closed, dome.ShutterState);
    }

    [Fact]
    public async Task ARoof_OpensAndCloses_ButRefusesToTurnParkOrFindHome()
    {
        var (host, roof) = await HostWithDome(roof: true);

        await Run(host, DeviceOperation.OpenDome);
        Assert.Equal(DomeShutterState.Open, roof.ShutterState);
        var turn = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(host, DeviceOperation.TurnDome, 90));
        Assert.Contains("no azimuth", turn.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(host, DeviceOperation.ParkDome));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(host, DeviceOperation.FindDomeHome));
        await Run(host, DeviceOperation.CloseDome);
        Assert.Equal(DomeShutterState.Closed, roof.ShutterState);
    }

    [Fact]
    public async Task ADomeThatIsNotConnected_IsNotMoved_AndTheStepSaysSo()
    {
        var (host, dome) = await HostWithDome();
        await dome.DisconnectAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(host, DeviceOperation.OpenDome));

        Assert.Contains("not connected", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDomeOperations_ClaimTheDomeAlone_NotAMountOrACamera()
    {
        var (host, _) = await HostWithDome();
        var action = new DeviceOperationAction(host.DeviceRegistry, DeviceOperation.OpenDome, Dome);

        Assert.Equal([Sidera.Core.Resources.ResourceId.ForDevice(Dome)], action.RequiredResources);
        Assert.Single(action.Claims);
        Assert.Equal("Open dome", action.Name);
        Assert.Equal("Turn dome to 90°", new DeviceOperationAction(host.DeviceRegistry, DeviceOperation.TurnDome, Dome, azimuthDegrees: 90).Name);
    }

    // ---- real equipment asks first

    private sealed class RealLookingDome(SimulatedDome inner) : IDomeControl
    {
        public DeviceId Id => inner.Id;
        public string Name => inner.Name;
        public DeviceType Type => DeviceType.Dome;
        public DeviceConnectionState ConnectionState => inner.ConnectionState;
        public DomeShutterState ShutterState => inner.ShutterState;
        public double? Azimuth => inner.Azimuth;
        public bool IsMoving => inner.IsMoving;
        public DeviceCapabilities<DomeCapabilities> Capabilities => inner.Capabilities;
        public DomeTelemetry? Telemetry => inner.Telemetry;
        public event EventHandler? CapabilitiesChanged { add => inner.CapabilitiesChanged += value; remove => inner.CapabilitiesChanged -= value; }
        public event EventHandler? StateChanged { add => inner.StateChanged += value; remove => inner.StateChanged -= value; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);
        public Task RefreshAsync(CancellationToken cancellationToken = default) => inner.RefreshAsync(cancellationToken);
        public Task OpenShutterAsync(CancellationToken cancellationToken = default) => inner.OpenShutterAsync(cancellationToken);
        public Task CloseShutterAsync(CancellationToken cancellationToken = default) => inner.CloseShutterAsync(cancellationToken);
        public Task SlewToAzimuthAsync(double azimuthDegrees, CancellationToken cancellationToken = default) => inner.SlewToAzimuthAsync(azimuthDegrees, cancellationToken);
        public Task ParkAsync(CancellationToken cancellationToken = default) => inner.ParkAsync(cancellationToken);
        public Task FindHomeAsync(CancellationToken cancellationToken = default) => inner.FindHomeAsync(cancellationToken);
        public Task HaltAsync(CancellationToken cancellationToken = default) => inner.HaltAsync(cancellationToken);
    }

    private (MainViewModel Vm, IDomeControl Dome) RealSession(params SessionAction[] start)
    {
        var host = new SideraRuntimeHost();
        _hosts.Add(host);
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        var real = new RealLookingDome(new SimulatedDome(Dome, "Roof", host.EventBus, shutterOnly: true, shutterTime: TimeSpan.FromMilliseconds(20)));
        host.AddDevice(real);
        var vm = new MainViewModel(host, a => a(), new DemoOptions(), withDemoSequence: false, startWithSettingsMode: true);
        vm.SessionEditor.Load(Session(Target("M42", [Lane(null, Block(1, 0.2, 1))])) with { Start = start });
        return (vm, real);
    }

    [Fact]
    public async Task ASequenceThatOpensARealShutter_Asks_EveryTimeItIsRun()
    {
        var (vm, dome) = RealSession(new OpenDomeAction(Guid.NewGuid()));
        foreach (var device in vm.Equipment.Devices.ToList())
        {
            await device.DeviceModel.ConnectAsync();
        }

        Assert.Equal([(Dome, true)], vm.SequenceDraft.DomeMoves());
        var running = vm.Sequencer.RunCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending);
        Assert.Contains("the dome (Roof)", vm.Safety.Message, StringComparison.Ordinal);
        vm.Safety.CancelCommand.Execute(null);
        await running;

        Assert.Equal(DomeShutterState.Closed, dome.ShutterState); // no is no movement
        Assert.Contains("Cancelled", vm.Sequencer.ReadinessHint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("You are asked each time", vm.Safety.RememberText, StringComparison.Ordinal);
        vm.Dispose();
    }

    [Fact]
    public async Task ASequenceThatOnlyClosesARealShutter_DoesNotAskForTheDomeEachTime_ButOnce()
    {
        var (vm, _) = RealSession(new CloseDomeAction(Guid.NewGuid()));
        foreach (var device in vm.Equipment.Devices.ToList())
        {
            await device.DeviceModel.ConnectAsync();
        }

        Assert.Equal([(Dome, false)], vm.SequenceDraft.DomeMoves());
        var running = vm.Sequencer.RunCommand.ExecuteAsync(null);
        Assert.True(vm.Safety.IsPending);
        Assert.Contains("until Sidera is closed", vm.Safety.RememberText, StringComparison.Ordinal);
        vm.Safety.CancelCommand.Execute(null);
        await running;
        vm.Dispose();
    }

    [Fact]
    public void ASequenceWithADomeThatIsNotConnected_CannotRun_AndSaysWhich()
    {
        var (vm, _) = RealSession(new OpenDomeAction(Guid.NewGuid()));

        Assert.False(vm.Sequencer.CanRun);
        Assert.Contains("Connect Roof to run the sequence", vm.Sequencer.ReadinessHint ?? string.Empty, StringComparison.Ordinal);
        vm.Dispose();
    }
}

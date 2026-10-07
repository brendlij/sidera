using Sidera.Core.Devices;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime.Sequencing;
using DeviceOperation = Sidera.Runtime.Sequencing.DeviceOperation;
using static Sidera.Desktop.Tests.Sessions.SessionFixture;

namespace Sidera.Desktop.Tests.Sessions;

/// <summary>A device operation is a step like any other: it is checked, built into the runtime step, shown by the editor, and not allowed where a track cannot run it.</summary>
public sealed class DeviceOperationDraftTests : IAsyncLifetime
{
    private readonly SessionFixture _fixture = Create();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private static DeviceOperationStepDraft Step(DeviceOperation operation, string? device, double celsius = 0, double ramp = 0) =>
        new(Guid.NewGuid(), operation, device is null ? null : new DeviceId(device), celsius, ramp);

    [Fact]
    public void Builds_TheRuntimeStep_ForEveryOperation()
    {
        var registry = _fixture.Host.DeviceRegistry;
        var steps = new SequenceStepDraft[]
        {
            Step(DeviceOperation.CoolCamera, "camera.a", -10, 5), Step(DeviceOperation.WarmCamera, "camera.a", 0, 10), Step(DeviceOperation.Park, "mount.1"),
            Step(DeviceOperation.Unpark, "mount.1"), Step(DeviceOperation.TrackingOn, "mount.1"), Step(DeviceOperation.TrackingOff, "mount.1"),
        };

        Assert.True(SequenceDraftBuilder.Validate(registry, steps).IsValid);
        var built = SequenceDraftBuilder.Build(registry, steps);

        Assert.Equal(
            ["Cool camera to -10 °C", "Warm camera", "Park mount", "Unpark mount", "Tracking on", "Tracking off"],
            built.Sequence.Steps.Select(s => s.Name));
        Assert.Equal(["Cool Camera", "Warm Camera", "Park", "Unpark", "Tracking On", "Tracking Off"], built.Steps.Select(s => s.Description.Title));
    }

    [Fact]
    public void ADeviceThatIsMissingOrOfTheWrongKind_IsAProblemOfTheStep()
    {
        var registry = _fixture.Host.DeviceRegistry;

        var none = SequenceDraftBuilder.Validate(registry, [Step(DeviceOperation.Park, null)]);
        var wrong = SequenceDraftBuilder.Validate(registry, [Step(DeviceOperation.CoolCamera, "mount.1", -10, 5)]);
        var hotStep = Step(DeviceOperation.CoolCamera, "camera.a", 200, 5);
        var hot = SequenceDraftBuilder.Validate(registry, [hotStep]);

        Assert.False(none.IsValid);
        Assert.False(wrong.IsValid);
        Assert.Contains(hot.ProblemsOf(hotStep.Id), p => p.Contains("target temperature", StringComparison.Ordinal));
    }

    [Fact]
    public void InsideARigTrack_ItIsRefused_BecauseItBelongsToTheWholeSession()
    {
        var registry = _fixture.Host.DeviceRegistry;
        var track = new RigTrackDraft(Guid.NewGuid(), new RigId("rig.main"), [Step(DeviceOperation.Park, "mount.1")]);
        var block = new MultiRigStepDraft(Guid.NewGuid(), [track], SingleTrack: true);
        var context = new SequenceDraftContext { Rigs = _fixture.Catalog };

        var validation = SequenceDraftBuilder.Validate(registry, [block], context);

        Assert.Contains(validation.ProblemsOf(track.Steps[0].Id), p => p.Contains("outside a Setup Sequence", StringComparison.Ordinal));
    }

    [Fact]
    public void TheEditor_ShowsIt_AndANewOneIsAPark()
    {
        var draft = new SequenceDraftViewModel(_fixture.Host.DeviceRegistry, SequenceDraftDefaults.From(new DemoOptions(), _fixture.Host.DeviceRegistry), null, rigs: _fixture.Catalog);

        draft.AddStepCommand.Execute(SequenceStepKind.DeviceOperation);

        var step = Assert.IsType<DeviceOperationStepDraftViewModel>(Assert.Single(draft.Steps));
        Assert.Equal(DeviceOperation.Park, step.SelectedOperation.Operation);
        Assert.True(step.ShowsMount);
        step.SelectedOperation = DeviceOperationStepDraftViewModel.Choices.First(c => c.Operation == DeviceOperation.CoolCamera);
        Assert.True(step.ShowsCamera);
        Assert.True(step.ShowsTemperature);
        Assert.Equal(SequenceStepKind.DeviceOperation, draft.Snapshot()[0].Kind);
    }
}

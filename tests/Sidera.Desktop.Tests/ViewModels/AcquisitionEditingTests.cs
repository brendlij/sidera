using Sidera.Core.Devices;
using Sidera.Core.Coordination;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Devices;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>
/// Acquisition settings in the editor: capability-driven inputs, inherit semantics, validation that distinguishes valid,
/// invalid and not verifiable, the same for an exposure and for the exposure of a rig, and compiled to the same action.
/// </summary>
public class AcquisitionEditingTests
{
    private static readonly DeviceId Main = DemoSetup.MainCameraId;

    private sealed class NoContext : ISequenceStepContext
    {
        public static NoContext Instance { get; } = new();

        public Task<SequenceStepResult> ExecuteChildAsync(ISequenceStep child, int index, int count, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SequenceStepResult> ExecuteBranchAsync(
            ISequenceStep child, int index, int count, CoordinationGroupId? group, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReachSafePointAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken) => operation(cancellationToken);
    }

    private static async Task<SideraRuntimeHost> CreateHost(bool withRigs = false, bool connect = false)
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        if (withRigs)
        {
            DemoSetup.AddDemoRigs(host);
        }

        if (connect)
        {
            foreach (var camera in host.DeviceRegistry.GetAll().OfType<ICamera>())
            {
                await camera.ConnectAsync();
            }
        }

        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        return new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, rigs: host.RigRegistry, acquisitionDefaults: host.AcquisitionDefaults);
    }

    private static ExposureStepDraftViewModel AddExposure(SequenceDraftViewModel draft)
    {
        draft.AddStepCommand.Execute(SequenceStepKind.Exposure);
        return Assert.IsType<ExposureStepDraftViewModel>(draft.SelectedStep);
    }

    private static ExposureStepDraft Draft(SequenceDraftViewModel draft) => Assert.IsType<ExposureStepDraft>(draft.Snapshot()[0]);

    // ---- The row and the compiled step

    [Fact]
    public async Task AnExposureThatSetsNothing_SaysItUsesTheCameraDefaults()
    {
        await using var host = await CreateHost();
        var draft = CreateDraft(host);

        var exposure = AddExposure(draft);

        Assert.EndsWith("· Camera defaults", exposure.Summary);
        Assert.True(Draft(draft).Acquisition.IsDefault);
    }

    [Fact]
    public async Task TheRowShowsTheExplicitOverrides_AndOnlyThose()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);

        exposure.Acquisition.Gain.ModeIndex = 1;
        exposure.Acquisition.Gain.ValueText = "100";
        exposure.Acquisition.BinXIndex = 2;

        Assert.Contains("Gain 100", exposure.Summary);
        Assert.Contains("Bin 2×2", exposure.Summary);
        Assert.DoesNotContain("Camera defaults", exposure.Summary);
        Assert.DoesNotContain("Offset", exposure.Summary);
    }

    [Fact]
    public async Task TheDraftCompilesToAnExposureActionThatCarriesTheIntent_AndTheCameraDefaultsSource()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);
        exposure.Acquisition.Gain.ModeIndex = 1;
        exposure.Acquisition.Gain.ValueText = "40";

        var built = draft.Build();

        var action = Assert.IsType<CameraExposureAction>(built.Sequence.Steps.Single());
        Assert.Equal(AcquisitionLevel.OfNumber(40), action.Intent.Gain);
    }

    // ---- Capability-driven inputs

    [Fact]
    public async Task AnExposureOfACameraThatIsNotConnected_OffersNothingToChoose_AndKeepsWhatItAlreadySets()
    {
        await using var host = await CreateHost();
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);

        Assert.False(exposure.Acquisition.IsVerifiable);
        Assert.False(exposure.Acquisition.HasChoices);
        Assert.False(exposure.Acquisition.ShowBinning);
        Assert.False(exposure.Acquisition.Gain.IsAvailable);
        Assert.False(exposure.Acquisition.IsNotVerifiable); // nothing is set, so nothing needs to be checked
    }

    [Fact]
    public async Task TheSimulatedCameraOffersWhatItSupports_AndFirstEverythingMeansTheCameraDefault()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);
        var acquisition = exposure.Acquisition;

        Assert.True(acquisition.IsVerifiable);
        Assert.True(acquisition.Gain.IsRange);
        Assert.Equal("0 to 100", acquisition.Gain.Hint);
        Assert.True(acquisition.ShowBinning);
        Assert.False(acquisition.ShowAsymmetricBinning);
        Assert.Equal(["Camera default", "1×1", "2×2", "3×3", "4×4"], acquisition.BinChoices);
        Assert.True(acquisition.ShowRegion);
        Assert.Equal(["Camera default", "Normal", "Slow"], acquisition.ReadoutChoices);
        Assert.False(acquisition.ShowFastReadout);
        Assert.Equal(0, acquisition.ReadoutIndex);
        Assert.Equal(0, acquisition.BinXIndex);
    }

    [Fact]
    public async Task PickingASetting_WritesItIntoTheDraft_AndPickingTheDefaultAgain_TakesItBack()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);

        exposure.Acquisition.ReadoutIndex = 2;
        Assert.Equal("Slow", Draft(draft).Acquisition.ReadoutMode);
        exposure.Acquisition.BinXIndex = 3;
        Assert.Equal((3, 3), (Draft(draft).Acquisition.BinX, Draft(draft).Acquisition.BinY));
        exposure.Acquisition.FrameTypeIndex = 1;
        Assert.Equal(FrameType.Dark, Draft(draft).Acquisition.FrameType);

        exposure.Acquisition.ReadoutIndex = 0;
        exposure.Acquisition.BinXIndex = 0;
        exposure.Acquisition.FrameTypeIndex = 0;

        Assert.True(Draft(draft).Acquisition.IsDefault);
    }

    [Fact]
    public async Task TheRegion_IsTheWholeSensorOrARectangle_AndAnIncompleteRectangleIsReportedNotGuessed()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);

        exposure.Acquisition.RegionIndex = 1;
        Assert.True(Draft(draft).Acquisition.Region!.IsFullFrame);

        exposure.Acquisition.RegionIndex = 2;
        exposure.Acquisition.RegionWidthText = "100";
        exposure.Acquisition.RegionHeightText = "50";
        Assert.Equal(AcquisitionRegion.Of(0, 0, 100, 50), Draft(draft).Acquisition.Region);

        exposure.Acquisition.RegionWidthText = "wide";
        Assert.True(draft.HasUnreadableFields);
        Assert.Contains(exposure.Problems, p => p.Contains("region needs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGainThatIsAList_IsPickedByName()
    {
        var editor = new AcquisitionLevelEditor("Gain", _ => { });

        editor.Load(IntegerControl.List(["Low", "High"]), AcquisitionLevel.OfName("High"));

        Assert.True(editor.IsList);
        Assert.Equal(["Camera default", "Low", "High"], editor.Choices);
        Assert.Equal(2, editor.ChoiceIndex);
        Assert.Equal(AcquisitionLevel.OfName("High"), editor.Read());
        editor.ChoiceIndex = 0;
        Assert.Null(editor.Read());
        await Task.CompletedTask;
    }

    [Fact]
    public void AValueTheCameraDoesNotHave_IsShownAsMissing_NeverReplacedByAnotherChoice()
    {
        var editor = new AcquisitionLevelEditor("Gain", _ => { });

        editor.Load(IntegerControl.List(["Low", "High"]), AcquisitionLevel.OfName("Ultra"));

        Assert.Equal("Ultra (not available)", editor.Choices[editor.ChoiceIndex]);
        Assert.Equal(AcquisitionLevel.OfName("Ultra"), editor.Read());
    }

    // ---- Valid, invalid, not verifiable

    [Fact]
    public async Task ASettingThatCannotBeCheckedYet_IsNotAProblem_ButOneThatCanBeAndFails_Is()
    {
        await using var host = await CreateHost();
        var draft = CreateDraft(host);
        var exposure = AddExposure(draft);
        exposure.Acquisition.Gain.ModeIndex = 0;

        // Not connected: nothing is offered, so write the value into the draft the way an opened document does.
        var steps = new SequenceStepDraft[] { new ExposureStepDraft(Guid.NewGuid(), Main, 10) { Acquisition = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(5000) } } };
        draft.Replace(steps, null);
        Assert.True(draft.IsValid);
        Assert.True(((ExposureStepDraftViewModel)draft.Steps[0]).Acquisition.IsNotVerifiable);

        await ((ICamera)host.DeviceRegistry.GetAll().First(d => d.Id == Main)).ConnectAsync();
        draft.RefreshDevices();

        Assert.False(draft.IsValid);
        Assert.Contains(draft.ValidationErrors, e => e.Contains("rejects gain 5000", StringComparison.Ordinal));
        Assert.Equal(5000, ((ExposureStepDraft)draft.Snapshot()[0]).Acquisition.Gain!.Number); // kept, never changed
        var failure = Assert.Throws<SequenceConfigurationException>(() => draft.Build());
        Assert.Contains("rejects gain 5000", failure.Message);
    }

    [Fact]
    public async Task ASettingThatFitsTheCamera_IsValid_AndABadRegionOrReadoutModeIsNot()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var good = new ExposureStepDraft(Guid.NewGuid(), Main, 10)
        {
            Acquisition = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50), BinX = 2, BinY = 2, Region = AcquisitionRegion.Of(0, 0, 100, 100), ReadoutMode = "Slow" },
        };
        draft.Replace([good], null);
        Assert.True(draft.IsValid);

        draft.Replace([good with { Acquisition = good.Acquisition with { Region = AcquisitionRegion.Of(0, 0, 900, 100) } }], null);
        Assert.False(draft.IsValid);
        Assert.Contains(draft.ValidationErrors, e => e.Contains("does not lie inside", StringComparison.Ordinal));

        draft.Replace([good with { Acquisition = good.Acquisition with { ReadoutMode = "Turbo" } }], null);
        Assert.False(draft.IsValid);
        Assert.Contains(draft.ValidationErrors, e => e.Contains("no readout mode 'Turbo'", StringComparison.Ordinal));
        var row = (ExposureStepDraftViewModel)draft.Steps[0];
        Assert.Equal("Turbo (not available)", row.Acquisition.ReadoutChoices[row.Acquisition.ReadoutIndex]); // needs repair, not replaced
    }

    [Fact]
    public async Task TheCameraDefaults_AreTakenIntoAccountWhenCheckingAnExposure()
    {
        await using var host = await CreateHost(connect: true);
        host.AcquisitionDefaults.Set(Main, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(900) });
        var draft = CreateDraft(host);
        draft.Replace([new ExposureStepDraft(Guid.NewGuid(), Main, 10)], null);

        Assert.False(draft.IsValid);
        Assert.Contains(draft.ValidationErrors, e => e.Contains("(the camera default)", StringComparison.Ordinal));

        host.AcquisitionDefaults.Set(Main, new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(50) });
        draft.RefreshDevices();

        Assert.True(draft.IsValid);
    }

    [Fact]
    public async Task TheDraftIsNotChangedByWhatTheCameraDoes_ConnectingOrReconnectingNeverEditsIt()
    {
        await using var host = await CreateHost();
        var draft = CreateDraft(host);
        var intent = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(30), BinX = 2, BinY = 2, ReadoutMode = "Slow" };
        draft.Replace([new ExposureStepDraft(Guid.NewGuid(), Main, 10) { Acquisition = intent }], null);
        var camera = (ICamera)host.DeviceRegistry.GetAll().First(d => d.Id == Main);

        var before = draft.Snapshot();
        await camera.ConnectAsync();
        draft.RefreshDevices();
        await camera.DisconnectAsync();
        await camera.ConnectAsync();
        draft.RefreshDevices();

        Assert.Equal(intent, ((ExposureStepDraft)before[0]).Acquisition);
        Assert.Equal(intent, ((ExposureStepDraft)draft.Snapshot()[0]).Acquisition);
    }

    [Fact]
    public async Task ARepeat_KeepsTheSettingsOfItsExposureForEveryIteration()
    {
        await using var host = await CreateHost(connect: true);
        var draft = CreateDraft(host);
        var intent = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(30) };
        draft.Replace([new RepeatStepDraft(Guid.NewGuid(), 3, [new ExposureStepDraft(Guid.NewGuid(), Main, 1) { Acquisition = intent }])], null);

        var built = draft.Build();

        var repeat = Assert.IsAssignableFrom<Sidera.Core.Sequencing.RepeatStep>(built.Sequence.Steps.Single());
        Assert.Equal(3, repeat.Count);
        var inner = built.Steps[0].Children![0];
        Assert.Equal(intent, ((CameraExposureAction)inner.Step).Intent);
    }

    // ---- The exposure of a rig

    [Fact]
    public async Task TheExposureOfARigTrack_HasTheSameAcquisition_ForTheCameraOfTheRig()
    {
        await using var host = await CreateHost(withRigs: true, connect: true);
        var shared = new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId);
        var context = new SequenceDraftContext(host.RigRegistry, shared, AcquisitionDefaults: host.AcquisitionDefaults);
        var intent = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(25), BinX = 2, BinY = 2 };
        var block = new MultiRigStepDraft(
            Guid.NewGuid(),
            [
                new RigTrackDraft(Guid.NewGuid(), DemoSetup.MainRigId, [new RigExposureStepDraft(Guid.NewGuid(), 60) { Acquisition = intent }]),
                new RigTrackDraft(Guid.NewGuid(), DemoSetup.WideRigId, [new RigExposureStepDraft(Guid.NewGuid(), 30) { Acquisition = new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(0) } }]),
            ]);

        var validation = SequenceDraftBuilder.Validate(host.DeviceRegistry, [block], context);
        var built = SequenceDraftBuilder.Build(host.DeviceRegistry, [block], context);

        Assert.True(validation.IsValid, string.Join(" ", validation.StepProblems.SelectMany(p => p.Value)));
        var tracks = built.Steps[0].Children!;
        var mainAction = (CameraExposureAction)tracks[0].Children![0].Step;
        var wideAction = (CameraExposureAction)tracks[1].Children![0].Step;
        Assert.Equal(intent, mainAction.Intent);
        Assert.Equal(AcquisitionLevel.OfNumber(0), wideAction.Intent.Gain);
        Assert.Equal(DemoSetup.MainCameraId, mainAction.CameraId);
        Assert.Equal(DemoSetup.WideCameraId, wideAction.CameraId);
        Assert.Contains("Gain 25", tracks[0].Children![0].Description.Summary);

        var bad = block with { Tracks = [block.Tracks[0] with { Steps = [new RigExposureStepDraft(Guid.NewGuid(), 60) { Acquisition = new AcquisitionIntent { BinX = 9, BinY = 9 } }] }, block.Tracks[1]] };
        Assert.False(SequenceDraftBuilder.Validate(host.DeviceRegistry, [bad], context).IsValid);
    }

    [Fact]
    public async Task TheEditorOfARigExposure_FollowsTheCameraOfTheRigOfItsTrack()
    {
        await using var host = await CreateHost(withRigs: true, connect: true);
        var draft = CreateDraft(host);
        draft.Replace(
            [new MultiRigStepDraft(Guid.NewGuid(), [new RigTrackDraft(Guid.NewGuid(), DemoSetup.MainRigId, [new RigExposureStepDraft(Guid.NewGuid(), 60)])])],
            new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId));

        var exposure = draft.Rows.OfType<RigExposureStepDraftViewModel>().Single();

        Assert.True(exposure.Acquisition.IsVerifiable);
        Assert.True(exposure.Acquisition.ShowBinning);
    }

    // ---- Several cameras at once, each its own settings

    [Fact]
    public async Task EachCameraOfATwoRigSession_RunsAtTheSameTimeWithItsOwnSettings()
    {
        await using var host = await CreateHost(withRigs: true, connect: true);
        var main = new CameraExposureAction(
            host.DeviceRegistry, DemoSetup.MainCameraId, TimeSpan.FromMilliseconds(200),
            new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(100), BinX = 1, BinY = 1 }, host.AcquisitionDefaults);
        var wide = new CameraExposureAction(
            host.DeviceRegistry, DemoSetup.WideCameraId, TimeSpan.FromMilliseconds(200),
            new AcquisitionIntent { Gain = AcquisitionLevel.OfNumber(0), BinX = 2, BinY = 2 }, host.AcquisitionDefaults);

        var results = await Task.WhenAll(
            main.ExecuteAsync(NoContext.Instance, CancellationToken.None),
            wide.ExecuteAsync(NoContext.Instance, CancellationToken.None));

        Assert.Equal(800, ((CameraFrame)results[0].Payload!).Width);
        Assert.Equal(400, ((CameraFrame)results[1].Payload!).Width);
    }
}

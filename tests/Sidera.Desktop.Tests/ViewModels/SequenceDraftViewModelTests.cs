using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

public class SequenceDraftViewModelTests
{
    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host, bool initialSteps = false)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults, initialSteps ? defaults.InitialSteps() : null);
    }

    private static T Add<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddStepCommand.Execute(kind);
        return Assert.IsType<T>(draft.SelectedStep);
    }

    private static readonly SequenceStepKind[] AllKinds = Enum.GetValues<SequenceStepKind>();

    public static TheoryData<SequenceStepKind, Type> KindsAndViewModels => new()
    {
        { SequenceStepKind.Exposure, typeof(ExposureStepDraftViewModel) },
        { SequenceStepKind.Delay, typeof(DelayStepDraftViewModel) },
        { SequenceStepKind.Slew, typeof(SlewStepDraftViewModel) },
        { SequenceStepKind.StartGuiding, typeof(StartGuidingStepDraftViewModel) },
        { SequenceStepKind.StopGuiding, typeof(StopGuidingStepDraftViewModel) },
        { SequenceStepKind.Dither, typeof(DitherStepDraftViewModel) },
        { SequenceStepKind.Repeat, typeof(RepeatStepDraftViewModel) },
    };

    // Draft operations

    [Fact]
    public async Task EmptyDraft_HasNoStepsAndNoSelection_IsInvalid_AndCannotBeBuilt()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        Assert.True(draft.IsEmpty);
        Assert.Empty(draft.Steps);
        Assert.Null(draft.SelectedStep);
        Assert.False(draft.IsValid);
        Assert.Equal(["The sequence has no steps."], draft.ValidationErrors);
        Assert.Throws<SequenceConfigurationException>(() => draft.Build());
        Assert.Empty(draft.Snapshot());
    }

    [Theory]
    [MemberData(nameof(KindsAndViewModels))]
    public async Task AddStep_AppendsAStepOfTheKind_SelectsIt_AndNumbersIt(SequenceStepKind kind, Type viewModel)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        draft.AddStepCommand.Execute(kind);

        Assert.Equal(2, draft.Steps.Count);
        Assert.IsType(viewModel, draft.Steps[1]);
        Assert.Equal(kind, draft.Steps[1].Kind);
        Assert.Same(draft.Steps[1], draft.SelectedStep);
        Assert.Equal([1, 2], draft.Steps.Select(s => s.Number));
        Assert.False(draft.IsEmpty);
    }

    [Fact]
    public async Task EveryKindCanBeAdded_AndANewDraftOfAllOfThemIsValidOnTheDemoEquipment()
    {
        await using var host = CreateHost();
        host.ConfigurePlateSolver(new Sidera.Astap.AstapPlateSolver(() => new()));
        // An autofocus needs a rig and something to measure focus with.
        var draft = new SequenceDraftViewModel(
            host.DeviceRegistry, SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry), null,
            rigs: host.RigRegistry, focusMetrics: host.FocusMetrics, plateSolving: host.PlateSolving);

        // Start, Dither, Stop is an order that is valid: every kind once, guiding stopped last.
        foreach (var kind in new[]
                 {
                     SequenceStepKind.StartGuiding, SequenceStepKind.Slew, SequenceStepKind.Exposure,
                     SequenceStepKind.Dither, SequenceStepKind.Delay, SequenceStepKind.MoveFocuser,
                     SequenceStepKind.ChangeFilter, SequenceStepKind.Autofocus, SequenceStepKind.StopGuiding, SequenceStepKind.PlateSolve,
                 })
        {
            draft.AddStepCommand.Execute(kind);
        }

        Assert.Equal(
            AllKinds.Where(k => k is not (SequenceStepKind.Repeat or SequenceStepKind.RigExposure or SequenceStepKind.MultiRig or SequenceStepKind.RigTrack
                or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus)).OrderBy(k => k),
            draft.Steps.Select(s => s.Kind).OrderBy(k => k));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal(10, draft.Build().Sequence.Steps.Count);
    }

    [Fact]
    public async Task AddedStepsKeepTheirIdsAndGetUniqueOnes()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var first = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var second = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        draft.MoveStepUpCommand.Execute(null);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal([second.Id, first.Id], draft.Snapshot().Select(s => s.Id));
    }

    [Fact]
    public async Task RemoveStep_RemovesOnlyTheSelectedStep_AndSelectsTheNextOne()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var a = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var b = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var c = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);
        draft.SelectedStep = b;

        draft.RemoveStepCommand.Execute(null);

        Assert.Equal([a, c], draft.Steps);
        Assert.Same(c, draft.SelectedStep);
        Assert.Equal([1, 2], draft.Steps.Select(s => s.Number));
    }

    [Fact]
    public async Task RemoveStep_OfTheLastStep_SelectsThePreviousOne_AndOfTheOnlyStep_EmptiesTheDraft()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var a = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        draft.RemoveStepCommand.Execute(null);
        Assert.Same(a, draft.SelectedStep);

        draft.RemoveStepCommand.Execute(null);
        Assert.True(draft.IsEmpty);
        Assert.Null(draft.SelectedStep);
        Assert.False(draft.RemoveStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task RemoveStep_IsUnavailableWithoutASelection()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        draft.SelectedStep = null;

        Assert.False(draft.RemoveStepCommand.CanExecute(null));
        Assert.False(draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(draft.MoveStepDownCommand.CanExecute(null));
        Assert.True(draft.AddStepCommand.CanExecute(SequenceStepKind.Exposure));
    }

    [Fact]
    public async Task MoveStep_ReordersTheDraft_KeepsTheSelection_AndRenumbers()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var a = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var b = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var c = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);

        draft.SelectedStep = c;
        draft.MoveStepUpCommand.Execute(null);
        Assert.Equal([a, c, b], draft.Steps);
        Assert.Same(c, draft.SelectedStep);
        Assert.Equal(2, c.Number);

        draft.MoveStepUpCommand.Execute(null);
        Assert.Equal([c, a, b], draft.Steps);

        draft.MoveStepDownCommand.Execute(null);
        draft.MoveStepDownCommand.Execute(null);
        Assert.Equal([a, b, c], draft.Steps);
        Assert.Same(c, draft.SelectedStep);
        Assert.Equal([1, 2, 3], draft.Steps.Select(s => s.Number));
    }

    [Fact]
    public async Task MoveStep_IsUnavailableAtTheBoundaries()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var a = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        Assert.False(draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(draft.MoveStepDownCommand.CanExecute(null)); // the only step

        var b = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var c = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        draft.SelectedStep = a;
        Assert.False(draft.MoveStepUpCommand.CanExecute(null));
        Assert.True(draft.MoveStepDownCommand.CanExecute(null));

        draft.SelectedStep = b;
        Assert.True(draft.MoveStepUpCommand.CanExecute(null));
        Assert.True(draft.MoveStepDownCommand.CanExecute(null));

        draft.SelectedStep = c;
        Assert.True(draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(draft.MoveStepDownCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_AnnounceTheirAvailabilityWhenSelectionAndListChange()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var announcements = 0;
        draft.RemoveStepCommand.CanExecuteChanged += (_, _) => announcements++;

        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var afterAdd = announcements;
        draft.SelectedStep = null;

        Assert.True(afterAdd > 0);
        Assert.True(announcements > afterAdd);
    }

    [Fact]
    public async Task Selection_FollowsAddAndStartsOnTheFirstStepOfAnInitialDraft()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, initialSteps: true);

        Assert.Same(draft.Steps[0], draft.SelectedStep);

        var added = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        Assert.Same(added, draft.SelectedStep);
        Assert.Same(added, draft.Steps[^1]);
    }

    // Initial draft and defaults

    [Fact]
    public async Task TheInitialDraft_IsTheDemoAsALinearSequence_AndIsValid()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, initialSteps: true);

        Assert.Equal(
            new[]
            {
                SequenceStepKind.StartGuiding, SequenceStepKind.Slew, SequenceStepKind.Exposure, SequenceStepKind.Dither,
                SequenceStepKind.Exposure, SequenceStepKind.Dither, SequenceStepKind.Exposure, SequenceStepKind.StopGuiding,
            },
            draft.Steps.Select(s => s.Kind));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal(
            new[] { "Start Guiding", "Slew", "Exposure", "Dither", "Exposure", "Dither", "Exposure", "Stop Guiding" },
            draft.Steps.Select(s => s.Title));
        Assert.Equal(8, draft.Build().Sequence.Steps.Count);
    }

    [Fact]
    public async Task NewExposure_GetsTheDemoCameraAndTheDefaultDuration()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        Assert.Equal("2", exposure.ExposureText);
        Assert.Equal("Main Camera · 2 s · Camera defaults", exposure.Summary);
        Assert.False(exposure.HasProblems);
    }

    [Fact]
    public async Task NewSlew_GetsTheMountAndTheDemoTarget()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var slew = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);

        Assert.Equal(new DeviceId("mount.eq6"), slew.Mount.SelectedId);
        Assert.Equal("5.588", slew.RightAscensionText);
        Assert.Equal("-5.39", slew.DeclinationText);
        Assert.Equal("RA 5.588 h · Dec -5.39°", slew.Summary);
    }

    [Fact]
    public async Task NewGuidingSteps_GetTheGuider()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var start = Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);
        var stop = Add<StopGuidingStepDraftViewModel>(draft, SequenceStepKind.StopGuiding);

        Assert.Equal(new DeviceId("guider.main"), start.Guider.SelectedId);
        Assert.Equal(new DeviceId("guider.main"), stop.Guider.SelectedId);
        Assert.Equal("Main Guider", start.Summary);
    }

    [Fact]
    public async Task NewDelay_GetsTheDefaultDuration()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var delay = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        Assert.Equal("2", delay.DurationText);
        Assert.Equal("2 s", delay.Summary);
    }

    [Fact]
    public async Task NewDither_GetsCompatibleDevicesAndTheDefaultSettleValues()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        Assert.Equal(new DeviceId("guider.main"), dither.Guider.SelectedId);
        Assert.Equal(new DeviceId("mount.eq6"), dither.Mount.SelectedId);
        Assert.Equal(new DeviceId("camera.main"), dither.Camera.SelectedId);
        Assert.Equal(
            new[] { "1.5", "0.5", "1", "10" },
            new[] { dither.AmplitudeText, dither.SettleThresholdText, dither.SettleStableText, dither.SettleTimeoutText });
        Assert.Equal("1.5 px · settle ≤ 0.5 px for 1 s", dither.Summary);
    }

    [Fact]
    public async Task Defaults_PickTheFirstDeviceOfAKindWhenTheDemoDevicesAreNotRegistered()
    {
        await using var host = new SideraRuntimeHost();
        host.AddSimulatedCamera(new DeviceId("camera.b"), "Camera B");
        host.AddSimulatedCamera(new DeviceId("camera.a"), "Camera A");
        var draft = CreateDraft(host);

        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var slew = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);

        Assert.Equal(new DeviceId("camera.a"), exposure.Camera.SelectedId);
        Assert.Equal(["camera.a", "camera.b"], exposure.Camera.Options.Select(o => o.IdText));
        Assert.Null(slew.Mount.SelectedId);
        Assert.Empty(slew.Mount.Options);
    }

    [Fact]
    public async Task Pickers_OfferOnlyCompatibleDevices_WithTheFriendlyNameFirstAndTheIdSecond()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var slew = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);
        var start = Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);

        Assert.Equal([("Main Camera", "camera.main")], exposure.Camera.Options.Select(o => (o.Name, o.IdText)));
        Assert.Equal([("EQ6 Mount", "mount.eq6")], slew.Mount.Options.Select(o => (o.Name, o.IdText)));
        Assert.Equal([("Main Guider", "guider.main")], start.Guider.Options.Select(o => (o.Name, o.IdText)));
        Assert.Equal(["guider.main"], dither.Guider.Options.Select(o => o.IdText));
        Assert.Equal(["mount.eq6"], dither.Mount.Options.Select(o => o.IdText));
        Assert.Equal(["camera.main"], dither.Camera.Options.Select(o => o.IdText));
    }

    // Parameter editing

    [Fact]
    public async Task EditingAParameter_UpdatesTheSummaryAndTheSnapshotAtOnce()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var changes = 0;
        draft.Changed += (_, _) => changes++;

        exposure.ExposureText = "300";

        Assert.Equal("Main Camera · 300 s · Camera defaults", exposure.Summary);
        Assert.Equal(300, Assert.IsType<ExposureStepDraft>(Assert.Single(draft.Snapshot())).Seconds);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task PickingAnotherDevice_UpdatesTheDraft()
    {
        await using var host = CreateHost();
        host.AddSimulatedCamera(new DeviceId("camera.second"), "Second Camera");
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        exposure.Camera.Selected = exposure.Camera.Options.Single(o => o.IdText == "camera.second");

        Assert.Equal("Second Camera · 2 s · Camera defaults", exposure.Summary);
        Assert.Equal(new DeviceId("camera.second"), Assert.IsType<ExposureStepDraft>(draft.Snapshot()[0]).CameraId);
    }

    [Fact]
    public async Task Numbers_AreAcceptedWithACommaOrADot()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var delay = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        delay.DurationText = "1.5";
        Assert.Equal(1.5, Assert.IsType<DelayStepDraft>(draft.Snapshot()[0]).Seconds);
        Assert.True(draft.IsValid);

        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            delay.DurationText = "2,5";
            Assert.Equal(2.5, Assert.IsType<DelayStepDraft>(draft.Snapshot()[0]).Seconds);
            Assert.True(draft.IsValid);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    // Validation

    [Fact]
    public async Task WithoutRegisteredDevices_EveryDeviceStepIsInvalid_AndNamesWhatIsMissing()
    {
        await using var host = new SideraRuntimeHost();
        var draft = CreateDraft(host);

        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var slew = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);
        var start = Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);
        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        Assert.Equal(["No camera selected."], exposure.Problems);
        Assert.Equal(["No mount selected."], slew.Problems);
        Assert.Equal(["No guider selected."], start.Problems);
        Assert.Equal(["No guider selected.", "No mount selected.", "No camera selected."], dither.Problems);
        Assert.False(draft.IsValid);
        Assert.Contains("Step 1 (Exposure): No camera selected.", draft.ValidationErrors);
    }

    [Fact]
    public async Task ADeviceThatDisappears_MakesTheStepInvalid_KeepsTheSelectionVisible_AndDisablesRunByValidity()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        Assert.True(draft.IsValid);

        host.DeviceRegistry.Unregister(new DeviceId("camera.main"));
        draft.Revalidate();

        Assert.False(draft.IsValid);
        Assert.Equal(["The camera 'camera.main' is not available."], exposure.Problems);
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);

        // The picker still shows what the step refers to, marked as missing, and does not pick a replacement.
        draft.RefreshDevices();
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        var shown = Assert.IsType<DeviceOption>(exposure.Camera.Selected);
        Assert.True(shown.IsMissing);
        Assert.Equal("camera.main (not available)", shown.Name);
        Assert.Throws<SequenceConfigurationException>(() => draft.Build());
    }

    [Fact]
    public async Task ADeviceThatAppearsLater_BecomesPickable_WithoutChangingTheSelection()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        var changes = 0;
        exposure.Camera.Changed += (_, _) => changes++;

        host.AddSimulatedCamera(new DeviceId("camera.second"), "Second Camera");
        draft.RefreshDevices();

        Assert.Equal(["camera.main", "camera.second"], exposure.Camera.Options.Select(o => o.IdText));
        Assert.Equal(new DeviceId("camera.main"), exposure.Camera.SelectedId);
        Assert.Equal(0, changes); // refreshing the list is not a choice
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1e")]
    public async Task InvalidExposure_MarksTheStep_AndOnlyThatStep(string text)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var good = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        exposure.ExposureText = text;

        Assert.True(exposure.HasProblems);
        Assert.False(good.HasProblems);
        Assert.False(draft.IsValid);
        Assert.Throws<SequenceConfigurationException>(() => draft.Build());
    }

    [Fact]
    public async Task UnreadableText_IsReportedAsSuch_NeverReplacedByANumber()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        exposure.ExposureText = "abc";

        Assert.Equal(["Exposure must be a number of seconds."], exposure.Problems);
        Assert.Equal("abc", exposure.ExposureText);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("x")]
    public async Task InvalidDelay_MarksTheStep(string text)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var delay = Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);

        delay.DurationText = text;

        Assert.True(delay.HasProblems);
        Assert.False(draft.IsValid);
    }

    [Theory]
    [InlineData("24.5", "0")]
    [InlineData("-1", "0")]
    [InlineData("5", "95")]
    [InlineData("5", "-91")]
    [InlineData("a", "0")]
    [InlineData("5", "b")]
    public async Task InvalidCoordinates_MarkTheSlewStep(string ra, string dec)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var slew = Add<SlewStepDraftViewModel>(draft, SequenceStepKind.Slew);

        slew.RightAscensionText = ra;
        slew.DeclinationText = dec;

        Assert.True(slew.HasProblems);
        Assert.False(draft.IsValid);
    }

    [Theory]
    [InlineData(nameof(DitherStepDraftViewModel.AmplitudeText), "0")]
    [InlineData(nameof(DitherStepDraftViewModel.AmplitudeText), "abc")]
    [InlineData(nameof(DitherStepDraftViewModel.SettleThresholdText), "0")]
    [InlineData(nameof(DitherStepDraftViewModel.SettleThresholdText), "")]
    [InlineData(nameof(DitherStepDraftViewModel.SettleStableText), "-1")]
    [InlineData(nameof(DitherStepDraftViewModel.SettleTimeoutText), "0")]
    [InlineData(nameof(DitherStepDraftViewModel.SettleTimeoutText), "1")] // not longer than the stable time of 1 s
    public async Task InvalidDitherAndSettleValues_MarkTheDitherStep(string field, string text)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        typeof(DitherStepDraftViewModel).GetProperty(field)!.SetValue(dither, text);

        Assert.True(dither.HasProblems);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public async Task AnUnreadableSettleField_ProducesOnlyItsOwnMessage()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        dither.SettleTimeoutText = "soon";
        Assert.Equal(["Settle timeout must be a number of seconds."], dither.Problems);

        dither.SettleTimeoutText = "10";
        dither.SettleStableText = "later";
        Assert.Equal(["Settle stable time must be a number of seconds."], dither.Problems);
    }

    [Fact]
    public async Task OneInvalidStep_MakesTheWholeDraftInvalid_UntilItIsFixed()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, initialSteps: true);
        Assert.True(draft.IsValid);
        var exposure = draft.Steps.OfType<ExposureStepDraftViewModel>().First();

        exposure.ExposureText = "0";

        Assert.False(draft.IsValid);
        Assert.Equal(["Step 3 (Exposure): Exposure must be greater than 0 s."], draft.ValidationErrors);
        Assert.Single(draft.Steps, s => s.HasProblems);

        exposure.ExposureText = "30";

        Assert.True(draft.IsValid);
        Assert.Empty(draft.ValidationErrors);
        Assert.All(draft.Steps, s => Assert.False(s.HasProblems));
    }

    [Fact]
    public async Task ProblemsAreFoundByPositionInTheSequence_AndMoveWithTheStep()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var bad = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        bad.ExposureText = "0";
        Assert.Equal(["Step 2 (Exposure): Exposure must be greater than 0 s."], draft.ValidationErrors);

        draft.MoveStepUpCommand.Execute(null);

        Assert.Equal(["Step 1 (Exposure): Exposure must be greater than 0 s."], draft.ValidationErrors);
    }

    [Fact]
    public async Task ReorderingCanMakeTheGuidingOrderInvalid_AndTheProblemIsOnTheRowThatIsAffected()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);
        var stop = Add<StopGuidingStepDraftViewModel>(draft, SequenceStepKind.StopGuiding);
        var dither = Add<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);

        Assert.Equal(["Dither needs guiding, but step 2 stopped it."], dither.Problems);
        Assert.False(stop.HasProblems);
        Assert.False(draft.IsValid);

        draft.SelectedStep = dither;
        draft.MoveStepUpCommand.Execute(null);

        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task ARowWithAProblem_ShowsTheProblemInsteadOfTheSummary()
    {
        await using var host = new SideraRuntimeHost();
        var draft = CreateDraft(host);

        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);

        Assert.Equal("No camera selected.", exposure.FirstProblem);
        Assert.True(exposure.HasProblems);
    }

    // The editing lock

    [Fact]
    public async Task WhenNotEditable_NoCommandThatChangesTheListIsAvailable()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, initialSteps: true);
        draft.SelectedStep = draft.Steps[3];
        Assert.True(draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.True(draft.RemoveStepCommand.CanExecute(null));
        Assert.True(draft.MoveStepUpCommand.CanExecute(null));
        Assert.True(draft.MoveStepDownCommand.CanExecute(null));

        draft.IsEditable = false;

        Assert.False(draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.False(draft.RemoveStepCommand.CanExecute(null));
        Assert.False(draft.MoveStepUpCommand.CanExecute(null));
        Assert.False(draft.MoveStepDownCommand.CanExecute(null));

        draft.IsEditable = true;

        Assert.True(draft.AddStepCommand.CanExecute(SequenceStepKind.Delay));
        Assert.True(draft.RemoveStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task Build_ValidatesOnItsOwn_EvenWhenTheEditorStateWasNotRefreshed()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        Assert.True(draft.IsValid);

        host.DeviceRegistry.Unregister(new DeviceId("camera.main")); // no Revalidate call

        var error = Assert.Throws<SequenceConfigurationException>(() => draft.Build());
        Assert.Contains("Step 1 (Exposure): The camera 'camera.main' is not available.", error.Problems);
    }
}

using Sidera.Core.Devices;
using Sidera.Core.Focusing;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;
using Sidera.Runtime.Focusing;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>The autofocus policy of a Rig Track in the editor, and what the sequencer says about the autofocus it makes.</summary>
public class AutofocusPolicyEditingTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        DemoSetup.AddDemoRigs(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        var shared = new SharedEquipmentDraft(DemoSetup.MountId, DemoSetup.GuiderId);
        return new SequenceDraftViewModel(
            host.DeviceRegistry, defaults, null, clipboard, host.RigRegistry, shared, host.FocusMetrics, host.EventBus);
    }

    // A block of two tracks (Main and Wide) with an exposure each; the main track is selected.
    private static (MultiRigStepDraftViewModel Block, RigTrackDraftViewModel Main, RigTrackDraftViewModel Wide) AddBlock(
        SequenceDraftViewModel draft)
    {
        draft.AddStepCommand.Execute(SequenceStepKind.MultiRig);
        var block = Assert.IsType<MultiRigStepDraftViewModel>(draft.SelectedStep);
        var tracks = new List<RigTrackDraftViewModel>();
        foreach (var rig in new[] { "rig.main", "rig.wide" })
        {
            draft.SelectedStep = block;
            draft.AddTrackCommand.Execute(null);
            var track = Assert.IsType<RigTrackDraftViewModel>(draft.SelectedStep);
            track.Rig.Selected = track.Rig.Options.Single(o => o.IdText == rig);
            draft.AddTrackStepCommand.Execute(SequenceStepKind.Exposure);
            tracks.Add(track);
        }

        draft.SelectedStep = tracks[0];
        return (block, tracks[0], tracks[1]);
    }

    private static RigAutofocusPolicyDraft PolicyOf(SequenceDraftViewModel draft, int track = 0) =>
        draft.Snapshot().OfType<MultiRigStepDraft>().Single().Tracks[track].AutofocusPolicy!;

    // The fields

    [Fact]
    public async Task ANewTrack_HasThePolicyOff_WithTheDefaultSettings_AndNothingIsInjectedIntoTheSequence()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);

        var (_, main, _) = AddBlock(draft);

        Assert.Equal((false, false, false), (main.AutofocusEnabled, main.AutofocusAtStart, main.AutofocusAfterFilterChange));
        Assert.Equal(("1", "400", "7"), (main.AutofocusExposureText, main.AutofocusStepSizeText, main.AutofocusSamplesText));
        Assert.Equal(RigAutofocusPolicyDraft.Default, PolicyOf(draft));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal("Main Camera", main.Summary);
    }

    [Fact]
    public async Task EachTrackHasItsOwnFields()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, wide) = AddBlock(draft);

        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;

        Assert.False(wide.AutofocusEnabled);
        Assert.False(wide.AutofocusAtStart);
        Assert.True(PolicyOf(draft, 0).AtTrackStart);
        Assert.False(PolicyOf(draft, 1).AtTrackStart);
    }

    [Fact]
    public async Task ThePolicyIsReadFromTheFields_AsADraft()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);

        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        main.AutofocusAfterFilterChange = true;
        main.AutofocusExposureText = "0.5";
        main.AutofocusStepSizeText = "300";
        main.AutofocusSamplesText = "9";

        Assert.Equal(new RigAutofocusPolicyDraft(true, true, true, 0.5, 300, 9), PolicyOf(draft));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task TheTrackSummary_SaysWhenItFocuses_OnlyWhileThePolicyIsOn()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);

        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        Assert.Equal("Main Camera\nAutofocus: track start", main.Summary);

        main.AutofocusAfterFilterChange = true;
        Assert.Equal("Main Camera\nAutofocus: track start + filter change", main.Summary);

        main.AutofocusEnabled = false;
        Assert.Equal("Main Camera", main.Summary);
    }

    // Validation in the editor

    [Fact]
    public async Task EnabledWithoutATrigger_IsReportedOnTheTrack_AndTheDraftIsNotRunnable()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);

        main.AutofocusEnabled = true;

        Assert.Contains("Enable at least one Autofocus trigger.", main.Problems);
        Assert.False(draft.IsValid);

        main.AutofocusAtStart = true;

        Assert.DoesNotContain(main.Problems, p => p.Contains("trigger", StringComparison.Ordinal));
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Theory]
    [InlineData("0", "400", "7", "Autofocus exposure must be greater than 0 s.")]
    [InlineData("abc", "400", "7", "Autofocus exposure must be a number of seconds.")]
    [InlineData("1", "0", "7", "Autofocus step size must be greater than 0.")]
    [InlineData("1", "x", "7", "Autofocus step size must be a whole number.")]
    [InlineData("1", "400", "6", "Autofocus samples must be an odd number between 5 and 21.")]
    [InlineData("1", "400", "7.5", "Autofocus samples must be a whole number.")]
    public async Task AWrongSetting_IsReportedOnTheTrack_WhileThePolicyIsOn(string exposure, string step, string samples, string expected)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;

        main.AutofocusExposureText = exposure;
        main.AutofocusStepSizeText = step;
        main.AutofocusSamplesText = samples;

        Assert.Contains(expected, main.Problems);
        Assert.False(draft.IsValid);
    }

    [Fact]
    public async Task WhileThePolicyIsOff_ItsFieldsAreKept_AndNotReported_EvenWhenWrong()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        main.AutofocusStepSizeText = "250";

        main.AutofocusEnabled = false;
        main.AutofocusExposureText = "abc";
        main.AutofocusSamplesText = "4";

        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
        Assert.Equal("250", main.AutofocusStepSizeText);
        Assert.True(main.AutofocusAtStart);
        var policy = PolicyOf(draft);
        Assert.Equal((false, true, 250), (policy.Enabled, policy.AtTrackStart, policy.StepSize));
        Assert.Equal(RigAutofocusPolicyDraft.Default.ExposureSeconds, policy.ExposureSeconds); // what is no number reads as the default
    }

    [Fact]
    public async Task AFilterTrigger_NeedsAFilterWheelOnTheRig()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, _, wide) = AddBlock(draft); // the wide rig has a focuser and no filter wheel
        wide.AutofocusEnabled = true;

        wide.AutofocusAfterFilterChange = true;

        Assert.Equal(
            ["The rig 'rig.wide' has no filter wheel, so Autofocus cannot follow a filter change."], wide.Problems);

        wide.AutofocusAfterFilterChange = false;
        wide.AutofocusAtStart = true;

        Assert.Empty(wide.Problems);
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task ARigWithoutAFocuser_CannotFocusAtTheStart_AndTheTrackSaysSo()
    {
        await using var host = CreateHost();
        host.AddRig(new Rig(new RigId("rig.bare"), "Bare Rig", DemoSetup.NarrowCameraId, new OpticalTrain(250, 60, 3.76, 23.5, 15.7, 6248, 4176)));
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        Assert.Empty(main.Problems);

        main.Rig.Selected = main.Rig.Options.Single(o => o.IdText == "rig.bare");

        Assert.Equal(["The rig 'rig.bare' has no focuser."], main.Problems);
    }

    [Fact]
    public async Task TheFocuserRange_LimitsTheStepSize_OnTheTrack()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, _, wide) = AddBlock(draft); // 0 to 12000
        wide.AutofocusEnabled = true;
        wide.AutofocusAtStart = true;

        wide.AutofocusStepSizeText = "3500";

        Assert.Contains(wide.Problems, p => p.Contains("does not have enough travel", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARigThatChanges_ChangesWhatThePolicyIsCheckedAgainst()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, _, wide) = AddBlock(draft);
        wide.AutofocusEnabled = true;
        wide.AutofocusAfterFilterChange = true;
        Assert.False(draft.IsValid);

        wide.Rig.Selected = wide.Rig.Options.Single(o => o.IdText == "rig.narrow"); // has a filter wheel

        Assert.Empty(wide.Problems);
    }

    // Dirty state, copy, lock

    [Fact]
    public async Task EveryPolicyField_IsAModification()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        main.AutofocusAfterFilterChange = true;
        main.AutofocusExposureText = "2";
        main.AutofocusStepSizeText = "300";
        main.AutofocusSamplesText = "9";

        Assert.Equal(6, modifications);
    }

    [Fact]
    public async Task ReplacingTheSteps_IsNotAModification_AndRestoresThePolicy()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAfterFilterChange = true;
        main.AutofocusSamplesText = "9";
        var snapshot = draft.Snapshot();
        var modifications = 0;
        draft.Modified += (_, _) => modifications++;

        draft.ReplaceSteps(snapshot);

        Assert.Equal(0, modifications);
        var again = draft.Rows.OfType<RigTrackDraftViewModel>().First();
        Assert.Equal((true, false, true, "9"), (again.AutofocusEnabled, again.AutofocusAtStart, again.AutofocusAfterFilterChange, again.AutofocusSamplesText));
    }

    [Fact]
    public async Task ADuplicatedBlock_KeepsThePolicyOfItsTracks_WithNewIds()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (block, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        main.AutofocusStepSizeText = "250";
        draft.SelectedStep = block;

        draft.DuplicateStepCommand.Execute(null);

        var blocks = draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal(2, blocks.Count);
        Assert.NotEqual(blocks[0].Tracks[0].Id, blocks[1].Tracks[0].Id);
        Assert.Equal(blocks[0].Tracks[0].AutofocusPolicy, blocks[1].Tracks[0].AutofocusPolicy);
        Assert.Equal(250, blocks[1].Tracks[0].AutofocusPolicy!.StepSize);
    }

    [Fact]
    public async Task ACopiedBlock_IsASnapshot_ItKeepsThePolicyAsItWasWhenCopied()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host, new SequenceStepClipboard());
        var (block, main, _) = AddBlock(draft);
        main.AutofocusEnabled = true;
        main.AutofocusAtStart = true;
        draft.SelectedStep = block;
        draft.CopyStepCommand.Execute(null);
        main.AutofocusAtStart = false; // later edits of the original do not reach the copy
        draft.SelectedStep = null;

        draft.PasteStepCommand.Execute(null);

        var blocks = draft.Snapshot().OfType<MultiRigStepDraft>().ToList();
        Assert.Equal([false, true], blocks.Select(b => b.Tracks[0].AutofocusPolicy!.AtTrackStart));
    }

    [Fact]
    public async Task WhileNotEditable_NoStepCanBeAdded_SoTheTrackIsLocked()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var (_, main, _) = AddBlock(draft);
        draft.SelectedStep = main;

        draft.IsEditable = false;

        Assert.False(draft.AddTrackStepCommand.CanExecute(SequenceStepKind.Autofocus));
        Assert.False(draft.AddTrackCommand.CanExecute(null));
    }

    // The sequencer

    private static readonly DemoOptions Fast = new()
    {
        GuiderStartDuration = TimeSpan.FromMilliseconds(20),
        GuiderStopDuration = TimeSpan.FromMilliseconds(20),
        SlewDuration = TimeSpan.FromMilliseconds(20),
        FocuserStepsPerSecond = 20000,
        FocuserMinimumMoveDuration = TimeSpan.FromMilliseconds(20),
        FilterWheelMoveDuration = TimeSpan.FromMilliseconds(100),
    };

    private sealed class App : IAsyncDisposable
    {
        public required SideraRuntimeHost Host { get; init; }
        public required MainViewModel Vm { get; init; }
        public SequenceDraftViewModel Draft => Vm.SequenceDraft;
        public SequencerViewModel Sequencer => Vm.Sequencer;

        public async ValueTask DisposeAsync()
        {
            Vm.Dispose();
            await Host.DisposeAsync();
        }
    }

    private static async Task<App> CreateApp()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host, Fast);
        DemoSetup.AddDemoRigs(host, Fast);
        foreach (var device in host.DeviceRegistry.GetAll())
        {
            await device.ConnectAsync();
        }

        var gate = new object();
        return new App { Host = host, Vm = new MainViewModel(host, action => { lock (gate) { action(); } }, Fast) };
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static RigTrackDraft MainTrack(RigAutofocusPolicyDraft? policy, params SequenceStepDraft[] steps) =>
        new(Guid.NewGuid(), new RigId("rig.main"), steps, policy);

    private static RigTrackDraft WideTrack() => new(
        Guid.NewGuid(), new RigId("rig.wide"), [new RepeatStepDraft(Guid.NewGuid(), 40, [new RigExposureStepDraft(Guid.NewGuid(), 0.1)])]);

    [Fact]
    public async Task AnAutomaticAutofocus_SaysWhyItRuns_EachTime_AndAnExplicitOneSaysItIsASequenceStep()
    {
        await using var app = await CreateApp();
        var policy = new RigAutofocusPolicyDraft(true, true, true, 0.1, 400, 7);
        var main = MainTrack(
            policy,
            new RigChangeFilterStepDraft(Guid.NewGuid(), 4),
            new RigExposureStepDraft(Guid.NewGuid(), 0.1),
            new RigAutofocusStepDraft(Guid.NewGuid(), 0.1, 400, 7)); // the user's own, at the end
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [main, WideTrack()])]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));
        var origins = new List<string?>();

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        while (!run.IsCompleted)
        {
            var origin = app.Sequencer.AutofocusStatuses.FirstOrDefault()?.Origin;
            if (origin is not null && (origins.Count == 0 || origins[^1] != origin))
            {
                origins.Add(origin);
            }

            await Task.Delay(2);
        }

        await run;
        Assert.Equal(
            ["Automatic · track start", "Automatic · after filter change", "Manual sequence step"],
            origins);
        var status = Assert.Single(app.Sequencer.AutofocusStatuses);
        Assert.Equal("AUTOFOCUS · MAIN", status.Title);
        Assert.Equal("Manual sequence step", status.Lines[0]);
        Assert.True(status.IsCompleted);
    }

    [Fact]
    public async Task WhileAnAutomaticAutofocusRuns_TheLinesStartWithItsOrigin_ThenTheSamples()
    {
        await using var app = await CreateApp();
        var policy = new RigAutofocusPolicyDraft(true, true, false, 0.15, 400, 7);
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [MainTrack(policy, new RigExposureStepDraft(Guid.NewGuid(), 0.1)), WideTrack()])]);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(
            () => app.Sequencer.AutofocusStatuses.FirstOrDefault() is { } s && s.Lines.Count >= 3 && s.Lines[1].StartsWith("Sample ", StringComparison.Ordinal),
            "a sample of the automatic autofocus");

        var lines = app.Sequencer.AutofocusStatuses.Single().Lines;
        Assert.Equal("Automatic · track start", lines[0]);
        Assert.StartsWith("Sample ", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("Position ", lines[2], StringComparison.Ordinal);
        await run.WaitAsync(Bound);
    }

    [Fact]
    public async Task TheEditorIsLocked_WhileAnAutomaticAutofocusRuns_Pauses_AndIsPaused_AndFreeAfterwards()
    {
        await using var app = await CreateApp();
        var policy = new RigAutofocusPolicyDraft(true, true, false, 0.15, 400, 7);
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [MainTrack(policy, new RigExposureStepDraft(Guid.NewGuid(), 0.1)), WideTrack()])]);

        var run = app.Sequencer.RunCommand.ExecuteAsync(null);
        await WaitUntil(() => app.Sequencer.AutofocusStatuses.FirstOrDefault() is { IsActive: true }, "the automatic autofocus");
        Assert.False(app.Draft.IsEditable);

        app.Sequencer.PauseCommand.Execute(null);
        Assert.Equal(SequenceState.Pausing, app.Sequencer.State);
        Assert.False(app.Draft.IsEditable);

        await WaitUntil(() => app.Sequencer.State == SequenceState.Paused, "paused");
        Assert.False(app.Draft.IsEditable);
        Assert.True(app.Sequencer.AutofocusStatuses.Single().IsCompleted); // the whole run was done first

        app.Sequencer.ResumeCommand.Execute(null);
        await run.WaitAsync(Bound);
        Assert.True(app.Draft.IsEditable);
    }

    [Fact]
    public async Task TheRowsOfARunWithAPolicy_AreTheUsersRowsOnly_AndAllDone()
    {
        await using var app = await CreateApp();
        var change = new RigChangeFilterStepDraft(Guid.NewGuid(), 4);
        var exposure = new RigExposureStepDraft(Guid.NewGuid(), 0.1);
        var main = MainTrack(new RigAutofocusPolicyDraft(true, true, true, 0.1, 400, 7), change, exposure);
        var wide = WideTrack();
        var policy = MultiRigDitherPolicyDraft.Default with
        {
            Enabled = true, TriggerRigId = new RigId("rig.wide"), EveryNFrames = 20, SettleStableSeconds = 0.1, SettleTimeoutSeconds = 5,
        };
        var block = new MultiRigStepDraft(Guid.NewGuid(), [main, wide], policy);
        app.Draft.ReplaceSteps([new StartGuidingStepDraft(Guid.NewGuid(), DemoSetup.GuiderId), block]);
        Assert.True(app.Draft.IsValid, string.Join(" ", app.Draft.ValidationErrors));

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        var rows = app.Sequencer.Definition;
        Assert.Equal(
            ["Start Guiding", "Multi-Rig Imaging", "Main Rig", "Change Filter", "Exposure", "Wide Rig", "Repeat × 40", "Exposure"],
            rows.Select(r => r.Title));
        Assert.Equal(change.Id, rows[3].DraftId);
        Assert.All(rows, row => Assert.Equal(NodeStatus.Done, row.Status));
    }

    [Fact]
    public async Task RunningAPolicy_NeverModifiesTheDocument()
    {
        await using var app = await CreateApp();
        var policy = new RigAutofocusPolicyDraft(true, true, false, 0.1, 400, 7);
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [MainTrack(policy, new RigExposureStepDraft(Guid.NewGuid(), 0.1)), WideTrack()])]);
        var modifications = 0;
        app.Draft.Modified += (_, _) => modifications++;

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(0, modifications);
        Assert.False(app.Vm.SequenceDocument.IsDirty);
    }

    [Fact]
    public async Task AFailedAutomaticAutofocus_ShowsTheReason_AndTheTrackNeverGoesOnImaging()
    {
        await using var app = await CreateApp();
        app.Host.AddSimulatedFocusModel(new RigId("rig.main"), new SimulatedFocusModel(20000, 2.5, 0));
        var policy = new RigAutofocusPolicyDraft(true, true, false, 0.1, 400, 7);
        var exposure = new RigExposureStepDraft(Guid.NewGuid(), 0.1);
        app.Draft.ReplaceSteps([new MultiRigStepDraft(Guid.NewGuid(), [MainTrack(policy, exposure), WideTrack()])]);

        await app.Sequencer.RunCommand.ExecuteAsync(null).WaitAsync(Bound);

        Assert.Equal(SequenceState.Failed, app.Sequencer.State);
        Assert.Contains("Autofocus failed: no reliable focus minimum was found.", app.Sequencer.ErrorMessage);
        Assert.Equal(["Automatic · track start", "Autofocus stopped"], app.Sequencer.AutofocusStatuses.Single().Lines);
    }
}

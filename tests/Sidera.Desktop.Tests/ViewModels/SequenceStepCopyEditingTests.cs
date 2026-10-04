using Sidera.Core.Devices;
using Sidera.Desktop.ViewModels;
using Sidera.Runtime;

namespace Sidera.Desktop.Tests.ViewModels;

/// <summary>Duplicate, Copy and Paste of steps in the draft: where things go, ids, independence, validation.</summary>
public class SequenceStepCopyEditingTests
{
    private static SideraRuntimeHost CreateHost()
    {
        var host = new SideraRuntimeHost();
        DemoSetup.AddDemoEquipment(host);
        return host;
    }

    private static SequenceDraftViewModel CreateDraft(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var defaults = SequenceDraftDefaults.From(new DemoOptions(), host.DeviceRegistry);
        return new SequenceDraftViewModel(host.DeviceRegistry, defaults, null, clipboard);
    }

    private static T Add<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddStepCommand.Execute(kind);
        return Assert.IsAssignableFrom<T>(draft.SelectedStep);
    }

    private static T AddChild<T>(SequenceDraftViewModel draft, SequenceStepKind kind) where T : StepDraftViewModel
    {
        draft.AddChildCommand.Execute(kind);
        return Assert.IsAssignableFrom<T>(draft.SelectedStep);
    }

    private static IEnumerable<Guid> Ids(IEnumerable<SequenceStepDraft> steps) =>
        steps.SelectMany(step => step is RepeatStepDraft repeat ? repeat.Children.Select(c => c.Id).Prepend(repeat.Id) : [step.Id]);

    // Repeat × 5 [Exposure 300 s, Delay 2 s, Dither] between Start Guiding and Stop Guiding.
    private sealed record Fixture(
        SequenceDraftViewModel Draft,
        StartGuidingStepDraftViewModel Start,
        RepeatStepDraftViewModel Repeat,
        ExposureStepDraftViewModel Exposure,
        DelayStepDraftViewModel Delay,
        DitherStepDraftViewModel Dither,
        StopGuidingStepDraftViewModel Stop);

    private static Fixture Build(SideraRuntimeHost host, ISequenceStepClipboard? clipboard = null)
    {
        var draft = CreateDraft(host, clipboard);
        var start = Add<StartGuidingStepDraftViewModel>(draft, SequenceStepKind.StartGuiding);
        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);
        repeat.CountText = "5";
        var exposure = AddChild<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        exposure.ExposureText = "300";
        var delay = AddChild<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay);
        var dither = AddChild<DitherStepDraftViewModel>(draft, SequenceStepKind.Dither);
        var stop = Add<StopGuidingStepDraftViewModel>(draft, SequenceStepKind.StopGuiding);
        return new Fixture(draft, start, repeat, exposure, delay, dither, stop);
    }

    // Drafts are equal by value, except that a Repeat's list of children is compared by reference: compare it by content.
    private static void AssertSameDraft(SequenceStepDraft expected, SequenceStepDraft actual)
    {
        if (expected is RepeatStepDraft e && actual is RepeatStepDraft a)
        {
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Count, a.Count);
            Assert.Equal(e.Children, a.Children);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    // Changes one value of a step, whatever kind it is.
    private static void Edit(StepDraftViewModel step)
    {
        switch (step)
        {
            case ExposureStepDraftViewModel e: e.ExposureText = "777"; break;
            case DelayStepDraftViewModel d: d.DurationText = "777"; break;
            case SlewStepDraftViewModel s: s.RightAscensionText = "7.77"; break;
            case StartGuidingStepDraftViewModel g: g.Guider.Selected = null; break;
            case StopGuidingStepDraftViewModel g: g.Guider.Selected = null; break;
            case DitherStepDraftViewModel d: d.AmplitudeText = "7.77"; break;
            case RepeatStepDraftViewModel r: r.CountText = "77"; break;
            default: throw new InvalidOperationException();
        }
    }

    public static TheoryData<SequenceStepKind> LeafKinds => new()
    {
        SequenceStepKind.Exposure, SequenceStepKind.Delay, SequenceStepKind.Slew,
        SequenceStepKind.StartGuiding, SequenceStepKind.StopGuiding, SequenceStepKind.Dither,
    };

    // Duplicate

    [Theory]
    [MemberData(nameof(LeafKinds))]
    public async Task DuplicatingALeafStep_PutsACopyRightAfterIt_SelectsIt_AndKeepsEveryValue(SequenceStepKind kind)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var source = Add<StepDraftViewModel>(draft, kind);
        Add<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay); // something after the source
        draft.SelectedStep = source;
        var before = Assert.IsAssignableFrom<LeafStepDraft>(draft.Snapshot()[0]);

        draft.DuplicateStepCommand.Execute(null);

        Assert.Equal(3, draft.Steps.Count);
        var clone = draft.Steps[1];
        Assert.IsType(source.GetType(), clone);
        Assert.Same(clone, draft.SelectedStep);
        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(source.Summary, clone.Summary);
        Assert.Equal([1, 2, 3], draft.Steps.Select(s => s.Number));
        Assert.Equal(before with { Id = clone.Id }, draft.Snapshot()[1]);
        Assert.Equal(before, draft.Snapshot()[0]);
    }

    [Theory]
    [MemberData(nameof(LeafKinds))]
    public async Task ChangingTheCopyOfALeafStep_DoesNotChangeTheSource_AndTheOtherWayRound(SequenceStepKind kind)
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var source = Add<StepDraftViewModel>(draft, kind);
        var original = draft.Snapshot()[0];
        draft.DuplicateStepCommand.Execute(null);
        var clone = draft.SelectedStep!;

        Edit(clone);

        Assert.Equal(original, draft.Snapshot()[0]);
        Assert.NotEqual(original with { Id = clone.Id }, draft.Snapshot()[1]);

        var cloneNow = draft.Snapshot()[1];
        Edit(source);
        Assert.Equal(cloneNow, draft.Snapshot()[1]);
    }

    [Fact]
    public async Task DuplicatingARepeat_PutsTheWholeSubtreeAfterIt_WithNewIds_AndSelectsTheCopy()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;

        f.Draft.DuplicateStepCommand.Execute(null);

        Assert.Equal(4, f.Draft.Steps.Count);
        var clone = Assert.IsType<RepeatStepDraftViewModel>(f.Draft.Steps[2]);
        Assert.Same(clone, f.Draft.SelectedStep);
        Assert.Same(f.Stop, f.Draft.Steps[3]);
        Assert.NotEqual(f.Repeat.Id, clone.Id);
        Assert.Equal("5", clone.CountText);
        Assert.Equal(["Repeat × 5", "3 steps"], new[] { clone.Title, clone.Summary });
        Assert.Equal(
            f.Repeat.Children.Select(c => c.GetType()), clone.Children.Select(c => c.GetType()));
        Assert.Equal(f.Repeat.Children.Select(c => c.Summary), clone.Children.Select(c => c.Summary));
        Assert.Empty(f.Repeat.Children.Select(c => c.Id).Intersect(clone.Children.Select(c => c.Id)));
        Assert.All(clone.Children, child => Assert.Same(clone, child.Parent));
        Assert.Equal(
            ["1", "2", "2.1", "2.2", "2.3", "3", "3.1", "3.2", "3.3", "4"],
            f.Draft.Rows.Select(r => r.NumberLabel));
        Assert.Equal(f.Draft.Rows.Count, f.Draft.Rows.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task ADuplicatedRepeat_IsDeeplyIndependent_OfItsSource()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        var original = f.Draft.Snapshot()[1];
        f.Draft.DuplicateStepCommand.Execute(null);
        var clone = (RepeatStepDraftViewModel)f.Draft.SelectedStep!;
        Assert.All(clone.Children, child => Assert.DoesNotContain(child, f.Repeat.Children));

        clone.CountText = "9";
        ((ExposureStepDraftViewModel)clone.Children[0]).ExposureText = "30";
        ((DitherStepDraftViewModel)clone.Children[2]).AmplitudeText = "4";
        f.Draft.SelectedStep = clone.Children[1];
        f.Draft.RemoveStepCommand.Execute(null);

        // Nothing of the source changed, and the copy has what was done to it.
        AssertSameDraft(original, f.Draft.Snapshot()[1]);
        Assert.Equal("5", f.Repeat.CountText);
        Assert.Equal(3, f.Repeat.Children.Count);
        Assert.Equal("300", f.Exposure.ExposureText);
        Assert.Equal("Repeat × 9", clone.Title);
        Assert.Equal(2, clone.Children.Count);

        // And the other way round.
        var cloneState = f.Draft.Snapshot()[2];
        f.Repeat.CountText = "2";
        f.Exposure.ExposureText = "1";
        AssertSameDraft(cloneState, f.Draft.Snapshot()[2]);
    }

    [Fact]
    public async Task DuplicatingAStepInsideARepeat_PutsTheCopyRightAfterItInsideTheSameRepeat()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Exposure;

        f.Draft.DuplicateStepCommand.Execute(null);

        var clone = Assert.IsType<ExposureStepDraftViewModel>(f.Draft.SelectedStep);
        Assert.Same(f.Repeat, clone.Parent);
        Assert.Equal([f.Exposure, clone, f.Delay, f.Dither], f.Repeat.Children);
        Assert.Equal("300", clone.ExposureText);
        Assert.NotEqual(f.Exposure.Id, clone.Id);
        Assert.Equal(["2.1", "2.2", "2.3", "2.4"], f.Repeat.Children.Select(c => c.NumberLabel));
        Assert.Equal(3, f.Draft.Steps.Count); // still three steps in the sequence
    }

    [Fact]
    public async Task DuplicatingTheLastStepOfTheSequence_AppendsIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Stop;

        f.Draft.DuplicateStepCommand.Execute(null);

        Assert.Equal(4, f.Draft.Steps.Count);
        Assert.IsType<StopGuidingStepDraftViewModel>(f.Draft.Steps[^1]);
        Assert.Same(f.Draft.Steps[^1], f.Draft.SelectedStep);
    }

    [Fact]
    public async Task ACopyKeepsTheDeviceIds_AlsoOfDevicesThatAreNotThere()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        draft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.observatory"), 5)]);
        draft.SelectedStep = draft.Steps[0];

        draft.DuplicateStepCommand.Execute(null);

        var clone = Assert.IsType<ExposureStepDraftViewModel>(draft.SelectedStep);
        Assert.Equal(new DeviceId("camera.observatory"), clone.Camera.SelectedId); // not replaced by camera.main
        Assert.True(clone.Camera.Selected!.IsMissing);
        Assert.Equal(["The camera 'camera.observatory' is not available."], clone.Problems);
    }

    [Fact]
    public async Task ACopyOfAStepWithNoDeviceSelected_HasNoDeviceEither()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        exposure.Camera.Selected = null;

        draft.DuplicateStepCommand.Execute(null);

        Assert.Null(((ExposureStepDraftViewModel)draft.SelectedStep!).Camera.SelectedId);
    }

    [Fact]
    public async Task DuplicateAndCopy_AreUnavailableWithoutASelection_AndForAStepWithAFieldThatIsNoNumber()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = null;
        Assert.False(f.Draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(f.Draft.CopyStepCommand.CanExecute(null));

        f.Draft.SelectedStep = f.Exposure;
        Assert.True(f.Draft.DuplicateStepCommand.CanExecute(null));

        f.Exposure.ExposureText = "abc";
        Assert.False(f.Draft.DuplicateStepCommand.CanExecute(null));
        Assert.False(f.Draft.CopyStepCommand.CanExecute(null));

        // The Repeat holds that step, so it cannot be copied faithfully either.
        f.Draft.SelectedStep = f.Repeat;
        Assert.False(f.Draft.DuplicateStepCommand.CanExecute(null));
        f.Draft.DuplicateStepCommand.Execute(null); // bypassing the disabled command does nothing
        Assert.Equal(3, f.Draft.Steps.Count);

        f.Exposure.ExposureText = "10";
        Assert.True(f.Draft.DuplicateStepCommand.CanExecute(null));
    }

    // Ids

    [Fact]
    public async Task DuplicatingAgainAndAgain_NeverRepeatsAnId()
    {
        await using var host = CreateHost();
        var f = Build(host);

        for (var i = 0; i < 20; i++)
        {
            f.Draft.SelectedStep = f.Repeat;
            f.Draft.DuplicateStepCommand.Execute(null);
            f.Draft.DuplicateStepCommand.Execute(null); // the copy of the copy
            f.Draft.SelectedStep = f.Exposure;
            f.Draft.DuplicateStepCommand.Execute(null);
        }

        var ids = Ids(f.Draft.Snapshot()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(ids.Count, f.Draft.Rows.Count);
        Assert.Equal(f.Draft.Rows.Select(r => r.Id).Order(), ids.Order());
    }

    [Fact]
    public async Task PastingTheSameCopyManyTimes_NeverRepeatsAnId()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);

        for (var i = 0; i < 30; i++)
        {
            f.Draft.PasteStepCommand.Execute(null);
        }

        var ids = Ids(f.Draft.Snapshot()).ToList();
        Assert.Equal(3 + 30, f.Draft.Steps.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // Copy and Paste

    [Fact]
    public async Task Copy_ChangesNeitherTheSequenceNorTheSelection_AndRaisesNoModification()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Delay;
        var before = f.Draft.Snapshot();
        var modifications = 0;
        f.Draft.Modified += (_, _) => modifications++;
        Assert.False(f.Draft.Clipboard.HasContent);

        f.Draft.CopyStepCommand.Execute(null);

        Assert.True(f.Draft.Clipboard.HasContent);
        Assert.Equal(SequenceStepKind.Delay, f.Draft.Clipboard.ContentKind);
        Assert.Same(f.Delay, f.Draft.SelectedStep);
        Assert.Equal(0, modifications);
        Assert.Equal(before.Select(s => s.Id), f.Draft.Snapshot().Select(s => s.Id));
    }

    [Fact]
    public async Task PasteIsUnavailableUntilSomethingWasCopied()
    {
        await using var host = CreateHost();
        var f = Build(host);

        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));

        f.Draft.SelectedStep = f.Delay;
        f.Draft.CopyStepCommand.Execute(null);

        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
    }

    [Fact]
    public async Task PastingACopiedLeafStep_AtTheTopLevel_GoesAfterTheSelectedStep_WithNewIds()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Start;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Stop;

        f.Draft.PasteStepCommand.Execute(null);

        Assert.Equal(4, f.Draft.Steps.Count);
        var pasted = Assert.IsType<StartGuidingStepDraftViewModel>(f.Draft.Steps[3]);
        Assert.Same(pasted, f.Draft.SelectedStep);
        Assert.NotEqual(f.Start.Id, pasted.Id);
        Assert.Equal(f.Start.Guider.SelectedId, pasted.Guider.SelectedId);
    }

    [Fact]
    public async Task PastingInTheMiddleOfTheSequence_GoesRightAfterTheSelectedStep()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Delay;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Start;

        f.Draft.PasteStepCommand.Execute(null);

        Assert.IsType<StartGuidingStepDraftViewModel>(f.Draft.Steps[0]);
        Assert.IsType<DelayStepDraftViewModel>(f.Draft.Steps[1]);
        Assert.Same(f.Repeat, f.Draft.Steps[2]);
        Assert.Same(f.Draft.Steps[1], f.Draft.SelectedStep);
        Assert.Equal(["1", "2", "3", "3.1", "3.2", "3.3", "4"], f.Draft.Rows.Select(r => r.NumberLabel));
    }

    [Fact]
    public async Task PastingACopiedRepeat_AtTheTopLevel_PastesTheWholeSubtree()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Start;

        f.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<RepeatStepDraftViewModel>(f.Draft.Steps[1]);
        Assert.Same(pasted, f.Draft.SelectedStep);
        Assert.NotEqual(f.Repeat.Id, pasted.Id);
        Assert.Equal("5", pasted.CountText);
        Assert.Equal(3, pasted.Children.Count);
        Assert.Equal(f.Repeat.Children.Select(c => c.Summary), pasted.Children.Select(c => c.Summary));
        Assert.Empty(f.Repeat.Children.Select(c => c.Id).Intersect(pasted.Children.Select(c => c.Id)));
        Assert.Same(f.Repeat, f.Draft.Steps[2]);
    }

    [Fact]
    public async Task PastingACopiedLeafStep_WithAStepInsideARepeatSelected_GoesAfterItInsideTheRepeat()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Delay;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Exposure;

        f.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<DelayStepDraftViewModel>(f.Draft.SelectedStep);
        Assert.Same(f.Repeat, pasted.Parent);
        Assert.Equal([f.Exposure, pasted, f.Delay, f.Dither], f.Repeat.Children);
        Assert.NotEqual(f.Delay.Id, pasted.Id);
        Assert.Equal(3, f.Draft.Steps.Count);
    }

    [Fact]
    public async Task ACopiedStepInsideARepeat_CanBePastedAtTheTopLevel_AfterATopLevelStep()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Dither;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Start;

        f.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<DitherStepDraftViewModel>(f.Draft.Steps[1]);
        Assert.True(pasted.IsTopLevel);
        Assert.Equal(f.Dither.AmplitudeText, pasted.AmplitudeText);
        Assert.Equal(3, f.Repeat.Children.Count);
    }

    [Fact]
    public async Task PastingACopiedLeafStep_WithARepeatSelected_GoesAfterTheRepeat_NotInside()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Start;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Repeat;

        f.Draft.PasteStepCommand.Execute(null);

        Assert.Equal(3, f.Repeat.Children.Count);
        Assert.Same(f.Repeat, f.Draft.Steps[1]);
        Assert.IsType<StartGuidingStepDraftViewModel>(f.Draft.Steps[2]);
        Assert.True(f.Draft.SelectedStep!.IsTopLevel);
    }

    [Fact]
    public async Task ACopiedRepeat_CannotBePastedInsideARepeat_NotEvenWhenTheCommandIsForced()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);

        f.Draft.SelectedStep = f.Delay; // a step inside a Repeat
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));
        Assert.False(f.Draft.CanPaste);
        f.Draft.PasteStepCommand.Execute(null); // bypassing the disabled command

        Assert.Equal(3, f.Draft.Steps.Count); // nothing was pasted, and not on another level either
        Assert.Equal(3, f.Repeat.Children.Count);
        Assert.Same(f.Delay, f.Draft.SelectedStep); // a refused paste leaves the selection alone
        Assert.All(f.Draft.Rows.Where(r => r.IsChild), child => Assert.IsNotType<RepeatStepDraftViewModel>(child));
    }

    [Fact]
    public async Task ACopiedRepeat_CanBePastedAgainAsSoonAsATopLevelStepIsSelected()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = f.Delay;
        Assert.False(f.Draft.PasteStepCommand.CanExecute(null));

        f.Draft.SelectedStep = f.Stop;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
        f.Draft.SelectedStep = f.Repeat;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithNothingSelected_PasteAppendsAtTheTopLevel(bool copyARepeat)
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = copyARepeat ? f.Repeat : f.Delay;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.SelectedStep = null;
        Assert.True(f.Draft.PasteStepCommand.CanExecute(null));

        f.Draft.PasteStepCommand.Execute(null);

        Assert.Equal(4, f.Draft.Steps.Count);
        Assert.Same(f.Draft.Steps[3], f.Draft.SelectedStep);
        Assert.Equal(copyARepeat ? typeof(RepeatStepDraftViewModel) : typeof(DelayStepDraftViewModel), f.Draft.Steps[3].GetType());
    }

    [Fact]
    public async Task PasteIntoAnEmptySequence_WorksAndStartsIt()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Exposure;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.ReplaceSteps([]); // New

        Assert.True(f.Draft.PasteStepCommand.CanExecute(null)); // the clipboard outlives New and Open
        f.Draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<ExposureStepDraftViewModel>(Assert.Single(f.Draft.Steps));
        Assert.Equal("300", pasted.ExposureText);
        Assert.NotEqual(f.Exposure.Id, pasted.Id);
    }

    // The clipboard is a snapshot

    [Fact]
    public async Task ACopiedLeafStep_IsPastedAsItWasWhenItWasCopied_NotAsItIsNow()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var exposure = Add<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        exposure.ExposureText = "10";
        draft.CopyStepCommand.Execute(null);

        exposure.ExposureText = "20";
        draft.PasteStepCommand.Execute(null);

        Assert.Equal("20", exposure.ExposureText);
        Assert.Equal("10", Assert.IsType<ExposureStepDraftViewModel>(draft.SelectedStep).ExposureText);
    }

    [Fact]
    public async Task ACopiedRepeat_IsPastedAsItWasWhenItWasCopied_NotAsItIsNow()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        var repeat = Add<RepeatStepDraftViewModel>(draft, SequenceStepKind.Repeat);
        repeat.CountText = "3";
        var exposure = AddChild<ExposureStepDraftViewModel>(draft, SequenceStepKind.Exposure);
        exposure.ExposureText = "10";
        draft.SelectedStep = repeat;
        draft.CopyStepCommand.Execute(null);

        repeat.CountText = "9";
        exposure.ExposureText = "30";
        AddChild<DelayStepDraftViewModel>(draft, SequenceStepKind.Delay); // and a step more in the original
        draft.SelectedStep = repeat;
        draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<RepeatStepDraftViewModel>(draft.SelectedStep);
        Assert.Equal("3", pasted.CountText);
        var child = Assert.IsType<ExposureStepDraftViewModel>(Assert.Single(pasted.Children));
        Assert.Equal("10", child.ExposureText);
        Assert.Equal("9", repeat.CountText);
        Assert.Equal(2, repeat.Children.Count);
    }

    [Fact]
    public async Task APastedStep_IsIndependentOfWhatWasCopied_AndOfOtherPastes()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.PasteStepCommand.Execute(null);
        var first = (RepeatStepDraftViewModel)f.Draft.SelectedStep!;
        f.Draft.PasteStepCommand.Execute(null);
        var second = (RepeatStepDraftViewModel)f.Draft.SelectedStep!;

        first.CountText = "11";
        ((ExposureStepDraftViewModel)first.Children[0]).ExposureText = "11";

        Assert.Equal("5", second.CountText);
        Assert.Equal("300", ((ExposureStepDraftViewModel)second.Children[0]).ExposureText);
        Assert.Equal("5", f.Repeat.CountText);
        f.Draft.PasteStepCommand.Execute(null); // and the clipboard still holds the copy-time state
        var third = (RepeatStepDraftViewModel)f.Draft.SelectedStep!;
        Assert.Equal("5", third.CountText);
    }

    // Validation

    [Fact]
    public async Task DuplicatingStartGuiding_Succeeds_AndTheDraftThenSaysWhyItCannotRun()
    {
        await using var host = CreateHost();
        var f = Build(host);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
        f.Draft.SelectedStep = f.Start;

        f.Draft.DuplicateStepCommand.Execute(null);

        var clone = Assert.IsType<StartGuidingStepDraftViewModel>(f.Draft.Steps[1]);
        Assert.Equal(["Guiding was already started by step 1."], clone.Problems);
        Assert.False(f.Draft.IsValid);
        Assert.Throws<SequenceConfigurationException>(() => f.Draft.Build());
    }

    [Fact]
    public async Task DuplicatingAValidExposure_KeepsTheDraftValid()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Exposure;

        f.Draft.DuplicateStepCommand.Execute(null);

        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors));
        Assert.Equal(3, f.Draft.Build().Sequence.Steps.Count);
    }

    [Fact]
    public async Task DuplicatingADither_AppliesTheUsualGuidingAndDeviceRules()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Dither;

        f.Draft.DuplicateStepCommand.Execute(null);
        Assert.True(f.Draft.IsValid, string.Join(" ", f.Draft.ValidationErrors)); // guiding runs there

        // The same dither after a Stop Guiding is the usual problem; and a copy needs the same devices.
        f.Draft.SelectedStep = f.Stop;
        f.Draft.CopyStepCommand.Execute(null);
        f.Draft.PasteStepCommand.Execute(null);
        Assert.Contains("Guiding was already stopped by step 3.", f.Draft.Steps[^1].Problems);
        Assert.False(f.Draft.IsValid);
    }

    [Fact]
    public async Task PastingAStepWithAnUnavailableDevice_Succeeds_ButTheDraftIsInvalid()
    {
        await using var host = CreateHost();
        var draft = CreateDraft(host);
        draft.ReplaceSteps([new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.observatory"), 5)]);
        draft.SelectedStep = draft.Steps[0];
        draft.CopyStepCommand.Execute(null);
        draft.ReplaceSteps([new DelayStepDraft(Guid.NewGuid(), 1)]);
        Assert.True(draft.IsValid);

        draft.SelectedStep = draft.Steps[0];
        draft.PasteStepCommand.Execute(null);

        var pasted = Assert.IsType<ExposureStepDraftViewModel>(draft.Steps[1]);
        Assert.Equal(new DeviceId("camera.observatory"), pasted.Camera.SelectedId);
        Assert.Equal(["The camera 'camera.observatory' is not available."], pasted.Problems);
        Assert.False(draft.IsValid);

        // A replacement can be chosen as for any other step.
        pasted.Camera.Selected = pasted.Camera.Options.Single(o => o.IdText == "camera.main");
        Assert.True(draft.IsValid, string.Join(" ", draft.ValidationErrors));
    }

    [Fact]
    public async Task APastedOrDuplicatedRepeat_IsValidatedLikeAnyOtherRepeat()
    {
        await using var host = CreateHost();
        var f = Build(host);
        f.Draft.SelectedStep = f.Repeat;
        f.Draft.DuplicateStepCommand.Execute(null);
        var clone = (RepeatStepDraftViewModel)f.Draft.SelectedStep!;

        clone.CountText = "0";

        Assert.Equal(["Repeat count must be at least 1."], clone.Problems);
        Assert.False(f.Draft.IsValid);
        Assert.False(f.Repeat.HasProblems);
    }
}

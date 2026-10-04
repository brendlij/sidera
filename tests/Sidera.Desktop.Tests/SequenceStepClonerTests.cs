using Sidera.Core.Devices;

namespace Sidera.Desktop.Tests;

public class SequenceStepClonerTests
{
    private static readonly DeviceId Camera = new("camera.main");
    private static readonly DeviceId Mount = new("mount.eq6");
    private static readonly DeviceId Guider = new("guider.main");

    public static TheoryData<LeafStepDraft> Leaves => new()
    {
        new ExposureStepDraft(Guid.NewGuid(), Camera, 300),
        new DelayStepDraft(Guid.NewGuid(), 2.5),
        new SlewStepDraft(Guid.NewGuid(), Mount, 5.588, -5.39),
        new StartGuidingStepDraft(Guid.NewGuid(), Guider),
        new StopGuidingStepDraft(Guid.NewGuid(), null),
        new DitherStepDraft(Guid.NewGuid(), Guider, Mount, Camera, 1.5, 0.5, 1, 10),
        new ExposureStepDraft(Guid.NewGuid(), new DeviceId("camera.observatory"), 1), // equipment that is not here
    };

    private static RepeatStepDraft SomeRepeat() => new(Guid.NewGuid(), 5,
    [
        new ExposureStepDraft(Guid.NewGuid(), Camera, 300),
        new DelayStepDraft(Guid.NewGuid(), 2),
        new DitherStepDraft(Guid.NewGuid(), Guider, Mount, Camera, 1.5, 0.5, 1, 10),
    ]);

    private static IEnumerable<Guid> Ids(SequenceStepDraft step) =>
        step is RepeatStepDraft repeat ? repeat.Children.Select(c => c.Id).Prepend(repeat.Id) : [step.Id];

    [Theory]
    [MemberData(nameof(Leaves))]
    public void ALeafClone_HasANewId_AndEveryOtherValueOfTheSource(LeafStepDraft source)
    {
        var clone = Assert.IsAssignableFrom<LeafStepDraft>(SequenceStepDraftCloner.CloneWithNewIds(source));

        Assert.NotEqual(source.Id, clone.Id);
        Assert.NotEqual(Guid.Empty, clone.Id);
        Assert.Equal(source with { Id = clone.Id }, clone); // the same kind, devices and numbers
        Assert.NotSame(source, clone);
    }

    [Theory]
    [MemberData(nameof(Leaves))]
    public void ACopy_KeepsEverythingIncludingTheId_ButIsAnObjectOfItsOwn(LeafStepDraft source)
    {
        var copy = SequenceStepDraftCloner.Copy(source);

        Assert.Equal(source, copy);
        Assert.NotSame(source, copy);
    }

    [Fact]
    public void ARepeatClone_HasANewIdForItselfAndForEveryChild_AndKeepsCountValuesAndOrder()
    {
        var source = SomeRepeat();

        var clone = Assert.IsType<RepeatStepDraft>(SequenceStepDraftCloner.CloneWithNewIds(source));

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Equal(5, clone.Count);
        Assert.Equal(source.Children.Count, clone.Children.Count);
        for (var i = 0; i < source.Children.Count; i++)
        {
            Assert.NotEqual(source.Children[i].Id, clone.Children[i].Id);
            Assert.Equal(source.Children[i] with { Id = clone.Children[i].Id }, clone.Children[i]);
            Assert.NotSame(source.Children[i], clone.Children[i]);
        }

        Assert.Equal(source.Children.Select(c => c.Kind), clone.Children.Select(c => c.Kind));
        Assert.Equal(8, Ids(clone).Concat(Ids(source)).Distinct().Count()); // eight ids, all different
    }

    [Fact]
    public void ARepeatClone_SharesNoListWithItsSource()
    {
        var source = SomeRepeat();

        var clone = Assert.IsType<RepeatStepDraft>(SequenceStepDraftCloner.CloneWithNewIds(source));
        var copy = Assert.IsType<RepeatStepDraft>(SequenceStepDraftCloner.Copy(source));

        Assert.NotSame(source.Children, clone.Children);
        Assert.NotSame(source.Children, copy.Children);
        Assert.Equal(source.Children.Select(c => c.Id), copy.Children.Select(c => c.Id));
    }

    [Fact]
    public void ANewId_NeverEqualsAnIdOfTheSequenceItGoesInto_NorAnotherNewId()
    {
        var source = SomeRepeat();
        var taken = new HashSet<Guid>(Ids(source));
        var all = new HashSet<Guid>(taken);

        for (var i = 0; i < 500; i++)
        {
            var clone = SequenceStepDraftCloner.CloneWithNewIds(source, taken);
            foreach (var id in Ids(clone))
            {
                Assert.True(all.Add(id), "an id was used twice");
            }

            foreach (var id in Ids(clone))
            {
                taken.Add(id);
            }
        }

        Assert.Equal(4 + 500 * 4, all.Count);
    }

    [Fact]
    public void ACloneOfAnEmptyRepeat_IsAnEmptyRepeatWithANewId()
    {
        var source = new RepeatStepDraft(Guid.NewGuid(), 2, []);

        var clone = Assert.IsType<RepeatStepDraft>(SequenceStepDraftCloner.CloneWithNewIds(source));

        Assert.NotEqual(source.Id, clone.Id);
        Assert.Empty(clone.Children);
        Assert.Equal(2, clone.Count);
    }

    [Fact]
    public void ATypeThatIsNoStep_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => SequenceStepDraftCloner.CloneWithNewIds(new UnknownDraft(Guid.NewGuid())));
    }

    private sealed record UnknownDraft(Guid Id) : SequenceStepDraft(Id)
    {
        public override SequenceStepKind Kind => SequenceStepKind.Delay;
        public override IEnumerable<DeviceId> DeviceIds => [];
    }

    // The clipboard

    [Fact]
    public void TheClipboard_IsEmptyAtFirst_AndKeepsOneSnapshotAtATime()
    {
        var clipboard = new SequenceStepClipboard();
        var changes = 0;
        clipboard.Changed += (_, _) => changes++;
        Assert.False(clipboard.HasContent);
        Assert.Null(clipboard.ContentKind);
        Assert.Throws<InvalidOperationException>(() => clipboard.CreateClone(new HashSet<Guid>()));

        clipboard.Copy(new DelayStepDraft(Guid.NewGuid(), 1));
        clipboard.Copy(SomeRepeat());

        Assert.True(clipboard.HasContent);
        Assert.Equal(SequenceStepKind.Repeat, clipboard.ContentKind);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void WhatTheClipboardGivesBack_IsAFreshCloneEveryTime()
    {
        var clipboard = new SequenceStepClipboard();
        var source = SomeRepeat();
        clipboard.Copy(source);

        var first = Assert.IsType<RepeatStepDraft>(clipboard.CreateClone(new HashSet<Guid>(Ids(source))));
        var second = Assert.IsType<RepeatStepDraft>(clipboard.CreateClone(new HashSet<Guid>(Ids(source))));

        Assert.Empty(Ids(first).Intersect(Ids(source)));
        Assert.NotSame(first, second);
        Assert.NotSame(first.Children, second.Children);
    }
}

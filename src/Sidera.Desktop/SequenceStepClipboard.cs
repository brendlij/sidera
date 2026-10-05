using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidera.Desktop;

/// <summary>
/// Copies drafts, in whole and with the structure they have. This is the one place that knows how to clone each kind
/// of draft: the clipboard and Duplicate both go through it, and nothing else clones steps. Cloning is a deep copy by
/// construction: the values of a draft are immutable and a Repeat gets a list of its own, so a clone shares nothing with
/// its source, and no view model or runtime object is involved.
/// </summary>
public static class SequenceStepDraftCloner
{
    /// <summary>An exact copy, ids included: a snapshot, not a step that may be put into a sequence next to its source.</summary>
    public static SequenceStepDraft Copy(SequenceStepDraft step) => Clone(step, null);

    /// <summary>
    /// A copy in which the step, and for a Repeat each of its children, has a new id. A new id differs from every
    /// id in <paramref name="takenIds"/> (the ids of the sequence it goes into) and from the others of the clone.
    /// </summary>
    public static SequenceStepDraft CloneWithNewIds(SequenceStepDraft step, IReadOnlySet<Guid>? takenIds = null)
    {
        var used = new HashSet<Guid>(takenIds ?? new HashSet<Guid>());

        Guid Next()
        {
            Guid id;
            do
            {
                id = Guid.NewGuid();
            }
            while (!used.Add(id));

            return id;
        }

        return Clone(step, Next);
    }

    private static SequenceStepDraft Clone(SequenceStepDraft step, Func<Guid>? newId) => step switch
    {
        MultiRigStepDraft multiRig => new MultiRigStepDraft(
            newId?.Invoke() ?? multiRig.Id,
            multiRig.Tracks.Select(track => (RigTrackDraft)Clone(track, newId)).ToList(),
            multiRig.DitherPolicy, // refers to a rig, not to a track: nothing to map
            multiRig.SingleTrack),
        RigTrackDraft track => new RigTrackDraft(
            newId?.Invoke() ?? track.Id, track.RigId, track.Steps.Select(inner => Clone(inner, newId)).ToList(),
            track.AutofocusPolicy), // values only: it belongs to the track, not to a step
        RepeatStepDraft repeat => new RepeatStepDraft(
            newId?.Invoke() ?? repeat.Id, repeat.Count, repeat.Children.Select(child => CloneLeaf(child, newId)).ToList()),
        LeafStepDraft leaf => CloneLeaf(leaf, newId),
        _ => throw new ArgumentException($"Unsupported step '{step.GetType().Name}'.", nameof(step)),
    };

    private static LeafStepDraft CloneLeaf(LeafStepDraft leaf, Func<Guid>? newId) =>
        leaf with { Id = newId?.Invoke() ?? leaf.Id };
}

/// <summary>
/// The internal clipboard of the sequence editor: one copied step (a leaf step, or a Repeat with its children) for the
/// length of the session. It holds draft data only, taken at the moment of copying; later edits of the original do not
/// reach it. It is not the operating system clipboard and puts nothing into it.
/// </summary>
public interface ISequenceStepClipboard
{
    /// <summary>A step has been copied and can be pasted.</summary>
    bool HasContent { get; }

    /// <summary>What the copied step is; <c>null</c> when nothing has been copied.</summary>
    SequenceStepKind? ContentKind { get; }

    /// <summary>What the steps directly inside the copied step are (a Repeat's steps); empty for any other step.</summary>
    IReadOnlyList<SequenceStepKind> ContentChildKinds { get; }

    /// <summary>Keeps a snapshot of <paramref name="step"/>, replacing what was there.</summary>
    void Copy(SequenceStepDraft step);

    /// <summary>A new copy of the copied step for pasting, with new ids that avoid <paramref name="takenIds"/>.</summary>
    /// <exception cref="InvalidOperationException">Nothing has been copied.</exception>
    SequenceStepDraft CreateClone(IReadOnlySet<Guid> takenIds);

    /// <summary>Raised when something was copied.</summary>
    event EventHandler? Changed;
}

public sealed class SequenceStepClipboard : ISequenceStepClipboard
{
    private SequenceStepDraft? _content;

    public bool HasContent => _content is not null;

    public SequenceStepKind? ContentKind => _content?.Kind;

    public IReadOnlyList<SequenceStepKind> ContentChildKinds =>
        _content is RepeatStepDraft repeat ? repeat.Children.Select(child => child.Kind).Distinct().ToList() : [];

    public event EventHandler? Changed;

    public void Copy(SequenceStepDraft step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _content = SequenceStepDraftCloner.Copy(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public SequenceStepDraft CreateClone(IReadOnlySet<Guid> takenIds)
    {
        ArgumentNullException.ThrowIfNull(takenIds);
        return SequenceStepDraftCloner.CloneWithNewIds(
            _content ?? throw new InvalidOperationException("Nothing has been copied."), takenIds);
    }
}

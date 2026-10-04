using System;

namespace Sidera.Desktop.ViewModels;

/// <summary>Where, in relation to the row the pointer is over, a dragged step would be put.</summary>
public enum DropPlacement
{
    Before,
    After,
}

/// <summary>What dropping a step at a place would do.</summary>
public enum StepDropOutcome
{
    /// <summary>The step would change its place: the only outcome that is drawn as an insertion line.</summary>
    Move,

    /// <summary>The place is where the step already is: dropping there changes nothing.</summary>
    Unchanged,

    /// <summary>The step may not go there: it would break the structure of the sequence (or the sequence cannot be changed now).</summary>
    Rejected,
}

/// <summary>
/// The place a dragged step would be dropped at, as the draft judges it: the list (<see cref="ParentId"/>, <c>null</c>
/// for the sequence itself), the index in that list as it is before the step is taken out, and, when the place was found
/// from a row under the pointer, the rows the view marks. The view only reads this; it decides nothing.
/// </summary>
/// <param name="Outcome">Whether the step would move, stay or is refused.</param>
/// <param name="ParentId">The container the step would be in, or <c>null</c> for the top level.</param>
/// <param name="Index">The insertion index among the siblings, 0 to their count.</param>
/// <param name="Over">The row the pointer is over; it is shown as refused when the outcome is.</param>
/// <param name="Marker">The row that carries the insertion line: <paramref name="Over"/>, or the last row of its steps.</param>
/// <param name="MarkerBefore">The line is at the top of the marker row; otherwise at its bottom.</param>
/// <param name="Reason">Why the step may not go there, in a sentence; <c>null</c> when it may.</param>
public sealed record StepDropPlan(
    StepDropOutcome Outcome,
    Guid? ParentId,
    int Index,
    StepDraftViewModel? Over,
    StepDraftViewModel? Marker,
    bool MarkerBefore,
    string? Reason
)
{
    public bool IsMove => Outcome == StepDropOutcome.Move;
}

using System.Collections.Generic;

namespace Sidera.Desktop.ViewModels;

/// <summary>One line of what a Rig Track does: a step, shown nested when it is inside a Repeat.</summary>
public sealed record LaneLine(string Text, bool IsNested);

/// <summary>
/// What a Rig Track of a Multi-Rig block does, as the editor's overview of the block shows it: the rig, the steps (a
/// Repeat with the steps inside it) and the autofocus policy when there is one.
/// </summary>
public sealed record LaneSummary(string Name, IReadOnlyList<LaneLine> Lines, string? Autofocus, bool HasProblems)
{
    public bool HasAutofocus => Autofocus is not null;
}

using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Sequencing;

namespace Sidera.Desktop.ViewModels;

/// <summary>One line of the live "where is the sequence" display.</summary>
/// <param name="IsContainer">True for the lines describing containers around the running step.</param>
public sealed record SequenceStatusLine(string Text, bool IsContainer)
{
    /// <summary>
    /// One line per running leaf branch: the full path "Parallel › Imaging Block › Exposure 2s". A position is a
    /// leaf when no other active position is nested directly inside it.
    /// </summary>
    public static IReadOnlyList<SequenceStatusLine> ForActiveBranches(IReadOnlyCollection<SequenceExecutionPosition> active)
    {
        return active
            .Where(position => !active.Any(other => Equals(other.Parent, position)))
            .Select(position => new SequenceStatusLine(
                string.Join(" › ", From(position).Select(line => line.Text)), false))
            .ToList();
    }

    /// <summary>
    /// Turns the position of the running step into lines from the outside in, e.g.
    /// "Repeat × 3 · 2 / 3", "Imaging Block", "Exposure 2s". A container line carries the
    /// "n / m" of its running child, but only when there is more than one; a lone child adds no noise.
    /// </summary>
    public static IReadOnlyList<SequenceStatusLine> From(SequenceExecutionPosition? position)
    {
        var lines = new List<SequenceStatusLine>();
        if (position is null)
        {
            return lines;
        }

        var path = new List<SequenceExecutionPosition>();
        for (var p = position; p is not null; p = p.Parent)
        {
            path.Insert(0, p);
        }

        if (path[0].Count > 1)
        {
            lines.Add(new SequenceStatusLine($"Step {path[0].Index + 1} / {path[0].Count}", true));
        }

        for (var i = 0; i < path.Count - 1; i++)
        {
            var child = path[i + 1];
            var text = child.Count > 1
                ? $"{path[i].StepName} · {child.Index + 1} / {child.Count}"
                : path[i].StepName;
            lines.Add(new SequenceStatusLine(text, true));
        }

        lines.Add(new SequenceStatusLine(path[^1].StepName, false));
        return lines;
    }
}

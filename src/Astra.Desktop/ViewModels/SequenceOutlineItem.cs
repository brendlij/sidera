using System.Collections.Generic;
using System.Globalization;
using Astra.Core.Sequencing;
using Astra.Runtime.Sequencing;

namespace Astra.Desktop.ViewModels;

/// <summary>One line of the read-only sequence outline shown in the UI.</summary>
/// <param name="Title">For example "Repeat" or "Exposure".</param>
/// <param name="Detail">For example "×3" or "2s"; may be empty.</param>
/// <param name="IndentWidth">Horizontal indent in device-independent pixels.</param>
public sealed record SequenceOutlineItem(string Title, string Detail, double IndentWidth)
{
    private const double IndentPerLevel = 20;

    public static IReadOnlyList<SequenceOutlineItem> From(Sequence sequence)
    {
        var items = new List<SequenceOutlineItem>();
        foreach (var step in sequence.Steps)
        {
            Add(items, step, 0);
        }

        return items;
    }

    private static void Add(List<SequenceOutlineItem> items, ISequenceStep step, int depth)
    {
        var indent = depth * IndentPerLevel;
        switch (step)
        {
            case RepeatStep repeat:
                items.Add(new SequenceOutlineItem("Repeat", $"×{repeat.Count}", indent));
                Add(items, repeat.Child, depth + 1);
                break;
            case SequenceGroup group:
                items.Add(new SequenceOutlineItem(group.Name, string.Empty, indent));
                foreach (var child in group.Children)
                {
                    Add(items, child, depth + 1);
                }

                break;
            case DelayAction delay:
                items.Add(new SequenceOutlineItem(
                    "Wait",
                    string.Create(CultureInfo.InvariantCulture, $"{delay.Duration.TotalSeconds:0.##}s"),
                    indent));
                break;
            case CameraExposureAction exposure:
                items.Add(new SequenceOutlineItem(
                    "Exposure",
                    string.Create(CultureInfo.InvariantCulture, $"{exposure.Duration.TotalSeconds:0.##}s"),
                    indent));
                break;
            default:
                items.Add(new SequenceOutlineItem(step.Name, string.Empty, indent));
                break;
        }
    }
}

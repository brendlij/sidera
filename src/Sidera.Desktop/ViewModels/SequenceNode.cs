using System;
using System.Collections.Generic;
using System.Globalization;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop.ViewModels;

public enum SequenceNodeKind
{
    Step,
    Group,
    Repeat,
    Parallel
}

/// <summary>
/// One step of a sequence definition as the UI sees it: a title, a detail and where it sits in the tree. Built by
/// <see cref="SequenceNodeBuilder"/> from the definition; this presentation layer lives in the Desktop project so
/// the sequencing contracts stay free of UI concerns. It holds no execution state.
/// </summary>
public sealed class SequenceNode
{
    public SequenceNode(
        ISequenceStep? step,
        SequenceNodeKind kind,
        string title,
        string detail,
        string? subText,
        int depth,
        Guid? draftId = null,
        string? numberLabel = null,
        bool isProblem = false,
        bool isHidden = false,
        AutofocusOrigin? autofocusOrigin = null)
    {
        AutofocusOrigin = autofocusOrigin;
        Step = step;
        DraftId = draftId;
        NumberLabel = numberLabel;
        IsProblem = isProblem;
        IsHidden = isHidden;
        Kind = kind;
        Title = title;
        Detail = detail;
        SubText = subText;
        Depth = depth;
    }

    /// <summary>The runtime step this node describes; <c>null</c> for a row of a draft that has not been built.</summary>
    public ISequenceStep? Step { get; }

    /// <summary>The step of the editor's draft this node was made from, if it was made from one.</summary>
    public Guid? DraftId { get; }

    /// <summary>The number shown in front of the node, for example "2." or "2.1"; <c>null</c> when nodes are not numbered.</summary>
    public string? NumberLabel { get; }

    /// <summary>
    /// The node exists so that running positions can be followed through it, but is not listed: the group a Repeat
    /// of the editor puts around its steps.
    /// </summary>
    public bool IsHidden { get; }

    /// <summary>
    /// Why this autofocus runs, when it is one the policy of a track generated: it has no draft step. <c>null</c> for
    /// every other node, and for an autofocus the user wrote.
    /// </summary>
    public AutofocusOrigin? AutofocusOrigin { get; }

    /// <summary>The <see cref="SubText"/> is a problem with the step, not a description.</summary>
    public bool IsProblem { get; }
    public SequenceNodeKind Kind { get; }
    public string Title { get; }
    public string Detail { get; }

    /// <summary>A second, static line, for example the settle criterion of a dither.</summary>
    public string? SubText { get; }

    public int Depth { get; }
    public List<SequenceNode> Children { get; } = new();

    /// <summary>
    /// The node of the child that runs as position <paramref name="index"/> of this node: for a repeat always its one
    /// child (the index is the iteration), for a group or parallel step the child with that index, otherwise none.
    /// </summary>
    public SequenceNode? ChildAt(int index) => Kind switch
    {
        SequenceNodeKind.Repeat => Children.Count > 0 ? Children[0] : null,
        SequenceNodeKind.Group or SequenceNodeKind.Parallel => index >= 0 && index < Children.Count ? Children[index] : null,
        _ => null,
    };
}

public static class SequenceNodeBuilder
{
    /// <summary>The top-level nodes of <paramref name="sequence"/>, in order, each with its descendants.</summary>
    public static IReadOnlyList<SequenceNode> Build(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var roots = new List<SequenceNode>();
        foreach (var step in sequence.Steps)
        {
            roots.Add(Build(step, 0));
        }

        return roots;
    }

    /// <summary>All nodes in display order (each node before its children).</summary>
    public static IReadOnlyList<SequenceNode> Flatten(IEnumerable<SequenceNode> roots)
    {
        var all = new List<SequenceNode>();
        void Visit(SequenceNode node)
        {
            if (!node.IsHidden)
            {
                all.Add(node);
            }

            node.Children.ForEach(Visit);
        }

        foreach (var root in roots)
        {
            Visit(root);
        }

        return all;
    }

    private static SequenceNode Build(ISequenceStep step, int depth)
    {
        switch (step)
        {
            case RepeatStep repeat:
            {
                var node = new SequenceNode(repeat, SequenceNodeKind.Repeat, "Repeat", $"×{repeat.Count}", null, depth);
                node.Children.Add(Build(repeat.Child, depth + 1));
                return node;
            }
            case SequenceGroup group:
            {
                var node = new SequenceNode(group, SequenceNodeKind.Group, group.Name, "Group", null, depth);
                foreach (var child in group.Children)
                {
                    node.Children.Add(Build(child, depth + 1));
                }

                return node;
            }
            case ParallelStep parallel:
            {
                var node = new SequenceNode(
                    parallel, SequenceNodeKind.Parallel, parallel.Name, $"Parallel · {parallel.Children.Count} branches",
                    parallel.CoordinationGroup is { } group ? $"Coordinated as {group}" : null, depth);
                foreach (var child in parallel.Children)
                {
                    node.Children.Add(Build(child, depth + 1));
                }

                return node;
            }
            case CameraExposureAction exposure:
                return Leaf(exposure, "Exposure", Seconds(exposure.Duration), null, depth);
            case DelayAction delay:
                return Leaf(delay, "Wait", Seconds(delay.Duration), null, depth);
            case SlewAction slew:
                return Leaf(
                    slew, "Slew",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"RA {slew.Target.RightAscensionHours:0.###} h · Dec {slew.Target.DeclinationDegrees:+0.##;-0.##;0}°"),
                    null, depth);
            case SafePointStep:
                return Leaf(step, "Safe Point", string.Empty, null, depth);
            case StartGuidingAction:
                return Leaf(step, "Start guiding", string.Empty, null, depth);
            case StopGuidingAction:
                return Leaf(step, "Stop guiding", string.Empty, null, depth);
            case DitherAction dither:
                return Leaf(
                    dither, "Dither",
                    string.Create(CultureInfo.InvariantCulture, $"{dither.AmplitudePixels:0.##} px"),
                    dither.SettleOptions is { } settle
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"then settle ≤ {settle.MaximumErrorPixels:0.##} px for {settle.StableDuration.TotalSeconds:0.##} s")
                        : null,
                    depth);
            default:
                return Leaf(step, step.Name, string.Empty, null, depth);
        }
    }

    private static SequenceNode Leaf(ISequenceStep step, string title, string detail, string? subText, int depth) =>
        new(step, SequenceNodeKind.Step, title, detail, subText, depth);

    private static string Seconds(TimeSpan duration) =>
        string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.##} s");
}

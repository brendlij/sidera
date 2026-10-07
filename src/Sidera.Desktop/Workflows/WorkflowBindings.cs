using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Rigs;
using Sidera.Runtime.Rigs;

namespace Sidera.Desktop.Workflows;

/// <summary>
/// What a workflow's references to imaging setups mean. A workflow refers to an imaging path (<see cref="ImagingBindingId"/>, derived from a camera) or, in a document from before paths existed, to a
/// setup by the id of the setup object. <see cref="Canonical(WorkflowDefinition, ISetupSource?)"/> turns every reference that resolves into the path of the setup it resolves to, so that from then on
/// bindings are compared as paths and never by the id of a setup object: an implicit setup that later becomes an explicit one, or a document that named a setup by its id, ends up with the same
/// binding. A reference that does not resolve (its camera was removed or replaced, or there are several cameras and nothing to say which) is left as it is: it is reported, never bound to another camera.
/// </summary>
public static class WorkflowBindings
{
    /// <summary>The path of the setup that a reference means; the reference itself when it does not resolve.</summary>
    public static ImagingBindingId? Canonical(ImagingBindingId? binding, ISetupSource? setups) =>
        binding is { } reference && setups is not null && setups.TryResolve(reference, out var rig) && rig is not null ? ImagingBindingId.Of(rig) : binding;

    /// <summary>The workflow with every reference to an imaging setup as the path of the setup it means; the same instance when nothing changes.</summary>
    public static WorkflowDefinition Canonical(WorkflowDefinition workflow, ISetupSource? setups)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        if (setups is null)
        {
            return workflow;
        }

        var changed = false;
        ImagingBindingId? Map(ImagingBindingId? binding)
        {
            var mapped = Canonical(binding, setups);
            changed |= mapped != binding;
            return mapped;
        }

        WorkflowStep MapStep(WorkflowStep step) => Map(step.Setup) is var setup && setup != step.Setup ? step with { Setup = setup } : step;

        var prepare = workflow.Prepare.Select(MapStep).ToList();
        var finish = workflow.Finish.Select(MapStep).ToList();
        var imaging = workflow.Imaging.Select(block => Map(block.Setup) is var setup && setup != block.Setup ? block with { Setup = setup } : block).ToList();
        var target = Map(workflow.Target.PointingSetup) is var pointing && pointing != workflow.Target.PointingSetup ? workflow.Target with { PointingSetup = pointing } : workflow.Target;
        var dither = Map(workflow.Dither.CountedSetup) is var counted && counted != workflow.Dither.CountedSetup ? workflow.Dither with { CountedSetup = counted } : workflow.Dither;
        var policies = workflow.AutofocusPolicies.Select(p => Map(p.Setup) is { } setup && setup != p.Setup ? p with { Setup = setup } : p).ToList();

        return changed
            ? workflow with { Target = target, Prepare = prepare, Imaging = imaging, Finish = finish, Dither = dither, AutofocusPolicies = policies }
            : workflow;
    }

    /// <summary>A reference in words for a message: "the imaging setup of camera 'camera.main'", "the imaging setup 'rig.main'".</summary>
    public static string Describe(ImagingBindingId binding) =>
        binding.TryGetCamera(out var camera) ? $"the imaging setup of camera '{camera}'" : $"the imaging setup '{binding}'";

    /// <summary>The sentence for a reference that does not resolve, with what may have happened and what to do.</summary>
    public static string Unavailable(ImagingBindingId binding) =>
        binding.TryGetCamera(out var camera)
            ? $"The imaging setup of camera '{camera}' is not available (camera removed or replaced). Choose another one."
            : $"The imaging setup '{binding}' does not exist any more. Choose another one.";
}

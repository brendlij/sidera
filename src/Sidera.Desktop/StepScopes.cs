using System;
using System.Collections.Generic;
using System.Linq;
using Sidera.Core.Devices;
using Sidera.Core.Rigs;

namespace Sidera.Desktop;

/// <summary>Whose a step is: the rig it works for, a device it names, or the session as a whole.</summary>
public enum StepScope
{
    /// <summary>Selected by a rig; the devices are the rig's. A rig that has another mount or guider than another rig uses its own.</summary>
    RigLocal,

    /// <summary>Names a device directly, which may be shared by several rigs (a mount, a guider) or may be in no rig at all.</summary>
    Device,

    /// <summary>Orchestrates other steps; holds no equipment of its own (each child takes its own when it runs).</summary>
    Session,
}

/// <summary>A device a step uses and what it is used for.</summary>
public sealed record ResolvedDevice(string Role, DeviceId Device);

/// <summary>
/// What every kind of step is, and which devices a step resolves to, in one place. A rig-local step never reads a global mount or guider: its devices are those of its rig. The table:
/// <code>
/// kind                 scope      devices it holds while it runs
/// -------------------  ---------  --------------------------------------------------------------
/// Exposure             Device     the camera it names
/// RigExposure          RigLocal   camera of the track's rig
/// MoveFocuser          Device     the focuser it names
/// RigMoveFocuser       RigLocal   focuser of the track's rig
/// ChangeFilter         Device     the filter wheel it names
/// RigChangeFilter      RigLocal   filter wheel of the track's rig
/// Autofocus            RigLocal   camera + focuser of the rig
/// RigAutofocus         RigLocal   camera + focuser of the track's rig
/// PlateSolve           RigLocal   camera of the rig (the mount of the rig only gives the solver a hint)
/// SlewAndCenter        RigLocal   camera + mount of the rig
/// RotateToAngle        RigLocal   rotator + every camera on it
/// RotateAndVerify      RigLocal   rotator + every camera on it
/// CenterAndRotate      RigLocal   mount + rotator + every camera on it
/// Slew                 Device     the mount it names
/// SyncMountToSolved    Device     the mount it names (and only that mount's own plate solve)
/// StartGuiding         Device     the guider it names
/// StopGuiding          Device     the guider it names
/// Dither               Device     guider + mount + camera it names
/// Repeat, MultiRig,    Session    nothing of its own; each child takes what it needs (a Delay holds nothing)
/// RigTrack, Delay
/// </code>
/// The plate solver is not a device: it is taken by the operation, never as a resource of the step.
/// </summary>
public static class StepScopes
{
    public static StepScope ScopeOf(SequenceStepKind kind) => kind switch
    {
        SequenceStepKind.RigExposure or SequenceStepKind.RigMoveFocuser or SequenceStepKind.RigChangeFilter or SequenceStepKind.RigAutofocus
            or SequenceStepKind.Autofocus or SequenceStepKind.PlateSolve or SequenceStepKind.SlewAndCenter or SequenceStepKind.RotateToAngle
            or SequenceStepKind.RotateAndVerify or SequenceStepKind.CenterAndRotate => StepScope.RigLocal,
        SequenceStepKind.Exposure or SequenceStepKind.MoveFocuser or SequenceStepKind.ChangeFilter or SequenceStepKind.Slew or SequenceStepKind.SyncMountToSolved
            or SequenceStepKind.StartGuiding or SequenceStepKind.StopGuiding or SequenceStepKind.Dither => StepScope.Device,
        SequenceStepKind.Repeat or SequenceStepKind.MultiRig or SequenceStepKind.RigTrack or SequenceStepKind.Delay => StepScope.Session,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind of step has no scope."),
    };

    /// <summary>
    /// The mount a rig-local step works with: the one of its rig. A step of an older file names a mount itself, which is kept when the rig has none; the session's shared mount is
    /// the last resort for a rig that has none and a step that names none. <c>null</c> when nobody names one.
    /// </summary>
    public static DeviceId? EffectiveMount(Rig? rig, DeviceId? namedByStep, SharedEquipmentDraft? shared) => rig?.MountId ?? namedByStep ?? shared?.MountId;

    /// <summary>The guider of a rig-local step: the one of the rig, else the session's shared one.</summary>
    public static DeviceId? EffectiveGuider(Rig? rig, DeviceId? namedByStep, SharedEquipmentDraft? shared) => rig?.GuiderId ?? namedByStep ?? shared?.GuiderId;

    /// <summary>
    /// The devices a step holds, from its rig. <paramref name="trackRig"/> is the rig of the track the step is in. Containers resolve to what their children resolve to, so a Repeat holds
    /// what its body holds.
    /// </summary>
    public static IReadOnlyList<ResolvedDevice> Resolve(SequenceStepDraft step, SequenceDraftContext? context, Rig? trackRig = null)
    {
        var devices = new List<ResolvedDevice>();

        void Add(string role, DeviceId? id)
        {
            if (id is { } device && !devices.Any(d => d.Device == device && d.Role == role))
            {
                devices.Add(new ResolvedDevice(role, device));
            }
        }

        Rig? RigOf(RigId? id) => id is { } rigId && context?.Rigs is { } rigs && rigs.TryGet(rigId, out var found) ? found : null;

        void Rotation(Rig? rig)
        {
            if (rig?.RotatorId is not { } rotator)
            {
                return;
            }

            Add("rotator", rotator);
            Add("camera", rig.CameraId);
            foreach (var other in context?.Rigs?.GetAll().Where(r => r.RotatorId == rotator) ?? [])
            {
                Add("camera", other.CameraId);
            }
        }

        switch (step)
        {
            case ExposureStepDraft e:
                Add("camera", e.CameraId);
                break;
            case RigExposureStepDraft:
                Add("camera", trackRig?.CameraId);
                break;
            case MoveFocuserStepDraft f:
                Add("focuser", f.FocuserId);
                break;
            case RigMoveFocuserStepDraft:
                Add("focuser", trackRig?.FocuserId);
                break;
            case ChangeFilterStepDraft c:
                Add("filter wheel", c.FilterWheelId);
                break;
            case RigChangeFilterStepDraft:
                Add("filter wheel", trackRig?.FilterWheelId);
                break;
            case AutofocusStepDraft a:
                Add("camera", RigOf(a.RigId)?.CameraId);
                Add("focuser", RigOf(a.RigId)?.FocuserId);
                break;
            case RigAutofocusStepDraft:
                Add("camera", trackRig?.CameraId);
                Add("focuser", trackRig?.FocuserId);
                break;
            case PlateSolveStepDraft p:
                Add("camera", RigOf(p.RigId)?.CameraId);
                break;
            case SlewAndCenterStepDraft c:
            {
                var rig = RigOf(c.RigId);
                Add("camera", rig?.CameraId);
                Add("mount", EffectiveMount(rig, c.MountId, context?.Shared));
                break;
            }

            case RotateToAngleStepDraft r:
                Rotation(RigOf(r.RigId));
                break;
            case RotateAndVerifyStepDraft r:
                Rotation(RigOf(r.RigId));
                break;
            case CenterAndRotateStepDraft c:
            {
                var rig = RigOf(c.RigId);
                Rotation(rig);
                Add("mount", EffectiveMount(rig, c.MountId, context?.Shared));
                break;
            }

            case SlewStepDraft s:
                Add("mount", s.MountId);
                break;
            case SyncMountStepDraft m:
                Add("mount", m.MountId);
                break;
            case StartGuidingStepDraft g:
                Add("guider", g.GuiderId);
                break;
            case StopGuidingStepDraft g:
                Add("guider", g.GuiderId);
                break;
            case DitherStepDraft d:
                Add("guider", d.GuiderId);
                Add("mount", d.MountId);
                Add("camera", d.CameraId);
                break;
            case RepeatStepDraft repeat:
                foreach (var child in repeat.Children)
                {
                    foreach (var resolved in Resolve(child, context, trackRig))
                    {
                        Add(resolved.Role, resolved.Device);
                    }
                }

                break;
        }

        return devices;
    }
}

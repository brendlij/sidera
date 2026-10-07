using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Sidera.Core.Devices;
using Sidera.Core.Mounts;
using Sidera.Core.Rigs;
using Sidera.Desktop.Workflows;
using Sidera.Runtime.Astrometry;
using Sidera.Runtime.Rigs;
using DeviceOperation = Sidera.Runtime.Sequencing.DeviceOperation;

namespace Sidera.Desktop.Sessions;

/// <summary>Something wrong with a session, in words about setups and actions; <see cref="ElementId"/> is the action, block, lane or target it is about, or <c>null</c> when it is about the session.</summary>
public sealed record SessionProblem(Guid? ElementId, string Message);

/// <summary>
/// What a session compiles to: the steps the editor, the document and the runtime already have, what is wrong, what the user should be told about how things were mapped (<see cref="Notes"/>: not errors), and for
/// each compiled step the action, block, lane or target of the session it came from, so that a problem of the sequence is shown where it was made.
/// </summary>
public sealed record SessionCompilation(
    IReadOnlyList<SequenceStepDraft> Steps,
    IReadOnlyList<SessionProblem> Problems,
    IReadOnlyList<string> Notes,
    IReadOnlyDictionary<Guid, Guid> Origins)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Compiles a <see cref="SessionDefinition"/> into the steps the runtime already runs; nothing below it knows about sessions:
/// <code>
/// Start          →  its actions, once, for every setup that is imaged
/// each Target    →  its preparation (a slew is made once for each mount, a guider is started once), then one Multi-Rig block:
///                   a Rig Track for each lane (its blocks in order: the actions that come before the first exposure once, then a Repeat of the rest),
///                   the dither and autofocus automation of the blocks, the meridian flip of the session with the coordinates of the target, the limits of the target
/// End            →  its actions, once, for every setup that was imaged
/// </code>
/// Compiling is deterministic: the same session gives the same steps with the same ids (they are derived from the ids of the session's elements). Devices come from the imaging setup of the lane (resolved by the
/// stable imaging path, never by a name); nothing is guessed between several setups. Setups on one mount are centered once; setups on different mounts are separate pointing systems.
/// </summary>
public static class SessionCompiler
{
    /// <summary>What a repeat without a count is given as one: it ends by its conditions long before.</summary>
    public const int OpenEndedRepeat = 100_000;

    /// <summary>A stable id for a draft step made from <paramref name="source"/>, told apart from the others made from it by <paramref name="purpose"/>.</summary>
    public static Guid Derive(Guid source, string purpose)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(source.ToString("N", CultureInfo.InvariantCulture) + "/" + purpose));
        return new Guid(bytes);
    }

    /// <param name="applicationFlip">The meridian flip settings of the application: what a session that follows the defaults uses.</param>
    /// <param name="usable">The setups that can image now (their camera is connected); <c>null</c>: every setup there is counts.</param>
    public static SessionCompilation Compile(SessionDefinition session, ISetupSource? setups, MeridianFlipSettings? applicationFlip = null, IReadOnlySet<RigId>? usable = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Compilation(session, setups, applicationFlip, usable).Run();
    }

    private sealed class Compilation(SessionDefinition session, ISetupSource? setups, MeridianFlipSettings? applicationFlip, IReadOnlySet<RigId>? usable)
    {
        private readonly List<SessionProblem> _problems = [];
        private readonly List<string> _notes = [];
        private readonly Dictionary<Guid, Guid> _origins = [];
        private readonly List<SequenceStepDraft> _steps = [];
        private readonly Dictionary<Guid, Rig?> _laneRig = [];
        private IReadOnlyList<Rig> _pool = [];

        public SessionCompilation Run()
        {
            var all = setups?.GetAll().OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? [];

            // The setups that "the only setup" can mean: the usable ones. Nothing is guessed between several of them.
            _pool = usable is null ? all : all.Where(r => usable.Contains(r.Id)).ToList();

            var targets = session.Targets.Where(t => t.Enabled).ToList();

            // The setups that are imaged, in the order they first appear: what the actions of the start and of the end work with.
            var imaged = new List<Rig>();
            foreach (var target in targets)
            {
                foreach (var lane in target.Lanes.Where(l => l.Blocks.Any(b => b.Enabled)))
                {
                    if (ResolveLane(lane) is { } rig && !imaged.Contains(rig))
                    {
                        imaged.Add(rig);
                    }
                }
            }

            var sessionScope = imaged.Count > 0 ? imaged : _pool.Count == 1 ? _pool : (IReadOnlyList<Rig>)[];
            var sessionTarget = targets.FirstOrDefault();

            CompileActions(session.Start, sessionScope, null, sessionTarget, "start");
            foreach (var target in targets)
            {
                CompileTarget(target);
            }

            CompileActions(session.End, sessionScope, null, sessionTarget, "end");
            return new SessionCompilation(_steps, _problems, _notes, _origins);
        }

        // ---- setups

        private Rig? Find(ImagingBindingId? id) => id is { } binding && setups is not null && setups.TryResolve(binding, out var rig) ? rig : null;

        private void Problem(Guid? element, string message) => _problems.Add(new SessionProblem(element, message));

        private Rig? ResolveLane(SetupLane lane)
        {
            if (_laneRig.TryGetValue(lane.Id, out var known))
            {
                return known;
            }

            Rig? rig;
            if (lane.Setup is { } binding)
            {
                rig = Find(binding);
                if (rig is null)
                {
                    Problem(lane.Id, WorkflowBindings.Unavailable(binding));
                }
            }
            else if (_pool.Count == 1)
            {
                rig = _pool[0];
            }
            else
            {
                rig = null;
                Problem(lane.Id, _pool.Count > 1
                    ? $"There are several imaging setups ({string.Join(", ", _pool.Select(r => r.Name))}): choose the one for this sequence."
                    : "No imaging setup. Connect a camera or add a setup on the Equipment page; with several cameras Sidera does not guess.");
            }

            _laneRig[lane.Id] = rig;
            return rig;
        }

        // ---- a target

        private void CompileTarget(SessionTarget target)
        {
            var conditionTarget = new ConditionTargetDraft(target.Name, target.RightAscensionHours, target.DeclinationDegrees);

            // The lanes that image, with their setup; two lanes for one setup are a mistake that is said, not merged.
            var lanes = new List<(SetupLane Lane, Rig Rig)>();
            foreach (var lane in target.Lanes.Where(l => l.Blocks.Any(b => b.Enabled)))
            {
                if (ResolveLane(lane) is not { } rig)
                {
                    continue;
                }

                if (lanes.Any(l => l.Rig.Id == rig.Id))
                {
                    Problem(lane.Id, $"{rig.Name} already has a sequence under this target. Put the blocks in one sequence.");
                    continue;
                }

                lanes.Add((lane, rig));
            }

            var scope = lanes.Count > 0 ? lanes.Select(l => l.Rig).ToList() : _pool.Count == 1 ? _pool : (IReadOnlyList<Rig>)[];
            CompileActions(target.Preparation, scope, target, target, "preparation");
            if (lanes.Count == 0)
            {
                return;
            }

            // ---- the tracks
            var tracks = new List<RigTrackDraft>();
            var rigs = lanes.Select(l => l.Rig).ToList();
            foreach (var (lane, rig) in lanes)
            {
                tracks.Add(CompileLane(target, lane, rig, conditionTarget));
            }

            var ditherPolicy = DitherOf(target, lanes);
            var flip = FlipOf(target, rigs, lanes, ditherPolicy);
            _steps.Add(new MultiRigStepDraft(
                Derive(target.Id, "imaging"), tracks, ditherPolicy, SingleTrack: true, MeridianFlip: flip,
                TargetStop: target.Limits.Count > 0 ? new StopConditionsDraft(target.Limits, conditionTarget) : null));
        }

        private RigTrackDraft CompileLane(SessionTarget target, SetupLane lane, Rig rig, ConditionTargetDraft conditionTarget)
        {
            var trackId = Derive(target.Id, "track:" + rig.Id.Value);
            var steps = new List<SequenceStepDraft>();
            int? slot = null;
            var blocks = lane.Blocks.Where(b => b.Enabled).ToList();
            _origins[trackId] = blocks[0].Id;

            foreach (var block in blocks)
            {
                var exposure = block.FirstExposure;
                if (exposure is null)
                {
                    Problem(block.Id, "Add an Exposure: a block without one makes no frames.");
                    continue;
                }

                if (!double.IsFinite(exposure.Seconds) || exposure.Seconds <= 0)
                {
                    Problem(block.Id, "The exposure must be longer than 0 seconds.");
                }

                if (!block.Repeat.HasEnd)
                {
                    Problem(block.Id, "Say how often the block repeats: a number of times, or until something happens.");
                }
                else if (block.Repeat.Count is < 1)
                {
                    Problem(block.Id, "A block needs at least 1 frame.");
                }

                var focus = block.Automation.Focus is { IsActive: true } f ? f : null;
                if (focus is not null && rig.FocuserId is null)
                {
                    Problem(block.Id, $"{rig.Name} has no focuser, so it cannot focus by itself. Give the setup a focuser or turn Autofocus off.");
                    focus = null;
                }

                if (focus is { AfterFilterChange: true } && rig.FilterWheelId is null)
                {
                    Problem(block.Id, $"{rig.Name} has no filter wheel, so Autofocus cannot follow a filter change.");
                }

                // The actions before the first exposure run once, when the block starts; the others are what repeats.
                var start = block.BodyStart;
                var before = block.Actions.Take(start).Where(a => a.Enabled).ToList();
                var body = block.Actions.Skip(start).Where(a => a.Enabled).ToList();

                var focused = false;
                foreach (var action in before)
                {
                    switch (action)
                    {
                        case SetFilterAction filter:
                        {
                            if (rig.FilterWheelId is null)
                            {
                                Problem(action.Id, $"{rig.Name} has no filter wheel, so it cannot use a filter. Remove Set Filter or give the setup a filter wheel.");
                                break;
                            }

                            if (filter.Slot != slot)
                            {
                                var change = Derive(action.Id, "filter");
                                _origins[change] = action.Id;
                                steps.Add(new RigChangeFilterStepDraft(change, filter.Slot));
                                slot = filter.Slot;

                                // After a filter change the lens may need other focus: the automation does it here, where the filter was turned.
                                if (focus is { AfterFilterChange: true } afterFilter && rig.FocuserId is not null)
                                {
                                    var af = Derive(action.Id, "focus");
                                    _origins[af] = action.Id;
                                    steps.Add(new RigAutofocusStepDraft(af, afterFilter.Settings.ExposureSeconds, afterFilter.Settings.StepSize, afterFilter.Settings.SampleCount));
                                    focused = true;
                                }
                            }

                            break;
                        }

                        default:
                            AddLeaf(action, rig, conditionTarget, steps, inRepeat: false, ref slot, ref focused);
                            break;
                    }
                }

                if (focus is { AtBlockStart: true } atStart && !focused && rig.FocuserId is not null)
                {
                    var af = Derive(block.Id, "focus-start");
                    _origins[af] = block.Id;
                    steps.Add(new RigAutofocusStepDraft(af, atStart.Settings.ExposureSeconds, atStart.Settings.StepSize, atStart.Settings.SampleCount));
                }

                var children = new List<LeafStepDraft>();
                var ignored = false;
                foreach (var action in body)
                {
                    var leaf = new List<SequenceStepDraft>();
                    AddLeaf(action, rig, conditionTarget, leaf, inRepeat: true, ref slot, ref ignored);
                    children.AddRange(leaf.OfType<LeafStepDraft>());
                }

                var until = block.Repeat.Until.Concat(block.Limits).ToList();
                var repeat = Derive(block.Id, "repeat");
                _origins[repeat] = block.Id;
                steps.Add(new RepeatStepDraft(
                    repeat, Math.Max(1, block.Repeat.Count ?? OpenEndedRepeat), children, until.Count > 0 ? new StopConditionsDraft(until, conditionTarget) : null));
            }

            return new RigTrackDraft(trackId, rig.Id, steps, FocusPolicyOf(rig, blocks));
        }

        // An action that is a step of a track: the ones a track may hold.
        private void AddLeaf(
            SessionAction action, Rig rig, ConditionTargetDraft conditionTarget, List<SequenceStepDraft> steps, bool inRepeat, ref int? slot, ref bool focused)
        {
            var id = Derive(action.Id, inRepeat ? "body" : "before");
            _origins[id] = action.Id;
            switch (action)
            {
                case ExposureAction exposure:
                    steps.Add(new RigExposureStepDraft(id, Math.Max(double.Epsilon, exposure.Seconds)));
                    break;
                case SetFilterAction filter:
                    if (rig.FilterWheelId is null)
                    {
                        Problem(action.Id, $"{rig.Name} has no filter wheel, so it cannot use a filter. Remove Set Filter or give the setup a filter wheel.");
                    }
                    else
                    {
                        steps.Add(new RigChangeFilterStepDraft(id, filter.Slot));
                        slot = filter.Slot;
                    }

                    break;
                case AutofocusAction autofocus:
                    if (rig.FocuserId is null)
                    {
                        Problem(action.Id, $"{rig.Name} has no focuser, so it cannot focus. Give the setup a focuser on the Equipment page.");
                    }
                    else
                    {
                        steps.Add(new RigAutofocusStepDraft(id, autofocus.Settings.ExposureSeconds, autofocus.Settings.StepSize, autofocus.Settings.SampleCount));
                        focused = true;
                    }

                    break;
                case MoveFocuserAction focuser:
                    if (rig.FocuserId is null)
                    {
                        Problem(action.Id, $"{rig.Name} has no focuser. Give the setup a focuser on the Equipment page.");
                    }
                    else
                    {
                        steps.Add(new RigMoveFocuserStepDraft(id, focuser.Position));
                    }

                    break;
                case WaitAction wait:
                    steps.Add(new DelayStepDraft(id, wait.Seconds));
                    break;
                case WaitUntilAction until:
                    if (until.Conditions.Count == 0)
                    {
                        Problem(action.Id, "Choose what to wait for.");
                    }

                    steps.Add(new WaitUntilStepDraft(id, until.Conditions, conditionTarget));
                    break;
                default:
                    Problem(action.Id, $"{ActionCatalog.Of(action.Kind).Title} cannot be part of a block: it belongs in the preparation of the target, or at the start or end of the session.");
                    break;
            }
        }

        // The autofocus the blocks of a lane ask for by interval: one clock for the lane, the shortest of its blocks.
        private RigAutofocusPolicyDraft? FocusPolicyOf(Rig rig, IReadOnlyList<SequenceBlock> blocks)
        {
            var asking = blocks.Select(b => b.Automation.Focus).Where(f => f is { EveryMinutes: > 0 }).Select(f => f!).ToList();
            if (asking.Count == 0 || rig.FocuserId is null)
            {
                return null;
            }

            var interval = asking.Min(f => f.EveryMinutes);
            if (asking.Select(f => f.EveryMinutes).Distinct().Count() > 1)
            {
                _notes.Add(string.Create(CultureInfo.InvariantCulture, $"{rig.Name} focuses every {interval:0.##} minutes while it images: the blocks ask for different intervals, and the shortest one counts."));
            }

            var settings = asking[0].Settings;
            return new RigAutofocusPolicyDraft(true, false, false, settings.ExposureSeconds, settings.StepSize, settings.SampleCount, interval);
        }

        // The dither the blocks ask for: counted on the first lane that asks, and every setup that shares its mount or guider waits for it.
        private MultiRigDitherPolicyDraft? DitherOf(SessionTarget target, IReadOnlyList<(SetupLane Lane, Rig Rig)> lanes)
        {
            var asking = lanes
                .SelectMany(l => l.Lane.Blocks.Where(b => b.Enabled && b.Automation.Dither is not null).Select(b => (l.Rig, Block: b, Dither: b.Automation.Dither!)))
                .ToList();
            if (asking.Count == 0)
            {
                return null;
            }

            var (rig, block, dither) = asking[0];
            if (dither.EveryFrames < 1)
            {
                Problem(block.Id, "Dither every N exposures needs N of at least 1.");
            }

            if (rig.MountId is null)
            {
                Problem(block.Id, $"Dither needs a mount: {rig.Name} has none. Give the setup a mount on the Equipment page.");
            }

            if (rig.GuiderId is null)
            {
                Problem(block.Id, $"Dither needs a guider: {rig.Name} has none. Give the setup a guider on the Equipment page.");
            }

            if (asking.Any(a => a.Rig.Id != rig.Id) || asking.Any(a => a.Dither.EveryFrames != dither.EveryFrames))
            {
                _notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dither: every {dither.EveryFrames} exposures of {rig.Name}. The other setups that share its mount or guider wait for each dither."));
            }

            var s = dither.Settings;
            return new MultiRigDitherPolicyDraft(true, rig.Id, Math.Max(1, dither.EveryFrames), s.AmplitudePixels, s.SettleThresholdPixels, s.SettleStableSeconds, s.SettleTimeoutSeconds);
        }

        // The meridian flip of the session for this target: for every mount of the imaged setups, once, for all the setups on it. It slews back to the target it was made for.
        private MeridianFlipPolicyDraft? FlipOf(
            SessionTarget target, IReadOnlyList<Rig> rigs, IReadOnlyList<(SetupLane Lane, Rig Rig)> lanes, MultiRigDitherPolicyDraft? dither)
        {
            var settings = session.Automation.Flip ?? applicationFlip ?? new MeridianFlipSettings();
            if (!settings.Enabled)
            {
                return null;
            }

            foreach (var problem in settings.Problems())
            {
                Problem(null, "Meridian flip: " + problem);
            }

            if (!rigs.Any(r => r.MountId is not null))
            {
                Problem(lanes[0].Lane.Id, "The meridian flip needs a setup with a mount. Give a setup a mount on the Equipment page, or turn the flip off.");
            }

            return new MeridianFlipPolicyDraft(
                settings, target.RightAscensionHours, target.DeclinationDegrees, target.Name, target.RotationDegrees, null,
                dither?.AmplitudePixels ?? 1.5, dither?.SettleThresholdPixels ?? 0.5, dither?.SettleStableSeconds ?? 1, Math.Max(dither?.SettleTimeoutSeconds ?? 60, 60));
        }

        // ---- actions outside the lanes: the start, the preparation of a target, the end

        private void CompileActions(IReadOnlyList<SessionAction> actions, IReadOnlyList<Rig> scope, SessionTarget? target, SessionTarget? conditionSource, string section)
        {
            var conditionTarget = conditionSource is null
                ? new ConditionTargetDraft(string.Empty, SequenceDraftDefaults.TargetRightAscensionHours, SequenceDraftDefaults.TargetDeclinationDegrees)
                : new ConditionTargetDraft(conditionSource.Name, conditionSource.RightAscensionHours, conditionSource.DeclinationDegrees);

            foreach (var action in actions.Where(a => a.Enabled))
            {
                var explicitRig = action.Setup is null ? null : Find(action.Setup);
                if (action.Setup is not null && explicitRig is null)
                {
                    Problem(action.Id, WorkflowBindings.Unavailable(action.Setup.Value));
                    continue;
                }

                // Without an override: the setups that are imaged; before there are any, the only setup there is.
                var rigs = explicitRig is not null ? [explicitRig] : scope;
                CompileAction(action, rigs, explicitRig, target, conditionTarget);
            }
        }

        private void CompileAction(SessionAction action, IReadOnlyList<Rig> rigs, Rig? explicitRig, SessionTarget? target, ConditionTargetDraft conditionTarget)
        {
            string Who(string what) => explicitRig is not null ? $"{explicitRig.Name} has no {what}." : $"No imaging setup has a {what}.";

            switch (action)
            {
                case WaitAction wait:
                    Add(Derive(action.Id, "x"), new DelayStepDraft(Derive(action.Id, "x"), wait.Seconds), action.Id);
                    break;
                case WaitUntilAction until:
                    if (until.Conditions.Count == 0)
                    {
                        Problem(action.Id, "Choose what to wait for.");
                    }

                    Add(Derive(action.Id, "x"), new WaitUntilStepDraft(Derive(action.Id, "x"), until.Conditions, conditionTarget), action.Id);
                    break;
                case StartGuidingAction or StopGuidingAction:
                {
                    var guiders = rigs.Select(r => r.GuiderId).OfType<DeviceId>().Distinct().ToList();
                    if (guiders.Count == 0)
                    {
                        Problem(action.Id, Who("guider") + " Give the setup a guider on the Equipment page.");
                        break;
                    }

                    foreach (var guider in guiders)
                    {
                        var id = Derive(action.Id, guider.Value);
                        Add(id, action is StartGuidingAction ? new StartGuidingStepDraft(id, guider) : new StopGuidingStepDraft(id, guider), action.Id);
                    }

                    break;
                }

                case AutofocusAction autofocus:
                {
                    var focusable = rigs.Where(r => r.FocuserId is not null).ToList();
                    if (focusable.Count == 0)
                    {
                        Problem(action.Id, Who("focuser") + " Give the setup a focuser on the Equipment page.");
                        break;
                    }

                    foreach (var rig in focusable)
                    {
                        var id = Derive(action.Id, rig.Id.Value);
                        Add(id, new AutofocusStepDraft(id, rig.Id, autofocus.Settings.ExposureSeconds, autofocus.Settings.StepSize, autofocus.Settings.SampleCount), action.Id);
                    }

                    break;
                }

                case SlewAction or SlewAndCenterAction or CenterAndRotateAction or PlateSolveAction or SyncMountAction or DitherNowAction:
                    CompileSky(action, rigs, explicitRig, target);
                    break;

                case CoolCameraAction or WarmCameraAction:
                {
                    var cameras = rigs.Select(r => r.CameraId).Distinct().ToList();
                    if (cameras.Count == 0)
                    {
                        Problem(action.Id, "No imaging setup has a camera.");
                        break;
                    }

                    foreach (var camera in cameras)
                    {
                        var id = Derive(action.Id, camera.Value);
                        Add(id, action is CoolCameraAction cool
                            ? new DeviceOperationStepDraft(id, DeviceOperation.CoolCamera, camera, cool.TargetCelsius, cool.RampMinutes)
                            : new DeviceOperationStepDraft(id, DeviceOperation.WarmCamera, camera, 0, ((WarmCameraAction)action).RampMinutes), action.Id);
                    }

                    break;
                }

                case ParkAction or UnparkAction or SetTrackingAction:
                {
                    var mounts = rigs.Select(r => r.MountId).OfType<DeviceId>().Distinct().ToList();
                    if (mounts.Count == 0)
                    {
                        Problem(action.Id, Who("mount") + " Give the setup a mount on the Equipment page.");
                        break;
                    }

                    foreach (var mount in mounts)
                    {
                        var id = Derive(action.Id, mount.Value);
                        var operation = action switch
                        {
                            ParkAction => DeviceOperation.Park,
                            UnparkAction => DeviceOperation.Unpark,
                            _ => ((SetTrackingAction)action).On ? DeviceOperation.TrackingOn : DeviceOperation.TrackingOff,
                        };
                        Add(id, new DeviceOperationStepDraft(id, operation, mount), action.Id);
                    }

                    break;
                }

                default:
                    Problem(action.Id, $"{ActionCatalog.Of(action.Kind).Title} belongs in a block, next to an exposure.");
                    break;
            }
        }

        // The actions that need the sky: they use the coordinates of the target and the mounts of the setups.
        private void CompileSky(SessionAction action, IReadOnlyList<Rig> rigs, Rig? explicitRig, SessionTarget? target)
        {
            if (target is null)
            {
                Problem(action.Id, $"{ActionCatalog.Of(action.Kind).Title} needs a target: put it in the preparation of a target.");
                return;
            }

            // The setups on one mount are centered once; the first of them (in the order of the lanes) is the one whose camera solves.
            var groups = rigs.Where(r => r.MountId is not null).GroupBy(r => r.MountId!.Value).ToList();
            if (explicitRig is not null && explicitRig.MountId is null)
            {
                Problem(action.Id, $"{explicitRig.Name} has no mount. Give the setup a mount on the Equipment page.");
                return;
            }

            if (groups.Count == 0)
            {
                Problem(action.Id, "No imaging setup has a mount, so there is nothing to point at the target. Give a setup a mount, or remove this action.");
                return;
            }

            foreach (var group in groups)
            {
                var pointing = group.First();
                var id = Derive(action.Id, "sky:" + group.Key.Value);
                switch (action)
                {
                    case SlewAction:
                        Add(id, new SlewStepDraft(id, group.Key, target.RightAscensionHours, target.DeclinationDegrees), action.Id);
                        break;
                    case SlewAndCenterAction center:
                        if (target.RotationDegrees is { } rotation && pointing.RotatorId is not null)
                        {
                            Add(id, new CenterAndRotateStepDraft(
                                id, null, pointing.Id, target.RightAscensionHours, target.DeclinationDegrees, center.ToleranceArcseconds, center.MaxAttempts, rotation,
                                RotationService.DefaultToleranceDegrees, RotationService.DefaultMaxAttempts, RotationService.DefaultMaxRounds, center.SolveExposureSeconds, target.Name), action.Id);
                        }
                        else
                        {
                            Add(id, new SlewAndCenterStepDraft(
                                id, null, pointing.Id, target.RightAscensionHours, target.DeclinationDegrees, center.ToleranceArcseconds, center.MaxAttempts, center.SolveExposureSeconds,
                                target.Name, target.RotationDegrees), action.Id);
                        }

                        break;
                    case CenterAndRotateAction rotate:
                        if (target.RotationDegrees is not { } wanted)
                        {
                            Problem(action.Id, "Center & Rotate needs a rotation: set the rotation of the target.");
                        }
                        else if (pointing.RotatorId is null)
                        {
                            Problem(action.Id, $"{pointing.Name} has no rotator. Give the setup a rotator on the Equipment page, or use Slew & Center.");
                        }
                        else
                        {
                            Add(id, new CenterAndRotateStepDraft(
                                id, null, pointing.Id, target.RightAscensionHours, target.DeclinationDegrees, rotate.ToleranceArcseconds, rotate.MaxAttempts, wanted,
                                RotationService.DefaultToleranceDegrees, RotationService.DefaultMaxAttempts, RotationService.DefaultMaxRounds, rotate.SolveExposureSeconds, target.Name), action.Id);
                        }

                        break;
                    case PlateSolveAction solve:
                        Add(id, new PlateSolveStepDraft(id, pointing.Id, solve.ExposureSeconds), action.Id);
                        break;
                    case SyncMountAction:
                        Add(id, new SyncMountStepDraft(id, group.Key), action.Id);
                        break;
                    case DitherNowAction dither:
                    {
                        var s = dither.Settings;
                        if (pointing.GuiderId is null)
                        {
                            Problem(action.Id, $"Dither needs a guider: {pointing.Name} has none. Give the setup a guider on the Equipment page.");
                            break;
                        }

                        Add(id, new DitherStepDraft(id, pointing.GuiderId, group.Key, pointing.CameraId, s.AmplitudePixels, s.SettleThresholdPixels, s.SettleStableSeconds, s.SettleTimeoutSeconds), action.Id);
                        break;
                    }
                }
            }
        }

        private void Add(Guid id, SequenceStepDraft step, Guid origin)
        {
            _origins[id] = origin;
            _steps.Add(step);
        }
    }
}

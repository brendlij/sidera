using System.Collections.Concurrent;
using Sidera.Core.Conditions;
using Sidera.Core.Devices;
using Sidera.Core.Location;
using Sidera.Core.Mounts;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Devices;

namespace Sidera.Runtime.Sequencing;

/// <summary>
/// What the conditions of a run need from the outside: the clock, the observing site, and how often to look. One clock and one polling interval for all of a run's conditions: nothing here owns a
/// timer per condition. Astronomy changes slowly, so the default is a look every two seconds; a test passes its own clock and a few milliseconds.
/// </summary>
public sealed record ConditionServices(TimeProvider? Time = null, Func<ObservingSite?>? Site = null, TimeSpan? PollInterval = null)
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    public DateTime UtcNow => (Time ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public TimeSpan Poll => PollInterval ?? DefaultPollInterval;

    /// <summary>The site of the application (Settings); where there is none, the one a mount reports, so that a session on a mount that knows where it is needs no setting.</summary>
    public static Func<ObservingSite?> SiteOf(Func<ObservingSite?>? configured, DeviceRegistry registry, DeviceId? mount) => () =>
        configured?.Invoke()
        ?? (mount is { } id && registry.TryGet(id, out var device) && device is IMountControl { Site: { } own } ? own.ToObservingSite()
            : registry.GetAll().OfType<IMountControl>().Select(m => m.Site).FirstOrDefault(s => s is not null)?.ToObservingSite());
}

/// <summary>What a condition-controlled part of a session is doing, for people to read.</summary>
public enum ConditionPhase
{
    /// <summary>Not started.</summary>
    Idle,

    /// <summary>Waiting for its start conditions or for a Wait step's conditions.</summary>
    Waiting,

    /// <summary>Running; no stop condition has been reached.</summary>
    Running,

    /// <summary>A stop condition was reached; the current exposure is finishing first.</summary>
    StopReached,

    /// <summary>Over: by its frames, by a stop condition, or because the target stopped.</summary>
    Done
}

/// <summary>The state of one waiting or imaging element: what it is doing and why, in words. Updated by the run; read by the user interface (which is told when it changes).</summary>
public sealed class ConditionStatus
{
    private readonly object _gate = new();
    private ConditionPhase _phase;
    private string _text = string.Empty;
    private string _detail = string.Empty;

    public event EventHandler? Changed;

    public ConditionPhase Phase { get { lock (_gate) { return _phase; } } }

    /// <summary>The headline: "Waiting for astronomical darkness", "Stop condition reached".</summary>
    public string Text { get { lock (_gate) { return _text; } } }

    /// <summary>The numbers behind it: "Sun altitude -14.2° · needs ≤ -18°".</summary>
    public string Detail { get { lock (_gate) { return _detail; } } }

    public void Set(ConditionPhase phase, string text, string detail = "")
    {
        lock (_gate)
        {
            if (_phase == phase && _text == text && _detail == detail)
            {
                return;
            }

            _phase = phase;
            _text = text;
            _detail = detail;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>The statuses of a run by the id of the draft step they belong to, so that the editor can show them where the user made the step.</summary>
public sealed class ConditionStatusBoard
{
    private readonly ConcurrentDictionary<Guid, ConditionStatus> _statuses = new();

    public ConditionStatus For(Guid draftId) => _statuses.GetOrAdd(draftId, _ => new ConditionStatus());

    public bool TryGet(Guid draftId, out ConditionStatus status) => _statuses.TryGetValue(draftId, out status!);

    public IReadOnlyCollection<Guid> Ids => _statuses.Keys.ToList();
}

/// <summary>A condition that can never be decided with what is known (no observing site, no darkness at this latitude): the step cannot honestly wait for it, so the sequence says so and stops.</summary>
public sealed class ConditionUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// The stop conditions of a block or of a target, and what is known about them while imaging runs. A block's scope has the target's scope as its parent: when the target stops, so does every
/// block of it. Durations count from when the scope started (the first look), a reached condition stays reached, and one evaluator serves all the conditions of the scope.
/// </summary>
public sealed class ConditionScope
{
    private readonly object _gate = new();
    private readonly ConditionEvaluator _evaluator = new();
    private readonly ConditionServices _services;
    private readonly CelestialCoordinates? _target;
    private readonly IReadOnlyList<WorkflowCondition> _stopAny;
    private DateTime? _startedUtc;
    private int _frames;

    public ConditionScope(ConditionServices services, CelestialCoordinates? target, IReadOnlyList<WorkflowCondition> stopAny, ConditionScope? parent = null)
    {
        _services = services;
        _target = target;
        _stopAny = stopAny;
        Parent = parent;
    }

    public ConditionScope? Parent { get; }

    /// <summary>The words for the condition that stopped it; <c>null</c> while none has.</summary>
    public string? StopReason { get; private set; }

    public bool HasConditions => _stopAny.Count > 0 || Parent?.HasConditions == true;

    public int Frames { get { lock (_gate) { return _frames; } } }

    public DateTime? StartedUtc { get { lock (_gate) { return _startedUtc; } } }

    /// <summary>Starts the clock of the scope (its elapsed time counts from here); a second call changes nothing.</summary>
    public void Start()
    {
        Parent?.Start();
        lock (_gate)
        {
            _startedUtc ??= _services.UtcNow;
        }
    }

    public void FrameCompleted()
    {
        lock (_gate)
        {
            _frames++;
        }
    }

    /// <summary>
    /// Whether a stop condition of this scope or of the one above it holds. The first time one does, it is remembered and says why; from then on the answer is yes without looking again, so
    /// nothing that was reached un-reaches, and "an exposure finished and the altitude is back above the limit" does not restart imaging.
    /// </summary>
    public bool Check()
    {
        if (Parent?.Check() == true)
        {
            lock (_gate)
            {
                StopReason ??= Parent.StopReason;
            }

            return true;
        }

        lock (_gate)
        {
            if (StopReason is not null)
            {
                return true;
            }

            if (_stopAny.Count == 0)
            {
                return false;
            }

            _startedUtc ??= _services.UtcNow;
            var context = new ConditionContext(_services.UtcNow, _services.Site?.Invoke(), _target, _startedUtc.Value, _frames);
            if (_evaluator.FirstMet(_stopAny, context) is { } met)
            {
                StopReason = met.Condition.Summary;
                return true;
            }

            return false;
        }
    }

    /// <summary>Where the stop conditions stand, for the status line: the ones that are not met, with their numbers.</summary>
    public string Describe()
    {
        lock (_gate)
        {
            if (_stopAny.Count == 0)
            {
                return string.Empty;
            }

            _startedUtc ??= _services.UtcNow;
            var context = new ConditionContext(_services.UtcNow, _services.Site?.Invoke(), _target, _startedUtc.Value, _frames);
            return string.Join(" · ", _stopAny.Select(c => _evaluator.Evaluate(c, context).Text));
        }
    }
}

/// <summary>
/// Waits until all of its conditions hold ("all" is the whole semantics: there is no nesting). It looks at the clock and the sky at the polling interval of the run, never in a loop without a
/// pause, and a cancel ends the wait at once. At each look it is at a safe point, so a dither or a flip that another track asks for does not wait for it. A condition that cannot be decided
/// (no observing site; no darkness at this latitude within two days) ends the wait with the reason instead of waiting for ever. A target that stops while this waits ends it too: the wait is
/// for imaging that will not happen.
/// </summary>
public sealed class WaitUntilStep(
    string name, IReadOnlyList<WorkflowCondition> conditions, CelestialCoordinates? target, ConditionServices services, ConditionStatus? status = null, ConditionScope? targetScope = null)
    : ISequenceStep
{
    public string Name => name;

    public IReadOnlyList<WorkflowCondition> Conditions => conditions;

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var evaluator = new ConditionEvaluator();
        var started = services.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new ConditionContext(services.UtcNow, services.Site?.Invoke(), target, started);
            var unmet = evaluator.Unmet(conditions, snapshot);
            if (unmet.Count == 0)
            {
                status?.Set(ConditionPhase.Done, "Conditions met", string.Join(" · ", conditions.Select(c => c.Summary)));
                return new SequenceStepResult();
            }

            if (unmet.FirstOrDefault(u => u.Result.Availability != ConditionAvailability.Available) is { Condition: not null } impossible)
            {
                var reason = $"{impossible.Condition.Summary}: {impossible.Result.Text}";
                status?.Set(ConditionPhase.Done, "Cannot wait", reason);
                throw new ConditionUnavailableException(reason);
            }

            if (targetScope?.Check() == true)
            {
                status?.Set(ConditionPhase.Done, "Not needed: the target stopped", targetScope.StopReason ?? string.Empty);
                return new SequenceStepResult();
            }

            status?.Set(ConditionPhase.Waiting, "Waiting · " + string.Join(" and ", unmet.Select(u => u.Condition.Summary)), string.Join(" · ", unmet.Select(u => u.Result.Text)));
            await context.ReachSafePointAsync(cancellationToken);
            await Task.Delay(services.Poll, cancellationToken);
        }
    }
}

/// <summary>
/// The control of one block's imaging: its stop conditions (and the target's), the frames it has taken, and the order that follows from "a stop beats everything that would come next". It is
/// the repeat's control (checked before and after each frame) and the guard of the body of the frame (checked before each of its steps), so once a stop condition holds nothing new starts: no
/// meridian flip, no autofocus, no dither, no exposure. An exposure that is running is never cut short; the stop is taken when it has ended, and until then the status says so. One monitor
/// looks at the conditions while the block runs, at the polling interval, so the stop is known the moment it is reached and not only at the next frame.
/// </summary>
public sealed class ConditionBlockControl : IRepeatControl, IGroupGuard
{
    private readonly ConditionScope _scope;
    private readonly ConditionServices _services;
    private readonly ConditionStatus? _status;
    private readonly IReadOnlySet<ISequenceStep> _counted;
    private readonly int _frames;
    private CancellationTokenSource? _monitor;
    private Task? _monitoring;
    private volatile bool _exposing;

    /// <param name="counted">The steps that take a frame: after one of them ran, the frame counts.</param>
    public ConditionBlockControl(ConditionScope scope, ConditionServices services, int frames, IReadOnlySet<ISequenceStep> counted, ConditionStatus? status)
    {
        _scope = scope;
        _services = services;
        _frames = frames;
        _counted = counted;
        _status = status;
    }

    public ConditionScope Scope => _scope;

    public void Begin()
    {
        _scope.Start();
        Publish();
        if (_scope.HasConditions)
        {
            _monitor = new CancellationTokenSource();
            _monitoring = MonitorAsync(_monitor.Token);
        }
    }

    public bool ShouldStop(int completed) => _scope.Check();

    public bool MayRun(int index, ISequenceStep child)
    {
        if (_scope.Check())
        {
            return false;
        }

        _exposing = _counted.Contains(child);
        return true;
    }

    public void Ran(int index, ISequenceStep child)
    {
        if (_counted.Contains(child))
        {
            _scope.FrameCompleted();
            _exposing = false;
            Publish();
        }
    }

    public bool AfterIteration(int completed) => _scope.Check();

    public void End(int completed)
    {
        _monitor?.Cancel();
        try
        {
            _monitoring?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // The monitor ends with the block.
        }

        _monitor?.Dispose();
        _monitor = null;
        var stopped = _scope.StopReason;
        _status?.Set(
            ConditionPhase.Done,
            stopped is null ? $"Completed · {_scope.Frames} / {_frames} frames" : $"Stopped · {_scope.Frames} / {_frames} frames",
            stopped is null ? string.Empty : "Stop: " + stopped);
    }

    private void Publish()
    {
        if (_scope.StopReason is { } reason)
        {
            _status?.Set(ConditionPhase.StopReached, _exposing ? "Stop condition reached · finishing current exposure" : "Stop condition reached", reason);
        }
        else
        {
            _status?.Set(ConditionPhase.Running, $"Imaging · {_scope.Frames} / {_frames} frames", _scope.Describe());
        }
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_services.Poll, cancellationToken);
            _scope.Check();
            Publish();
        }
    }
}

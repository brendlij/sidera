using System.Diagnostics;
using Astra.Core.Coordination;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Runtime.Coordination;

/// <summary>What a coordination group looks like right now; a snapshot.</summary>
/// <param name="Participants">All registered participants.</param>
/// <param name="AtSafePoint">Participants currently waiting at a safe point.</param>
/// <param name="RequestPending">A coordinated operation is waiting for safe points or running.</param>
/// <param name="OperationRunning">The coordinated operation itself is running.</param>
public sealed record SafePointGroupStatus(
    IReadOnlyList<ParticipantId> Participants,
    IReadOnlyList<ParticipantId> AtSafePoint,
    bool RequestPending,
    bool OperationRunning
);

/// <summary>
/// Lets a disruptive shared operation wait until the affected execution branches are at a safe point.
/// <para>
/// This is not a resource lock. It only answers "are the other branches somewhere it is safe to interrupt?";
/// whoever runs the operation still takes the physical resources it needs from the ResourceManager.
/// </para>
/// <para>
/// Lifecycle of a request: it is pending from the start of <see cref="ExecuteWhenSafeAsync"/>; while pending,
/// branches that reach a safe point wait there; when every required participant is at a safe point the operation
/// runs exactly once; when it ends, however it ends, the request is removed and every waiting branch is released.
/// Requests of one group run one after another.
/// </para>
/// <para>
/// Diagnostics: the rounds are logged with the group and participant ids and how many of the required participants had
/// arrived: the request (Information), each arrival and the release (Debug), the start and the end of the operation
/// (Information), and a round that was called off because a participant failed (Warning). That is the trail to follow
/// when a coordinated operation, such as a dither, does not start.
/// </para>
/// </summary>
public sealed class SafePointCoordinator
{
    private enum ParticipantState
    {
        Running,
        AtSafePoint,

        // Waiting for its own turn to request an operation: counts as safe for the request that is ahead.
        Requesting
    }

    private sealed class Request(ParticipantId? requester, IReadOnlyCollection<ParticipantId>? subset)
    {
        public ParticipantId? Requester { get; } = requester;
        public IReadOnlyCollection<ParticipantId>? Subset { get; } = subset;
        public bool OperationStarted { get; set; }

        public TaskCompletionSource AllSafe { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Group
    {
        public Dictionary<ParticipantId, ParticipantState> Participants { get; } = new();
        public Request? Active { get; set; }
        public SemaphoreSlim Turn { get; } = new(1, 1);
    }

    private readonly object _gate = new();
    private readonly Dictionary<CoordinationGroupId, Group> _groups = new();
    private readonly ILogger _logger;
    private long _lastParticipant;

    /// <param name="logger">Where the coordination rounds are reported.</param>
    public SafePointCoordinator(ILogger<SafePointCoordinator>? logger = null)
    {
        _logger = logger ?? NullLogger<SafePointCoordinator>.Instance;
    }

    /// <summary>
    /// Raised, outside the coordinator's lock, when a coordinated operation of a group became pending. Lets other
    /// parts of the runtime re-evaluate decisions that depend on pending coordination (such as pausing).
    /// </summary>
    public event EventHandler<CoordinationGroupId>? RequestStarted;

    private void RaiseRequestStarted(CoordinationGroupId group)
    {
        try
        {
            RequestStarted?.Invoke(this, group);
        }
        catch
        {
            // An observer must not break coordination.
        }
    }

    /// <summary>Registers <paramref name="count"/> new participants, all at once, so none can be missed by a request.</summary>
    public IReadOnlyList<ParticipantId> RegisterParticipants(CoordinationGroupId group, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(count, 0);

        lock (_gate)
        {
            var g = GetOrCreate(group);
            var ids = new List<ParticipantId>(count);
            for (var i = 0; i < count; i++)
            {
                var id = new ParticipantId($"participant-{++_lastParticipant}");
                g.Participants[id] = ParticipantState.Running;
                ids.Add(id);
            }

            _logger.LogDebug(
                "Group {CoordinationGroupId}: {Count} participants registered ({ParticipantIds})",
                group, count, string.Join(", ", ids));
            return ids;
        }
    }

    /// <summary>
    /// Removes a participant whose branch ended. A participant that is gone is no longer waited for, so a
    /// pending request can proceed. If it ended by <paramref name="failed"/> without having reached a safe point,
    /// a request that was still waiting for it is called off with a <see cref="CoordinationAbortedException"/>
    /// (the operation does not run).
    /// </summary>
    public void Unregister(CoordinationGroupId group, ParticipantId participant, bool failed = false)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var g) || !g.Participants.Remove(participant, out var state))
            {
                return;
            }

            _logger.LogDebug(
                "Group {CoordinationGroupId}: participant {ParticipantId} left (failed: {Failed}, {Remaining} remaining)",
                group, participant, failed, g.Participants.Count);

            if (g.Active is { OperationStarted: false } request)
            {
                if (failed && state == ParticipantState.Running && IsRequired(g, request, participant, includeRemoved: true))
                {
                    _logger.LogWarning(
                        "Group {CoordinationGroupId}: the coordinated operation is called off, participant {ParticipantId} " +
                        "failed before it reached a safe point",
                        group, participant);
                    request.AllSafe.TrySetException(new CoordinationAbortedException(
                        $"Participant '{participant}' failed before reaching a safe point; " +
                        "the coordinated operation was not run."));
                }
                else
                {
                    CheckBarrier(g);
                }
            }
        }
    }

    /// <summary>
    /// A branch declares itself safe. Returns at once if no request is pending (or the participant is unknown);
    /// otherwise waits until the pending operation is over. Cancelling leaves the participant running again.
    /// </summary>
    public async Task ReachSafePointAsync(
        CoordinationGroupId group,
        ParticipantId participant,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task waitForEnd;
        Group g;
        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out g!) || !g.Participants.ContainsKey(participant) || g.Active is null)
            {
                return;
            }

            g.Participants[participant] = ParticipantState.AtSafePoint;
            var (required, safe) = Count(g, g.Active);

            // Logged before the barrier is completed, so that the trail reads in the order things happened: the arrival, and
            // only then the operation it releases. (The logger queues the entry; it does not wait for any sink.)
            _logger.LogDebug(
                "Group {CoordinationGroupId}: participant {ParticipantId} reached a safe point ({Arrived} of {Required} required are safe)",
                group, participant, safe, required);
            CheckBarrier(g);
            waitForEnd = g.Active.Done.Task;
        }

        try
        {
            await waitForEnd.WaitAsync(cancellationToken);
            _logger.LogDebug("Group {CoordinationGroupId}: participant {ParticipantId} released", group, participant);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Group {CoordinationGroupId}: participant {ParticipantId} stopped waiting at the safe point (cancelled)",
                group, participant);
            lock (_gate)
            {
                if (g.Participants.TryGetValue(participant, out var state) && state == ParticipantState.AtSafePoint)
                {
                    g.Participants[participant] = ParticipantState.Running;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> exactly once, when every required participant is at a safe point, and
    /// releases them afterwards. Required are the other participants of the group (or just
    /// <paramref name="participants"/>, if given) that are still registered; the requester itself is never waited for.
    /// <list type="bullet">
    /// <item>Cancelling while waiting for safe points: the operation never runs, waiting branches are released.</item>
    /// <item>Cancelling during the operation: the operation receives the token; branches are released afterwards.</item>
    /// <item>The operation throws: the exception propagates and the branches are released.</item>
    /// <item>A required participant fails before reaching a safe point: this call throws
    /// <see cref="CoordinationAbortedException"/> (a cancellation), the operation does not run, branches are released.</item>
    /// </list>
    /// </summary>
    public async Task ExecuteWhenSafeAsync(
        CoordinationGroupId group,
        ParticipantId? requester,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<ParticipantId>? participants = null
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        Group g;
        lock (_gate)
        {
            g = GetOrCreate(group);

            // While waiting for its turn the requester is itself at a place where it stopped; a request that is
            // ahead of it must not wait for it forever.
            if (requester is { } waiting && g.Participants.ContainsKey(waiting))
            {
                g.Participants[waiting] = ParticipantState.Requesting;
                CheckBarrier(g);
            }
        }

        try
        {
            await g.Turn.WaitAsync(cancellationToken);
        }
        catch
        {
            ResetRequester(g, requester);
            throw;
        }

        try
        {
            Request request;
            int required, safe;
            lock (_gate)
            {
                ResetRequester(g, requester, alreadyLocked: true);
                request = new Request(requester, participants);
                g.Active = request;
                CheckBarrier(g);
                (required, safe) = Count(g, request);
            }

            _logger.LogInformation(
                "Group {CoordinationGroupId}: coordinated operation requested by {ParticipantId}; " +
                "{Arrived} of {Required} required participants are at a safe point",
                group, requester?.ToString() ?? "none", safe, required);
            RaiseRequestStarted(group);

            var started = Stopwatch.GetTimestamp();
            try
            {
                await request.AllSafe.Task.WaitAsync(cancellationToken);

                lock (_gate)
                {
                    request.OperationStarted = true;
                }

                _logger.LogInformation(
                    "Group {CoordinationGroupId}: all {Required} required participants are at a safe point after {WaitMs:0} ms, " +
                    "the coordinated operation starts",
                    group, required, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                var operationStarted = Stopwatch.GetTimestamp();
                await operation(cancellationToken);
                _logger.LogInformation(
                    "Group {CoordinationGroupId}: coordinated operation completed in {DurationMs:0} ms",
                    group, Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
            }
            catch (CoordinationAbortedException)
            {
                // Called off because a participant failed; already reported where it was noticed.
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "Group {CoordinationGroupId}: coordinated operation cancelled ({Phase})",
                    group, request.OperationStarted ? "while it ran" : "while waiting for safe points");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Group {CoordinationGroupId}: coordinated operation failed: {Reason}", group, ex.Message);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    // Everyone waiting is about to continue: mark them running first, so a later request
                    // never mistakes them for still being at a safe point.
                    foreach (var id in g.Participants.Where(p => p.Value == ParticipantState.AtSafePoint).Select(p => p.Key).ToList())
                    {
                        g.Participants[id] = ParticipantState.Running;
                    }

                    g.Active = null;
                }

                request.Done.TrySetResult();
                _logger.LogDebug("Group {CoordinationGroupId}: waiting participants released", group);
            }
        }
        finally
        {
            g.Turn.Release();
        }
    }

    public SafePointGroupStatus GetStatus(CoordinationGroupId group)
    {
        lock (_gate)
        {
            if (!_groups.TryGetValue(group, out var g))
            {
                return new SafePointGroupStatus([], [], false, false);
            }

            return new SafePointGroupStatus(
                g.Participants.Keys.OrderBy(p => p.Value, StringComparer.Ordinal).ToList(),
                g.Participants.Where(p => p.Value == ParticipantState.AtSafePoint)
                    .Select(p => p.Key).OrderBy(p => p.Value, StringComparer.Ordinal).ToList(),
                g.Active is not null,
                g.Active is { OperationStarted: true });
        }
    }

    // Called with the lock held.
    private Group GetOrCreate(CoordinationGroupId group)
    {
        if (!_groups.TryGetValue(group, out var g))
        {
            g = new Group();
            _groups[group] = g;
        }

        return g;
    }

    // Called with the lock held: how many participants the request waits for, and how many of them are safe now.
    private static (int Required, int Safe) Count(Group g, Request request)
    {
        var required = g.Participants.Where(p => IsRequired(g, request, p.Key, includeRemoved: false)).ToList();
        return (required.Count, required.Count(p => p.Value != ParticipantState.Running));
    }

    // Called with the lock held. Completes the request's barrier once every required participant is at a safe point.
    private static void CheckBarrier(Group g)
    {
        if (g.Active is not { OperationStarted: false } request)
        {
            return;
        }

        var allSafe = g.Participants
            .Where(p => IsRequired(g, request, p.Key, includeRemoved: false))
            .All(p => p.Value != ParticipantState.Running);

        if (allSafe)
        {
            request.AllSafe.TrySetResult();
        }
    }

    private static bool IsRequired(Group g, Request request, ParticipantId participant, bool includeRemoved)
    {
        if (request.Requester == participant)
        {
            return false;
        }

        if (!includeRemoved && !g.Participants.ContainsKey(participant))
        {
            return false;
        }

        return request.Subset is null || request.Subset.Contains(participant);
    }

    private void ResetRequester(Group g, ParticipantId? requester, bool alreadyLocked = false)
    {
        if (requester is not { } id)
        {
            return;
        }

        if (alreadyLocked)
        {
            Reset();
            return;
        }

        lock (_gate)
        {
            Reset();
        }

        void Reset()
        {
            if (g.Participants.TryGetValue(id, out var state) && state == ParticipantState.Requesting)
            {
                g.Participants[id] = ParticipantState.Running;
            }
        }
    }
}

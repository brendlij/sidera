using Astra.Core.Coordination;

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
    private long _lastParticipant;

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

            if (g.Active is { OperationStarted: false } request)
            {
                if (failed && state == ParticipantState.Running && IsRequired(g, request, participant, includeRemoved: true))
                {
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
            CheckBarrier(g);
            waitForEnd = g.Active.Done.Task;
        }

        try
        {
            await waitForEnd.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
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
            lock (_gate)
            {
                ResetRequester(g, requester, alreadyLocked: true);
                request = new Request(requester, participants);
                g.Active = request;
                CheckBarrier(g);
            }

            try
            {
                await request.AllSafe.Task.WaitAsync(cancellationToken);

                lock (_gate)
                {
                    request.OperationStarted = true;
                }

                await operation(cancellationToken);
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

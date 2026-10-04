using Sidera.Core.Coordination;
using Sidera.Runtime.Coordination;

namespace Sidera.Runtime.Tests.Coordination;

public class SafePointCoordinatorTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private static readonly CoordinationGroupId Group = new("session.mount.eq6");

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(5);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>An operation that signals when it starts, counts its runs and ends when the test releases it.</summary>
    private sealed class Operation
    {
        private int _runs;
        public TaskCompletionSource Started { get; } = NewSignal();
        public TaskCompletionSource Release { get; } = NewSignal();
        public int Runs => _runs;
        public bool SawCancellation { get; private set; }
        public Exception? Failure { get; init; }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _runs);
            Started.TrySetResult();

            if (Failure is not null)
            {
                throw Failure;
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SawCancellation = true;
                throw;
            }
        }
    }

    // Identifiers

    [Fact]
    public void GroupId_AcceptsValue_AndTrims()
    {
        var id = new CoordinationGroupId("  rig-group.main-wide ");

        Assert.Equal("rig-group.main-wide", id.Value);
        Assert.Equal("rig-group.main-wide", id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void GroupId_RejectsEmpty(string? value)
    {
        Assert.Throws<ArgumentException>(() => new CoordinationGroupId(value!));
        Assert.Throws<ArgumentException>(() => new ParticipantId(value!));
    }

    [Fact]
    public void Ids_HaveValueEquality()
    {
        Assert.Equal(new CoordinationGroupId("a"), new CoordinationGroupId(" a "));
        Assert.NotEqual(new CoordinationGroupId("a"), new CoordinationGroupId("b"));
        Assert.Equal(new ParticipantId("p1"), new ParticipantId("p1"));
    }

    [Fact]
    public void RegisterParticipants_CreatesDistinctDeterministicIds()
    {
        var coordinator = new SafePointCoordinator();

        var first = coordinator.RegisterParticipants(Group, 2);
        var second = coordinator.RegisterParticipants(Group, 1);

        Assert.Equal(3, first.Concat(second).Distinct().Count());
        Assert.Equal(new[] { "participant-1", "participant-2" }, first.Select(p => p.Value));
        Assert.Equal(3, coordinator.GetStatus(Group).Participants.Count);
    }

    // Safe point behavior

    [Fact]
    public async Task SafePoint_ContinuesImmediately_WhenNoRequestIsPending()
    {
        var coordinator = new SafePointCoordinator();
        var a = coordinator.RegisterParticipants(Group, 1)[0];

        var task = coordinator.ReachSafePointAsync(Group, a);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
        Assert.Empty(coordinator.GetStatus(Group).AtSafePoint);
    }

    [Fact]
    public async Task SafePoint_ForAnUnknownParticipantOrGroup_Continues()
    {
        var coordinator = new SafePointCoordinator();

        await coordinator.ReachSafePointAsync(Group, new ParticipantId("nobody")).WaitAsync(Bound);
        coordinator.RegisterParticipants(Group, 1);
        await coordinator.ReachSafePointAsync(Group, new ParticipantId("nobody")).WaitAsync(Bound);
    }

    [Fact]
    public async Task SafePoint_WaitsWhileARequestIsPending_AndIsReleasedAfterTheOperation()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 2);
        var (a, requester) = (ids[0], ids[1]);
        var op = new Operation();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
        var safePoint = coordinator.ReachSafePointAsync(Group, a);

        // The only other participant is at its safe point: the operation runs, and the participant stays put.
        await op.Started.Task.WaitAsync(Bound);
        Assert.False(safePoint.IsCompleted);
        Assert.Equal(new[] { a }, coordinator.GetStatus(Group).AtSafePoint);

        op.Release.SetResult();
        await request.WaitAsync(Bound);
        await safePoint.WaitAsync(Bound);

        var status = coordinator.GetStatus(Group);
        Assert.False(status.RequestPending);
        Assert.Empty(status.AtSafePoint);
    }

    // Barrier

    [Fact]
    public async Task Operation_DoesNotStartBeforeAllRequiredParticipantsAreSafe_ThenRunsExactlyOnce()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, b, requester) = (ids[0], ids[1], ids[2]);
        var op = new Operation();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
        var safeA = coordinator.ReachSafePointAsync(Group, a);

        await WaitUntil(() => coordinator.GetStatus(Group).AtSafePoint.Count == 1, "first participant waiting");
        Assert.Equal(0, op.Runs); // b is not safe yet
        var status = coordinator.GetStatus(Group);
        Assert.True(status.RequestPending);
        Assert.False(status.OperationRunning);

        var safeB = coordinator.ReachSafePointAsync(Group, b); // the final participant
        await op.Started.Task.WaitAsync(Bound);
        Assert.Equal(1, op.Runs);
        Assert.True(coordinator.GetStatus(Group).OperationRunning);
        Assert.False(safeA.IsCompleted);
        Assert.False(safeB.IsCompleted);

        op.Release.SetResult();
        await Task.WhenAll(request, safeA, safeB).WaitAsync(Bound);

        Assert.Equal(1, op.Runs);
    }

    [Fact]
    public async Task RequestWithoutOtherParticipants_RunsImmediately()
    {
        var coordinator = new SafePointCoordinator();
        var requester = coordinator.RegisterParticipants(Group, 1)[0];
        var op = new Operation();
        op.Release.SetResult();

        await coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync).WaitAsync(Bound);

        Assert.Equal(1, op.Runs);
    }

    [Fact]
    public async Task RequestForASubset_DoesNotWaitForOthers()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, requester) = (ids[0], ids[2]);
        var op = new Operation();
        op.Release.SetResult();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync, participants: [a]);
        Assert.False(request.IsCompleted); // waits for a only
        var safeA = coordinator.ReachSafePointAsync(Group, a);

        await Task.WhenAll(request, safeA).WaitAsync(Bound);
        Assert.Equal(1, op.Runs);
    }

    [Fact]
    public async Task ParticipantThatFinishesNormally_IsNoLongerWaitedFor()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, finishing, requester) = (ids[0], ids[1], ids[2]);
        var op = new Operation();
        op.Release.SetResult();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
        var safeA = coordinator.ReachSafePointAsync(Group, a);
        await WaitUntil(() => coordinator.GetStatus(Group).AtSafePoint.Count == 1, "a waiting");
        Assert.Equal(0, op.Runs);

        coordinator.Unregister(Group, finishing); // its branch simply ended

        await Task.WhenAll(request, safeA).WaitAsync(Bound);
        Assert.Equal(1, op.Runs);
    }

    // Cancellation

    [Fact]
    public async Task CancellingAWaitingParticipant_RemovesItFromTheSafePoint()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, b, requester) = (ids[0], ids[1], ids[2]);
        var op = new Operation();
        using var requestCts = new CancellationTokenSource();
        using var participantCts = new CancellationTokenSource();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync, requestCts.Token);
        var safeA = coordinator.ReachSafePointAsync(Group, a, participantCts.Token);
        await WaitUntil(() => coordinator.GetStatus(Group).AtSafePoint.Count == 1, "a waiting");

        await participantCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => safeA.WaitAsync(Bound));
        Assert.Empty(coordinator.GetStatus(Group).AtSafePoint);

        // The request is cancelled too, the branches end, and nothing is left behind.
        await requestCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
        coordinator.Unregister(Group, a);
        coordinator.Unregister(Group, b);
        coordinator.Unregister(Group, requester);

        var status = coordinator.GetStatus(Group);
        Assert.Empty(status.Participants);
        Assert.False(status.RequestPending);
        Assert.Equal(0, op.Runs);
    }

    [Fact]
    public async Task CancellingThePendingRequest_NeverRunsTheOperation_AndReleasesSafeBranches()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, requester) = (ids[0], ids[2]); // ids[1] never becomes safe
        var op = new Operation();
        using var cts = new CancellationTokenSource();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync, cts.Token);
        var safeA = coordinator.ReachSafePointAsync(Group, a);
        await WaitUntil(() => coordinator.GetStatus(Group).AtSafePoint.Count == 1, "a waiting");

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
        await safeA.WaitAsync(Bound); // released, not cancelled
        Assert.Equal(0, op.Runs);
        var status = coordinator.GetStatus(Group);
        Assert.False(status.RequestPending);
        Assert.Empty(status.AtSafePoint);
    }

    [Fact]
    public async Task CancellingDuringTheOperation_PassesTheTokenOn_AndStillReleasesParticipants()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 2);
        var (a, requester) = (ids[0], ids[1]);
        var op = new Operation();
        using var cts = new CancellationTokenSource();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync, cts.Token);
        var safeA = coordinator.ReachSafePointAsync(Group, a);
        await op.Started.Task.WaitAsync(Bound);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(Bound));
        await safeA.WaitAsync(Bound);
        Assert.True(op.SawCancellation);
        Assert.False(coordinator.GetStatus(Group).RequestPending);
    }

    // Failure

    [Fact]
    public async Task OperationFailure_PropagatesTheOriginalException_AndReleasesEveryone()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, b, requester) = (ids[0], ids[1], ids[2]);
        var failure = new InvalidOperationException("operation broke");
        var op = new Operation { Failure = failure };

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
        var safeA = coordinator.ReachSafePointAsync(Group, a);
        var safeB = coordinator.ReachSafePointAsync(Group, b);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(Bound));
        await Task.WhenAll(safeA, safeB).WaitAsync(Bound);

        Assert.Same(failure, error);
        var status = coordinator.GetStatus(Group);
        Assert.False(status.RequestPending);
        Assert.Empty(status.AtSafePoint);
    }

    [Fact]
    public async Task ParticipantFailingBeforeItsSafePoint_FailsTheRequest_InsteadOfWaitingForever()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, failing, requester) = (ids[0], ids[1], ids[2]);
        var op = new Operation();

        var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
        var safeA = coordinator.ReachSafePointAsync(Group, a);
        await WaitUntil(() => coordinator.GetStatus(Group).AtSafePoint.Count == 1, "a waiting");

        coordinator.Unregister(Group, failing, failed: true);

        var error = await Assert.ThrowsAsync<CoordinationAbortedException>(() => request.WaitAsync(Bound));
        await safeA.WaitAsync(Bound);
        Assert.Contains("failed before reaching a safe point", error.Message);
        Assert.Equal(0, op.Runs);
        Assert.False(coordinator.GetStatus(Group).RequestPending);
    }

    // Repeated rounds and concurrent requests

    [Fact]
    public async Task SameParticipants_CanCoordinateRepeatedly_WithoutLeakingStateBetweenRounds()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, b, requester) = (ids[0], ids[1], ids[2]);

        for (var round = 1; round <= 3; round++)
        {
            var op = new Operation();
            op.Release.SetResult();

            var request = coordinator.ExecuteWhenSafeAsync(Group, requester, op.RunAsync);
            await Task.WhenAll(
                coordinator.ReachSafePointAsync(Group, a),
                coordinator.ReachSafePointAsync(Group, b),
                request).WaitAsync(Bound);

            Assert.Equal(1, op.Runs);
            var status = coordinator.GetStatus(Group);
            Assert.False(status.RequestPending);
            Assert.Empty(status.AtSafePoint);
            Assert.Equal(3, status.Participants.Count);

            // Without a request the next safe point is free again.
            Assert.True(coordinator.ReachSafePointAsync(Group, a).IsCompletedSuccessfully);
        }
    }

    [Fact]
    public async Task TwoSimultaneousRequests_DoNotDeadlock_AndRunOneAfterTheOther()
    {
        var coordinator = new SafePointCoordinator();
        var ids = coordinator.RegisterParticipants(Group, 3);
        var (a, r1, r2) = (ids[0], ids[1], ids[2]);
        var running = 0;
        var maxRunning = 0;
        var completed = 0;

        async Task Op(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Yield();
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref completed);
        }

        var stop = false;
        var safeLoop = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                await coordinator.ReachSafePointAsync(Group, a);
                await Task.Yield();
            }
        });

        // Each requester's branch ends after its request, as a real branch would.
        async Task RequestThenFinish(ParticipantId requester)
        {
            await coordinator.ExecuteWhenSafeAsync(Group, requester, Op);
            coordinator.Unregister(Group, requester);
        }

        await Task.WhenAll(RequestThenFinish(r1), RequestThenFinish(r2)).WaitAsync(Bound);
        Volatile.Write(ref stop, true);
        await safeLoop.WaitAsync(Bound);

        Assert.Equal(2, completed);
        Assert.Equal(1, maxRunning); // requests never overlap
        Assert.False(coordinator.GetStatus(Group).RequestPending);
    }
}

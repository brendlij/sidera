using Astra.Core.Coordination;
using Astra.Core.Sequencing;
using Astra.Runtime.Coordination;
using Astra.Runtime.Resources;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Runs the steps of one sequence at a time, strictly in order. Container steps (such as
/// <see cref="RepeatStep"/>) run their children through the runner, so nested executions are tracked
/// and reported exactly like top-level ones. The first step that throws stops the run
/// (state <see cref="SequenceState.Failed"/>, exception rethrown); cancelling the token stops it too
/// (state <see cref="SequenceState.Cancelled"/>, <see cref="OperationCanceledException"/> rethrown).
/// A runner can be reused once its previous run has ended.
/// </summary>
public sealed class SequenceRunner
{
    private readonly object _gate = new();
    private readonly ResourceManager _resources;
    private readonly SafePointCoordinator _coordinator;
    private SequenceState _state = SequenceState.Idle;
    private SequenceExecutionPosition? _currentPosition;
    private readonly List<SequenceExecutionPosition> _active = new();
    private readonly List<SequenceExecutionPosition> _waitingToStart = new();
    private readonly SequencePauseGate _pause = new();
    private Exception? _failure;

    /// <param name="resourceManager">
    /// Coordinates exclusive resources between runners. Runners that work on the same equipment must share
    /// one manager, normally <see cref="AstraRuntimeHost.ResourceManager"/>. Without one the runner uses a
    /// private manager, which only coordinates its own steps.
    /// </param>
    /// <param name="safePointCoordinator">
    /// Coordinates the branches of parallel steps that name a coordination group. Like the resource manager it
    /// should normally be the host's, so that all runners of one runtime share it.
    /// </param>
    public SequenceRunner(ResourceManager? resourceManager = null, SafePointCoordinator? safePointCoordinator = null)
    {
        _resources = resourceManager ?? new ResourceManager();
        _coordinator = safePointCoordinator ?? new SafePointCoordinator();
        _pause.PhaseChanged += (_, _) => RaiseChanged();
    }

    /// <summary>
    /// The state of the run. While a run is in progress it is <see cref="SequenceState.Running"/>, and after a pause
    /// request <see cref="SequenceState.Pausing"/> (some branch is still finishing its current step) and then
    /// <see cref="SequenceState.Paused"/> (every active branch waits at a boundary). Completed, Failed and Cancelled
    /// end a run, whatever the pause state was.
    /// </summary>
    public SequenceState State
    {
        get
        {
            SequenceState lifecycle;
            lock (_gate)
            {
                lifecycle = _state;
            }

            return lifecycle != SequenceState.Running
                ? lifecycle
                : _pause.Phase switch
                {
                    PausePhase.Pausing => SequenceState.Pausing,
                    PausePhase.Paused => SequenceState.Paused,
                    _ => SequenceState.Running,
                };
        }
    }

    /// <summary>A run is in progress, including while it is pausing or paused.</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _state == SequenceState.Running; } }
    }

    /// <summary>
    /// Steps that are about to start but wait because a pause was requested: the position each branch will continue
    /// with after a resume. Such a step is not active yet and holds no resources. A snapshot.
    /// </summary>
    public IReadOnlyCollection<SequenceExecutionPosition> PausedPositions
    {
        get { lock (_gate) { return _waitingToStart.ToArray(); } }
    }

    /// <summary>
    /// Asks the run to pause. Cooperative: nothing is interrupted. Every branch finishes the step it is executing and
    /// then waits before starting the next one; the state is <see cref="SequenceState.Pausing"/> until all of them
    /// do, then <see cref="SequenceState.Paused"/>. Returns false, and changes nothing, if no run is in progress or a
    /// pause was already requested. If the run ends first, it simply ends.
    /// </summary>
    public bool RequestPause()
    {
        lock (_gate)
        {
            if (_state != SequenceState.Running)
            {
                return false;
            }
        }

        return _pause.RequestPause();
    }

    /// <summary>
    /// Requests a pause and completes with true once the run is paused, or with false if it ended (or was resumed)
    /// before that. Cancelling <paramref name="cancellationToken"/> only stops this wait, not the pause request.
    /// </summary>
    public async Task<bool> PauseAsync(CancellationToken cancellationToken = default)
    {
        var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Check(object? sender, EventArgs e)
        {
            var state = State;
            if (state == SequenceState.Paused)
            {
                settled.TrySetResult(true);
            }
            else if (state != SequenceState.Pausing)
            {
                settled.TrySetResult(false);
            }
        }

        Changed += Check;
        try
        {
            RequestPause();
            Check(this, EventArgs.Empty);
            return await settled.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Changed -= Check;
        }
    }

    /// <summary>
    /// Lets a paused run continue where it stopped. While the run is still pausing it withdraws the request instead.
    /// Returns false, and changes nothing, if no pause was requested. Cancelling a run never needs a resume.
    /// </summary>
    public bool Resume() => _pause.Resume();

    /// <summary>
    /// The execution that started most recently (it keeps its value after it ended); <c>null</c> before the first step.
    /// With a single chain of nested steps this is the innermost running step, its parents lead up to the
    /// top-level step. With parallel branches several executions are active, see <see cref="ActivePositions"/>.
    /// </summary>
    public SequenceExecutionPosition? CurrentPosition
    {
        get { lock (_gate) { return _currentPosition; } }
    }

    /// <summary>
    /// Every execution that is in progress right now, containers and their running children alike, in the order
    /// they started; empty when no run is active. A snapshot, safe to enumerate.
    /// </summary>
    public IReadOnlyCollection<SequenceExecutionPosition> ActivePositions
    {
        get { lock (_gate) { return _active.ToArray(); } }
    }

    /// <summary>Index of the running top-level step, or of the last one that ran; -1 before the first step.</summary>
    public int CurrentStepIndex => CurrentPosition?.Root.Index ?? -1;

    /// <summary>Name of the top-level step at <see cref="CurrentStepIndex"/>; <c>null</c> before the first step.</summary>
    public string? CurrentStepName => CurrentPosition?.Root.StepName;

    /// <summary>The exception that ended the last run, if its state is <see cref="SequenceState.Failed"/>.</summary>
    public Exception? Failure
    {
        get { lock (_gate) { return _failure; } }
    }

    /// <summary>
    /// Raised when <see cref="State"/>, <see cref="CurrentPosition"/> or <see cref="ActivePositions"/> changed: when a
    /// run starts, when any step or branch starts or finishes (however it ends), and when the run ends.
    /// Raised on whichever thread made the change, possibly from several branches at once, and never while
    /// the runner holds a lock. Observers read the properties themselves.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised after a step, top-level or nested, has completed successfully and before the next
    /// execution starts, with its position and result. Never raised for an execution that threw or was cancelled.
    /// Raised on the thread that ran the step. Parallel branches raise it concurrently, in real completion order.
    /// </summary>
    public event EventHandler<SequenceStepCompletedEventArgs>? StepCompleted;

    public async Task RunAsync(Sequence sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        lock (_gate)
        {
            if (_state == SequenceState.Running)
            {
                throw new InvalidOperationException("A sequence is already running.");
            }

            _pause.Reset();
            _pause.AddLines(1); // the top-level sequence is the first line of execution
            _state = SequenceState.Running;
            _currentPosition = null;
            _active.Clear();
            _waitingToStart.Clear();
            _failure = null;
        }

        RaiseChanged();

        // A coordinated operation that starts while a pause is requested must be able to finish: the branches it waits
        // for are let through their boundaries, see ExecuteStepAsync.
        void OnCoordinationStarted(object? sender, CoordinationGroupId group) => _pause.Poke();
        _coordinator.RequestStarted += OnCoordinationStarted;

        try
        {
            for (var i = 0; i < sequence.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = sequence.Steps[i];
                var position = new SequenceExecutionPosition(step.Name, i, sequence.Steps.Count);
                await ExecuteStepAsync(step, position, null, false, cancellationToken);
            }

            SetState(SequenceState.Completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetState(SequenceState.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _failure = ex;
            }

            SetState(SequenceState.Failed);
            throw;
        }
        finally
        {
            _coordinator.RequestStarted -= OnCoordinationStarted;

            // The run is over: no stale pause may leak into the next one. Waiting branches have already been
            // released by the cancellation that ends every run which is not completed.
            _pause.Reset();
            lock (_gate)
            {
                _waitingToStart.Clear();
            }
        }
    }

    // One execution of one step: mark it current, run it, then report its result.
    private async Task<SequenceStepResult> ExecuteStepAsync(
        ISequenceStep step,
        SequenceExecutionPosition position,
        BranchScope? branch,
        bool insideCoordinatedOperation,
        CancellationToken cancellationToken
    )
    {
        // The pause boundary: before the step starts and before it takes any resource, so a paused branch holds
        // nothing. Skipped for the parts of an already started coordinated operation (a dither and its settle wait):
        // those finish first, the branches pause afterwards. Every step of a branch whose group has a coordinated
        // operation pending is let through, because that operation is waiting for the branch to reach its safe point
        // and pausing it here would leave the whole round stuck.
        if (!insideCoordinatedOperation)
        {
            await _pause.WaitAtBoundaryAsync(
                () => IsCoordinationPending(branch),
                waiting =>
                {
                    lock (_gate)
                    {
                        if (waiting)
                        {
                            _waitingToStart.Add(position);
                        }
                        else
                        {
                            _waitingToStart.Remove(position);
                        }
                    }

                    RaiseChanged();
                },
                cancellationToken);
        }

        lock (_gate)
        {
            _currentPosition = position;
            _active.Add(position);
        }

        RaiseChanged();

        SequenceStepResult result;

        try
        {
            // Only steps that declare requirements acquire anything. Containers do not, so a repeat, group
            // or parallel step never holds what its children need: each child takes its own resources.
            var required = step is IResourceAwareSequenceStep aware ? aware.RequiredResources : [];
            using (await _resources.AcquireAsync(required, cancellationToken))
            {
                // Cancelled while waiting for the resource (or just as it was handed over): do not start the step.
                cancellationToken.ThrowIfCancellationRequested();
                result = await step.ExecuteAsync(
                    new StepContext(this, position, branch, insideCoordinatedOperation), cancellationToken);
            }
        }
        finally
        {
            // Success, failure or cancellation: this execution is no longer active.
            lock (_gate)
            {
                _active.Remove(position);
            }

            RaiseChanged();
        }

        // Reported after the resources are released, so observers never run while holding them.
        RaiseStepCompleted(new SequenceStepCompletedEventArgs(position, result));
        return result;
    }

    private bool IsCoordinationPending(BranchScope? branch) =>
        branch is not null && _coordinator.GetStatus(branch.Group).RequestPending;

    private void SetState(SequenceState state)
    {
        lock (_gate)
        {
            _state = state;
        }

        RaiseChanged();
    }

    // Observers must not be able to break a sequence, so each handler is isolated.
    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private void RaiseStepCompleted(SequenceStepCompletedEventArgs args)
    {
        foreach (var handler in StepCompleted?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<SequenceStepCompletedEventArgs>)handler)(this, args);
            }
            catch
            {
            }
        }
    }

    // The coordination group and participant a branch belongs to; inherited by everything nested in the branch.
    private sealed record BranchScope(CoordinationGroupId Group, ParticipantId Participant);

    // Created per execution, so a child is always positioned under the execution that started it. It also holds
    // the per-execution state of the branches this step launches, never anything on the step definition.
    private sealed class StepContext(
        SequenceRunner runner,
        SequenceExecutionPosition parent,
        BranchScope? branch,
        bool insideCoordinatedOperation
    ) : ISequenceStepContext
    {
        private readonly object _gate = new();
        private IReadOnlyList<ParticipantId>? _participants;
        private bool _branchesRegistered;
        private int _branchesRemaining;
        private volatile bool _operationRunning;

        public Task<SequenceStepResult> ExecuteChildAsync(
            ISequenceStep child,
            int index,
            int count,
            CancellationToken cancellationToken
        )
        {
            var position = new SequenceExecutionPosition(child.Name, index, count, parent);

            // Steps run by a coordinated operation are part of it and do not stop at pause boundaries.
            return runner.ExecuteStepAsync(
                child, position, branch, insideCoordinatedOperation || _operationRunning, cancellationToken);
        }

        public async Task<SequenceStepResult> ExecuteBranchAsync(
            ISequenceStep child,
            int index,
            int count,
            CoordinationGroupId? group,
            CancellationToken cancellationToken
        )
        {
            var position = new SequenceExecutionPosition(child.Name, index, count, parent);

            // For pausing, the step that launches branches stops being a line of execution while they run, and
            // every branch is one. All are counted when the first is launched, so a branch that reaches a boundary
            // early cannot make the run look paused before its siblings have started.
            lock (_gate)
            {
                if (!_branchesRegistered)
                {
                    _branchesRegistered = true;
                    _branchesRemaining = count;
                    runner._pause.AddLines(count - 1);
                }
            }

            try
            {
                return await RunBranchAsync(child, position, index, count, group, cancellationToken);
            }
            finally
            {
                bool last;
                lock (_gate)
                {
                    last = --_branchesRemaining == 0;
                }

                // The last branch hands its line back to the launching step, so the count never dips to zero.
                if (!last)
                {
                    runner._pause.RemoveLines(1);
                }
            }
        }

        private async Task<SequenceStepResult> RunBranchAsync(
            ISequenceStep child,
            SequenceExecutionPosition position,
            int index,
            int count,
            CoordinationGroupId? group,
            CancellationToken cancellationToken
        )
        {
            var inOperation = insideCoordinatedOperation || _operationRunning;

            if (group is not { } groupId)
            {
                return await runner.ExecuteStepAsync(child, position, branch, inOperation, cancellationToken);
            }

            // All branches are registered together when the first one is launched, so a branch that starts
            // running and asks for a coordinated operation cannot miss a sibling that has not started yet.
            ParticipantId participant;
            lock (_gate)
            {
                _participants ??= runner._coordinator.RegisterParticipants(groupId, count);
                participant = _participants[index];
            }

            var failed = false;
            try
            {
                return await runner.ExecuteStepAsync(
                    child, position, new BranchScope(groupId, participant), inOperation, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                runner._coordinator.Unregister(groupId, participant, failed);
            }
        }

        public Task ReachSafePointAsync(CancellationToken cancellationToken)
        {
            return branch is null
                ? Task.CompletedTask
                : runner._coordinator.ReachSafePointAsync(branch.Group, branch.Participant, cancellationToken);
        }

        public Task ExecuteWhenSafeAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            // From the moment the operation starts until it ends, the steps it runs are exempt from pausing.
            async Task Run(CancellationToken ct)
            {
                _operationRunning = true;
                try
                {
                    await operation(ct);
                }
                finally
                {
                    _operationRunning = false;
                }
            }

            return branch is null
                ? Run(cancellationToken)
                : runner._coordinator.ExecuteWhenSafeAsync(branch.Group, branch.Participant, Run, cancellationToken);
        }
    }
}

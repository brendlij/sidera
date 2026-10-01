using Astra.Core.Sequencing;
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
    private SequenceState _state = SequenceState.Idle;
    private SequenceExecutionPosition? _currentPosition;
    private Exception? _failure;

    /// <param name="resourceManager">
    /// Coordinates exclusive resources between runners. Runners that work on the same equipment must share
    /// one manager, normally <see cref="AstraRuntimeHost.ResourceManager"/>. Without one the runner uses a
    /// private manager, which only coordinates its own steps.
    /// </param>
    public SequenceRunner(ResourceManager? resourceManager = null)
    {
        _resources = resourceManager ?? new ResourceManager();
    }

    public SequenceState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool IsRunning => State == SequenceState.Running;

    /// <summary>
    /// The innermost execution that is running, or that ran last; <c>null</c> before the first step.
    /// For a repeat this is its child with the current iteration; its parents lead up to the top-level step.
    /// </summary>
    public SequenceExecutionPosition? CurrentPosition
    {
        get { lock (_gate) { return _currentPosition; } }
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
    /// Raised when <see cref="State"/> or <see cref="CurrentPosition"/> changed: when a run starts,
    /// when each step or repeated child starts, and when the run ends.
    /// Raised on the thread running the sequence; observers read the properties themselves.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised after a step, top-level or nested, has completed successfully and before the next
    /// execution starts, with its position and result. Never raised for an execution that threw or was cancelled.
    /// Raised on the thread running the sequence.
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

            _state = SequenceState.Running;
            _currentPosition = null;
            _failure = null;
        }

        RaiseChanged();

        try
        {
            for (var i = 0; i < sequence.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = sequence.Steps[i];
                var position = new SequenceExecutionPosition(step.Name, i, sequence.Steps.Count);
                await ExecuteStepAsync(step, position, cancellationToken);
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
    }

    // One execution of one step: mark it current, run it, then report its result.
    private async Task<SequenceStepResult> ExecuteStepAsync(
        ISequenceStep step,
        SequenceExecutionPosition position,
        CancellationToken cancellationToken
    )
    {
        lock (_gate)
        {
            _currentPosition = position;
        }

        RaiseChanged();

        SequenceStepResult result;

        // Only steps that declare requirements acquire anything. Containers do not, so a repeat or group
        // never holds what its children need: each child takes its own resources when it runs.
        var required = step is IResourceAwareSequenceStep aware ? aware.RequiredResources : [];
        using (await _resources.AcquireAsync(required, cancellationToken))
        {
            result = await step.ExecuteAsync(new StepContext(this, position), cancellationToken);
        }

        // Reported after the resources are released, so observers never run while holding them.
        RaiseStepCompleted(new SequenceStepCompletedEventArgs(position, result));
        return result;
    }

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

    // Created per execution, so a child is always positioned under the execution that started it.
    private sealed class StepContext(SequenceRunner runner, SequenceExecutionPosition parent) : ISequenceStepContext
    {
        public Task<SequenceStepResult> ExecuteChildAsync(
            ISequenceStep child,
            int index,
            int count,
            CancellationToken cancellationToken
        )
        {
            var position = new SequenceExecutionPosition(child.Name, index, count, parent);
            return runner.ExecuteStepAsync(child, position, cancellationToken);
        }
    }
}

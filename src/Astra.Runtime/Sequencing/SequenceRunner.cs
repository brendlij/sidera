using Astra.Core.Sequencing;

namespace Astra.Runtime.Sequencing;

/// <summary>
/// Runs the steps of one sequence at a time, strictly in order. The first step that throws stops
/// the run (state <see cref="SequenceState.Failed"/>, exception rethrown); cancelling the token
/// stops it too (state <see cref="SequenceState.Cancelled"/>, <see cref="OperationCanceledException"/> rethrown).
/// A runner can be reused once its previous run has ended.
/// </summary>
public sealed class SequenceRunner
{
    private readonly object _gate = new();
    private SequenceState _state = SequenceState.Idle;
    private int _currentStepIndex = -1;
    private string? _currentStepName;
    private Exception? _failure;

    public SequenceState State
    {
        get { lock (_gate) { return _state; } }
    }

    public bool IsRunning => State == SequenceState.Running;

    /// <summary>Index of the running step, or of the last step that ran; -1 before the first step.</summary>
    public int CurrentStepIndex
    {
        get { lock (_gate) { return _currentStepIndex; } }
    }

    /// <summary>Name of the step at <see cref="CurrentStepIndex"/>; <c>null</c> before the first step.</summary>
    public string? CurrentStepName
    {
        get { lock (_gate) { return _currentStepName; } }
    }

    /// <summary>The exception that ended the last run, if its state is <see cref="SequenceState.Failed"/>.</summary>
    public Exception? Failure
    {
        get { lock (_gate) { return _failure; } }
    }

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
            _currentStepIndex = -1;
            _currentStepName = null;
            _failure = null;
        }

        try
        {
            for (var i = 0; i < sequence.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var step = sequence.Steps[i];
                lock (_gate)
                {
                    _currentStepIndex = i;
                    _currentStepName = step.Name;
                }

                await step.ExecuteAsync(cancellationToken);
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
                _state = SequenceState.Failed;
            }

            throw;
        }
    }

    private void SetState(SequenceState state)
    {
        lock (_gate)
        {
            _state = state;
        }
    }
}

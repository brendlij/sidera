namespace Sidera.Runtime.Sequencing;

internal enum PausePhase
{
    Running,

    /// <summary>A pause was requested, but at least one line of execution is still doing work.</summary>
    Pausing,

    /// <summary>A pause was requested and every line of execution is waiting at a boundary.</summary>
    Paused
}

/// <summary>
/// Cooperative pause for one run. A pause request does not stop anything by itself: each line of execution (the
/// top-level sequence, and every branch of a parallel step) checks <see cref="WaitAtBoundaryAsync"/> before it starts
/// a new step and waits there while a pause is requested. The run counts as paused once every live line is waiting.
/// <para>
/// Lines are counted by the runner: the top-level sequence is one line; a step that launches parallel branches
/// stops being a line while they run and each branch is one. A line that waits holds no resources, because the
/// boundary comes before the resources of the next step are acquired.
/// </para>
/// <para>
/// Everything is guarded by one lock; waiting uses task completion sources, never polling. A wake-up cannot get
/// lost: waiters capture the current resume and poke signals before they evaluate their pass condition.
/// </para>
/// </summary>
internal sealed class SequencePauseGate
{
    private readonly object _gate = new();
    private bool _requested;
    private int _lines;
    private int _waiting;
    private PausePhase _phase = PausePhase.Running;
    private TaskCompletionSource _resume = NewSignal();
    private TaskCompletionSource _poke = NewSignal();

    public PausePhase Phase
    {
        get { lock (_gate) { return _phase; } }
    }

    /// <summary>Raised, outside the lock, whenever <see cref="Phase"/> changed.</summary>
    public event EventHandler? PhaseChanged;

    /// <summary>Forgets everything of the previous run. Waiters of an earlier run are released by their cancellation, not here.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _requested = false;
            _lines = 0;
            _waiting = 0;
            _phase = PausePhase.Running;
            _resume = NewSignal();
            _poke = NewSignal();
        }
    }

    public void AddLines(int count) => Change(() => _lines += count);

    public void RemoveLines(int count) => Change(() => _lines -= count);

    /// <summary>Asks the run to pause at its next boundaries. Returns false if a pause was already requested.</summary>
    public bool RequestPause()
    {
        var wasRequested = false;
        Change(() =>
        {
            wasRequested = _requested;
            _requested = true;
        });
        return !wasRequested;
    }

    /// <summary>Releases every waiting line and withdraws the request. Returns false if no pause was requested.</summary>
    public bool Resume()
    {
        TaskCompletionSource? released = null;
        Change(() =>
        {
            if (_requested)
            {
                _requested = false;
                released = _resume;
                _resume = NewSignal();
            }
        });
        released?.TrySetResult();
        return released is not null;
    }

    /// <summary>Makes waiting lines evaluate their pass condition again, for conditions that changed outside the gate.</summary>
    public void Poke()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _poke;
            _poke = NewSignal();
        }

        old.TrySetResult();
    }

    /// <summary>
    /// Returns at once unless a pause is requested and <paramref name="mayPass"/> is false; then waits, counted as a
    /// waiting line, until the pause is withdrawn, <paramref name="mayPass"/> becomes true after a
    /// <see cref="Poke"/>, or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <param name="onWaiting">Called with true when the line starts waiting and with false when it stops.</param>
    public async Task WaitAtBoundaryAsync(Func<bool> mayPass, Action<bool> onWaiting, CancellationToken cancellationToken)
    {
        var counted = false;
        try
        {
            while (true)
            {
                Task resume;
                Task poke;
                lock (_gate)
                {
                    if (!_requested)
                    {
                        return;
                    }

                    resume = _resume.Task;
                    poke = _poke.Task;
                }

                // Evaluated outside the lock; the signals above were captured first, so a change in between wakes us.
                if (mayPass())
                {
                    return;
                }

                if (!counted)
                {
                    var changed = false;
                    lock (_gate)
                    {
                        if (!_requested)
                        {
                            return;
                        }

                        _waiting++;
                        counted = true;
                        changed = Recompute();
                    }

                    onWaiting(true);
                    if (changed)
                    {
                        PhaseChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                await Task.WhenAny(resume, poke).WaitAsync(cancellationToken);
            }
        }
        finally
        {
            if (counted)
            {
                var changed = false;
                lock (_gate)
                {
                    _waiting--;
                    changed = Recompute();
                }

                onWaiting(false);
                if (changed)
                {
                    PhaseChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    private void Change(Action mutate)
    {
        bool changed;
        lock (_gate)
        {
            mutate();
            changed = Recompute();
        }

        if (changed)
        {
            PhaseChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // Called with the lock held. Returns whether the phase changed.
    private bool Recompute()
    {
        var phase = !_requested
            ? PausePhase.Running
            : _lines > 0 && _waiting >= _lines ? PausePhase.Paused : PausePhase.Pausing;

        if (phase == _phase)
        {
            return false;
        }

        _phase = phase;
        return true;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

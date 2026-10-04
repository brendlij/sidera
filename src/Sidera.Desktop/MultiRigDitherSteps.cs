using System;
using System.Threading;
using System.Threading.Tasks;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Sequencing;

namespace Sidera.Desktop;

/// <summary>
/// The count of completed exposures of the trigger rig of one Multi-Rig block. It belongs to the track that is counted
/// (<see cref="RigTrackStep"/> sets it back to zero whenever the track starts, so a sequence that is run again counts
/// again from the start) and is used by nothing but the steps after that track's exposures, which run one after
/// another in that one branch. It is not shared with any other branch.
/// </summary>
public sealed class FrameCounter
{
    public int Count { get; private set; }

    internal void Reset() => Count = 0;

    internal int Increment() => ++Count;
}

/// <summary>
/// Put after each exposure of the trigger rig of a Multi-Rig block: counts the exposure, and when the count reaches
/// <c>everyNFrames</c> runs the <see cref="DitherAction"/> of the block and starts counting from zero again.
/// <para>
/// The step runs only after an exposure that completed (a failed or cancelled one ends the branch before it), counts
/// nothing else, and never at the start. The dither itself is not done here: the <see cref="DitherAction"/> asks the
/// coordination group of the block for a coordinated operation, so the mount only moves when every other live track
/// is at a safe point, and this branch is held in the request meanwhile, so it cannot count, let alone request,
/// another dither before this one is over.
/// </para>
/// </summary>
public sealed class DitherEveryNthFrameStep : ISequenceStep
{
    private readonly FrameCounter _counter;
    private readonly int _everyNFrames;
    private readonly DitherAction _dither;

    public DitherEveryNthFrameStep(FrameCounter counter, int everyNFrames, DitherAction dither)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(dither);
        ArgumentOutOfRangeException.ThrowIfLessThan(everyNFrames, 1);
        _counter = counter;
        _everyNFrames = everyNFrames;
        _dither = dither;
    }

    public string Name => $"Dither every {_everyNFrames} frames";

    public int EveryNFrames => _everyNFrames;

    public DitherAction Dither => _dither;

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        if (_counter.Increment() < _everyNFrames)
        {
            return new SequenceStepResult();
        }

        await context.ExecuteChildAsync(_dither, 0, 1, cancellationToken);

        // Only a dither that succeeded starts the next count.
        _counter.Reset();
        return new SequenceStepResult();
    }
}

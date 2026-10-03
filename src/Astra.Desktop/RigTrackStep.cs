using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Astra.Core.Sequencing;

namespace Astra.Desktop;

/// <summary>A step of a Rig Track failed. <see cref="InnerException"/> is what went wrong; the track says where.</summary>
public sealed class RigTrackFailedException(Guid trackId, string trackName, Exception inner)
    : Exception($"{trackName}: {inner.Message}", inner)
{
    /// <summary>The id of the track in the draft it was built from.</summary>
    public Guid TrackId { get; } = trackId;

    public string TrackName { get; } = trackName;
}

/// <summary>
/// The runtime step of one Rig Track, the branch of the <see cref="ParallelStep"/> that a Multi-Rig block becomes. It
/// does what a <see cref="SequenceGroup"/> does (runs its steps in order, each as a child of this one, so every
/// execution is tracked, paused, cancelled and given its resources by the runner like any other) and nothing else,
/// with one difference: when a step fails, the failure says which track it was. A parallel step only reports the
/// exception of a failed branch, so without this there is no telling the branches apart. Cancellation, including that
/// of a branch whose sibling failed, passes through unchanged.
/// </summary>
public sealed class RigTrackStep : ISequenceStep
{
    public RigTrackStep(Guid trackId, string name, IEnumerable<ISequenceStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(steps);

        var list = steps.ToArray();
        if (list.Length == 0)
        {
            throw new ArgumentException("A rig track needs at least one step.", nameof(steps));
        }

        if (list.Any(step => step is null))
        {
            throw new ArgumentException("A rig track cannot contain null steps.", nameof(steps));
        }

        TrackId = trackId;
        Name = name;
        Steps = list;
    }

    public Guid TrackId { get; }
    public string Name { get; }
    public IReadOnlyList<ISequenceStep> Steps { get; }

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        var results = new List<SequenceStepResult>(Steps.Count);

        try
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await context.ExecuteChildAsync(Steps[i], i, Steps.Count, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RigTrackFailedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RigTrackFailedException(TrackId, Name, ex);
        }

        return new SequenceStepResult(results.AsReadOnly());
    }
}

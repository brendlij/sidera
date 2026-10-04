using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Sidera.Core.Rigs;
using Sidera.Core.Sequencing;
using Sidera.Runtime.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Sidera.Desktop;

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
/// <para>
/// The track is a logging context: while it runs, everything its steps and their resources and coordination write to the
/// log carries the rig and the track (<c>RigId</c>, <c>TrackId</c>), and its start and end are logged at Debug.
/// </para>
/// </summary>
public sealed class RigTrackStep : ISequenceStep
{
    private readonly FrameCounter? _counter;
    private readonly RigId? _rigId;
    private readonly ILogger _logger;

    /// <param name="counter">The count of frames of this track, if its frames are counted; set back to zero at every start.</param>
    /// <param name="rigId">The rig of the track, for the log.</param>
    /// <param name="logger">Where the track is reported.</param>
    public RigTrackStep(
        Guid trackId, string name, IEnumerable<ISequenceStep> steps, FrameCounter? counter = null,
        RigId? rigId = null, ILogger<RigTrackStep>? logger = null)
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
        _counter = counter;
        _rigId = rigId;
        _logger = logger ?? NullLogger<RigTrackStep>.Instance;
    }

    public Guid TrackId { get; }

    /// <summary>The rig the track images with, when it was told (for what shows the run, and for the log).</summary>
    public RigId? RigId => _rigId;

    public string Name { get; }
    public IReadOnlyList<ISequenceStep> Steps { get; }

    public async Task<SequenceStepResult> ExecuteAsync(ISequenceStepContext context, CancellationToken cancellationToken)
    {
        _counter?.Reset();
        var results = new List<SequenceStepResult>(Steps.Count);

        using var scope = _logger.Begin(
            (LogContext.RigId, _rigId?.ToString() ?? Name), (LogContext.TrackId, TrackId.ToString("N")[..8]));
        var started = Stopwatch.GetTimestamp();
        _logger.LogDebug("Rig track {TrackName} started with {StepCount} steps", Name, Steps.Count);

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
            _logger.LogDebug(
                "Rig track {TrackName} cancelled after {DurationSeconds:0.0} s", Name, Stopwatch.GetElapsedTime(started).TotalSeconds);
            throw;
        }
        catch (RigTrackFailedException ex)
        {
            _logger.LogWarning(
                "Rig track {TrackName} failed after {DurationSeconds:0.0} s: {Reason}",
                Name, Stopwatch.GetElapsedTime(started).TotalSeconds, ex.InnerException?.Message ?? ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Rig track {TrackName} failed after {DurationSeconds:0.0} s: {Reason}",
                Name, Stopwatch.GetElapsedTime(started).TotalSeconds, ex.Message);
            throw new RigTrackFailedException(TrackId, Name, ex);
        }

        _logger.LogDebug(
            "Rig track {TrackName} completed in {DurationSeconds:0.0} s", Name, Stopwatch.GetElapsedTime(started).TotalSeconds);
        return new SequenceStepResult(results.AsReadOnly());
    }
}
